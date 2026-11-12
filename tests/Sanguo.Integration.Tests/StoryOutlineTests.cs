using System.Text.Json.Nodes;
using Sanguo.Content;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class StoryOutlineTests
{
    private static readonly string[] Generals = ["guanyu", "zhaoyun", "zhangliao", "huatuo", "caocao", "lvbu"];
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StoryFixtures/story-outline.json"));
    private static JsonNode Document() => JsonNode.Parse(Fixture)!;
    private static StoryOutlineResult Check(JsonNode node) => StoryOutlineValidator.Validate(new("planning/story-outline.json", node.ToJsonString()), Generals);
    private static JsonNode Route(JsonNode node, int index = 0) => node["routes"]![index]!;
    private static JsonNode Beat(JsonNode node, int index = 0) => Route(node)["beats"]![index]!;

    [Fact]
    public void RepositoryDraftContainsThreeCompleteOutlinesWithoutRuntimeContent()
    {
        var result = StoryOutlineValidator.Validate(new("planning/story-outline.json", Fixture), Generals);
        Assert.True(result.Success, string.Join("\n", result.Issues));
        Assert.Equal(3, result.RouteCount);
        Assert.Equal(45, result.BeatCount);
        Assert.False(Document()["includedInRuntime"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{}")]
    public void InvalidJsonCannotPass(string json)
        => Assert.False(StoryOutlineValidator.Validate(new("broken.json", json), Generals).Success);

    [Theory]
    [InlineData("status", "frozen")]
    [InlineData("scope", "runtime")]
    public void DraftCannotBePromotedByChangingMetadata(string field, string value)
    {
        var doc = Document();
        doc[field] = value;
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.scope");
        doc = Document();
        doc["includedInRuntime"] = true;
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.scope");
    }

    [Fact]
    public void DuplicatePropertiesUnknownFieldsAndMissingFieldsFailClosed()
    {
        var duplicate = Fixture.Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1", StringComparison.Ordinal);
        Assert.Contains(StoryOutlineValidator.Validate(new("duplicate.json", duplicate), Generals).Issues, i => i.Code == "json.invalid");
        var doc = Document();
        Beat(doc)["optionalScript"] = "arbitrary";
        Assert.Contains(Check(doc).Issues, i => i.Code == "json.invalid");
        doc = Document();
        Beat(doc).AsObject().Remove("nextId");
        Assert.Contains(Check(doc).Issues, i => i.Code == "json.invalid");
    }

    [Theory]
    [InlineData("route")]
    [InlineData("beat")]
    [InlineData("participant")]
    [InlineData("ending")]
    public void NullArrayItemsReturnIssuesWithoutCrashing(string kind)
    {
        var doc = Document();
        switch (kind)
        {
            case "route": doc["routes"]![0] = null; break;
            case "beat": Route(doc)["beats"]![0] = null; break;
            case "participant": Beat(doc, 1)["participants"]![0] = null; break;
            case "ending": Route(doc)["endings"]![0] = null; break;
        }
        Assert.False(Check(doc).Success);
    }

    [Fact]
    public void AllOriginsAreRequiredAndCannotBeRemapped()
    {
        var doc = Document();
        Route(doc, 1)["origin"] = "damage";
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.origin");
        doc = Document();
        doc["routes"]!.AsArray().RemoveAt(2);
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.routes");
    }

    [Fact]
    public void DuplicateIdsIdentifyTheFileAndField()
    {
        var doc = Document();
        Beat(doc, 1)["id"] = Beat(doc)["id"]!.GetValue<string>();
        var result = Check(doc);
        Assert.Contains(result.Issues, i => i.Code == "id.duplicate" && i.File == "planning/story-outline.json"
            && i.Field == "$.routes[0].beats[1].id");
    }

    [Theory]
    [InlineData("story.damage.missing", "reference.missing")]
    [InlineData("story.control.02", "story.cross_route")]
    [InlineData("story.damage.01", "story.sequence")]
    [InlineData("story.damage.03", "story.sequence")]
    [InlineData(null, "story.sequence")]
    public void MissingCrossRouteCyclicSkippedAndEarlyTerminalLinksFail(string? next, string code)
    {
        var doc = Document();
        Beat(doc)["nextId"] = next;
        Assert.Contains(Check(doc).Issues, i => i.Code == code && i.Field == "$.routes[0].beats[0].nextId");
    }

    [Theory]
    [InlineData(2, "first_city", 4)]
    [InlineData(7, "dual_general", 2)]
    [InlineData(10, "relay_battle", 5)]
    [InlineData(13, "unification", 12)]
    [InlineData(14, "epilogue", 13)]
    public void MilestonesCannotMoveOutsideTheirStoryStage(int original, string milestone, int target)
    {
        var doc = Document();
        Beat(doc, original)["milestone"] = null;
        Beat(doc, target)["milestone"] = milestone;
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.milestone");
    }

    [Fact]
    public void DuplicateMilestonesAndUnknownMilestonesFail()
    {
        var doc = Document();
        Beat(doc, 6)["milestone"] = "dual_general";
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.milestone");
        Beat(doc, 6)["milestone"] = "recruit_count_10";
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.milestone");
    }

    [Fact]
    public void ChapterCoverageOrderAndNarrativeTextAreRequired()
    {
        var doc = Document();
        Beat(doc, 2)["chapter"] = 1;
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.chapter");
        doc = Document();
        Beat(doc, 6)["chapter"] = 1;
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.chapter");
        doc = Document();
        Beat(doc)["consequence"] = " ";
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.text");
    }

    [Theory]
    [InlineData("zhaoyun")]
    [InlineData("lvbu")]
    public void LaterCharactersRequireAnExplicitTimeSkip(string general)
    {
        var doc = Document();
        Beat(doc)["participants"]!.AsArray().Add(new JsonObject { ["generalId"] = general, ["role"] = "不可提前进入序章" });
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.era");
        doc = Document();
        Beat(doc, 3)["era"] = "opening_184";
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.era");
        doc = Document();
        Beat(doc, 1)["era"] = "later_campaign";
        Beat(doc, 2)["era"] = "later_campaign";
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.era");
    }

    [Fact]
    public void ParticipantsNeedARegisteredIdentityAndAnExplicitRole()
    {
        var doc = Document();
        var participant = Beat(doc, 1)["participants"]![0]!;
        participant["generalId"] = "unregistered.general";
        participant["role"] = "";
        Assert.Contains(Check(doc).Issues, i => i.Code == "reference.missing");
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.text");
    }

    [Fact]
    public void EndingTonesAreAvailableToEveryOrigin()
    {
        var doc = Document();
        Route(doc)["endings"]!.AsArray().RemoveAt(2);
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.ending");
        doc = Document();
        Route(doc)["endings"]![2]!["tone"] = "benevolence";
        Assert.Contains(Check(doc).Issues, i => i.Code == "story.ending");
    }

    [Fact]
    public void DirectoryValidationUsesTheActualRegistryAndRequiresTheOutline()
    {
        var root = Path.Combine(Path.GetTempPath(), "sanguo-story-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "manifests"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "SourceFixtures/sgsol-generals.json"), Path.Combine(root, "manifests/sgsol-generals.json"));
            Assert.Contains(StoryOutlineValidator.ValidateDirectory(root).Issues, i => i.Code == "story.io" && i.File == "planning/story-outline.json");
            Directory.CreateDirectory(Path.Combine(root, "planning"));
            File.WriteAllText(Path.Combine(root, "planning/story-outline.json"), Fixture);
            Assert.True(StoryOutlineValidator.ValidateDirectory(root).Success);
            var registryPath = Path.Combine(root, "manifests/sgsol-generals.json");
            File.WriteAllText(registryPath, File.ReadAllText(registryPath).Replace("guanyu", "another.general", StringComparison.Ordinal));
            Assert.Contains(StoryOutlineValidator.ValidateDirectory(root).Issues, i => i.Code == "reference.missing");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
