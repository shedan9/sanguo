using Sanguo.BattleCore.Randomness;

namespace Sanguo.BattleCore.Basic;

public static class BattleValidation
{
    public static void Validate(BattleState state, bool requireDecision = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.BattleId) || state.RulesetId != BasicBattle.RulesetId
            || state.StateVersion < 0 || state.Combatants.IsDefaultOrEmpty || state.Combatants.Length > 8
            || state.DrawPile.IsDefault || state.Processing.IsDefault || state.DiscardPile.IsDefault
            || state.CardInventory.IsDefault || state.Frames.IsDefault || state.Events.IsDefault
            || state.TurnCursor < 0 || state.TurnCursor >= state.Combatants.Length || state.TurnNumber < 1
            || state.SlashesUsed is < 0 or > 1 || !Enum.IsDefined(state.Phase))
            throw new ArgumentException("基础战斗元信息不完整。");
        _ = new Pcg32(state.Random);
        var units = state.Combatants;
        if (units.Any(u => u is null || string.IsNullOrWhiteSpace(u.EntityId) || string.IsNullOrWhiteSpace(u.ControllerId)
            || string.IsNullOrWhiteSpace(u.TeamId) || u.MaxHp <= 0 || u.Hp > u.MaxHp || u.Hand.IsDefault
            || (!u.Alive && (u.Hp > 0 || !u.Hand.IsEmpty)))
            || units.Select(u => u.EntityId).Distinct(StringComparer.Ordinal).Count() != units.Length
            || units.Where((u, i) => u.SeatId != i).Any())
            throw new ArgumentException("战斗单位、座次、体力或手牌无效。");
        var groups = units.GroupBy(u => u.TeamId).ToArray();
        if (groups.Length != 2 || groups.Any(g => g.Count() is < 1 or > 4))
            throw new ArgumentException("必须有两个阵营，每方1至4个单位。");
        var cards = state.DrawPile.Concat(state.Processing).Concat(state.DiscardPile).Concat(units.SelectMany(u => u.Hand)).ToArray();
        if (cards.Any(c => c is null || string.IsNullOrWhiteSpace(c.InstanceId) || !Enum.IsDefined(c.Kind)
            || c.Suit is not ("spade" or "heart" or "club" or "diamond") || c.Rank is < 1 or > 13)
            || cards.Select(c => c.InstanceId).Distinct(StringComparer.Ordinal).Count() != cards.Length
            || state.CardInventory.Any(string.IsNullOrWhiteSpace)
            || state.CardInventory.Distinct(StringComparer.Ordinal).Count() != state.CardInventory.Length
            || !cards.Select(c => c.InstanceId).Order(StringComparer.Ordinal).SequenceEqual(state.CardInventory))
            throw new ArgumentException("实体牌无效、重复、丢失或不属于开局清单。");
        for (var i = 0; i < state.Events.Length; i++)
            if (state.Events[i] is not { } e || e.Sequence != i + 1L || string.IsNullOrWhiteSpace(e.Code)
                || string.IsNullOrWhiteSpace(e.ActorId))
                throw new ArgumentException("战斗日志游标无效。");

        if (!state.Frames.IsEmpty)
        {
            if (state.Phase != BattlePhase.Play || state.Frames.Length != 2 || state.Processing.Length != 1)
                throw new ArgumentException("基础战斗效果栈或处理区无效。");
            var finish = state.Frames[0];
            var response = state.Frames[1];
            if (finish is null || response is null || finish.Kind != EffectKind.FinishCard
                || response.Kind is not (EffectKind.Dodge or EffectKind.Rescue)
                || finish.SourceId != response.SourceId || finish.TargetId != response.TargetId
                || finish.CardId != response.CardId || finish.Amount != 0 || finish.Cursor != 0
                || state.SlashesUsed != 1 || finish.SourceId != units[state.TurnCursor].EntityId
                || finish.SourceId == finish.TargetId || !units.Any(u => u.EntityId == finish.TargetId && u.Alive)
                || state.Processing[0].InstanceId != finish.CardId || state.Processing[0].Kind != BasicCardKind.Slash
                || response.Cursor < 0 || response.Cursor >= units.Count(u => u.Alive)
                || (response.Kind == EffectKind.Dodge && (response.Amount != 0 || response.Cursor != 0))
                || (response.Kind == EffectKind.Rescue && (response.Amount <= 0 || units.Single(u => u.EntityId == response.TargetId).Hp > 0)))
                throw new ArgumentException("响应帧的来源、目标、支付或游标无效。");
        }
        else if (!state.Processing.IsEmpty)
            throw new ArgumentException("稳定决策点不能遗留无效果帧的处理牌。");
        if (units.Any(u => u.Alive && u.Hp <= 0
            && (state.Frames.IsEmpty || state.Frames[^1].Kind != EffectKind.Rescue || state.Frames[^1].TargetId != u.EntityId)))
            throw new ArgumentException("非濒死救援目标不能以非正体力存活。");
        var teams = units.Where(u => u.Alive).Select(u => u.TeamId).Distinct(StringComparer.Ordinal).ToArray();
        if (state.Phase == BattlePhase.Finished)
        {
            if (teams.Length > 1 || state.WinnerTeamId != teams.SingleOrDefault()
                || state.PendingDecision is not null || !state.Frames.IsEmpty)
                throw new ArgumentException("终局与存活阵营不一致。");
        }
        else
        {
            if (teams.Length != 2 || state.WinnerTeamId is not null || !units[state.TurnCursor].Alive
                || (state.Phase == BattlePhase.Discard && units[state.TurnCursor].Hand.Length <= units[state.TurnCursor].Hp)
                || (requireDecision && state.PendingDecision != BasicBattle.ExpectedDecision(state)))
                throw new ArgumentException("回合、终局或待决策请求不一致。");
        }
    }
}
