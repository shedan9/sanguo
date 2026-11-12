using System.Collections.Immutable;
using Sanguo.BattleCore.Randomness;

namespace Sanguo.BattleCore.Probe;

public sealed record ProbeCard(string InstanceId, string DefinitionId, string Suit, int Rank);
public sealed record ProbeDecision(string RequestId, string ControllerId, string ActorId);
public sealed record DrawProbeState(
    ImmutableArray<ProbeCard> DrawPile,
    ImmutableArray<ProbeCard> Hand,
    ProbeDecision? PendingDecision,
    RandomState Random);

/// <summary>G0抽样试验，不实现牌效、回合、战斗胜负或OL武将。</summary>
public static class DrawProbe
{
    public const string RulesetId = "probe.draw.v1";

    public static DrawProbeState Create(IEnumerable<ProbeCard> cards, ulong seed)
    {
        var pile = cards.ToList();
        if (pile.Count == 0 || pile.Select(c => c.InstanceId).Distinct(StringComparer.Ordinal).Count() != pile.Count)
            throw new ArgumentException("试验牌组不能为空或包含重复实例。", nameof(cards));
        var random = new Pcg32(seed, 54);
        random.Shuffle(pile);
        return new(pile.ToImmutableArray(), [], new("probe.draw.0", "player", "probe.actor"), random.Capture());
    }

    public static DrawProbeState Draw(DrawProbeState state, long nextVersion)
    {
        if (state.PendingDecision is null || state.DrawPile.IsEmpty)
            throw new InvalidOperationException("试验牌堆已经抽完。请新建会话。");
        return state with
        {
            DrawPile = state.DrawPile.RemoveAt(0),
            Hand = state.Hand.Add(state.DrawPile[0]),
            PendingDecision = state.DrawPile.Length == 1 ? null
                : new($"probe.draw.{nextVersion}", "player", "probe.actor")
        };
    }
}
