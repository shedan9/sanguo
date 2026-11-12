using System.Collections.Immutable;
using Sanguo.BattleCore.Randomness;

namespace Sanguo.BattleCore.Basic;

/// <summary>通用角色、单批次基础牌开发规则；尚未冻结为OL规则包。</summary>
public static class BasicBattle
{
    public const string RulesetId = "rules.basic-development.v1";

    public static BattleState Create(string battleId, IEnumerable<CombatantSetup> setup,
        IEnumerable<BattleCard> cards, ulong seed, int initialHandSize = 4)
    {
        var units = setup.Select((u, i) => new Combatant(u.EntityId, u.ControllerId, u.TeamId,
            i, u.MaxHp, u.MaxHp, true, [])).ToImmutableArray();
        var pile = cards.ToList();
        if (initialHandSize < 0 || pile.Count < (long)units.Length * initialHandSize)
            throw new ArgumentException("开局牌不足或手牌数量无效。");
        var random = new Pcg32(seed, 54);
        random.Shuffle(pile);
        var state = new BattleState(battleId, RulesetId, 0, units, pile.ToImmutableArray(), [], [],
            pile.Select(c => c.InstanceId).Order(StringComparer.Ordinal).ToImmutableArray(), random.Capture(),
            0, 1, BattlePhase.Play, 0, [], null, null, []);
        // 配置先完整校验，防止空队伍或非法实体在发牌前进入规则执行。
        BattleValidation.Validate(state, requireDecision: false);
        for (var i = 0; i < units.Length; i++) state = Draw(state, i, initialHandSize);
        state = Draw(Log(state, "turn.started", units[0].EntityId), 0, 2);
        state = WithDecision(state);
        BattleValidation.Validate(state);
        return state;
    }

    public static ImmutableArray<BattleAction> GetLegalActions(BattleState state)
    {
        if (state.PendingDecision is null) return [];
        var actor = Unit(state, state.PendingDecision.ActorId);
        var actions = ImmutableArray.CreateBuilder<BattleAction>();
        if (!state.Frames.IsEmpty)
        {
            var frame = state.Frames[^1];
            var kind = frame.Kind == EffectKind.Dodge ? BasicCardKind.Dodge : BasicCardKind.Peach;
            foreach (var card in actor.Hand.Where(c => c.Kind == kind))
                actions.Add(new(frame.Kind == EffectKind.Dodge ? BattleActionKind.Dodge : BattleActionKind.Peach,
                    card.InstanceId, frame.TargetId));
            actions.Add(new(BattleActionKind.Pass));
        }
        else if (state.Phase == BattlePhase.Discard)
        {
            foreach (var card in actor.Hand) actions.Add(new(BattleActionKind.Discard, card.InstanceId));
        }
        else
        {
            foreach (var card in actor.Hand)
            {
                if (card.Kind == BasicCardKind.Peach && actor.Hp < actor.MaxHp)
                    actions.Add(new(BattleActionKind.Peach, card.InstanceId, actor.EntityId));
                if (card.Kind != BasicCardKind.Slash || state.SlashesUsed != 0) continue;
                foreach (var target in state.Combatants.Where(u => u.Alive && u.EntityId != actor.EntityId
                    && Distance(state, actor.EntityId, u.EntityId) <= 1))
                    actions.Add(new(BattleActionKind.Slash, card.InstanceId, target.EntityId));
            }
            actions.Add(new(BattleActionKind.EndPlay));
        }
        return actions.ToImmutable();
    }

    public static BattleView Observe(BattleState state, string controllerId)
        => new(state.BattleId, state.StateVersion, state.Phase, state.TurnNumber,
            state.Combatants[state.TurnCursor].EntityId,
            state.Combatants.Select(u => new CombatantView(u.EntityId, u.TeamId, u.SeatId, u.Hp,
                u.MaxHp, u.Alive, u.Hand.Length, u.ControllerId == controllerId ? u.Hand : [])).ToImmutableArray(),
            state.DrawPile.Length, state.Processing, state.DiscardPile,
            state.PendingDecision, state.PendingDecision?.ControllerId == controllerId ? GetLegalActions(state) : [],
            state.WinnerTeamId);

    public static int Distance(BattleState state, string sourceId, string targetId)
    {
        var alive = state.Combatants.Where(u => u.Alive).Select(u => u.EntityId).ToList();
        var source = alive.IndexOf(sourceId);
        var target = alive.IndexOf(targetId);
        if (source < 0 || target < 0) throw new ArgumentException("距离只能用于存活单位。");
        var gap = Math.Abs(source - target);
        return Math.Min(gap, alive.Count - gap);
    }

    public static BattleState Apply(BattleState state, BattleAction action)
    {
        if (!GetLegalActions(state).Contains(action)) throw new ArgumentException("当前动作不合法。", nameof(action));
        var actorId = state.PendingDecision!.ActorId;
        state = state with { StateVersion = checked(state.StateVersion + 1), PendingDecision = null };
        switch (action.Kind)
        {
            case BattleActionKind.Slash:
                state = Pay(state, actorId, action.CardId!);
                state = Log(state with { SlashesUsed = 1 }, "slash.used", actorId, action.TargetId, action.CardId);
                state = Push(state, new(EffectKind.FinishCard, actorId, action.TargetId!, action.CardId!));
                state = Push(state, new(EffectKind.Dodge, actorId, action.TargetId!, action.CardId!));
                break;
            case BattleActionKind.Dodge:
                state = FinishPayment(Pay(state, actorId, action.CardId!), action.CardId!, actorId);
                state = Log(Pop(state), "slash.dodged", actorId, action.TargetId, action.CardId);
                break;
            case BattleActionKind.Peach:
                state = Pay(state, actorId, action.CardId!);
                var target = Unit(state, action.TargetId!);
                state = SetUnit(state, target with { Hp = target.Hp + 1 });
                state = Log(state, "hp.recovered", actorId, target.EntityId, action.CardId, 1);
                state = FinishPayment(state, action.CardId!, actorId);
                if (!state.Frames.IsEmpty && Unit(state, target.EntityId).Hp > 0)
                    state = Log(Pop(state), "dying.saved", target.EntityId);
                break;
            case BattleActionKind.Pass:
                var frame = state.Frames[^1];
                state = Log(state, "response.passed", actorId, frame.TargetId);
                state = Pop(state);
                state = Push(state, frame.Kind == EffectKind.Dodge
                    ? frame with { Kind = EffectKind.Damage, Amount = 1 }
                    : frame with { Cursor = frame.Cursor + 1 });
                break;
            case BattleActionKind.EndPlay:
                state = Log(state with { Phase = BattlePhase.Discard }, "play.ended", actorId);
                break;
            case BattleActionKind.Discard:
                state = FinishPayment(Pay(state, actorId, action.CardId!), action.CardId!, actorId);
                break;
        }
        return Advance(state);
    }

    private static BattleState Advance(BattleState state)
    {
        while (!state.Frames.IsEmpty)
        {
            var frame = state.Frames[^1];
            if (frame.Kind == EffectKind.Dodge) return WithDecision(state);
            if (frame.Kind == EffectKind.Rescue)
            {
                if (frame.Cursor < state.Combatants.Count(u => u.Alive)) return WithDecision(state);
                var target = Unit(state, frame.TargetId);
                state = SetUnit(Pop(state), target with { Alive = false, Hand = [] });
                state = state with { DiscardPile = state.DiscardPile.AddRange(target.Hand) };
                state = Log(state, "unit.died", target.EntityId, frame.SourceId, amount: target.Hand.Length);
                continue;
            }
            state = Pop(state);
            if (frame.Kind == EffectKind.FinishCard)
                state = FinishPayment(state, frame.CardId, frame.SourceId);
            else
            {
                var target = Unit(state, frame.TargetId);
                target = target with { Hp = target.Hp - frame.Amount };
                state = Log(SetUnit(state, target), "damage.dealt", frame.SourceId, target.EntityId, frame.CardId, frame.Amount);
                if (target.Hp <= 0)
                    state = Push(Log(state, "dying.started", target.EntityId), frame with { Kind = EffectKind.Rescue, Cursor = 0 });
            }
        }
        var teams = state.Combatants.Where(u => u.Alive).Select(u => u.TeamId).Distinct(StringComparer.Ordinal).ToArray();
        if (teams.Length <= 1)
            return Log(state with { Phase = BattlePhase.Finished, WinnerTeamId = teams.SingleOrDefault() },
                "battle.finished", state.BattleId, teams.SingleOrDefault());
        var active = state.Combatants[state.TurnCursor];
        if (active.Alive && (state.Phase != BattlePhase.Discard || active.Hand.Length > active.Hp))
            return WithDecision(state);
        state = Log(state, "turn.ended", active.EntityId);
        var next = (state.TurnCursor + 1) % state.Combatants.Length;
        while (!state.Combatants[next].Alive) next = (next + 1) % state.Combatants.Length;
        state = state with { TurnCursor = next, TurnNumber = checked(state.TurnNumber + 1), Phase = BattlePhase.Play, SlashesUsed = 0 };
        state = Draw(Log(state, "turn.started", state.Combatants[next].EntityId), next, 2);
        return WithDecision(state);
    }

    private static BattleState Draw(BattleState state, int index, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (state.DrawPile.IsEmpty && !state.DiscardPile.IsEmpty)
            {
                var pile = state.DiscardPile.ToList();
                var random = new Pcg32(state.Random);
                random.Shuffle(pile);
                state = Log(state with { DrawPile = pile.ToImmutableArray(), DiscardPile = [], Random = random.Capture() },
                    "deck.reshuffled", state.Combatants[index].EntityId, amount: pile.Count);
            }
            if (state.DrawPile.IsEmpty)
                return Log(state, "draw.exhausted", state.Combatants[index].EntityId, amount: count - i);
            var card = state.DrawPile[0];
            state = SetUnit(state, state.Combatants[index] with { Hand = state.Combatants[index].Hand.Add(card) });
            state = Log(state with { DrawPile = state.DrawPile.RemoveAt(0) }, "card.drawn", state.Combatants[index].EntityId, cardId: card.InstanceId);
        }
        return state;
    }

    private static BattleState Pay(BattleState state, string actorId, string cardId)
    {
        var actor = Unit(state, actorId);
        var card = actor.Hand.Single(c => c.InstanceId == cardId);
        return SetUnit(state with { Processing = state.Processing.Add(card) }, actor with { Hand = actor.Hand.Remove(card) });
    }

    private static BattleState FinishPayment(BattleState state, string cardId, string actorId)
    {
        var card = state.Processing.Single(c => c.InstanceId == cardId);
        return Log(state with { Processing = state.Processing.Remove(card), DiscardPile = state.DiscardPile.Add(card) },
            "card.discarded", actorId, cardId: cardId);
    }

    internal static BattleDecision ExpectedDecision(BattleState state)
    {
        var actor = state.Frames.IsEmpty ? state.Combatants[state.TurnCursor] : state.Frames[^1].Kind == EffectKind.Dodge
            ? Unit(state, state.Frames[^1].TargetId)
            : RescueOrder(state)[state.Frames[^1].Cursor];
        return new($"{state.BattleId}.decision.{state.StateVersion}", actor.ControllerId, actor.EntityId);
    }

    private static Combatant[] RescueOrder(BattleState state)
        => Enumerable.Range(0, state.Combatants.Length).Select(i => state.Combatants[(state.TurnCursor + i) % state.Combatants.Length])
            .Where(u => u.Alive).ToArray();
    private static BattleState WithDecision(BattleState state) => state with { PendingDecision = ExpectedDecision(state) };
    private static BattleState Push(BattleState state, EffectFrame frame) => state with { Frames = state.Frames.Add(frame) };
    private static BattleState Pop(BattleState state) => state with { Frames = state.Frames.RemoveAt(state.Frames.Length - 1) };
    private static Combatant Unit(BattleState state, string id) => state.Combatants.Single(u => u.EntityId == id);
    private static BattleState SetUnit(BattleState state, Combatant unit)
        => state with { Combatants = state.Combatants.SetItem(unit.SeatId, unit) };
    private static BattleState Log(BattleState state, string code, string actor, string? target = null, string? cardId = null, int amount = 0)
        => state with { Events = state.Events.Add(new(state.Events.Length + 1L, code, actor, target, cardId, amount)) };
}
