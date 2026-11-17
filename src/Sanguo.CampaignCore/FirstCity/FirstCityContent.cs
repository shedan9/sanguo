using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sanguo.CampaignCore.FirstCity;

public sealed record FirstCityEventData(int Stage, string OutlineBeatId, string Id, string Title,
    string Text, ImmutableArray<StoryChoice> Choices)
{
    public StoryEvent ToStoryEvent() => new(Id, Title, Text, Choices);
}
public sealed record FirstCityEpilogue(string OpeningChoiceId, string Text);
public sealed record FirstCityRouteData(string Origin, ImmutableArray<FirstCityEventData> Events,
    ImmutableArray<FirstCityEpilogue> Epilogues);
public sealed record FirstCityContentPackage(int FormatVersion, string PackageId, string Scope, string Status,
    string CityId, ImmutableArray<FirstCityRouteData> Routes)
{
    public FirstCityRouteData Route(Origin origin)
    {
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        return Routes.Single(r => r.Origin == origin.ToString().ToLowerInvariant());
    }
}
public sealed record FirstCityContentIssue(string Field, string Code, string Message);
public sealed record FirstCityContentResult(FirstCityContentPackage? Package, ImmutableArray<FirstCityContentIssue> Issues)
{
    public bool Success => Package is not null && Issues.IsEmpty;
}

/// <summary>首城开发事件的唯一解析入口；仅描述文本与既有选择，不执行脚本或修改战斗胜负。</summary>
public static class FirstCityContent
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static FirstCityContentPackage LoadEmbedded()
    {
        using var stream = typeof(FirstCityContent).Assembly.GetManifestResourceStream("Sanguo.FirstCity.json")
            ?? throw new InvalidDataException("程序中缺少首城事件资源。");
        using var reader = new StreamReader(stream);
        var result = Load(reader.ReadToEnd());
        return result.Package ?? throw new InvalidDataException("首城事件资源无效：" + string.Join("；",
            result.Issues.Select(i => $"{i.Field} [{i.Code}] {i.Message}")));
    }

    public static FirstCityContentResult Load(string json)
    {
        FirstCityContentPackage? data;
        try { data = JsonSerializer.Deserialize<FirstCityContentPackage>(json, Options); }
        catch (JsonException ex)
        { return new(null, [new(ex.Path ?? "$", "json.invalid", ex.Message)]); }
        if (data is null) return new(null, [new("$", "schema.invalid", "首城事件包不能为null。")]);

        var issues = ImmutableArray.CreateBuilder<FirstCityContentIssue>();
        Require(data.FormatVersion == 1, "$.formatVersion", "schema.invalid", "需要首城事件格式版本1。");
        Require(data.PackageId == FirstCityStory.Version && data.Scope == "first-city-development"
            && data.Status == "development" && data.CityId == FirstCityStory.CityId,
            "$", "campaign.scope", "仅接受当前首城开发包及柳平城，不代表正式MVP内容已验收。");
        if (data.Routes.IsDefaultOrEmpty)
        {
            Require(false, "$.routes", "campaign.routes", "需要三种出身的首城事件。");
            return new(null, issues.ToImmutable());
        }
        Require(data.Routes.Length == 3, "$.routes", "campaign.routes", "需要且仅需要三条路线。");
        var origins = new HashSet<string>(StringComparer.Ordinal);
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        for (var r = 0; r < data.Routes.Length; r++)
        {
            var route = data.Routes[r];
            var at = $"$.routes[{r}]";
            if (route is null) { Require(false, at, "schema.invalid", "路线不能为null。"); continue; }
            Require(route.Origin is "damage" or "control" or "tank" && origins.Add(route.Origin),
                at + ".origin", "campaign.origin", "出身必须为damage、control、tank且不能重复。");
            if (route.Events.IsDefaultOrEmpty)
                Require(false, at + ".events", "campaign.events", "每线需要三个事件。");
            else
            {
                Require(route.Events.Length == 3, at + ".events", "campaign.events", "每线需要且仅需要三个事件。");
                var stages = new HashSet<int>();
                for (var e = 0; e < route.Events.Length; e++)
                {
                    var entry = route.Events[e];
                    var field = $"{at}.events[{e}]";
                    if (entry is null) { Require(false, field, "schema.invalid", "事件不能为null。"); continue; }
                    Require(entry.Stage is >= 0 and <= 2 && stages.Add(entry.Stage), field + ".stage",
                        "campaign.stage", "事件阶段为0至2且不能重复；阶段3沿用攻城事件。");
                    var suffix = $"{route.Origin}.{entry.Stage + 1:D2}";
                    Require(entry.Id == "first-city." + suffix, field + ".id", "campaign.event_id", "事件ID须对应本路线和阶段。");
                    Require(eventIds.Add(entry.Id), field + ".id", "id.duplicate", "事件ID重复。");
                    Require(entry.OutlineBeatId == "story." + suffix, field + ".outlineBeatId",
                        "campaign.outline", "事件必须对应本路线前三个制作大纲单元。");
                    Text(entry.Title, field + ".title"); Text(entry.Text, field + ".text");
                    if (entry.Choices.IsDefaultOrEmpty)
                        Require(false, field + ".choices", "campaign.choices", "事件需要a、b两个选项。");
                    else
                    {
                        var choices = new HashSet<string>(StringComparer.Ordinal);
                        for (var c = 0; c < entry.Choices.Length; c++)
                        {
                            var choice = entry.Choices[c];
                            var cf = $"{field}.choices[{c}]";
                            if (choice is null) { Require(false, cf, "schema.invalid", "选项不能为null。"); continue; }
                            Require(choice.Id is "a" or "b" && choices.Add(choice.Id), cf + ".id", "campaign.choice_id",
                                "选项ID只能是a或b且不能重复。");
                            Text(choice.Text, cf + ".text"); Text(choice.Consequence, cf + ".consequence");
                        }
                        Require(entry.Choices.Length == 2 && choices.SetEquals(["a", "b"]), field + ".choices",
                            "campaign.choices", "事件需要且仅需要a、b两个选项。");
                    }
                }
            }
            if (route.Epilogues.IsDefaultOrEmpty)
                Require(false, at + ".epilogues", "campaign.epilogues", "需要两个序章选择对应的结语。");
            else
            {
                var openings = new HashSet<string>(StringComparer.Ordinal);
                for (var e = 0; e < route.Epilogues.Length; e++)
                {
                    var ending = route.Epilogues[e];
                    var field = $"{at}.epilogues[{e}]";
                    if (ending is null) { Require(false, field, "schema.invalid", "结语不能为null。"); continue; }
                    Require(ending.OpeningChoiceId is "a" or "b" && openings.Add(ending.OpeningChoiceId),
                        field + ".openingChoiceId", "campaign.epilogue_choice", "结语必须唯一对应序章a或b选项。");
                    Text(ending.Text, field + ".text");
                }
                Require(route.Epilogues.Length == 2 && openings.SetEquals(["a", "b"]), at + ".epilogues",
                    "campaign.epilogues", "每条路线需要且仅需要a、b各一个结语。");
            }
        }
        return new(issues.Count == 0 ? data : null, issues.ToImmutable());

        void Require(bool condition, string field, string code, string message)
        { if (!condition) issues.Add(new(field, code, message)); }
        void Text(string? value, string field)
            => Require(!string.IsNullOrWhiteSpace(value), field, "campaign.text", "文本不能为空。");
    }
}
