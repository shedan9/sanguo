using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.CampaignCore;
using Sanguo.CampaignCore.FirstCity;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class FirstCityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sanguo-first-city-" + Guid.NewGuid().ToString("N"));
    private string SavePath => Path.Combine(_directory, "session.json");

    [Theory]
    [InlineData(Origin.Damage, "a", true)]
    [InlineData(Origin.Damage, "b", false)]
    [InlineData(Origin.Control, "a", false)]
    [InlineData(Origin.Control, "b", true)]
    [InlineData(Origin.Tank, "a", true)]
    [InlineData(Origin.Tank, "b", false)]
    public void AllOriginsChoicesAndOptionalRecruitmentReachFirstCity(Origin origin, string choice, bool recruit)
    {
        var session = FirstCitySession.Create("test", origin);
        var steps = 0;
        var restores = 0;
        while (!session.OwnsFirstCity)
        {
            Assert.True(++steps < 10000);
            Assert.True(session.Progress.Attempt <= 30);
            var before = session.Snapshot;
            Step(session, choice, recruit);
            Assert.All(session.Progress.Roster, id => Assert.Contains(id, new[] { FirstCityStory.Protagonist, FirstCityStory.Volunteer }));
            if (session.ActiveBattle?.State.Battle.StateVersion % 30 == 0 || before.Progress != session.Progress)
            {
                var restored = new FirstCitySession(FirstCitySnapshotCodec.Decode(FirstCitySnapshotCodec.Encode(session.Snapshot)));
                Assert.Equal(FirstCitySnapshotCodec.StateHash(session.Snapshot), FirstCitySnapshotCodec.StateHash(restored.Snapshot));
                session = restored; restores++;
            }
            if (session.Progress.Stage == 3) Assert.False(session.OwnsFirstCity);
        }
        Assert.True(restores > 10);
        Assert.Equal(4, session.Snapshot.Results.Count(r => r.FinalState.Battle.WinnerTeamId == "a"));
        Assert.Equal(recruit ? 2 : 1, session.Progress.Roster.Length);
        Assert.Equal(3, session.Progress.Choices.Length);
        Assert.All(session.Progress.Choices, c => Assert.Equal(choice, c.ChoiceId));
        var ending = FirstCityStory.Epilogue(session.Progress);
        Assert.NotEmpty(ending);
        Assert.False(Command(session, "start", FirstCityStory.Protagonist).Accepted);
        Assert.False(Command(session, "resolve").Accepted);
    }

    [Fact]
    public void DefeatRetryPreservesChoicesRosterAndAllowsProtagonistToStayOut()
    {
        var session = FirstCitySession.Create("test", Origin.Damage);
        AdvanceToStageOne(session);
        Assert.True(Command(session, "recruit", FirstCityStory.Volunteer).Accepted);
        Assert.True(Command(session, "choose", "b").Accepted);
        Assert.True(Command(session, "start", FirstCityStory.Volunteer).Accepted);
        Assert.DoesNotContain(session.ActiveBattle!.State.Battle.Combatants, u => u.EntityId == FirstCityStory.Protagonist);
        FinishBattle(session.ActiveBattle, passivePlayer: true);
        Assert.Equal("b", session.ActiveBattle.State.Battle.WinnerTeamId);
        Assert.True(Command(session, "resolve").Accepted);
        Assert.Equal(CampaignPhase.Defeat, session.Progress.Phase);
        var oldProgress = session.Progress;
        session = new(FirstCitySnapshotCodec.Decode(FirstCitySnapshotCodec.Encode(session.Snapshot)));
        Assert.True(Command(session, "retry").Accepted);
        Assert.Equal(oldProgress.Roster.ToArray(), session.Progress.Roster.ToArray());
        Assert.Equal(oldProgress.Choices.ToArray(), session.Progress.Choices.ToArray());
        Assert.Equal(oldProgress.Attempt + 1, session.Progress.Attempt);
        Assert.True(Command(session, "start", string.Join('|', session.Progress.Roster)).Accepted);
        Assert.Equal(4, session.ActiveBattle!.State.Battle.Combatants[0].Hp);
        Assert.Contains(session.ActiveBattle.State.Battle.Combatants, u => u.EntityId == FirstCityStory.Protagonist);
        Assert.Equal(FirstCitySnapshotCodec.StateHash(session.Snapshot),
            FirstCitySnapshotCodec.StateHash(FirstCitySnapshotCodec.Decode(FirstCitySnapshotCodec.Encode(session.Snapshot))));
    }

    [Fact]
    public void PreviewCannotMintBattleResultsOrBypassChoicesAndRecruitment()
    {
        var session = FirstCitySession.Create("test", Origin.Control);
        var hash = FirstCitySnapshotCodec.StateHash(session.Snapshot);
        foreach (var (kind, value) in new[] { ("start", "protagonist"), ("resolve", "a"), ("retry", (string?)null),
            ("recruit", FirstCityStory.Volunteer), ("choose", "unknown") })
            Assert.False(Command(session, kind, value).Accepted);
        Assert.Equal(hash, FirstCitySnapshotCodec.StateHash(session.Snapshot));
        Assert.True(Command(session, "choose", "a").Accepted);
        Assert.True(Command(session, "start", FirstCityStory.Protagonist).Accepted);
        hash = FirstCitySnapshotCodec.StateHash(session.Snapshot);
        Assert.Equal("battle.unfinished", Command(session, "resolve").Code);
        Assert.Equal("result.invalid", Command(session, "resolve", "a").Code);
        Assert.Equal(hash, FirstCitySnapshotCodec.StateHash(session.Snapshot));
        Assert.False(session.OwnsFirstCity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("protagonist|protagonist")]
    [InlineData("guanyu")]
    [InlineData("companion.volunteer")]
    [InlineData("protagonist|")]
    public void InvalidDeploymentDoesNotCreateBattle(string deployment)
    {
        var session = FirstCitySession.Create("test", Origin.Tank);
        Assert.True(Command(session, "choose", "a").Accepted);
        var hash = FirstCitySnapshotCodec.StateHash(session.Snapshot);
        Assert.Equal("deployment.invalid", Command(session, "start", deployment).Code);
        Assert.Equal(hash, FirstCitySnapshotCodec.StateHash(session.Snapshot));
        Assert.Null(session.ActiveBattle);
    }

    [Fact]
    public void CommandsAreIdempotentAcrossRestoreAndRejectStaleOrConflictingInputs()
    {
        var session = FirstCitySession.Create("test", Origin.Damage);
        var command = new FirstCityCommand("choice", 0, "choose", "a");
        Assert.True(session.Submit(command).Accepted);
        session = new(FirstCitySnapshotCodec.Decode(FirstCitySnapshotCodec.Encode(session.Snapshot)));
        var hash = FirstCitySnapshotCodec.StateHash(session.Snapshot);
        Assert.True(session.Submit(command).Duplicate);
        Assert.Equal("command.id_conflict", session.Submit(command with { Value = "b" }).Code);
        Assert.Equal("state.stale", session.Submit(new("old", 0, "start", "protagonist")).Code);
        Assert.Equal(hash, FirstCitySnapshotCodec.StateHash(session.Snapshot));
    }

    [Fact]
    public void CampaignBattleInteractionUsesOwnedBattleAndClearsUncommittedPreviewOnRestore()
    {
        var session = FirstCitySession.Create("test", Origin.Damage);
        Command(session, "choose", "a"); Command(session, "start", "protagonist");
        var interaction = new BattleInteraction(session.ActiveBattle!);
        var action = interaction.View.LegalActions.First();
        Assert.True(interaction.Select(action));
        var saved = FirstCitySnapshotCodec.Encode(session.Snapshot);
        Assert.True(interaction.Confirm().Accepted);
        Assert.Equal(1, session.Snapshot.Battle!.Battle.StateVersion);
        var restored = new FirstCitySession(FirstCitySnapshotCodec.Decode(saved));
        var restoredInteraction = new BattleInteraction(restored.ActiveBattle!);
        Assert.Null(restoredInteraction.Selection);
        Assert.Equal(0, restoredInteraction.View.StateVersion);
        Assert.All(restoredInteraction.View.Combatants.Where(u => u.TeamId == "b"), u => Assert.Empty(u.VisibleHand));
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("roster")]
    [InlineData("hp")]
    [InlineData("seed")]
    [InlineData("receipt")]
    [InlineData("card")]
    public void ChecksummedSemanticTamperingIsRejected(string field)
    {
        var session = FirstCitySession.Create("test", Origin.Damage);
        Command(session, "choose", "a"); Command(session, "start", "protagonist");
        var envelope = JsonNode.Parse(FirstCitySnapshotCodec.Encode(session.Snapshot))!;
        var payload = JsonNode.Parse(envelope["payload"]!.GetValue<string>())!;
        switch (field)
        {
            case "stage": payload["progress"]!["stage"] = 3; break;
            case "roster": payload["progress"]!["roster"]!.AsArray().Add(FirstCityStory.Volunteer); break;
            case "hp": payload["battle"]!["battle"]!["combatants"]![0]!["hp"] = 3; break;
            case "seed": payload["progress"]!["seed"] = 999; break;
            case "receipt": payload["receipts"]![0]!["command"]!["value"] = "b"; break;
            case "card":
                var card = payload["battle"]!["battle"]!["combatants"]![0]!["hand"]![0]!;
                card["rank"] = card["rank"]!.GetValue<int>() == 13 ? 1 : 13; break;
        }
        var text = payload.ToJsonString(); envelope["payload"] = text;
        envelope["checksum"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => FirstCitySnapshotCodec.Decode(Encoding.UTF8.GetBytes(envelope.ToJsonString()))).Code);
    }

    [Fact]
    public void FileStoreFallsBackWithoutOverwritingGoodBackupAndRejectsIncompatibleMain()
    {
        var store = new FirstCitySaveStore();
        var session = FirstCitySession.Create("test", Origin.Damage);
        store.Save(SavePath, session.Snapshot);
        var original = File.ReadAllBytes(SavePath);
        Command(session, "choose", "a"); store.Save(SavePath, session.Snapshot);
        File.WriteAllText(SavePath, "broken");
        Assert.True(store.Load(SavePath).UsedBackup);
        Assert.Equal(CampaignPhase.Choice, store.Load(SavePath).State.Progress.Phase);
        store.Save(SavePath, session.Snapshot);
        Assert.Equal(original, File.ReadAllBytes(SavePath + ".bak"));
        var envelope = JsonNode.Parse(File.ReadAllBytes(SavePath))!; envelope["formatVersion"] = 99;
        File.WriteAllText(SavePath, envelope.ToJsonString());
        var incompatible = File.ReadAllBytes(SavePath);
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => store.Load(SavePath)).Code);
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => store.Save(SavePath, session.Snapshot)).Code);
        Assert.Equal(incompatible, File.ReadAllBytes(SavePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void DuplicateJsonAndUnknownFieldsAreRejected()
    {
        var bytes = FirstCitySnapshotCodec.Encode(FirstCitySession.Create("test", Origin.Damage).Snapshot);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => FirstCitySnapshotCodec.Decode(Encoding.UTF8.GetBytes(json.Insert(1, "\"formatVersion\":1,")))).Code);
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => FirstCitySnapshotCodec.Decode(Encoding.UTF8.GetBytes(json.Insert(1, "\"extra\":1,")))).Code);
    }

    private static CommandResult Command(FirstCitySession session, string kind, string? value = null)
        => session.Submit(new($"campaign.command.{session.Version}", session.Version, kind, value));
    private static void AdvanceToStageOne(FirstCitySession session)
    {
        var guard = 0;
        while (session.Progress.Stage == 0) { Assert.True(++guard < 4000); Step(session, "a", false); }
    }
    private static void Step(FirstCitySession session, string choice, bool recruit)
    {
        if (session.ActiveBattle is { } battle && battle.State.Battle.PendingDecision is not null) { BattleStep(battle); return; }
        var p = session.Progress;
        var result = p.Phase switch
        {
            CampaignPhase.Choice when p.Stage == 1 && recruit && !p.Roster.Contains(FirstCityStory.Volunteer) => Command(session, "recruit", FirstCityStory.Volunteer),
            CampaignPhase.Choice => Command(session, "choose", choice),
            CampaignPhase.Deployment => Command(session, "start", string.Join('|', p.Roster)),
            CampaignPhase.Battle => Command(session, "resolve"), CampaignPhase.Defeat => Command(session, "retry"),
            _ => throw new InvalidOperationException("未知阶段。")
        };
        Assert.True(result.Accepted, result.Message);
    }
    private static void FinishBattle(BasicBattleSession battle, bool passivePlayer = false)
    {
        var guard = 0;
        while (battle.State.Battle.PendingDecision is not null) { Assert.True(++guard < 4000); BattleStep(battle, passivePlayer); }
    }
    private static void BattleStep(BasicBattleSession battle, bool passivePlayer = false)
    {
        var d = battle.State.Battle.PendingDecision!;
        var view = BasicBattle.Observe(battle.State.Battle, d.ControllerId);
        var action = passivePlayer && d.ControllerId == "player"
            ? view.LegalActions.FirstOrDefault(a => a.Kind is BattleActionKind.EndPlay or BattleActionKind.Pass or BattleActionKind.Discard) ?? BasicBattleAi.Choose(view)!.Action
            : BasicBattleAi.Choose(view)!.Action;
        var command = new BattleCommand($"campaign.battle.{view.StateVersion}", view.StateVersion, d.ControllerId, d.RequestId, d.ActorId, action);
        Assert.True(battle.Submit(command).Accepted);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
