using System.Collections.Immutable;
using System.Text.Json;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class BattleTableTests
{
    [Fact]
    public void All16ScenariosHaveUniqueHashesAndFinishWithRecoverablePublicLogs()
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        for (var playerCount = 1; playerCount <= 4; playerCount++)
        for (var opponentCount = 1; opponentCount <= 4; opponentCount++)
        {
            var session = DevelopmentBattleScenario.Create(42, playerCount, opponentCount);
            Assert.True(hashes.Add(session.State.ContentHash));
            Assert.Equal(playerCount, session.State.Battle.Combatants.Count(u => u.TeamId == "a"));
            Assert.Equal(opponentCount, session.State.Battle.Combatants.Count(u => u.TeamId == "b"));
            var steps = 0;
            while (session.State.Battle.PendingDecision is { } decision)
            {
                Assert.True(++steps < 4000, $"{playerCount}对{opponentCount}触发步数保护。");
                var view = BasicBattle.Observe(session.State.Battle, decision.ControllerId);
                var action = BasicBattleAi.Choose(view)!.Action;
                Assert.True(session.Submit(new($"client.command.{view.StateVersion}", view.StateVersion,
                    decision.ControllerId, decision.RequestId, decision.ActorId, action)).Accepted);
            }
            Assert.NotNull(session.State.Battle.WinnerTeamId);
            var restored = BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(session.State), session.State.ContentHash);
            Assert.Equal(session.State.Battle.Events.Length, PublicBattleLog.Observe(restored.Battle).Length);
            Assert.Equal(PublicBattleLog.Observe(session.State.Battle).ToArray(), PublicBattleLog.Observe(restored.Battle).ToArray());
            if (playerCount == 2 && opponentCount == 2)
            {
                Assert.Equal(DevelopmentBattleScenario.ContentHash, session.State.ContentHash);
                Assert.Equal("b8f0850af706d77985cf1b56fd130213d47284457c53b59482d03f8b16474398", BasicBattleSnapshotCodec.StateHash(session.State));
            }
        }
    }

    [Theory]
    [InlineData(0, 2)] [InlineData(5, 2)] [InlineData(2, 0)] [InlineData(2, 5)]
    public void ScenarioRejectsInvalidCounts(int players, int opponents)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DevelopmentBattleScenario.Create(42, players, opponents));

    [Fact]
    public void SeatRotationCoversAllUnitsAndRetainsDeadSeatsWithoutChangingRules()
    {
        for (var players = 1; players <= 4; players++)
        for (var opponents = 1; opponents <= 4; opponents++)
        {
            var session = DevelopmentBattleScenario.Create(42, players, opponents);
            var view = BasicBattle.Observe(session.State.Battle, "player");
            var before = BasicBattleSnapshotCodec.StateHash(session.State);
            foreach (var focus in view.Combatants)
            {
                var layout = BattleSeatLayout.Arrange(view, focus.EntityId);
                Assert.Equal(view.Combatants.Length, layout.Length);
                Assert.Equal(focus.EntityId, layout[0].EntityId);
                Assert.Equal((.5, .82), (layout[0].X, layout[0].Y));
                Assert.Equal(layout.Length, layout.Select(p => (p.X, p.Y)).Distinct().Count());
                Assert.All(layout, p =>
                {
                    Assert.InRange(p.X, .1, .9); Assert.InRange(p.Y, .18, .82);
                    Assert.Equal(view.Combatants.Single(u => u.EntityId == p.EntityId).SeatId, p.SeatId);
                });
                var deadView = view with { Combatants = view.Combatants.SetItem(0, view.Combatants[0] with { Alive = false, Hp = 0 }) };
                Assert.Equal(layout.ToArray(), BattleSeatLayout.Arrange(deadView, focus.EntityId).ToArray());
            }
            Assert.Equal(before, BasicBattleSnapshotCodec.StateHash(session.State));
            Assert.Throws<ArgumentException>(() => BattleSeatLayout.Arrange(view, "unknown"));
        }
    }

    [Theory]
    [InlineData("card.drawn")]
    [InlineData("card.snatched")]
    public void HiddenTransfersNeverExposeCardIdsOrFacesEvenAfterCardsBecomePublic(string code)
    {
        var state = DevelopmentBattleScenario.Create().State.Battle;
        var secret = state.DrawPile[0];
        state = state with { Events = [new(1, code, state.Combatants[0].EntityId, state.Combatants[1].EntityId,
            secret.InstanceId, 99, secret.InstanceId), new(2, "card.discarded", state.Combatants[0].EntityId, CardId: secret.InstanceId)] };
        var log = PublicBattleLog.Observe(state);
        Assert.Null(log[0].Card); Assert.Null(log[0].RelatedCardId); Assert.Null(log[0].RelatedCard); Assert.Equal(1, log[0].Amount);
        Assert.Equal(secret, log[1].Card);
    }

    [Fact]
    public void UnknownEventsAreOmittedAndPagingDoesNotDuplicateEntries()
    {
        var state = DevelopmentBattleScenario.Create().State.Battle;
        state = state with { Events = state.Events.Add(new(state.Events.Length + 1, "future.private-event", "secret", CardId: "secret.card")) };
        var all = PublicBattleLog.Observe(state);
        Assert.DoesNotContain(all, e => e.Code == "future.private-event");
        var cursor = all[all.Length / 2].Sequence;
        Assert.Equal(all, all.Where(e => e.Sequence <= cursor).Concat(PublicBattleLog.Observe(state, cursor)));
        Assert.Empty(PublicBattleLog.Observe(state, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => PublicBattleLog.Observe(state, -1));
    }

    [Fact]
    public void ChangingHiddenCardsAndFutureOrderCannotChangePublicInitialLog()
    {
        var first = DevelopmentBattleScenario.Create().State.Battle;
        var second = first with
        {
            DrawPile = first.DrawPile.Reverse().Select(c => c with { Kind = BasicCardKind.Peach }).ToImmutableArray(),
            Combatants = first.Combatants.Select(u => u.ControllerId == "player" ? u : u with
                { Hand = u.Hand.Select(c => c with { Kind = BasicCardKind.Peach }).ToImmutableArray() }).ToImmutableArray()
        };
        Assert.Equal(JsonSerializer.Serialize(PublicBattleLog.Observe(first)), JsonSerializer.Serialize(PublicBattleLog.Observe(second)));
    }

    [Fact]
    public void PublicPlayedCardAndSkillContextRemainVisibleAfterSnapshotRestore()
    {
        var session = DevelopmentBattleScenario.Create();
        var client = new BattleInteraction(session);
        var action = client.View.LegalActions.First(a => a.Kind == BattleActionKind.Slash && a.SkillId == BattleSkills.Wusheng);
        client.Select(action);
        Assert.True(client.Confirm().Accepted);
        var played = Assert.Single(client.ReadLog().Where(e => e.Code == "slash.used"));
        Assert.Equal(action.CardId, played.Card!.InstanceId);
        Assert.Equal(action.TargetId, played.TargetId);
        Assert.Equal(BattleSkills.Wusheng, Assert.Single(client.ReadLog().Where(e => e.Code == "skill.used")).RelatedCardId);
        var before = client.ReadLog();
        client.Restore(BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(client.Snapshot), client.Snapshot.ContentHash));
        Assert.Equal(before.ToArray(), client.ReadLog().ToArray());
    }

    [Fact]
    public void ExternalSessionAdvanceCannotConfirmAnOldPreviewAgainstANewRequest()
    {
        var session = DevelopmentBattleScenario.Create();
        var client = new BattleInteraction(session);
        client.Select(new(BattleActionKind.EndPlay));
        var view = client.View;
        var decision = view.PendingDecision!;
        Assert.True(session.Submit(new("external", view.StateVersion, decision.ControllerId, decision.RequestId,
            decision.ActorId, new(BattleActionKind.EndPlay))).Accepted);
        var before = BasicBattleSnapshotCodec.StateHash(session.State);
        Assert.Equal("selection.stale", client.Confirm().Code);
        Assert.Equal(before, BasicBattleSnapshotCodec.StateHash(session.State));
        Assert.Null(client.Selection);
    }
}
