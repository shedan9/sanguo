using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sanguo.Content;

public sealed record StoryOutlineResult(int RouteCount, int BeatCount, ImmutableArray<ContentIssue> Issues)
{
    public bool Success => Issues.IsEmpty;
}

/// <summary>制作大纲结构检查；不加载运行内容，不证明剧情可玩或规则已实现。</summary>
public static class StoryOutlineValidator
{
    private const string OutlineFile = "planning/story-outline.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static StoryOutlineResult ValidateDirectory(string contentRoot)
    {
        var file = "manifests/sgsol-generals.json";
        try
        {
            var generals = JsonSerializer.Deserialize<GeneralsManifest>(
                File.ReadAllText(Path.Combine(contentRoot, file)), Options);
            if (generals is null || generals.Generals.IsDefault || generals.Generals.Any(g => g is null))
                return new(0, 0, [new("manifests/sgsol-generals.json", "$.generals", "story.registry", "缺少有效人物登记数组。")]);
            file = OutlineFile;
            return Validate(new(file, File.ReadAllText(Path.Combine(contentRoot, file))),
                generals.Generals.Select(g => g.GeneralId));
        }
        catch (JsonException ex)
        { return new(0, 0, [new(file, ex.Path ?? "$", "json.invalid", ex.Message)]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(0, 0, [new(file, "$", "story.io", ex.Message)]); }
    }

    public static StoryOutlineResult Validate(ContentSource source, IEnumerable<string> generalIds)
    {
        var issues = ImmutableArray.CreateBuilder<ContentIssue>();
        StoryOutline? data;
        try { data = JsonSerializer.Deserialize<StoryOutline>(source.Json, Options); }
        catch (JsonException ex)
        { return new(0, 0, [new(source.Path, ex.Path ?? "$", "json.invalid", ex.Message)]); }
        if (data is null)
            return new(0, 0, [new(source.Path, "$", "schema.invalid", "大纲不能为null。")]);
        Require(data.FormatVersion == 1, "$.formatVersion", "schema.invalid", "需要大纲格式版本1。");
        Require(data.Scope == "story-outline" && data.Status == "draft" && !data.IncludedInRuntime,
            "$", "story.scope", "本阶段仅接受不进入运行包的story-outline草稿。");
        if (data.Routes.IsDefaultOrEmpty)
            return new(0, 0, [new(source.Path, "$.routes", "story.routes", "需要三种出身的完整大纲。")]);

        var knownGenerals = generalIds.ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var origins = new HashSet<string>(StringComparer.Ordinal);
        var beatCount = 0;
        Require(data.Routes.Length == 3, "$.routes", "story.routes", "需要且仅需要三条故事路线。");
        for (var r = 0; r < data.Routes.Length; r++)
        {
            var route = data.Routes[r];
            var field = $"$.routes[{r}]";
            if (route is null) { Require(false, field, "schema.invalid", "路线不能为null。"); continue; }
            Id(route.Id, field + ".id");
            Require(route.Origin is "damage" or "control" or "tank" && origins.Add(route.Origin),
                field + ".origin", "story.origin", "出身必须为damage、control、tank且不能重复。");
            Require(route.Id == "story." + route.Origin, field + ".id", "story.origin", "路线ID必须对应固定出身。");
            Text(route.Title, field + ".title");
            Text(route.Premise, field + ".premise");
            Text(route.Resolution, field + ".resolution");
            if (route.Beats.IsDefaultOrEmpty)
            { Require(false, field + ".beats", "story.beats", "需要15个主线单元。"); continue; }
            beatCount += route.Beats.Length;
            Require(route.Beats.Length == 15, field + ".beats", "story.beats", "当前制作基线为每线15个主线单元。");
            var localIds = route.Beats.Where(b => b is not null).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            var milestones = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var chapters = new HashSet<int>();
            var previousChapter = -1;
            var previousEra = -1;
            for (var b = 0; b < route.Beats.Length; b++)
            {
                var beat = route.Beats[b];
                var at = $"{field}.beats[{b}]";
                if (beat is null) { Require(false, at, "schema.invalid", "主线单元不能为null。"); continue; }
                Id(beat.Id, at + ".id");
                Require(beat.Id.StartsWith(route.Id + ".", StringComparison.Ordinal), at + ".id", "story.cross_route", "单元ID必须属于本路线。");
                foreach (var (name, value) in new[] { ("title", beat.Title), ("goal", beat.Goal), ("obstacle", beat.Obstacle),
                    ("choice", beat.Choice), ("consequence", beat.Consequence) }) Text(value, at + "." + name);
                Require(beat.Chapter is >= 0 and <= 5 && beat.Chapter >= previousChapter, at + ".chapter", "story.chapter", "章节0至5必须按叙事顺序排列。");
                previousChapter = beat.Chapter;
                chapters.Add(beat.Chapter);
                var era = beat.Era switch { "opening_184" => 0, "after_time_skip" => 1, "later_campaign" => 2, _ => -1 };
                Require(era >= 0 && era >= previousEra, at + ".era", "story.era", "时间阶段必须合法且不可倒退。");
                Require(b != 0 || era == 0, at + ".era", "story.era", "序章从184年开场。");
                Require(b != 1 || era == 1, at + ".era", "story.era", "第二单元须明确章间跳时，不能省略过渡。");
                previousEra = era;
                if (beat.NextId is not null)
                {
                    Require(localIds.Contains(beat.NextId), at + ".nextId", "reference.missing", "后继单元必须存在于本路线。");
                    Require(beat.NextId.StartsWith(route.Id + ".", StringComparison.Ordinal), at + ".nextId", "story.cross_route", "不能跳到另一出身故事。");
                }
                var expectedNext = b + 1 < route.Beats.Length ? route.Beats[b + 1]?.Id : null;
                Require(beat.NextId == expectedNext, at + ".nextId", "story.sequence", "大纲主干须逐单元连通，只有末单元可结束；局部选择在单元内展开。");
                if (beat.Milestone is not null)
                {
                    if (!milestones.TryGetValue(beat.Milestone, out var positions)) milestones[beat.Milestone] = positions = [];
                    positions.Add(b + 1);
                    Require(beat.Milestone is "first_city" or "dual_general" or "relay_battle" or "unification" or "epilogue",
                        at + ".milestone", "story.milestone", "未知主线里程碑。");
                }
                if (beat.Participants.IsDefault)
                { Require(false, at + ".participants", "schema.invalid", "需要人物交集数组，可为空。"); continue; }
                var participants = new HashSet<string>(StringComparer.Ordinal);
                for (var p = 0; p < beat.Participants.Length; p++)
                {
                    var participant = beat.Participants[p];
                    var pf = $"{at}.participants[{p}]";
                    if (participant is null) { Require(false, pf, "schema.invalid", "人物交集不能为null。"); continue; }
                    Require(knownGenerals.Contains(participant.GeneralId), pf + ".generalId", "reference.missing", "人物必须先进入来源登记，登记不代表已冻结。");
                    Require(participants.Add(participant.GeneralId), pf + ".generalId", "id.duplicate", "同单元人物交集不能重复。");
                    Text(participant.Role, pf + ".role");
                    Require(era != 0 || participant.GeneralId is not ("zhaoyun" or "lvbu"), pf + ".generalId", "story.era", "赵云和吕布不进入184年开场，需明确章间跳时。");
                }
            }
            Require(chapters.SetEquals([0, 1, 2, 3, 4, 5]), field + ".beats", "story.chapter", "需覆盖序章至第五章。");
            CheckMilestone("first_city", 3, 3);
            CheckMilestone("dual_general", 7, 9);
            CheckMilestone("relay_battle", 10, 12);
            CheckMilestone("unification", 14, 14);
            CheckMilestone("epilogue", 15, 15);
            var tones = new HashSet<string>(StringComparer.Ordinal);
            if (!route.Endings.IsDefault)
                for (var e = 0; e < route.Endings.Length; e++)
                {
                    var ending = route.Endings[e];
                    var at = $"{field}.endings[{e}]";
                    if (ending is null) { Require(false, at, "schema.invalid", "尾声不能为null。"); continue; }
                    Require(ending.Tone is "benevolence" or "hegemony" or "compact" && tones.Add(ending.Tone),
                        at + ".tone", "story.ending", "结局取向必须合法且不重复。");
                    Text(ending.Text, at + ".text");
                }
            Require(tones.SetEquals(["benevolence", "hegemony", "compact"]), field + ".endings", "story.ending", "每条出身故事均须提供仁政、霸业、盟约三类尾声。");

            void CheckMilestone(string name, int first, int last)
                => Require(milestones.TryGetValue(name, out var positions) && positions.Count == 1
                    && positions[0] >= first && positions[0] <= last, field + ".beats", "story.milestone", $"{name}须唯一并位于第{first}至{last}单元。");
        }
        return new(data.Routes.Length, beatCount, issues.ToImmutable());

        void Require(bool condition, string field, string code, string message)
        { if (!condition) issues.Add(new(source.Path, field, code, message)); }
        void Text(string value, string field) => Require(!string.IsNullOrWhiteSpace(value), field, "story.text", "需要非空故事文本。");
        void Id(string value, string field)
        {
            Text(value, field);
            Require(ids.Add(value), field, "id.duplicate", "路线与单元ID不能重复。");
        }
    }
}

internal sealed record StoryOutline(int FormatVersion, string Scope, string Status, bool IncludedInRuntime, ImmutableArray<StoryRoute> Routes);
internal sealed record StoryRoute(string Id, string Origin, string Title, string Premise, string Resolution,
    ImmutableArray<StoryBeat> Beats, ImmutableArray<StoryEnding> Endings);
internal sealed record StoryBeat(string Id, string Title, int Chapter, string Era, string Goal, string Obstacle,
    string Choice, string Consequence, string? NextId, string? Milestone, ImmutableArray<StoryParticipant> Participants);
internal sealed record StoryParticipant(string GeneralId, string Role);
internal sealed record StoryEnding(string Tone, string Text);
