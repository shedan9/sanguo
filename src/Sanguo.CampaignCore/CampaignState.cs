using System.Collections.Immutable;

namespace Sanguo.CampaignCore;

public enum Origin { Damage, Control, Tank }

public sealed record CampaignState(string CampaignId, Origin Origin, ImmutableArray<string> Roster)
{
    public static CampaignState Create(string campaignId, Origin origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        return new(campaignId, origin, ["protagonist"]);
    }
}
