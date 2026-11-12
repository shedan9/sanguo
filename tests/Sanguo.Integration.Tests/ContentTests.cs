using Sanguo.Content;
using Xunit;

namespace Sanguo.Integration.Tests;

public class ContentTests
{
    private const string ValidJson = """
        {"formatVersion":1,"packageId":"probe.test","scope":"g0-probe",
         "cards":[{"id":"probe.slash","name":"杀","effect":"slash","implementation":"declared-only"}],
         "deck":[{"instanceId":"probe.001","definitionId":"probe.slash","suit":"spade","rank":7}]}
        """;

    [Fact]
    public void DuplicateIdsAndMissingReferencesIncludeFileAndField()
    {
        var invalid = ValidJson.Replace("probe.001", "probe.slash", StringComparison.Ordinal)
            .Replace("\"definitionId\":\"probe.slash\"", "\"definitionId\":\"probe.missing\"", StringComparison.Ordinal);
        var result = ContentLoader.Load([new("cards/invalid.json", invalid)]);
        Assert.False(result.Success);
        Assert.Null(result.Catalog);
        Assert.Contains(result.Issues, e => e.Code == "id.duplicate" && e.File == "cards/invalid.json" && e.Field == "$.deck[0].instanceId");
        Assert.Contains(result.Issues, e => e.Code == "reference.missing" && e.Field == "$.deck[0].definitionId");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"formatVersion\":1}")]
    public void MalformedOrIncompleteJsonFailsWithoutCatalog(string json)
        => Assert.False(ContentLoader.Load([new("bad.json", json)]).Success);

    [Fact]
    public void InvalidSuitRankUnknownFieldsAndNullCardsAreRejected()
    {
        Assert.False(Load(ValidJson.Replace("spade", "unknown", StringComparison.Ordinal)).Success);
        Assert.False(Load(ValidJson.Replace("\"rank\":7", "\"rank\":14", StringComparison.Ordinal)).Success);
        Assert.False(Load(ValidJson.Replace("\"name\"", "\"typoName\"", StringComparison.Ordinal)).Success);
        Assert.False(Load(ValidJson.Replace("\"cards\":[{\"id\":\"probe.slash\",\"name\":\"杀\",\"effect\":\"slash\",\"implementation\":\"declared-only\"}]", "\"cards\":[null]", StringComparison.Ordinal)).Success);
    }

    [Fact]
    public void DuplicateIdsAcrossFilesAreRejected()
    {
        var result = ContentLoader.Load([new("a.json", ValidJson), new("b.json", ValidJson)]);
        Assert.Contains(result.Issues, i => i.File == "b.json" && i.Code == "id.duplicate");
    }

    [Fact]
    public void ContentHashIsStableAcrossFileEnumerationOrderAndSensitiveToContent()
    {
        var first = new ContentSource("a.json", ValidJson);
        var second = new ContentSource("b.json", ValidJson.Replace("probe.", "other.", StringComparison.Ordinal));
        var left = ContentLoader.Load([first, second]);
        var right = ContentLoader.Load([second, first]);
        Assert.True(left.Success && right.Success);
        Assert.Equal(left.Catalog!.ContentHash, right.Catalog!.ContentHash);
        Assert.NotEqual(left.Catalog.ContentHash, ContentLoader.Load([first, second with { Json = second.Json + "\n" }]).Catalog!.ContentHash);
    }

    private static ContentLoadResult Load(string json) => ContentLoader.Load([new("cards/test.json", json)]);
}
