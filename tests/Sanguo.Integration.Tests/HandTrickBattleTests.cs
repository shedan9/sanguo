using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public class HandTrickBattleTests
{
    private static readonly string ContentHash = new('e', 64);
    private static readonly BattleAction Pass = new(BattleActionKind.Pass);

    [Fact]
    public void DismantlementIgnoresDistanceWhileSnatchRequiresDistanceOne()
    {
        var session = Fixture([["r0", "t0"], ["s0"], ["s1"], ["s2"]]);
        Assert.Equal(2, BasicBattle.Distance(session.State.Battle, "u0", "u2"));
        var actions = BasicBattle.GetLegalActions(session.State.Battle);
        Assert.Contains(new(BattleActionKind.Dismantlement, "r0", "u2"), actions);
        Reject(session, new(BattleActionKind.Snatch, "t0", "u2"));
        Assert.Contains(new(BattleActionKind.Snatch, "t0", "u1"), actions);
        Assert.Contains(new(BattleActionKind.Snatch, "t0", "u3"), actions);
    }

    [Theory]
    [InlineData(BattleActionKind.Dismantlement, "r0")]
    [InlineData(BattleActionKind.Snatch, "t0")]
    public void TargetMustBeAnotherLivingUnitWithHandCards(BattleActionKind kind, string card)
    {
        var session = Fixture([[card], [], ["s0"]]);
        Reject(session, new(kind, card, "u0"));
        Reject(session, new(kind, card, "u1"));
        Reject(session, new(kind, card, "missing"));
        // 同队是合法目标，规则层不替玩家决定阵营策略。
        Assert.Contains(new(kind, card, "u2"), BasicBattle.GetLegalActions(session.State.Battle));
        var battle = session.State.Battle;
        session = new(session.State with { Battle = battle with
        {
            Combatants = battle.Combatants.SetItem(2, battle.Combatants[2] with { Alive = false, Hp = 0, Hand = [] }),
            DiscardPile = battle.Combatants[2].Hand
        } });
        Reject(session, new(kind, card, "u2"));
    }

    [Theory]
    [InlineData(BattleActionKind.Dismantlement, "r0", false)]
    [InlineData(BattleActionKind.Snatch, "t0", true)]
    public void OriginalUserSelectsOpaqueSlotAndMovesExactlyOneCard(BattleActionKind kind, string card, bool snatch)
    {
        var session = Fixture([[card], ["s0", "p0"]]);
        var random = session.State.Battle.Random;
        Submit(session, new(kind, card, "u1"));
        Assert.Equal(EffectKind.Nullification, session.State.Battle.Frames[^1].Kind);
        PassWindow(session);
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
        var view = BasicBattle.Observe(session.State.Battle, "c0");
        Assert.Equal(EffectKind.SelectHandCard, view.Response!.Kind);
        Assert.Empty(view.Combatants[1].VisibleHand);
        Assert.Equal(new int?[] { 0, 1 }, view.LegalActions.Select(a => a.HandIndex));
        Assert.All(view.LegalActions, a =>
        {
            Assert.Null(a.CardId);
            Assert.Equal(card, a.ResponseToCardId);
            Assert.Equal("u1", a.TargetId);
        });
        Assert.Empty(BasicBattle.Observe(session.State.Battle, "c1").LegalActions);
        Assert.Equal(card, Assert.Single(session.State.Battle.Processing).InstanceId);
        Submit(session, new(BattleActionKind.SelectHandCard, TargetId: "u1", ResponseToCardId: card, HandIndex: 1));
        Assert.Equal("s0", Assert.Single(session.State.Battle.Combatants[1].Hand).InstanceId);
        Assert.Equal(snatch ? new[] { "p0" } : [], session.State.Battle.Combatants[0].Hand.Select(c => c.InstanceId));
        Assert.Equal(snatch ? new[] { card } : new[] { "p0", card }, session.State.Battle.DiscardPile.Select(c => c.InstanceId));
        Assert.Empty(session.State.Battle.Processing);
        Assert.Empty(session.State.Battle.Frames);
        Assert.Equal(random, session.State.Battle.Random);
        var moved = Assert.Single(session.State.Battle.Events.Where(e => e.Code == (snatch ? "card.snatched" : "card.dismantled")));
        Assert.Equal(("u0", "u1", "p0", card), (moved.ActorId, moved.TargetId, moved.CardId, moved.RelatedCardId));
        Assert.Equal(0, session.State.Battle.SlashesUsed);
    }

    [Fact]
    public void HiddenHandPermutationDoesNotChangeLegalActionsOrPublicView()
    {
        var session = Fixture([["t0"], ["s0", "p0"]]);
        Submit(session, new(BattleActionKind.Snatch, "t0", "u1"));
        PassWindow(session);
        var battle = session.State.Battle;
        var swapped = battle with { Combatants = battle.Combatants.SetItem(1,
            battle.Combatants[1] with { Hand = battle.Combatants[1].Hand.Reverse().ToImmutableArray() }) };
        Assert.Equal(JsonSerializer.Serialize(BasicBattle.Observe(battle, "c0")),
            JsonSerializer.Serialize(BasicBattle.Observe(swapped, "c0")));
        Assert.Empty(BasicBattle.Observe(battle, "spectator").LegalActions);
        Assert.All(BasicBattle.Observe(battle, "spectator").Combatants, u => Assert.Empty(u.VisibleHand));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SnatchedCardIsPrivateAndImmediatelyUsableWithinExistingSlashAllowance(int slashesUsed)
    {
        var session = Fixture([["t0"], ["s0"]]);
        session = new(session.State with { Battle = session.State.Battle with { SlashesUsed = slashesUsed } });
        Submit(session, new(BattleActionKind.Snatch, "t0", "u1"));
        PassWindow(session);
        var command = Command(session, BasicBattle.GetLegalActions(session.State.Battle)[0]);
        Submit(session, command.Action);
        var owner = BasicBattle.Observe(session.State.Battle, "c0");
        Assert.Equal("s0", Assert.Single(owner.Combatants[0].VisibleHand).InstanceId);
        Assert.Empty(BasicBattle.Observe(session.State.Battle, "c1").Combatants[0].VisibleHand);
        Assert.DoesNotContain(owner.DiscardPile, c => c.InstanceId == "s0");
        Assert.Equal(slashesUsed == 0, owner.LegalActions.Any(a => a.Kind == BattleActionKind.Slash && a.CardId == "s0"));
        var before = Hash(session);
        Assert.Equal("state.stale", session.Submit(command with { CommandId = "late.choice" }).Code);
        Assert.Equal(before, Hash(session));
        if (slashesUsed == 0) Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
    }

    [Theory]
    [InlineData(-1, "u1", "t0", null)]
    [InlineData(2, "u1", "t0", null)]
    [InlineData(null, "u1", "t0", null)]
    [InlineData(0, "u0", "t0", null)]
    [InlineData(0, "u1", "wrong", null)]
    [InlineData(0, "u1", "t0", "s0")]
    public void SelectionRejectsInvalidSlotsTargetsAndHiddenCardIds(int? index, string target, string responseTo, string? cardId)
    {
        var session = Fixture([["t0"], ["s0", "p0"]]);
        Reject(session, new(BattleActionKind.SelectHandCard, TargetId: "u1", ResponseToCardId: "t0", HandIndex: 0));
        Submit(session, new(BattleActionKind.Snatch, "t0", "u1"));
        Reject(session, new(BattleActionKind.SelectHandCard, TargetId: "u1", ResponseToCardId: "t0", HandIndex: 0));
        PassWindow(session);
        Reject(session, new(BattleActionKind.SelectHandCard, cardId, target, responseTo, index));
        Reject(session, Pass);
        Reject(session, new(BattleActionKind.EndPlay));
    }

    [Theory]
    [InlineData(0, BattleActionKind.Dismantlement, "r0")]
    [InlineData(1, BattleActionKind.Dismantlement, "r0")]
    [InlineData(2, BattleActionKind.Dismantlement, "r0")]
    [InlineData(0, BattleActionKind.Snatch, "t0")]
    [InlineData(1, BattleActionKind.Snatch, "t0")]
    [InlineData(2, BattleActionKind.Snatch, "t0")]
    public void CounterParityDeterminesWhetherHandSelectionOpens(int depth, BattleActionKind kind, string card)
    {
        var session = Fixture([[card, "n0", "n1"], ["s0"]]);
        Submit(session, new(kind, card, "u1"));
        for (var i = 0; i < depth; i++)
            Submit(session, new(BattleActionKind.Nullify, $"n{i}", "u0", i == 0 ? card : $"n{i - 1}"));
        PassWindow(session);
        Assert.Equal(depth % 2 == 0, session.State.Battle.Frames.Any(f => f.Kind == EffectKind.SelectHandCard));
        if (depth % 2 == 1)
        {
            Assert.Empty(session.State.Battle.Processing);
            Assert.Equal("s0", Assert.Single(session.State.Battle.Combatants[1].Hand).InstanceId);
        }
        else Submit(session, BasicBattle.GetLegalActions(session.State.Battle)[0]);
    }

    [Theory]
    [InlineData(BattleActionKind.Dismantlement, "r0")]
    [InlineData(BattleActionKind.Snatch, "t0")]
    public void TargetSpendingLastHandCardInEvenCounterChainSkipsEmptySelection(BattleActionKind kind, string card)
    {
        var session = Fixture([[card, "n1"], ["n0"]]);
        Submit(session, new(kind, card, "u1"));
        Submit(session, Pass);
        Submit(session, new(BattleActionKind.Nullify, "n0", "u0", card));
        Submit(session, new(BattleActionKind.Nullify, "n1", "u1", "n0"));
        PassWindow(session);
        Assert.Empty(session.State.Battle.Frames);
        Assert.Empty(session.State.Battle.Processing);
        Assert.Empty(session.State.Battle.Combatants[1].Hand);
        Assert.Contains(session.State.Battle.Events, e => e.Code == "trick.no_target_cards");
        Assert.Equal(new[] { "n1", "n0", card }, session.State.Battle.DiscardPile.Select(c => c.InstanceId));
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
    }

    [Fact]
    public void SharedControllerStillMustUseOriginalActorAndCurrentRequest()
    {
        var session = Fixture([["t0"], ["s0"]]);
        var battle = session.State.Battle;
        session = new(session.State with { Battle = battle with { Combatants = battle.Combatants.SetItem(1,
            battle.Combatants[1] with { ControllerId = "c0" }) } });
        Submit(session, new(BattleActionKind.Snatch, "t0", "u1"));
        PassWindow(session);
        var command = Command(session, BasicBattle.GetLegalActions(session.State.Battle)[0]);
        var hash = Hash(session);
        Assert.Equal("actor.denied", session.Submit(command with { ActorId = "u1" }).Code);
        Assert.Equal("decision.stale", session.Submit(command with { RequestId = "old" }).Code);
        Assert.Equal(hash, Hash(session));
        Submit(session, command.Action);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("effect")]
    [InlineData("cursor")]
    [InlineData("amount")]
    [InlineData("empty")]
    [InlineData("request")]
    public void SnapshotRejectsMalformedSelectionEvenWithValidChecksum(string fault)
    {
        var session = Fixture([["t0"], ["s0"]]);
        Submit(session, new(BattleActionKind.Snatch, "t0", "u1"));
        PassWindow(session);
        var envelope = JsonNode.Parse(BasicBattleSnapshotCodec.Encode(session.State))!;
        var payload = JsonNode.Parse(envelope["payload"]!.GetValue<string>())!;
        var battle = payload["battle"]!;
        var frame = battle["frames"]![1]!;
        switch (fault)
        {
            case "source": frame["sourceId"] = "u1"; break;
            case "target": frame["targetId"] = "u0"; break;
            case "effect": frame["kind"] = "duel"; break;
            case "cursor": frame["cursor"] = 1; break;
            case "amount": frame["amount"] = 2; break;
            case "empty":
                var hand = battle["combatants"]![1]!["hand"]!;
                battle["discardPile"]!.AsArray().Add(hand[0]!.DeepClone());
                hand.AsArray().Clear();
                break;
            case "request": battle["pendingDecision"]!["actorId"] = "u1"; break;
        }
        var text = payload.ToJsonString();
        envelope["payload"] = text;
        envelope["checksum"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(
            Encoding.UTF8.GetBytes(envelope.ToJsonString()), ContentHash)).Code);
    }

    [Fact]
    public void PreviousFormatTwoIsExplicitlyIncompatible()
    {
        var session = Fixture([["t0"], ["s0"]]);
        var envelope = JsonNode.Parse(BasicBattleSnapshotCodec.Encode(session.State))!;
        envelope["formatVersion"] = 2;
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(
            Encoding.UTF8.GetBytes(envelope.ToJsonString()), ContentHash)).Code);
    }

    [Fact]
    public void EightCardBattlesAcross100SeedsRestoreEveryDecisionAndConserveCards()
    {
        var formations = new HashSet<(int, int)>();
        var moved = new HashSet<string>();
        for (ulong seed = 0; seed < 100; seed++)
        {
            var left = (int)(seed % 4) + 1;
            var right = (int)(seed / 4 % 4) + 1;
            formations.Add((left, right));
            var session = BasicBattleSession.Create($"handtricks.{seed}", Enumerable.Range(0, left + right)
                .Select(i => new CombatantSetup($"u{i}", $"c{i}", i < left ? "a" : "b")),
                Enumerable.Range(0, 64).Select(i => new BattleCard($"card.{i}", (i % 16) switch
                {
                    0 or 1 => BasicCardKind.Dodge, 2 => BasicCardKind.Peach, 3 or 4 => BasicCardKind.Duel,
                    5 => BasicCardKind.ExNihilo, 6 or 7 => BasicCardKind.Nullification,
                    8 => BasicCardKind.Dismantlement, 9 => BasicCardKind.Snatch, _ => BasicCardKind.Slash
                }, "spade", i % 13 + 1)), ContentHash, seed);
            var steps = 0;
            while (session.State.Battle.PendingDecision is { } d && steps++ < 4000)
            {
                var view = BasicBattle.Observe(session.State.Battle, d.ControllerId);
                var actions = view.LegalActions;
                var actor = view.Combatants.Single(u => u.EntityId == d.ActorId);
                bool Enemy(string id) => view.Combatants.Single(u => u.EntityId == id).TeamId != actor.TeamId;
                var action = actions.FirstOrDefault(a => a.Kind is BattleActionKind.Dodge or BattleActionKind.RespondSlash or BattleActionKind.SelectHandCard)
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Nullify && view.Response is { } r && Enemy(r.SourceId))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Peach && !Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.ExNihilo)
                    ?? actions.FirstOrDefault(a => a.Kind is BattleActionKind.Snatch or BattleActionKind.Dismantlement && Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind is BattleActionKind.Duel or BattleActionKind.Slash && Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind is BattleActionKind.Pass or BattleActionKind.Discard)
                    ?? new(BattleActionKind.EndPlay);
                Submit(session, action);
                BattleValidation.Validate(session.State.Battle);
            }
            Assert.True(steps < 4000, $"八牌场景步数保护触发：种子{seed}，{left}对{right}");
            Assert.Equal(BattlePhase.Finished, session.State.Battle.Phase);
            Assert.NotNull(session.State.Battle.WinnerTeamId);
            moved.UnionWith(session.State.Battle.Events.Select(e => e.Code));
        }
        Assert.Equal(16, formations.Count);
        Assert.Contains("card.snatched", moved);
        Assert.Contains("card.dismantled", moved);
    }

    private static BasicBattleSession Fixture(string[][] hands)
    {
        var names = hands.SelectMany(h => h).Concat(Enumerable.Range(0, 20).Select(i => $"s{i}")).Distinct().ToArray();
        var cards = names.Select(id => new BattleCard(id, id[0] switch
        {
            'r' => BasicCardKind.Dismantlement, 't' => BasicCardKind.Snatch,
            'n' => BasicCardKind.Nullification, 'p' => BasicCardKind.Peach, _ => BasicCardKind.Slash
        }, "spade", 1)).ToArray();
        var state = BasicBattle.Create("handtricks", Enumerable.Range(0, hands.Length)
            .Select(i => new CombatantSetup($"u{i}", $"c{i}", i % 2 == 0 ? "a" : "b")), cards, 42, 0);
        var assigned = hands.SelectMany(h => h).ToHashSet();
        state = state with
        {
            Combatants = state.Combatants.Select((u, i) => u with
                { Hand = hands[i].Select(id => cards.Single(c => c.InstanceId == id)).ToImmutableArray() }).ToImmutableArray(),
            DrawPile = cards.Where(c => !assigned.Contains(c.InstanceId)).ToImmutableArray()
        };
        return new(new(BasicBattleSession.EngineVersion, ContentHash, state, []));
    }

    private static void PassWindow(BasicBattleSession session)
    {
        while (session.State.Battle.Frames.LastOrDefault()?.Kind == EffectKind.Nullification) Submit(session, Pass);
    }
    private static BattleCommand Command(BasicBattleSession session, BattleAction action)
    {
        var battle = session.State.Battle;
        var d = battle.PendingDecision!;
        return new($"handtricks.command.{battle.StateVersion}", battle.StateVersion, d.ControllerId, d.RequestId, d.ActorId, action);
    }
    private static void Submit(BasicBattleSession session, BattleAction action)
    {
        var restored = new BasicBattleSession(BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(session.State), ContentHash));
        var command = Command(session, action);
        Assert.True(session.Submit(command).Accepted);
        Assert.True(restored.Submit(command).Accepted);
        Assert.Equal(Hash(session), Hash(restored));
        Assert.True(restored.Submit(command).Duplicate);
        Assert.Equal(Hash(session), Hash(restored));
    }
    private static void Reject(BasicBattleSession session, BattleAction action)
    {
        var before = Hash(session);
        Assert.Equal("action.illegal", session.Submit(Command(session, action)).Code);
        Assert.Equal(before, Hash(session));
    }
    private static string Hash(BasicBattleSession session) => BasicBattleSnapshotCodec.StateHash(session.State);
}
