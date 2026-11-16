using System.Collections.Immutable;
using System.Text.Json;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class BattleProcessingTests
{
    [Fact]
    public void ConvertedSlashKeepsPhysicalFaceAndPublicSourceTarget()
    {
        var client = new BattleInteraction(DevelopmentBattleScenario.Create());
        var action = client.View.LegalActions.First(a => a.SkillId == BattleSkills.Wusheng);
        var original = client.View.Combatants.SelectMany(u => u.VisibleHand).Single(c => c.InstanceId == action.CardId);
        Assert.True(client.Select(action)); Assert.True(client.Confirm().Accepted);
        var card = Assert.Single(BattleProcessingPresentation.Observe(client.View, client.ReadLog()));
        Assert.Equal(original, card.Card); Assert.Equal(BasicCardKind.Slash, card.EffectiveKind);
        Assert.Equal(BattleSkills.Wusheng, card.SkillId);
        Assert.Equal("development.guanyu", card.SourceId); Assert.Equal(action.TargetId, card.TargetId);
        Assert.True(card.AwaitingResponse); Assert.False(card.OriginalTrick);
    }

    [Fact]
    public void CounterChainDistinguishesOriginalTrickFromCurrentResponseAndLinksEveryCounter()
    {
        var view = BasicBattle.Observe(DevelopmentBattleScenario.Create().State.Battle, "player");
        BattleCard trick = new("trick", BasicCardKind.Duel, "spade", 1);
        BattleCard counter = new("counter", BasicCardKind.Nullification, "club", 2);
        BattleCard recounter = new("recounter", BasicCardKind.Nullification, "heart", 3);
        view = view with { Processing = [trick, counter, recounter], Response = new(EffectKind.Nullification,
            "a", "b", recounter.InstanceId, 2, Trick: new("a", "b", trick.InstanceId, trick.Kind)) };
        ImmutableArray<PublicBattleEvent> log = [new(1, "duel.used", "a", "b", trick, 0, null),
            new(2, "nullification.used", "b", "a", counter, 0, trick.InstanceId),
            new(3, "nullification.used", "a", "b", recounter, 0, counter.InstanceId)];
        var cards = BattleProcessingPresentation.Observe(view, log);
        Assert.Equal(trick, Assert.Single(cards.Where(c => c.OriginalTrick)).Card);
        Assert.Equal(recounter, Assert.Single(cards.Where(c => c.AwaitingResponse)).Card);
        Assert.Null(cards[0].RespondsToCardId);
        Assert.Equal(trick.InstanceId, cards[1].RespondsToCardId);
        Assert.Equal(counter.InstanceId, cards[2].RespondsToCardId);
    }

    [Theory]
    [InlineData("slash.used")]
    [InlineData("duel.slash_responded")]
    public void ReusedPhysicalCardDoesNotCarryAnEarlierConversion(string useCode)
    {
        var view = BasicBattle.Observe(DevelopmentBattleScenario.Create().State.Battle, "player");
        BattleCard card = new("red", BasicCardKind.ExNihilo, "heart", 5);
        view = view with { Processing = [card] };
        ImmutableArray<PublicBattleEvent> log = [new(1, "skill.used", "a", "b", card, 0, BattleSkills.Wusheng),
            new(2, useCode, "a", "b", card, 0, null), new(3, "card.discarded", "a", null, card, 0, null),
            new(4, "exnihilo.used", "b", "b", card, 0, null)];
        var shown = Assert.Single(BattleProcessingPresentation.Observe(view, log));
        Assert.Null(shown.SkillId); Assert.Equal(card.Kind, shown.EffectiveKind); Assert.Equal("b", shown.SourceId);
    }

    [Fact]
    public void HistoryAndUnknownEventsCannotIntroduceCardsOutsideThePublicProcessingZone()
    {
        var state = DevelopmentBattleScenario.Create().State.Battle;
        var view = BasicBattle.Observe(state, "player");
        var secret = state.DrawPile[0];
        ImmutableArray<PublicBattleEvent> log = [new(1, "future.private", "secret", null, secret, 0, null),
            new(2, "slash.used", "old", "old-target", secret, 0, null)];
        Assert.Empty(BattleProcessingPresentation.Observe(view, log));
        view = view with { Processing = [secret] };
        var shown = Assert.Single(BattleProcessingPresentation.Observe(view, [log[0]]));
        Assert.Null(shown.SourceId); Assert.Null(shown.TargetId); Assert.Null(shown.SkillId);
    }

    [Fact]
    public void All16BattlesRebuildTheSameProcessingCardsAtEveryRestoredDecision()
    {
        var observed = new HashSet<EffectKind>();
        for (var players = 1; players <= 4; players++)
        for (var opponents = 1; opponents <= 4; opponents++)
        {
            var session = DevelopmentBattleScenario.Create(42, players, opponents);
            var steps = 0;
            while (session.State.Battle.PendingDecision is { } decision)
            {
                Assert.True(++steps <= 4000);
                var view = BasicBattle.Observe(session.State.Battle, "player");
                var hash = BasicBattleSnapshotCodec.StateHash(session.State);
                var cards = BattleProcessingPresentation.Observe(view, PublicBattleLog.Observe(session.State.Battle));
                Assert.Equal(view.Processing.ToArray(), cards.Select(c => c.Card).ToArray());
                if (view.Response is { } response) observed.Add(response.Kind);
                var restored = BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(session.State), session.State.ContentHash);
                Assert.Equal(cards.ToArray(), BattleProcessingPresentation.Observe(BasicBattle.Observe(restored.Battle, "player"),
                    PublicBattleLog.Observe(restored.Battle)).ToArray());
                var hiddenChanged = session.State.Battle with
                {
                    DrawPile = session.State.Battle.DrawPile.Reverse().ToImmutableArray(),
                    Combatants = session.State.Battle.Combatants.Select(u => u.ControllerId == "player" ? u : u with
                        { Hand = u.Hand.Select(c => c with { Kind = BasicCardKind.Peach }).ToImmutableArray() }).ToImmutableArray()
                };
                Assert.Equal(JsonSerializer.Serialize(cards), JsonSerializer.Serialize(BattleProcessingPresentation.Observe(
                    BasicBattle.Observe(hiddenChanged, "player"), PublicBattleLog.Observe(hiddenChanged))));
                Assert.Equal(hash, BasicBattleSnapshotCodec.StateHash(session.State));
                var aiView = BasicBattle.Observe(session.State.Battle, decision.ControllerId);
                Assert.True(session.Submit(new($"processing.{aiView.StateVersion}", aiView.StateVersion, decision.ControllerId,
                    decision.RequestId, decision.ActorId, BasicBattleAi.Choose(aiView)!.Action)).Accepted);
            }
            Assert.Empty(BattleProcessingPresentation.Observe(BasicBattle.Observe(session.State.Battle, "player"), PublicBattleLog.Observe(session.State.Battle)));
        }
        Assert.Contains(EffectKind.Nullification, observed); Assert.Contains(EffectKind.Dodge, observed);
        Assert.Contains(EffectKind.Duel, observed); Assert.Contains(EffectKind.Rescue, observed);
        Assert.Contains(EffectKind.InvokeJianxiong, observed); Assert.Contains(EffectKind.SelectHandCard, observed);
    }
}
