using System.Collections.Immutable;

namespace Sanguo.BattleCore.Basic;

public sealed record PublicBattleEvent(long Sequence, string Code, string ActorId, string? TargetId,
    BattleCard? Card, int Amount, string? RelatedCardId, BattleCard? RelatedCard = null);

/// <summary>公开记录投影。摸牌和暗手牌转移永不显示实体ID或牌面，未知事件默认不公开。</summary>
public static class PublicBattleLog
{
    public static ImmutableArray<PublicBattleEvent> Observe(BattleState state, long afterSequence = 0)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var cards = state.DrawPile.Concat(state.Processing).Concat(state.DiscardPile)
            .Concat(state.Combatants.SelectMany(u => u.Hand)).ToDictionary(c => c.InstanceId, StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<PublicBattleEvent>();
        foreach (var e in state.Events.Where(e => e.Sequence > afterSequence))
        {
            switch (e.Code)
            {
                case "card.drawn":
                case "card.snatched":
                    result.Add(new(e.Sequence, e.Code, e.ActorId, e.TargetId, null, 1, null));
                    break;
                case "turn.started": case "turn.ended": case "play.ended":
                case "deck.reshuffled": case "draw.exhausted": case "response.passed":
                case "dying.started": case "dying.saved": case "unit.died": case "battle.finished":
                    result.Add(new(e.Sequence, e.Code, e.ActorId, e.TargetId, null, e.Amount, null));
                    break;
                case "slash.used": case "slash.dodged": case "duel.used": case "exnihilo.used":
                case "dismantlement.used": case "snatch.used": case "duel.slash_responded":
                case "nullification.used": case "hp.recovered": case "card.dismantled":
                case "card.discarded": case "card.finished_after_gain": case "trick.no_target_cards":
                case "nullification.window_closed": case "trick.negated": case "damage.dealt":
                case "skill.used": case "skill.declined": case "skill.jianxiong_gained":
                    result.Add(new(e.Sequence, e.Code, e.ActorId, e.TargetId,
                        e.CardId is { } id ? cards[id] : null, e.Amount, e.RelatedCardId,
                        e.RelatedCardId is { } related && cards.TryGetValue(related, out var relatedCard) ? relatedCard : null));
                    break;
            }
        }
        return result.ToImmutable();
    }
}
