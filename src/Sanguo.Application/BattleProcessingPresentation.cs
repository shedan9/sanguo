using System.Collections.Immutable;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Application;

public sealed record ProcessingCardView(BattleCard Card, BasicCardKind EffectiveKind,
    string? SourceId, string? TargetId, string? SkillId, string? RespondsToCardId,
    bool AwaitingResponse, bool OriginalTrick);

/// <summary>仅从控制者视图和公开记录还原正在结算的牌，不保留已经离开处理区的历史牌。</summary>
public static class BattleProcessingPresentation
{
    public static ImmutableArray<ProcessingCardView> Observe(BattleView view, ImmutableArray<PublicBattleEvent> log)
    {
        var uses = new Dictionary<string, PublicBattleEvent>(StringComparer.Ordinal);
        var skills = new Dictionary<string, string?>(StringComparer.Ordinal);
        var pendingSkills = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in log)
        {
            if (entry.Card is not { } card) continue;
            if (entry.Code == "skill.used") pendingSkills[card.InstanceId] = entry.RelatedCardId;
            if (entry.Code is not ("slash.used" or "duel.slash_responded" or "slash.dodged" or "hp.recovered" or "duel.used" or "exnihilo.used"
                or "dismantlement.used" or "snatch.used" or "nullification.used")) continue;
            uses[card.InstanceId] = entry;
            skills[card.InstanceId] = pendingSkills.Remove(card.InstanceId, out var skill) ? skill : null;
        }
        return view.Processing.Select(card =>
        {
            uses.TryGetValue(card.InstanceId, out var use);
            skills.TryGetValue(card.InstanceId, out var skill);
            return new ProcessingCardView(card, use?.Code is "slash.used" or "duel.slash_responded" ? BasicCardKind.Slash : card.Kind,
                use?.ActorId, use?.TargetId, skill, use?.Code == "nullification.used" ? use.RelatedCardId : null,
                view.Response?.CardId == card.InstanceId, view.Response?.Trick?.CardId == card.InstanceId);
        }).ToImmutableArray();
    }
}
