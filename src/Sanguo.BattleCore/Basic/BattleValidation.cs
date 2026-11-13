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

        if (!state.Frames.IsEmpty) ValidateFrames(state);
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

    private static void ValidateFrames(BattleState state)
    {
        var frames = state.Frames;
        var units = state.Combatants;
        var aliveCount = units.Count(u => u.Alive);
        if (state.Phase != BattlePhase.Play || frames.Length < 2 || state.Processing.IsEmpty
            || frames.Any(f => f is null || !Enum.IsDefined(f.Kind) || f.Cursor < 0 || f.ChainDepth < 0))
            throw new ArgumentException("基础战斗效果栈或处理区无效。");
        var root = frames[0];
        var response = frames[^1];
        var card = state.Processing[0];
        if (root.Kind != EffectKind.FinishCard || root.CardId != card.InstanceId || root.Amount != 0 || root.Cursor != 0
            || root.CounteredCardId is not null || root.ChainDepth != 0
            || root.SourceId != units[state.TurnCursor].EntityId || !units.Any(u => u.EntityId == root.SourceId && u.Alive)
            || !units.Any(u => u.EntityId == root.TargetId && u.Alive)
            || card.Kind is not (BasicCardKind.Slash or BasicCardKind.Duel or BasicCardKind.ExNihilo or BasicCardKind.Dismantlement or BasicCardKind.Snatch)
            || (card.Kind == BasicCardKind.Snatch && BasicBattle.Distance(state, root.SourceId, root.TargetId) > 1)
            || ((root.SourceId == root.TargetId) != (card.Kind == BasicCardKind.ExNihilo)))
            throw new ArgumentException("使用牌收尾帧与原牌、来源或目标不一致。");

        if (response.Kind == EffectKind.Nullification)
        {
            if (card.Kind is not (BasicCardKind.Duel or BasicCardKind.ExNihilo or BasicCardKind.Dismantlement or BasicCardKind.Snatch) || frames.Length < 3
                || state.Processing.Length != frames.Length - 2 || response.Cursor >= aliveCount
                || response.ChainDepth != frames.Length - 3 || response.ChainDepth > state.StateVersion
                || response.CounteredCardId is not null || response.Amount != 0 || response.TargetId != root.TargetId)
                throw new ArgumentException("无懈窗口、连续放弃游标或反制层数无效。");
            var effect = frames[1];
            if (effect.Kind != (card.Kind switch
                {
                    BasicCardKind.Duel => EffectKind.Duel, BasicCardKind.ExNihilo => EffectKind.DrawCards,
                    _ => EffectKind.SelectHandCard
                })
                || effect.SourceId != root.SourceId || effect.TargetId != root.TargetId || effect.CardId != root.CardId
                || effect.Amount != (card.Kind == BasicCardKind.ExNihilo ? 2 : 0) || effect.Cursor != 0
                || effect.CounteredCardId is not null || effect.ChainDepth != 0)
                throw new ArgumentException("锦囊效果帧与原始使用不一致。");
            var previousCardId = root.CardId;
            var previousSource = root.SourceId;
            for (var i = 2; i < frames.Length - 1; i++)
            {
                var counter = frames[i];
                var payment = state.Processing[i - 1];
                if (counter.Kind != EffectKind.FinishCard || payment.Kind != BasicCardKind.Nullification
                    || payment.InstanceId != counter.CardId || counter.CounteredCardId != previousCardId
                    || counter.TargetId != previousSource || !units.Any(u => u.EntityId == counter.SourceId && u.Alive)
                    || counter.Amount != 0 || counter.Cursor != 0 || counter.ChainDepth != 0)
                    throw new ArgumentException("反无懈链的实体支付或被反制对象无效。");
                previousCardId = counter.CardId;
                previousSource = counter.SourceId;
            }
            if (response.CardId != previousCardId || response.SourceId != previousSource)
                throw new ArgumentException("无懈窗口未指向最后一张待反制锦囊。");
            return;
        }

        if (frames.Length != 2 || state.Processing.Length != 1 || response.CardId != root.CardId
            || response.CounteredCardId is not null || response.ChainDepth != 0
            || !units.Any(u => u.EntityId == response.SourceId && u.Alive)
            || !units.Any(u => u.EntityId == response.TargetId && u.Alive))
            throw new ArgumentException("响应帧的实体支付、来源或目标无效。");
        if (card.Kind is BasicCardKind.Dismantlement or BasicCardKind.Snatch)
        {
            if (response.Kind != EffectKind.SelectHandCard || response.SourceId != root.SourceId
                || response.TargetId != root.TargetId || response.Amount != 0 || response.Cursor != 0
                || units.Single(u => u.EntityId == response.TargetId).Hand.IsEmpty)
                throw new ArgumentException("拆顺手牌选择帧的对象、区域或游标无效。");
            return;
        }
        if (card.Kind == BasicCardKind.Slash)
        {
            if (state.SlashesUsed != 1 || response.SourceId != root.SourceId || response.TargetId != root.TargetId
                || response.Kind is not (EffectKind.Dodge or EffectKind.Rescue))
                throw new ArgumentException("杀的响应类型、次数或对象无效。");
        }
        else if (card.Kind == BasicCardKind.Duel)
        {
            if (response.Kind is not (EffectKind.Duel or EffectKind.Rescue)
                || !((response.SourceId == root.SourceId && response.TargetId == root.TargetId)
                    || (response.SourceId == root.TargetId && response.TargetId == root.SourceId)))
                throw new ArgumentException("决斗响应或伤害双方无效。");
        }
        else throw new ArgumentException("摸牌锦囊不能挂起为杀、决斗或濒死响应。");

        if (response.Kind == EffectKind.Rescue)
        {
            if (response.Amount != 1 || response.Cursor >= aliveCount || units.Single(u => u.EntityId == response.TargetId).Hp > 0)
                throw new ArgumentException("濒死帧的伤害、救援游标或体力无效。");
        }
        else if (response.Amount != 0 || response.Cursor > state.StateVersion
            || (response.Kind == EffectKind.Dodge && response.Cursor != 0)
            || (response.Kind == EffectKind.Duel && (response.SourceId != (response.Cursor % 2 == 0 ? root.SourceId : root.TargetId)
                || response.TargetId != (response.Cursor % 2 == 0 ? root.TargetId : root.SourceId))))
            throw new ArgumentException("杀或决斗的响应轮次无效。");
    }
}
