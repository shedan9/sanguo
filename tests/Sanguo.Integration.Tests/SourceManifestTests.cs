using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Sanguo.Content;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class SourceManifestTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sanguo-sources-" + Guid.NewGuid().ToString("N"));
    private const string Rules = "rules-reference.json";
    private const string Generals = "sgsol-generals.json";
    private const string Ui = "sgsol-battle-ui-reference.json";

    public SourceManifestTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "manifests"));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "SourceFixtures"), "*.json"))
            File.Copy(path, Path.Combine(root, "manifests", Path.GetFileName(path)));
    }

    [Fact]
    public void DraftRegistrationIsValidButCannotPassFreezeCheck()
    {
        var result = Check();
        Assert.True(result.Success, string.Join("\n", result.Issues));
        Assert.False(result.ReadyToFreeze);
        Assert.Contains(result.Pending, i => i.Field == "$.generals[0].status");
        Assert.Contains(result.Pending, i => i.Field == "$.interactionStates[9].status");
        Assert.Contains(result.Pending, i => i.Field == "$.adaptations[1].status");
    }

    [Fact]
    public void ConsistentSyntheticEvidencePassesMachineChecks()
    {
        FreezeFixture();
        var result = Check();
        Assert.True(result.ReadyToFreeze, string.Join("\n", result.Issues.Concat(result.Pending)));
    }

    [Fact]
    public void ChangingOnlyStatusesCannotPretendToFreezeSources()
    {
        foreach (var name in new[] { Rules, Generals, Ui }) Edit(name, data => data["status"] = "frozen");
        var result = Check();
        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Field == "$.sourceSnapshotId");
        Assert.Contains(result.Issues, i => i.Field == "$.blockingItems");
        Assert.Contains(result.Issues, i => i.Field == "$.generals[0].status");
        Assert.Contains(result.Issues, i => i.Field == "$.evidence");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("bad-hash")]
    [InlineData("other-snapshot")]
    [InlineData("other-version")]
    [InlineData("bad-url")]
    public void EvidenceMustMatchLocalFileAndSourceMetadata(string invalid)
    {
        FreezeFixture();
        if (invalid == "missing") File.Delete(Path.Combine(root, "evidence", "sample.txt"));
        if (invalid == "tampered") File.AppendAllText(Path.Combine(root, "evidence", "sample.txt"), "tampered");
        Edit(Rules, data =>
        {
            var evidence = data["evidence"]![0]!;
            if (invalid == "bad-hash") evidence["sha256"] = "invalid";
            if (invalid == "other-snapshot") evidence["sourceSnapshotId"] = "other";
            if (invalid == "other-version") evidence["clientVersion"] = "other";
            if (invalid == "bad-url") evidence["sourceUrl"] = "file:///tmp/source";
        });
        Assert.Contains(Check().Issues, i => i.File == "manifests/" + Rules && i.Field.StartsWith("$.evidence[0]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/tmp/evidence.txt")]
    [InlineData("C:\\evidence.txt")]
    [InlineData("evidence/../../outside.txt")]
    public void EvidenceCannotEscapeContentDirectory(string path)
    {
        FreezeFixture();
        Edit(Rules, data => data["evidence"]![0]!["path"] = path);
        Assert.Contains(Check().Issues, i => i.Code == "evidence.path");
    }

    [Fact]
    public void RequiredCharactersAndInteractionStatesCannotBeDeletedOrDuplicated()
    {
        Edit(Generals, data => data["generals"]![1]!["generalId"] = "guanyu");
        Edit(Ui, data => data["interactionStates"]!.AsArray().RemoveAt(0));
        var result = Check();
        Assert.Contains(result.Issues, i => i.Code == "id.duplicate");
        Assert.Contains(result.Issues, i => i.Field == "$.generals" && i.Message.Contains("zhaoyun", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.Field == "$.interactionStates" && i.Message.Contains("idle", StringComparison.Ordinal));
    }

    [Fact]
    public void PendingCharactersCannotEnterRuntime()
    {
        Edit(Generals, data => data["generals"]![0]!["includedInRuntime"] = true);
        Assert.Contains(Check().Issues, i => i.Field == "$.generals[0].includedInRuntime");
    }

    [Fact]
    public void FrozenUiRequiresMatchingEvidenceKindsAndResolvedAdaptations()
    {
        FreezeFixture();
        Edit(Ui, data =>
        {
            data["recordings"] = new JsonArray("screen");
            data["interactionStates"]![0]!["evidence"] = new JsonArray("missing");
            data["adaptations"]![0]!["status"] = "awaiting_reference_mapping";
        });
        var result = Check();
        Assert.Contains(result.Issues, i => i.Code == "reference.missing" && i.Field == "$.recordings[0]");
        Assert.Contains(result.Issues, i => i.Code == "reference.missing" && i.Field == "$.interactionStates[0].evidence[0]");
        Assert.Contains(result.Issues, i => i.Field == "$.adaptations[0].status");
    }

    [Fact]
    public void ConflictingSnapshotHeadersAndUnknownStatusesAreRejected()
    {
        FreezeFixture();
        Edit(Generals, data => data["clientVersion"] = "other-version");
        Edit(Ui, data => data["status"] = "typo-frozen");
        var result = Check();
        Assert.Contains(result.Issues, i => i.Code == "source.snapshot_mismatch" && i.Field == "$.clientVersion");
        Assert.Contains(result.Issues, i => i.File == "manifests/" + Ui && i.Field == "$.status");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"unknown\":1}")]
    public void MalformedOrIncompleteJsonReturnsLocatedIssues(string json)
    {
        File.WriteAllText(Path.Combine(root, "manifests", Rules), json);
        Assert.Contains(Check().Issues, i => i.File == "manifests/" + Rules);
    }

    [Fact]
    public void NullEntriesAndMissingFilesReturnIssuesInsteadOfCrashing()
    {
        Edit(Generals, data => data["generals"]![0] = null);
        Edit(Ui, data => data["interactionStates"]![0] = null);
        File.Delete(Path.Combine(root, "manifests", Rules));
        var result = Check();
        Assert.Contains(result.Issues, i => i.Code == "source.io");
        Assert.Contains(result.Issues, i => i.Field == "$.generals[0]");
        Assert.Contains(result.Issues, i => i.Field == "$.interactionStates[0]");
    }

    [Theory]
    [InlineData("sourceGame", "null")]
    [InlineData("sourceGame", "\"other-product\"")]
    [InlineData("formatVersion", "2")]
    [InlineData("evidence", "null")]
    [InlineData("evidence", "[null]")]
    [InlineData("deckList", "[null]")]
    [InlineData("unknownField", "true")]
    public void InvalidFieldTypesAndUnknownPropertiesAreRejected(string field, string json)
    {
        Edit(Rules, data => data[field] = JsonNode.Parse(json));
        Assert.False(Check().Success);
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejectedInsteadOfOverwritingStatus()
    {
        var path = Path.Combine(root, "manifests", Rules);
        File.WriteAllText(path, File.ReadAllText(path).TrimEnd()[..^1] + ",\"status\":\"frozen\"}");
        Assert.Contains(Check().Issues, i => i.Code == "json.invalid");
    }

    [Fact]
    public void FrozenSourcesRequireCompleteSkillsAttributesAndLocalEvidence()
    {
        FreezeFixture();
        Edit(Generals, data =>
        {
            data["generals"]![0]!["completeSkills"] = new JsonArray();
            data["generals"]![1]!["baseAttributes"]!["maxHp"] = 0;
            data["generals"]![2]!["evidence"] = new JsonArray();
        });
        var result = Check();
        Assert.Contains(result.Issues, i => i.Field == "$.generals[0].completeSkills");
        Assert.Contains(result.Issues, i => i.Field == "$.generals[1].baseAttributes.maxHp");
        Assert.Contains(result.Issues, i => i.Field == "$.generals[2].evidence");
    }

    private SourceManifestResult Check() => SourceManifestValidator.ValidateDirectory(root);
    private void Edit(string name, Action<JsonObject> change)
    {
        var path = Path.Combine(root, "manifests", name);
        var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(data);
        File.WriteAllText(path, data.ToJsonString());
    }

    private void FreezeFixture()
    {
        Directory.CreateDirectory(Path.Combine(root, "evidence"));
        File.WriteAllText(Path.Combine(root, "evidence", "sample.txt"), "synthetic fixture; not OL evidence");
        foreach (var name in new[] { Rules, Generals, Ui })
            Edit(name, data => { data["status"] = "frozen"; data["sourceSnapshotId"] = "test.snapshot"; data["clientVersion"] = "test.client"; });
        Edit(Rules, data =>
        {
            data["frozenAt"] = "2026-11-12";
            data["blockingItems"] = new JsonArray();
            data["deckList"] = new JsonArray("test.card");
            data["rulings"] = new JsonArray("test.ruling");
            data["evidence"] = new JsonArray(Evidence("rule", "rules"), Evidence("ruling", "ruling"));
        });
        Edit(Generals, data =>
        {
            foreach (var general in data["generals"]!.AsArray())
            {
                general!["status"] = "frozen";
                general["variantId"] = "test.variant";
                general["portraitId"] = "test.portrait";
                general["baseAttributes"] = new JsonObject { ["maxHp"] = 4, ["gender"] = "test", ["faction"] = "test" };
                general["completeSkills"] = new JsonArray(new JsonObject { ["skillId"] = "test.skill", ["text"] = "合成测试技能" });
                general["ruleCaseIds"] = new JsonArray("test.case");
                general["evidence"] = new JsonArray(Evidence("general", "general"), Evidence("portrait", "portrait"));
            }
        });
        Edit(Ui, data =>
        {
            data["theme"] = "test.theme";
            data["scale"] = 1;
            data["capturedAt"] = "2026-11-12T10:00:00+08:00";
            data["screenshots"] = new JsonArray("screen");
            data["recordings"] = new JsonArray("video");
            data["evidence"] = new JsonArray(Evidence("screen", "screenshot"), Evidence("video", "recording"));
            foreach (var state in data["interactionStates"]!.AsArray())
            { state!["status"] = "frozen"; state["evidence"] = new JsonArray("screen", "video"); }
            foreach (var adaptation in data["adaptations"]!.AsArray()) adaptation!["status"] = "specified";
        });
    }

    private JsonObject Evidence(string id, string kind) => new()
    {
        ["id"] = id, ["kind"] = kind, ["path"] = "evidence/sample.txt",
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "evidence", "sample.txt")))),
        ["sourceUrl"] = "https://example.invalid/synthetic", ["sourceSnapshotId"] = "test.snapshot", ["clientVersion"] = "test.client"
    };

    public void Dispose() => Directory.Delete(root, true);
}
