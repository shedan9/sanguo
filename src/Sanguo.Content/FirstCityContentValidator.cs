using System.Collections.Immutable;
using System.Text.Json;
using Sanguo.CampaignCore.FirstCity;

namespace Sanguo.Content;

public sealed record FirstCityValidationResult(int RouteCount, int EventCount, ImmutableArray<ContentIssue> Issues)
{
    public bool Success => Issues.IsEmpty;
}

/// <summary>运行事件结构与制作大纲引用检查；不授予事件占城、招募或胜负权限。</summary>
public static class FirstCityContentValidator
{
    public const string RelativePath = "campaigns/first-city.json";

    public static FirstCityValidationResult ValidateDirectory(string contentRoot)
    {
        var file = RelativePath;
        try
        {
            var source = new ContentSource(file, File.ReadAllText(Path.Combine(contentRoot, file)));
            file = "planning/story-outline.json";
            // 大纲自身的字段、重复ID及路线合法性由StoryOutlineValidator检查。
            using var outline = JsonDocument.Parse(File.ReadAllText(Path.Combine(contentRoot, file)));
            var ids = outline.RootElement.GetProperty("routes").EnumerateArray()
                .SelectMany(r => r.GetProperty("beats").EnumerateArray())
                .Select(b => b.GetProperty("id").GetString()!).ToArray();
            return Validate(source, ids);
        }
        catch (JsonException ex)
        { return new(0, 0, [new(file, ex.Path ?? "$", "json.invalid", ex.Message)]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(0, 0, [new(file, "$", "campaign.io", ex.Message)]); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        { return new(0, 0, [new(file, "$", "campaign.outline", "无法读取制作大纲单元：" + ex.Message)]); }
    }

    public static FirstCityValidationResult Validate(ContentSource source, IEnumerable<string> outlineBeatIds)
    {
        var loaded = FirstCityContent.Load(source.Json);
        if (!loaded.Success) return new(0, 0, loaded.Issues
            .Select(i => new ContentIssue(source.Path, i.Field, i.Code, i.Message)).ToImmutableArray());
        var data = loaded.Package!;
        var known = outlineBeatIds.ToHashSet(StringComparer.Ordinal);
        var issues = ImmutableArray.CreateBuilder<ContentIssue>();
        for (var r = 0; r < data.Routes.Length; r++)
            for (var e = 0; e < data.Routes[r].Events.Length; e++)
                if (!known.Contains(data.Routes[r].Events[e].OutlineBeatId))
                    issues.Add(new(source.Path, $"$.routes[{r}].events[{e}].outlineBeatId", "reference.missing", "制作大纲中不存在此单元。"));
        return new(data.Routes.Length, data.Routes.Sum(r => r.Events.Length), issues.ToImmutable());
    }
}
