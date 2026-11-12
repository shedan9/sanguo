using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public class BasicBattleTests
{
    private static readonly string ContentHash = new('c', 64);
    private static readonly BattleAction Pass = new(BattleActionKind.Pass);

    [Fact]
    public void SlashAndDodgeKeepSlashInProcessingUntilResponseCompletes()
    {
        var session = Fixture([["s0"], ["d0"]]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Assert.Equal("u1", session.State.Battle.PendingDecision!.ActorId);
        Assert.Equal("s0", Assert.Single(session.State.Battle.Processing).InstanceId);
        Assert.Empty(session.State.Battle.DiscardPile);
        var restored = Restore(session);
        Submit(restored, new(BattleActionKind.Dodge, "d0", "u1"));
        Assert.Equal(4, restored.State.Battle.Combatants[1].Hp);
        Assert.Empty(restored.State.Battle.Processing);
        Assert.Equal(new[] { "d0", "s0" }, restored.State.Battle.DiscardPile.Select(c => c.InstanceId));
        Assert.Equal(new[] { "slash.used", "card.discarded", "slash.dodged", "card.discarded" },
            restored.State.Battle.Events.TakeLast(4).Select(e => e.Code));
    }

    [Fact]
    public void PassingDodgeDealsDamageWithSourceAndPeachHealsOnlySelfInPlay()
    {
        var session = Fixture([["s0"], ["p0"]]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Assert.Equal(3, session.State.Battle.Combatants[1].Hp);
        var damage = Assert.Single(session.State.Battle.Events.Where(e => e.Code == "damage.dealt"));
        Assert.Equal(("u0", "u1", "s0", 1), (damage.ActorId, damage.TargetId, damage.CardId, damage.Amount));
        Submit(session, new(BattleActionKind.EndPlay));
        Assert.Equal("u1", session.State.Battle.PendingDecision!.ActorId);
        Submit(session, new(BattleActionKind.Peach, "p0", "u1"));
        Assert.Equal(4, session.State.Battle.Combatants[1].Hp);
    }

    [Theory]
    [InlineData(BattleActionKind.Slash, "s0", "u0")]
    [InlineData(BattleActionKind.Slash, "s0", "missing")]
    [InlineData(BattleActionKind.Slash, "s1", "u1")]
    [InlineData(BattleActionKind.Dodge, "d0", "u0")]
    [InlineData(BattleActionKind.Peach, "p0", "u0")]
    [InlineData(BattleActionKind.Peach, "p0", "u1")]
    [InlineData(BattleActionKind.Pass, null, null)]
    public void IllegalTimingPaymentAndTargetLeaveFullStateUntouched(BattleActionKind kind, string? card, string? target)
    {
        var session = Fixture([["s0", "d0", "p0"], ["s1"]]);
        RejectUnchanged(session, Command(session, new(kind, card, target)), "action.illegal");
    }

    [Fact]
    public void SlashLimitAndRangeUseLiveSeatsAndResetOnNextTurn()
    {
        var session = Fixture([["s0", "s1"], [], [], []], leftCount: 2);
        Assert.Equal(2, BasicBattle.Distance(session.State.Battle, "u0", "u2"));
        RejectUnchanged(session, Command(session, new(BattleActionKind.Slash, "s0", "u2")), "action.illegal");
        Submit(session, new(BattleActionKind.Slash, "s0", "u3"));
        Submit(session, Pass);
        RejectUnchanged(session, Command(session, new(BattleActionKind.Slash, "s1", "u3")), "action.illegal");
        Submit(session, new(BattleActionKind.EndPlay));
        Assert.Equal(0, session.State.Battle.SlashesUsed);
        Assert.Equal(2, session.State.Battle.TurnNumber);
    }

    [Fact]
    public void RescueStartsAtActiveSeatAllowsEnemyHelpAndRepeatsUntilPositiveHp()
    {
        var session = Fixture([["s0", "p0", "p1"], ["p2"]], hp: [4, 1]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
        // 注入合法稳定濒死状态，覆盖未来多点伤害所需的负体力恢复语义。
        var battle = session.State.Battle;
        session = new(session.State with { Battle = battle with
            { Combatants = battle.Combatants.SetItem(1, battle.Combatants[1] with { Hp = -1 }) } });
        Submit(session, new(BattleActionKind.Peach, "p0", "u1"));
        Assert.Equal(0, session.State.Battle.Combatants[1].Hp);
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
        Assert.Equal(EffectKind.Rescue, session.State.Battle.Frames[^1].Kind);
        session = Restore(session);
        Submit(session, new(BattleActionKind.Peach, "p1", "u1"));
        Assert.Equal(1, session.State.Battle.Combatants[1].Hp);
        Assert.True(session.State.Battle.Combatants[1].Alive);
        Assert.Empty(session.State.Battle.Frames);
        Assert.Equal("s0", session.State.Battle.DiscardPile[^1].InstanceId);
    }

    [Fact]
    public void DyingUnitCanSaveSelfAfterEarlierRescuersPass()
    {
        var session = Fixture([["s0"], ["p0"]], hp: [4, 1]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Submit(session, Pass);
        Assert.Equal("u1", session.State.Battle.PendingDecision!.ActorId);
        Submit(session, new(BattleActionKind.Peach, "p0", "u1"));
        Assert.Equal(1, session.State.Battle.Combatants[1].Hp);
        Assert.Null(session.State.Battle.WinnerTeamId);
    }

    [Fact]
    public void RescueOrderWrapsFromCurrentTurnRatherThanDyingSeatOrSeatZero()
    {
        var session = Fixture([[], [], ["s0"], []], hp: [4, 1, 4, 4], leftCount: 2);
        Submit(session, new(BattleActionKind.EndPlay));
        Submit(session, new(BattleActionKind.EndPlay));
        while (session.State.Battle.Phase == BattlePhase.Discard)
            Submit(session, BasicBattle.GetLegalActions(session.State.Battle)[0]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        foreach (var expected in new[] { "u2", "u3", "u0", "u1" })
        {
            Assert.Equal(expected, session.State.Battle.PendingDecision!.ActorId);
            Submit(session, Pass);
        }
        Assert.False(session.State.Battle.Combatants[1].Alive);
    }

    [Theory]
    [InlineData(BattleActionKind.EndPlay, null, null)]
    [InlineData(BattleActionKind.Peach, "p0", "u1")]
    [InlineData(BattleActionKind.Dodge, "d0", "u0")]
    [InlineData(BattleActionKind.Discard, "d0", null)]
    public void DodgeRequestRejectsUnrelatedActions(BattleActionKind kind, string? cardId, string? target)
    {
        var session = Fixture([["s0"], ["d0", "p0"]]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        RejectUnchanged(session, Command(session, new(kind, cardId, target)), "action.illegal");
    }

    [Fact]
    public void EveryonePassingRescueCleansDeadHandBeforeCardAndFinalOutcome()
    {
        var session = Fixture([["s0"], ["d0", "p0"]], hp: [4, 1]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Assert.Null(session.State.Battle.WinnerTeamId);
        Submit(session, Pass);
        session = Restore(session);
        Submit(session, Pass);
        var state = session.State.Battle;
        Assert.Equal(BattlePhase.Finished, state.Phase);
        Assert.Equal("a", state.WinnerTeamId);
        Assert.False(state.Combatants[1].Alive);
        Assert.Empty(state.Combatants[1].Hand);
        Assert.Equal(new[] { "d0", "p0", "s0" }, state.DiscardPile.Select(c => c.InstanceId));
        Assert.Equal(new[] { "unit.died", "card.discarded", "battle.finished" }, state.Events.TakeLast(3).Select(e => e.Code));
        RejectUnchanged(session, Command(session, Pass), "decision.none");
    }

    [Fact]
    public void DeadSeatsAreSkippedForDistanceAndTurnsWithoutPrematureTeamVictory()
    {
        var session = Fixture([["s0"], ["p0"], [], []], hp: [4, 1, 4, 4], leftCount: 1);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        for (var i = 0; i < 4; i++) Submit(session, Pass);
        Assert.Null(session.State.Battle.WinnerTeamId);
        Assert.Equal(1, BasicBattle.Distance(session.State.Battle, "u0", "u2"));
        Submit(session, new(BattleActionKind.EndPlay));
        Assert.Equal("u2", session.State.Battle.PendingDecision!.ActorId);
    }

    [Fact]
    public void EndPlayRequiresDiscardToHpAndDeckExhaustionDoesNotInventCardsOrVictory()
    {
        var session = Fixture([["s0", "s1", "s2", "s3"], []], hp: [2, 4]);
        Submit(session, new(BattleActionKind.EndPlay));
        Assert.Equal(BattlePhase.Discard, session.State.Battle.Phase);
        RejectUnchanged(session, Command(session, new(BattleActionKind.EndPlay)), "action.illegal");
        Submit(session, new(BattleActionKind.Discard, "s0"));
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
        Submit(session, new(BattleActionKind.Discard, "s1"));
        Assert.Equal("u1", session.State.Battle.PendingDecision!.ActorId);
        var empty = BasicBattleSession.Create("empty", Setups(1, 1), [], ContentHash, 42, 0);
        Submit(empty, new(BattleActionKind.EndPlay));
        Assert.Null(empty.State.Battle.WinnerTeamId);
        Assert.Contains(empty.State.Battle.Events, e => e.Code == "draw.exhausted");
        Assert.Empty(empty.State.Battle.CardInventory);
    }

    [Fact]
    public void ReshuffleUsesDiscardOnlyAndRestoresRandomStream()
    {
        var state = BasicBattle.Create("shuffle", Setups(1, 1), [Card("s0"), Card("s1"), Card("s2")], 42, 0);
        var all = state.DrawPile.Concat(state.Combatants.SelectMany(u => u.Hand)).ToArray();
        state = state with { DrawPile = [], DiscardPile = all.ToImmutableArray(),
            Combatants = state.Combatants.Select(u => u with { Hand = ImmutableArray<BattleCard>.Empty }).ToImmutableArray() };
        var session = new BasicBattleSession(new(BasicBattleSession.EngineVersion, ContentHash, state, []));
        var restored = Restore(session);
        Submit(session, new(BattleActionKind.EndPlay));
        Submit(restored, new(BattleActionKind.EndPlay));
        Assert.Equal(Hash(session), Hash(restored));
        Assert.Equal(2, session.State.Battle.Combatants[1].Hand.Length);
        Assert.Empty(session.State.Battle.DiscardPile);
        Assert.True(session.State.Battle.Random.DrawCount > state.Random.DrawCount);
    }

    [Fact]
    public void ControllerActorVersionAndRequestChecksPreserveStateAndRandomness()
    {
        var session = Fixture([["s0"], []]);
        var command = Command(session, new(BattleActionKind.Slash, "s0", "u1"));
        RejectUnchanged(session, command with { ExpectedStateVersion = 99 }, "state.stale");
        RejectUnchanged(session, command with { ControllerId = "enemy" }, "controller.denied");
        RejectUnchanged(session, command with { ActorId = "u1" }, "actor.denied");
        RejectUnchanged(session, command with { RequestId = "old" }, "decision.stale");
        RejectUnchanged(session, command with { CommandId = "" }, "command.invalid");
    }

    [Fact]
    public void DuplicateCommandsCommitOnceIncludingConcurrentAndRestoredInputs()
    {
        var session = Fixture([["s0"], []]);
        var command = Command(session, new(BattleActionKind.Slash, "s0", "u1"));
        Parallel.For(0, 20, _ => Assert.True(session.Submit(command).Accepted));
        Assert.Single(session.State.Receipts);
        session = Restore(session);
        var before = Hash(session);
        Assert.True(session.Submit(command).Duplicate);
        Assert.Equal(before, Hash(session));
        RejectUnchanged(session, command with { Action = Pass }, "command.id_conflict");
    }

    [Fact]
    public void ViewsHideOtherControllersHandsDeckOrderAndLegalCosts()
    {
        var session = Fixture([["s0"], ["d0"]]);
        var view = BasicBattle.Observe(session.State.Battle, "player");
        Assert.Single(view.Combatants[0].VisibleHand);
        Assert.Empty(view.Combatants[1].VisibleHand);
        Assert.Equal(1, view.Combatants[1].HandCount);
        Assert.Empty(BasicBattle.Observe(session.State.Battle, "enemy").LegalActions);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Assert.Empty(BasicBattle.Observe(session.State.Battle, "player").LegalActions);
        Assert.Contains(BasicBattle.Observe(session.State.Battle, "enemy").LegalActions, a => a.Kind == BattleActionKind.Dodge);
    }

    [Fact]
    public void SameControllerStillCannotOperateAnotherUnitDuringResponse()
    {
        var session = Fixture([["s0"], ["d0"], []], leftCount: 2);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Assert.Equal("player", session.State.Battle.PendingDecision!.ControllerId);
        RejectUnchanged(session, Command(session, new(BattleActionKind.Dodge, "d0", "u1")) with { ActorId = "u0" }, "actor.denied");
    }

    [Fact]
    public void HiddenHandAndDeckChangesProduceIdenticalControllerView()
    {
        var first = Fixture([["s0"], ["d0"]]);
        var second = Fixture([["s0"], ["p0"]]);
        Assert.Equal(JsonSerializer.Serialize(BasicBattle.Observe(first.State.Battle, "player")),
            JsonSerializer.Serialize(BasicBattle.Observe(second.State.Battle, "player")));
    }

    [Fact]
    public void SnapshotsRejectCorruptionMismatchAndSemanticallyInvalidStates()
    {
        var session = Fixture([["s0"], []]);
        var bytes = BasicBattleSnapshotCodec.Encode(session.State);
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(bytes, new string('a', 64))).Code);
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode("{"u8.ToArray(), ContentHash)).Code);
        var modified = Encoding.UTF8.GetString(bytes).Replace("s0", "tampered", StringComparison.Ordinal);
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(Encoding.UTF8.GetBytes(modified), ContentHash)).Code);
        var envelope = JsonNode.Parse(bytes)!;
        envelope["formatVersion"] = 99;
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(Encoding.UTF8.GetBytes(envelope.ToJsonString()), ContentHash)).Code);
        var battle = session.State.Battle;
        Assert.Throws<ArgumentException>(() => BasicBattleSnapshotCodec.Encode(session.State with
            { Battle = battle with { PendingDecision = null } }));
        Assert.Throws<ArgumentException>(() => BasicBattleSnapshotCodec.Encode(session.State with
            { Battle = battle with { DrawPile = battle.DrawPile.Add(battle.Combatants[0].Hand[0]) } }));
        Assert.Throws<ArgumentException>(() => BasicBattleSnapshotCodec.Encode(session.State with
            { Battle = battle with { DrawPile = battle.DrawPile.RemoveAt(0) } }));
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        battle = session.State.Battle;
        Assert.Throws<ArgumentException>(() => BasicBattleSnapshotCodec.Encode(session.State with
            { Battle = battle with { Frames = battle.Frames.SetItem(1, battle.Frames[1] with { Cursor = 9 }) } }));
    }

    [Fact]
    public void SnapshotRejectsInvalidRescueEvenWithRecomputedChecksum()
    {
        var session = Fixture([["s0"], []], hp: [4, 1]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        var envelope = JsonNode.Parse(BasicBattleSnapshotCodec.Encode(session.State))!;
        var payload = JsonNode.Parse(envelope["payload"]!.GetValue<string>())!;
        payload["battle"]!["frames"]![1]!["cursor"] = 999;
        var text = payload.ToJsonString();
        envelope["payload"] = text;
        envelope["checksum"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(
            Encoding.UTF8.GetBytes(envelope.ToJsonString()), ContentHash)).Code);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(5, 1)]
    [InlineData(1, 5)]
    public void InvalidTeamSizesAreRejected(int left, int right)
        => Assert.Throws<ArgumentException>(() => BasicBattle.Create("invalid", Setups(left, right), [], 42, 0));

    [Fact]
    public void All16TeamSizesAnd100SeedsRestoreAtEveryDecisionAndConserveEveryCard()
    {
        for (var left = 1; left <= 4; left++)
        for (var right = 1; right <= 4; right++) RunToEnd(left, right, 42);
        for (ulong seed = 0; seed < 100; seed++) RunToEnd(2, 2, seed);
    }

    private static void RunToEnd(int left, int right, ulong seed)
    {
        var session = BasicBattleSession.Create($"run.{left}.{right}.{seed}", Setups(left, right),
            Enumerable.Range(0, 64).Select(i => new BattleCard($"card.{i}", BasicCardKind.Slash, "spade", i % 13 + 1)), ContentHash, seed);
        var steps = 0;
        while (session.State.Battle.PendingDecision is not null && steps++ < 2000)
        {
            var restored = Restore(session);
            var state = session.State.Battle;
            var actions = BasicBattle.GetLegalActions(state);
            var actor = state.Combatants.Single(u => u.EntityId == state.PendingDecision!.ActorId);
            var action = actions.FirstOrDefault(a => a.Kind == BattleActionKind.Slash
                && state.Combatants.Single(u => u.EntityId == a.TargetId).TeamId != actor.TeamId)
                ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Pass)
                ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Discard)
                ?? new(BattleActionKind.EndPlay);
            var command = Command(session, action);
            Assert.True(session.Submit(command).Accepted);
            Assert.True(restored.Submit(command).Accepted);
            Assert.Equal(Hash(session), Hash(restored));
            BattleValidation.Validate(session.State.Battle);
        }
        Assert.True(steps < 2000, $"步数保护触发：{left}对{right}，种子{seed}");
        Assert.Equal(BattlePhase.Finished, session.State.Battle.Phase);
        Assert.NotNull(session.State.Battle.WinnerTeamId);
    }

    private static BasicBattleSession Fixture(string[][] hands, int[]? hp = null, int leftCount = 1)
    {
        BattleCard[] cards = [Card("s0"), Card("s1"), Card("s2"), Card("s3"), Card("d0"), Card("d1"), Card("p0"), Card("p1"), Card("p2"), Card("s4"), Card("s5"), Card("s6")];
        var state = BasicBattle.Create("fixture", Setups(leftCount, hands.Length - leftCount), cards, 42, 0);
        var assigned = hands.SelectMany(h => h).ToHashSet(StringComparer.Ordinal);
        state = state with
        {
            Combatants = state.Combatants.Select((u, i) => u with
                { Hp = hp?[i] ?? 4, Hand = hands[i].Select(id => cards.Single(c => c.InstanceId == id)).ToImmutableArray() }).ToImmutableArray(),
            DrawPile = cards.Where(c => !assigned.Contains(c.InstanceId)).ToImmutableArray()
        };
        return new(new(BasicBattleSession.EngineVersion, ContentHash, state, []));
    }

    private static BattleCard Card(string id) => new(id, id[0] switch
        { 'd' => BasicCardKind.Dodge, 'p' => BasicCardKind.Peach, _ => BasicCardKind.Slash }, "heart", 1);
    private static IEnumerable<CombatantSetup> Setups(int left, int right)
        => Enumerable.Range(0, left + right).Select(i => new CombatantSetup($"u{i}", i < left ? "player" : "enemy", i < left ? "a" : "b"));
    private static BasicBattleSession Restore(BasicBattleSession session)
        => new(BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(session.State), ContentHash));
    private static string Hash(BasicBattleSession session) => BasicBattleSnapshotCodec.StateHash(session.State);
    private static BattleCommand Command(BasicBattleSession session, BattleAction action)
    {
        var decision = session.State.Battle.PendingDecision;
        return new($"command.{session.State.Battle.StateVersion}", session.State.Battle.StateVersion,
            decision?.ControllerId ?? "player", decision?.RequestId ?? "none", decision?.ActorId ?? "u0", action);
    }
    private static void Submit(BasicBattleSession session, BattleAction action)
        => Assert.True(session.Submit(Command(session, action)).Accepted);
    private static void RejectUnchanged(BasicBattleSession session, BattleCommand command, string expectedCode)
    {
        var before = Hash(session);
        Assert.Equal(expectedCode, session.Submit(command).Code);
        Assert.Equal(before, Hash(session));
    }
}
