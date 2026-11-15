using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class BattleClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sanguo-battle-client-" + Guid.NewGuid().ToString("N"));
    private string SavePath => Path.Combine(_directory, "battle.json");

    [Theory]
    [InlineData(0UL)]
    [InlineData(42UL)]
    public void PlayerPreviewCancelAndAiResumeReachSameOutcome(ulong seed)
    {
        var client = new BattleInteraction(DevelopmentBattleScenario.Create(seed));
        var reference = DevelopmentBattleScenario.Create(seed);
        var requests = new HashSet<EffectKind>();
        var steps = 0;
        while (client.View.PendingDecision is not null)
        {
            Assert.True(++steps < 4000, "客户端对局触发步数保护。");
            var before = BasicBattleSnapshotCodec.StateHash(client.Snapshot);
            Assert.All(client.View.Combatants.Where(u => u.TeamId == "b"), u => Assert.Empty(u.VisibleHand));
            if (client.View.Response is { } response) requests.Add(response.Kind);
            var d = reference.State.Battle.PendingDecision!;
            var action = BasicBattleAi.Choose(BasicBattle.Observe(reference.State.Battle, d.ControllerId))!.Action;
            if (client.WaitingForAi)
            {
                Assert.Empty(client.View.LegalActions);
                Assert.False(client.Select(action));
                Assert.True(client.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action)!.Accepted);
            }
            else
            {
                Assert.Null(client.AdvanceAi(_ => throw new InvalidOperationException("不能替玩家行动")));
                Assert.True(client.Select(action));
                client.Cancel();
                Assert.Null(client.Selection);
                Assert.Equal(before, BasicBattleSnapshotCodec.StateHash(client.Snapshot));
                Assert.False(client.Confirm().Accepted);
                Assert.True(client.Select(action));
                // 即使在已选方案时存档，恢复也必须回到同一请求并清空预览。
                var restored = BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(client.Snapshot), DevelopmentBattleScenario.ContentHash);
                client.Restore(restored);
                Assert.Null(client.Selection);
                Assert.Equal(before, BasicBattleSnapshotCodec.StateHash(client.Snapshot));
                Assert.True(client.Select(action));
                Assert.True(client.Confirm().Accepted);
                Assert.False(client.Confirm().Accepted);
            }
            Assert.True(reference.Submit(new($"client.command.{reference.State.Battle.StateVersion}", reference.State.Battle.StateVersion,
                d.ControllerId, d.RequestId, d.ActorId, action)).Accepted);
            Assert.Equal(BasicBattleSnapshotCodec.StateHash(reference.State), BasicBattleSnapshotCodec.StateHash(client.Snapshot));
        }
        Assert.NotNull(client.View.WinnerTeamId);
        Assert.Contains(EffectKind.Nullification, requests);
        Assert.Contains(EffectKind.Dodge, requests);
        Assert.Contains(EffectKind.Rescue, requests);
    }

    [Fact]
    public void InvalidAiOrSelectionCannotChangeState()
    {
        var client = new BattleInteraction(DevelopmentBattleScenario.Create());
        var before = BasicBattleSnapshotCodec.StateHash(client.Snapshot);
        Assert.False(client.Select(new(BattleActionKind.SelectHandCard, HandIndex: 999)));
        Assert.False(client.Confirm().Accepted);
        Assert.Equal(before, BasicBattleSnapshotCodec.StateHash(client.Snapshot));
        while (!client.WaitingForAi)
        {
            client.Select(BasicBattleAi.Choose(client.View)!.Action);
            Assert.True(client.Confirm().Accepted);
        }
        before = BasicBattleSnapshotCodec.StateHash(client.Snapshot);
        Assert.Throws<InvalidOperationException>(() => client.AdvanceAi(_ => new(BattleActionKind.SelectHandCard, HandIndex: 999)));
        Assert.Equal(before, BasicBattleSnapshotCodec.StateHash(client.Snapshot));
    }

    [Fact]
    public void IncompatibleRestoreRetainsCurrentSelectionAndState()
    {
        var client = new BattleInteraction(DevelopmentBattleScenario.Create());
        var action = client.View.LegalActions.First();
        client.Select(action);
        var before = BasicBattleSnapshotCodec.StateHash(client.Snapshot);
        Assert.Throws<ArgumentException>(() => client.Restore(client.Snapshot with { ContentHash = new string('b', 64) }));
        Assert.Equal(before, BasicBattleSnapshotCodec.StateHash(client.Snapshot));
        Assert.Equal(action, client.Selection);
    }

    [Fact]
    public void BattleFileStorePreservesBackupAcrossCorruptionAndSupportsMissingPrimary()
    {
        var store = new BasicBattleFileSaveStore();
        var client = new BattleInteraction(DevelopmentBattleScenario.Create());
        store.Save(SavePath, client.Snapshot);
        var first = BasicBattleSnapshotCodec.StateHash(client.Snapshot);
        client.Select(new(BattleActionKind.EndPlay));
        client.Confirm();
        store.Save(SavePath, client.Snapshot);
        Assert.Equal(BasicBattleSnapshotCodec.StateHash(client.Snapshot),
            BasicBattleSnapshotCodec.StateHash(store.Load(SavePath, DevelopmentBattleScenario.ContentHash).State));
        File.WriteAllText(SavePath, "broken");
        var result = store.Load(SavePath, DevelopmentBattleScenario.ContentHash);
        Assert.True(result.UsedBackup);
        Assert.Equal(first, BasicBattleSnapshotCodec.StateHash(result.State));
        var backup = File.ReadAllBytes(SavePath + ".bak");
        store.Save(SavePath, result.State);
        Assert.Equal(backup, File.ReadAllBytes(SavePath + ".bak"));
        File.Delete(SavePath);
        Assert.True(store.Load(SavePath, DevelopmentBattleScenario.ContentHash).UsedBackup);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void BattleFileStoreRejectsIncompatibleAndInvalidStateWithoutOverwriting()
    {
        var store = new BasicBattleFileSaveStore();
        var state = DevelopmentBattleScenario.Create().State;
        store.Save(SavePath, state);
        store.Save(SavePath, state);
        var original = File.ReadAllBytes(SavePath);
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => store.Save(SavePath,
            state with { ContentHash = new string('b', 64) })).Code);
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => store.Load(SavePath, new string('b', 64))).Code);
        Assert.Throws<ArgumentException>(() => store.Save(SavePath, state with { Battle = state.Battle with { StateVersion = 99 } }));
        Assert.Equal(original, File.ReadAllBytes(SavePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void BattleFileStoreReportsMissingOrUnrecoverableSlots()
    {
        var store = new BasicBattleFileSaveStore();
        Assert.Equal("save.unrecoverable", Assert.Throws<SaveException>(() => store.Load(SavePath, DevelopmentBattleScenario.ContentHash)).Code);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SavePath, "bad"); File.WriteAllText(SavePath + ".bak", "bad");
        Assert.Equal("save.unrecoverable", Assert.Throws<SaveException>(() => store.Load(SavePath, DevelopmentBattleScenario.ContentHash)).Code);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
