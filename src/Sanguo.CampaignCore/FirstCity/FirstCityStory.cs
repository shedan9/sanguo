using System.Collections.Immutable;

namespace Sanguo.CampaignCore.FirstCity;

public enum CampaignPhase { Choice, Deployment, Battle, Defeat, Complete }
public sealed record StoryChoice(string Id, string Text, string Consequence);
public sealed record StoryEvent(string Id, string Title, string Text, ImmutableArray<StoryChoice> Choices);
public sealed record CampaignChoice(int Stage, string ChoiceId);
public sealed record CampaignProgress(string CampaignId, Origin Origin, ulong Seed, int Stage, int Attempt,
    CampaignPhase Phase, ImmutableArray<string> Roster, ImmutableArray<CampaignChoice> Choices);

/// <summary>首城开发切片；独立于制作大纲及G0试验，不声明正式人物或完整技能已接入。</summary>
public static class FirstCityStory
{
    public const string Version = "campaign.first-city-development.v1";
    public const string Protagonist = "protagonist";
    public const string Volunteer = "companion.volunteer";
    public const string CityId = "city.liuping";
    private static readonly FirstCityContentPackage Content = FirstCityContent.LoadEmbedded();

    public static string OriginName(Origin origin) => origin switch
    {
        Origin.Damage => "乡勇", Origin.Control => "寒门学子", Origin.Tank => "山野农户",
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };
    public static string EncounterName(int stage) => stage switch
    {
        0 => "故乡撤离", 1 => "驿路通行", 2 => "柳平外城", 3 => "柳平内城",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    public static StoryEvent Event(Origin origin, int stage)
    {
        if (stage is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(stage));
        return Content.Route(origin).Events.Single(e => e.Stage == stage).ToStoryEvent();
    }
    public static string Epilogue(CampaignProgress progress)
    {
        var opening = progress.Choices.Single(c => c.Stage == 0).ChoiceId;
        return Content.Route(progress.Origin).Epilogues.Single(e => e.OpeningChoiceId == opening).Text;
    }
}
