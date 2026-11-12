using Sanguo.BattleCore.Probe;
using Sanguo.BattleCore.Randomness;

namespace Sanguo.Application;

public static class SessionValidation
{
    public static void Validate(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.EngineVersion != ProbeSession.EngineVersion || state.RulesetId != DrawProbe.RulesetId)
            throw new ArgumentException("会话引擎或规则版本不兼容。");
        if (state.ContentHash is null || state.ContentHash.Length != 64 || !state.ContentHash.All(Uri.IsHexDigit))
            throw new ArgumentException("内容摘要无效。");
        if (state.Campaign is null || string.IsNullOrWhiteSpace(state.Campaign.CampaignId)
            || !Enum.IsDefined(state.Campaign.Origin) || state.Campaign.Roster.IsDefaultOrEmpty
            || state.Campaign.Roster.Any(string.IsNullOrWhiteSpace)
            || state.Campaign.Roster.Distinct(StringComparer.Ordinal).Count() != state.Campaign.Roster.Length)
            throw new ArgumentException("战役出身或永久名册无效。");
        var probe = state.Probe;
        if (probe is null || probe.DrawPile.IsDefault || probe.Hand.IsDefault || probe.Random is null
            || state.Receipts.IsDefault || state.StateVersion < 0)
            throw new ArgumentException("试验状态不完整。");
        var allCards = probe.DrawPile.Concat(probe.Hand).ToArray();
        if (allCards.Length == 0 || allCards.Any(c => c is null || string.IsNullOrWhiteSpace(c.InstanceId)
            || string.IsNullOrWhiteSpace(c.DefinitionId) || c.Suit is not ("spade" or "heart" or "club" or "diamond")
            || c.Rank is < 1 or > 13)
            || allCards.Select(c => c.InstanceId).Distinct(StringComparer.Ordinal).Count() != allCards.Length)
            throw new ArgumentException("试验牌实例无效或重复。");
        _ = new Pcg32(probe.Random);
        var expectedDecision = probe.DrawPile.IsEmpty ? null
            : new ProbeDecision($"probe.draw.{state.StateVersion}", "player", "probe.actor");
        if (probe.PendingDecision != expectedDecision || state.StateVersion != probe.Hand.Length
            || state.Receipts.Length != state.StateVersion)
            throw new ArgumentException("决策请求、手牌或命令游标不一致。");
        var commandIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < state.Receipts.Length; i++)
        {
            var receipt = state.Receipts[i];
            if (receipt is null || receipt.Command is null || string.IsNullOrWhiteSpace(receipt.Command.CommandId)
                || !commandIds.Add(receipt.Command.CommandId) || receipt.CommittedVersion != i + 1
                || receipt.Command.ExpectedStateVersion != i || receipt.Command.RequestId != $"probe.draw.{i}"
                || receipt.Command.ControllerId != "player")
                throw new ArgumentException("命令去重记录不一致。");
        }
    }
}
