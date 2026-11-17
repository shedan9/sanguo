using System.Text.Json.Nodes;
using Sanguo.Application;
using Sanguo.CampaignCore;
using Sanguo.CampaignCore.FirstCity;
using Sanguo.Content;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class FirstCityContentTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CampaignFixtures/first-city.json"));
    private static readonly string[] OutlineIds = (from origin in new[] { "damage", "control", "tank" }
        from stage in Enumerable.Range(1, 3) select $"story.{origin}.{stage:D2}").ToArray();
    private static JsonNode Document() => JsonNode.Parse(Fixture)!;
    private static JsonNode Route(JsonNode doc) => doc["routes"]![0]!;
    private static JsonNode Event(JsonNode doc) => Route(doc)["events"]![0]!;
    private static FirstCityValidationResult Check(JsonNode doc) => FirstCityContentValidator.Validate(
        new(FirstCityContentValidator.RelativePath, doc.ToJsonString()), OutlineIds);

    [Fact]
    public void RuntimeUsesValidatedRepositoryDataForAllEventsAndOpeningEpilogues()
    {
        var result = FirstCityContentValidator.Validate(new(FirstCityContentValidator.RelativePath, Fixture), OutlineIds);
        Assert.True(result.Success, string.Join("\n", result.Issues));
        Assert.Equal(3, result.RouteCount);
        Assert.Equal(9, result.EventCount);
        var package = FirstCityContent.Load(Fixture).Package!;
        foreach (var origin in Enum.GetValues<Origin>())
        {
            var route = package.Route(origin);
            foreach (var entry in route.Events)
            {
                var actual = FirstCityStory.Event(origin, entry.Stage);
                Assert.Equal(entry.Id, actual.Id);
                Assert.Equal(entry.Title, actual.Title);
                Assert.Equal(entry.Text, actual.Text);
                Assert.Equal(entry.Choices.ToArray(), actual.Choices.ToArray());
            }
            foreach (var ending in route.Epilogues)
                Assert.Equal(ending.Text, FirstCityStory.Epilogue(new("test", origin, 42, 4, 1,
                    CampaignPhase.Complete, [], [new(0, ending.OpeningChoiceId)])));
        }
    }

    [Fact]
    public void MigrationPreservesExistingCampaignContentHash()
        => Assert.Equal("fb530fbecc2c6e6aa65d62e12251b764084dd6da0eafb9e92e638afd12accbe4", FirstCityScenario.ContentHash);

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{broken")]
    [InlineData("[]")]
    public void InvalidDocumentsAreRejected(string json)
    {
        var result = FirstCityContent.Load(json);
        Assert.False(result.Success);
        Assert.Null(result.Package);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void DuplicateUnknownAndMissingPropertiesAreRejectedByRuntimeReader()
    {
        var duplicate = Fixture.Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1", StringComparison.Ordinal);
        Assert.Contains(FirstCityContent.Load(duplicate).Issues, i => i.Code == "json.invalid");
        var doc = Document();
        Event(doc)["script"] = "award_city";
        Assert.Contains(Check(doc).Issues, i => i.Code == "json.invalid");
        doc = Document();
        Event(doc)["choices"]![0]!.AsObject().Remove("consequence");
        Assert.Contains(Check(doc).Issues, i => i.Code == "json.invalid");
    }

    [Theory]
    [InlineData("routes")]
    [InlineData("route")]
    [InlineData("events")]
    [InlineData("event")]
    [InlineData("choices")]
    [InlineData("choice")]
    [InlineData("epilogues")]
    [InlineData("epilogue")]
    public void NullCollectionsAndEntriesCannotCrashOrPassValidation(string kind)
    {
        var doc = Document();
        switch (kind)
        {
            case "routes": doc["routes"] = null; break;
            case "route": doc["routes"]![0] = null; break;
            case "events": Route(doc)["events"] = null; break;
            case "event": Route(doc)["events"]![0] = null; break;
            case "choices": Event(doc)["choices"] = null; break;
            case "choice": Event(doc)["choices"]![0] = null; break;
            case "epilogues": Route(doc)["epilogues"] = null; break;
            case "epilogue": Route(doc)["epilogues"]![0] = null; break;
        }
        Assert.False(Check(doc).Success);
    }

    [Theory]
    [InlineData("formatVersion", "2")]
    [InlineData("packageId", "\"campaign.other\"")]
    [InlineData("scope", "\"release\"")]
    [InlineData("status", "\"approved\"")]
    [InlineData("cityId", "\"city.other\"")]
    public void UnsupportedPackageMetadataCannotEnterRuntime(string field, string value)
    {
        var doc = Document(); doc[field] = JsonNode.Parse(value);
        Assert.False(Check(doc).Success);
    }

    [Theory]
    [InlineData("missing-route", "campaign.routes")]
    [InlineData("duplicate-origin", "campaign.origin")]
    [InlineData("unknown-origin", "campaign.origin")]
    [InlineData("missing-event", "campaign.events")]
    [InlineData("duplicate-stage", "campaign.stage")]
    [InlineData("invalid-stage", "campaign.stage")]
    [InlineData("cross-route", "campaign.event_id")]
    [InlineData("duplicate-event", "id.duplicate")]
    [InlineData("cross-outline", "campaign.outline")]
    [InlineData("duplicate-choice", "campaign.choice_id")]
    [InlineData("missing-choice", "campaign.choices")]
    [InlineData("missing-epilogue", "campaign.epilogues")]
    [InlineData("duplicate-epilogue", "campaign.epilogue_choice")]
    [InlineData("title", "campaign.text")]
    [InlineData("text", "campaign.text")]
    [InlineData("consequence", "campaign.text")]
    [InlineData("epilogue-text", "campaign.text")]
    public void BrokenRouteAndChoiceContractsReportLocatedIssues(string mutation, string code)
    {
        var doc = Document();
        switch (mutation)
        {
            case "missing-route": doc["routes"]!.AsArray().RemoveAt(2); break;
            case "duplicate-origin": doc["routes"]![1]!["origin"] = "damage"; break;
            case "unknown-origin": Route(doc)["origin"] = "unknown"; break;
            case "missing-event": Route(doc)["events"]!.AsArray().RemoveAt(2); break;
            case "duplicate-stage": Route(doc)["events"]![1]!["stage"] = 0; break;
            case "invalid-stage": Event(doc)["stage"] = 3; break;
            case "cross-route": Event(doc)["id"] = "first-city.control.01"; break;
            case "duplicate-event": Route(doc)["events"]![1]!["id"] = "first-city.damage.01"; break;
            case "cross-outline": Event(doc)["outlineBeatId"] = "story.control.01"; break;
            case "duplicate-choice": Event(doc)["choices"]![1]!["id"] = "a"; break;
            case "missing-choice": Event(doc)["choices"]!.AsArray().RemoveAt(1); break;
            case "missing-epilogue": Route(doc)["epilogues"]!.AsArray().RemoveAt(1); break;
            case "duplicate-epilogue": Route(doc)["epilogues"]![1]!["openingChoiceId"] = "a"; break;
            case "title": Event(doc)["title"] = " "; break;
            case "text": Event(doc)["text"] = ""; break;
            case "consequence": Event(doc)["choices"]![0]!["consequence"] = " "; break;
            case "epilogue-text": Route(doc)["epilogues"]![0]!["text"] = ""; break;
        }
        Assert.Contains(Check(doc).Issues, i => i.Code == code && i.File == FirstCityContentValidator.RelativePath && i.Field.StartsWith("$.routes", StringComparison.Ordinal));
    }

    [Fact]
    public void DanglingOutlineReferencesAreLocated()
    {
        var result = FirstCityContentValidator.Validate(new(FirstCityContentValidator.RelativePath, Fixture), OutlineIds.Skip(1));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("reference.missing", issue.Code);
        Assert.Equal("$.routes[0].events[0].outlineBeatId", issue.Field);
    }

    [Fact]
    public void FileValidationRejectsMissingDataAndMalformedOutlines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sanguo-first-city-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var issue = Assert.Single(FirstCityContentValidator.ValidateDirectory(directory).Issues);
            Assert.Equal(FirstCityContentValidator.RelativePath, issue.File);
            Assert.Equal("campaign.io", issue.Code);
            Directory.CreateDirectory(Path.Combine(directory, "campaigns"));
            Directory.CreateDirectory(Path.Combine(directory, "planning"));
            File.WriteAllText(Path.Combine(directory, FirstCityContentValidator.RelativePath), Fixture);
            foreach (var outline in new[] { "null", "{}", "{broken", "{\"routes\":[null]}" })
            {
                File.WriteAllText(Path.Combine(directory, "planning/story-outline.json"), outline);
                Assert.False(FirstCityContentValidator.ValidateDirectory(directory).Success);
            }
            var routes = new JsonArray(new JsonObject { ["beats"] = new JsonArray(OutlineIds
                .Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()) });
            File.WriteAllText(Path.Combine(directory, "planning/story-outline.json"), new JsonObject { ["routes"] = routes }.ToJsonString());
            Assert.True(FirstCityContentValidator.ValidateDirectory(directory).Success);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
