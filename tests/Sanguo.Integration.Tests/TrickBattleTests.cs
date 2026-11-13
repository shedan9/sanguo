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

public class TrickBattleTests
{
    private static readonly string ContentHash = new('d', 64);
    private static readonly BattleAction Pass = new(BattleActionKind.Pass);

    [Fact]
    public void DuelIgnoresAttackRangeAndOpensOneGlobalNullificationWindow()
    {
        var session = Fixture([["j0"], [], [], []]);
        Assert.Equal(2, BasicBattle.Distance(session.State.Battle, "u0", "u2"));
        Submit(session, new(BattleActionKind.Duel, "j0", "u2"));
        Assert.Equal(EffectKind.Nullification, session.State.Battle.Frames[^1].Kind);
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal($"u{i}", session.State.Battle.PendingDecision!.ActorId);
            Submit(session, Pass);
        }
        Assert.Equal(EffectKind.Duel, session.State.Battle.Frames[^1].Kind);
        Assert.Equal("u2", session.State.Battle.PendingDecision!.ActorId);
        Assert.Equal("j0", Assert.Single(session.State.Battle.Processing).InstanceId);
    }

    [Fact]
    public void DuelAlternatesSlashResponsesAndKeepsActiveSlashAllowance()
    {
        var session = Fixture([["j0", "s0", "s2"], ["s1"]]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        Submit(session, new(BattleActionKind.RespondSlash, "s1", "u0"));
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
        session = Restore(session);
        Submit(session, new(BattleActionKind.RespondSlash, "s0", "u1"));
        Assert.Equal("u1", session.State.Battle.PendingDecision!.ActorId);
        Submit(session, Pass);
        Assert.Equal(3, session.State.Battle.Combatants[1].Hp);
        Assert.Equal(0, session.State.Battle.SlashesUsed);
        Assert.Contains(BasicBattle.GetLegalActions(session.State.Battle), a => a.Kind == BattleActionKind.Slash && a.CardId == "s2");
        Assert.Equal(new[] { "s1", "s0", "j0" }, session.State.Battle.DiscardPile.Select(c => c.InstanceId));
        Assert.Equal(1, session.State.Battle.Events.Count(e => e.Code == "nullification.window_closed"));
        var damage = Assert.Single(session.State.Battle.Events.Where(e => e.Code == "damage.dealt"));
        Assert.Equal(("u0", "u1", "j0"), (damage.ActorId, damage.TargetId, damage.CardId));
    }

    [Fact]
    public void DuelCanKillInitiatorAndContinueAtNextLivingSeat()
    {
        var session = Fixture([["j0"], ["s0"], []], hp: [1, 4, 4]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        Submit(session, new(BattleActionKind.RespondSlash, "s0", "u0"));
        Submit(session, Pass);
        Assert.Equal("u0", session.State.Battle.Frames[^1].TargetId);
        Assert.Equal(0, session.State.Battle.Combatants[0].Hp);
        for (var i = 0; i < 3; i++) Submit(session, Pass);
        Assert.False(session.State.Battle.Combatants[0].Alive);
        Assert.Null(session.State.Battle.WinnerTeamId);
        Assert.Equal("u1", session.State.Battle.PendingDecision!.ActorId);
        Assert.Equal(2, session.State.Battle.TurnNumber);
        Assert.Empty(session.State.Battle.Processing);
        var damage = Assert.Single(session.State.Battle.Events.Where(e => e.Code == "damage.dealt"));
        Assert.Equal(("u1", "u0", "j0"), (damage.ActorId, damage.TargetId, damage.CardId));
    }

    [Fact]
    public void DuelResponseDoesNotResetAlreadySpentActiveSlashAllowance()
    {
        var session = Fixture([["s0", "s1", "s3", "j0"], ["s2"]]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        Submit(session, new(BattleActionKind.RespondSlash, "s2", "u0"));
        Submit(session, new(BattleActionKind.RespondSlash, "s1", "u1"));
        Submit(session, Pass);
        Assert.Equal(1, session.State.Battle.SlashesUsed);
        RejectUnchanged(session, new(BattleActionKind.Slash, "s3", "u1"));
    }

    [Fact]
    public void InitiatorCanSaveSelfDuringDuelAndResumeOriginalPlayPhase()
    {
        var session = Fixture([["j0", "p0"], ["s0"]], hp: [1, 4]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        Submit(session, new(BattleActionKind.RespondSlash, "s0", "u0"));
        Submit(session, Pass);
        session = Restore(session);
        Submit(session, new(BattleActionKind.Peach, "p0", "u0"));
        Assert.Equal(1, session.State.Battle.Combatants[0].Hp);
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
        Assert.Equal(1, session.State.Battle.TurnNumber);
        Assert.Empty(session.State.Battle.Frames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CounterChainUsesLatestCardAndParityWithRecoveryAtEveryStep(int depth)
    {
        var hand = new[] { "x0" }.Concat(Enumerable.Range(0, depth).Select(i => $"n{i}")).ToArray();
        var session = Fixture([hand, []]);
        Submit(session, new(BattleActionKind.ExNihilo, "x0", "u0"));
        for (var i = 0; i < depth; i++)
        {
            var frame = session.State.Battle.Frames[^1];
            Assert.Equal(i == 0 ? "x0" : $"n{i - 1}", frame.CardId);
            Submit(session, new(BattleActionKind.Nullify, $"n{i}", "u0", frame.CardId));
            Assert.Equal(i + 1, session.State.Battle.Frames[^1].ChainDepth);
            Assert.Equal(i + 2, session.State.Battle.Processing.Length);
            session = Restore(session);
        }
        PassWindow(session);
        Assert.Empty(session.State.Battle.Processing);
        Assert.Empty(session.State.Battle.Frames);
        Assert.Equal(depth % 2 == 0 ? 2 : 0, session.State.Battle.Combatants[0].Hand.Length);
        Assert.Equal(Enumerable.Range(0, depth).Reverse().Select(i => $"n{i}").Append("x0"),
            session.State.Battle.DiscardPile.Select(c => c.InstanceId));
        Assert.Equal(depth % 2, session.State.Battle.Events.Count(e => e.Code == "trick.negated"));
        foreach (var e in session.State.Battle.Events.Where(e => e.Code == "nullification.used"))
        {
            var number = int.Parse(e.CardId![1..]);
            Assert.Equal(number == 0 ? "x0" : $"n{number - 1}", e.RelatedCardId);
        }
    }

    [Fact]
    public void CounterResetsAllPassesAndLetsEarlierPasserCounterAgain()
    {
        var session = Fixture([["x0", "n1"], ["n0"]]);
        Submit(session, new(BattleActionKind.ExNihilo, "x0", "u0"));
        Submit(session, Pass);
        Assert.Equal("u1", session.State.Battle.PendingDecision!.ActorId);
        Submit(session, new(BattleActionKind.Nullify, "n0", "u0", "x0"));
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
        Assert.Equal(0, session.State.Battle.Frames[^1].Cursor);
        RejectUnchanged(session, new(BattleActionKind.Nullify, "n1", "u1", "x0"));
        Submit(session, new(BattleActionKind.Nullify, "n1", "u1", "n0"));
        PassWindow(session);
        Assert.Equal(2, session.State.Battle.Combatants[0].Hand.Length);
    }

    [Fact]
    public void NegatedDuelDoesNotAskForSlashDealDamageOrConsumeSlashLimit()
    {
        var session = Fixture([["j0", "n0"], []]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        Submit(session, new(BattleActionKind.Nullify, "n0", "u0", "j0"));
        PassWindow(session);
        Assert.Empty(session.State.Battle.Frames);
        Assert.All(session.State.Battle.Combatants, u => Assert.Equal(4, u.Hp));
        Assert.Equal(0, session.State.Battle.SlashesUsed);
        Assert.DoesNotContain(session.State.Battle.Events, e => e.Code == "damage.dealt");
    }

    [Theory]
    [InlineData(BattleActionKind.Duel, "j0", "u0", null)]
    [InlineData(BattleActionKind.ExNihilo, "x0", "u1", null)]
    [InlineData(BattleActionKind.Nullify, "n0", "u1", "x0")]
    [InlineData(BattleActionKind.RespondSlash, "s0", "u1", null)]
    public void TrickTargetAndResponseTimingAreValidated(BattleActionKind kind, string card, string target, string? responseTo)
        => RejectUnchanged(Fixture([["j0", "x0", "n0", "s0"], []]), new(kind, card, target, responseTo));

    [Theory]
    [InlineData(BattleActionKind.Slash, "s0", "u1", null)]
    [InlineData(BattleActionKind.RespondSlash, "s0", "u1", null)]
    [InlineData(BattleActionKind.Dodge, "d0", "u0", null)]
    [InlineData(BattleActionKind.Peach, "p0", "u0", null)]
    [InlineData(BattleActionKind.Nullify, "s0", "u0", "x0")]
    [InlineData(BattleActionKind.Nullify, "n0", "u1", "x0")]
    [InlineData(BattleActionKind.EndPlay, null, null, null)]
    public void NullificationWindowRejectsUnrelatedActions(BattleActionKind kind, string? card, string? target, string? responseTo)
    {
        var session = Fixture([["x0", "n0", "s0", "d0", "p0"], []]);
        Submit(session, new(BattleActionKind.ExNihilo, "x0", "u0"));
        RejectUnchanged(session, new(kind, card, target, responseTo));
    }

    [Fact]
    public void NullificationCannotRespondToSlashOrDuelSlashExchange()
    {
        var session = Fixture([["s0"], ["n0"]]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        RejectUnchanged(session, new(BattleActionKind.Nullify, "n0", "u0", "s0"));
        session = Fixture([["j0"], ["n0"]]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        RejectUnchanged(session, new(BattleActionKind.Nullify, "n0", "u0", "j0"));
    }

    [Fact]
    public void ExNihiloDrawsTwoOnlyAfterAllPassAndKeepsOwnCardOutOfReshuffle()
    {
        var session = Fixture([["x0"], []]);
        var state = session.State.Battle;
        var retained = new[] { "x0", "s0", "s1" }.ToHashSet();
        state = state with { DrawPile = state.DrawPile.Where(c => c.InstanceId == "s0").ToImmutableArray(),
            DiscardPile = state.DrawPile.Where(c => c.InstanceId == "s1").ToImmutableArray(),
            CardInventory = state.CardInventory.Where(retained.Contains).ToImmutableArray() };
        session = new(session.State with { Battle = state });
        Submit(session, new(BattleActionKind.ExNihilo, "x0", "u0"));
        Assert.Empty(session.State.Battle.Combatants[0].Hand);
        Submit(session, Pass);
        Assert.Empty(session.State.Battle.Combatants[0].Hand);
        Submit(session, Pass);
        Assert.Equal(new[] { "s0", "s1" }, session.State.Battle.Combatants[0].Hand.Select(c => c.InstanceId));
        Assert.Equal("x0", Assert.Single(session.State.Battle.DiscardPile).InstanceId);
        Assert.Contains(session.State.Battle.Events, e => e.Code == "deck.reshuffled");
    }

    [Fact]
    public void EmptyDeckExNihiloDoesNotRecycleItselfOrCreateCards()
    {
        var session = Fixture([["x0"], []]);
        session = new(session.State with { Battle = session.State.Battle with { DrawPile = [], CardInventory = ["x0"] } });
        Submit(session, new(BattleActionKind.ExNihilo, "x0", "u0"));
        PassWindow(session);
        Assert.Empty(session.State.Battle.Combatants[0].Hand);
        Assert.Equal("x0", Assert.Single(session.State.Battle.DiscardPile).InstanceId);
        Assert.Contains(session.State.Battle.Events, e => e.Code == "draw.exhausted" && e.Amount == 2);
        Assert.Null(session.State.Battle.WinnerTeamId);
    }

    [Fact]
    public void ResolvedCounterCardsMayReshuffleBeforeOriginalDrawEffect()
    {
        var session = Fixture([["x0", "n0", "n1"], []]);
        session = new(session.State with { Battle = session.State.Battle with { DrawPile = [], CardInventory = ["n0", "n1", "x0"] } });
        Submit(session, new(BattleActionKind.ExNihilo, "x0", "u0"));
        Submit(session, new(BattleActionKind.Nullify, "n0", "u0", "x0"));
        Submit(session, new(BattleActionKind.Nullify, "n1", "u0", "n0"));
        PassWindow(session);
        Assert.Equal(new[] { "n0", "n1" }, session.State.Battle.Combatants[0].Hand.Select(c => c.InstanceId).Order());
        Assert.Equal("x0", Assert.Single(session.State.Battle.DiscardPile).InstanceId);
    }

    [Fact]
    public void PublicResponseShowsExactCounteredCardWithoutHiddenHands()
    {
        var session = Fixture([["x0", "n0"], ["n1"]]);
        Submit(session, new(BattleActionKind.ExNihilo, "x0", "u0"));
        Submit(session, new(BattleActionKind.Nullify, "n0", "u0", "x0"));
        Submit(session, Pass);
        var view = BasicBattle.Observe(session.State.Battle, "c1");
        Assert.Equal("n0", view.Response!.CardId);
        Assert.Equal(1, view.Response.ChainDepth);
        Assert.Empty(view.Combatants[0].VisibleHand);
        Assert.Contains(view.LegalActions, a => a.Kind == BattleActionKind.Nullify && a.ResponseToCardId == "n0");
        Assert.Empty(BasicBattle.Observe(session.State.Battle, "c0").LegalActions);
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("cursor")]
    [InlineData("latest")]
    [InlineData("countered")]
    [InlineData("effect")]
    [InlineData("payment")]
    public void SnapshotRejectsMalformedCounterChainWithValidChecksum(string fault)
    {
        var session = Fixture([["x0", "n0"], []]);
        Submit(session, new(BattleActionKind.ExNihilo, "x0", "u0"));
        Submit(session, new(BattleActionKind.Nullify, "n0", "u0", "x0"));
        var payload = Payload(session);
        var battle = payload["battle"]!;
        switch (fault)
        {
            case "depth": battle["frames"]![3]!["chainDepth"] = 0; break;
            case "cursor": battle["frames"]![3]!["cursor"] = 2; break;
            case "latest": battle["frames"]![3]!["cardId"] = "x0"; break;
            case "countered": battle["frames"]![2]!["counteredCardId"] = "n0"; break;
            case "effect": battle["frames"]![1]!["amount"] = 99; break;
            case "payment": battle["processing"]![1]!["kind"] = "slash"; break;
        }
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => DecodePayload(payload)).Code);
    }

    [Fact]
    public void SnapshotRejectsImpossibleDuelExchangeAndOldVersions()
    {
        var session = Fixture([["j0"], ["s0"]]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        var payload = Payload(session);
        payload["battle"]!["frames"]![1]!["cursor"] = 1;
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => DecodePayload(payload)).Code);
        var envelope = JsonNode.Parse(BasicBattleSnapshotCodec.Encode(session.State))!;
        envelope["formatVersion"] = 1;
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(Encoding.UTF8.GetBytes(envelope.ToJsonString()), ContentHash)).Code);
        payload = Payload(session);
        payload["engineVersion"] = "0.2.0-basic-development";
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => DecodePayload(payload)).Code);
        payload = Payload(session);
        payload["battle"]!["rulesetId"] = "rules.basic-development.v1";
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => DecodePayload(payload)).Code);
    }

    [Fact]
    public void MixedTrickBattlesAcross100SeedsAnd16FormationsRestoreEveryDecision()
    {
        var sawDuelExchange = false;
        var sawCounterChain = false;
        var formations = new HashSet<(int, int)>();
        for (ulong seed = 0; seed < 100; seed++)
        {
            var left = (int)(seed % 4) + 1;
            var right = (int)(seed / 4 % 4) + 1;
            formations.Add((left, right));
            var state = BasicBattle.Create($"mixed.{seed}", Enumerable.Range(0, left + right)
                .Select(i => new CombatantSetup($"u{i}", $"c{i}", i < left ? "a" : "b")),
                Enumerable.Range(0, 64).Select(i => new BattleCard($"mixed.card.{i}", (i % 16) switch
                {
                    0 or 1 => BasicCardKind.Dodge, 2 => BasicCardKind.Peach, 3 or 4 => BasicCardKind.Duel,
                    5 => BasicCardKind.ExNihilo, 6 or 7 => BasicCardKind.Nullification, _ => BasicCardKind.Slash
                }, "spade", i % 13 + 1)), seed);
            var session = new BasicBattleSession(new(BasicBattleSession.EngineVersion, ContentHash, state, []));
            var steps = 0;
            while (session.State.Battle.PendingDecision is { } decision && steps++ < 4000)
            {
                state = session.State.Battle;
                sawDuelExchange |= state.Frames.Any(f => f.Kind == EffectKind.Duel && f.Cursor > 0);
                sawCounterChain |= state.Frames.Any(f => f.Kind == EffectKind.Nullification && f.ChainDepth >= 2);
                var view = BasicBattle.Observe(state, decision.ControllerId);
                var actions = view.LegalActions;
                var actor = view.Combatants.Single(u => u.EntityId == decision.ActorId);
                bool Enemy(string id) => view.Combatants.Single(u => u.EntityId == id).TeamId != actor.TeamId;
                var action = actions.FirstOrDefault(a => a.Kind is BattleActionKind.Dodge or BattleActionKind.RespondSlash)
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Nullify && view.Response is { } response && Enemy(response.SourceId))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Peach && !Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Duel && Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Slash && Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.ExNihilo)
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Pass)
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Discard)
                    ?? new(BattleActionKind.EndPlay);
                Submit(session, action);
                BattleValidation.Validate(session.State.Battle);
            }
            Assert.True(steps < 4000, $"混合牌步数保护触发：种子{seed}，{left}对{right}");
            Assert.Equal(BattlePhase.Finished, session.State.Battle.Phase);
            Assert.NotNull(session.State.Battle.WinnerTeamId);
        }
        Assert.Equal(16, formations.Count);
        Assert.True(sawDuelExchange);
        Assert.True(sawCounterChain);
    }

    private static BasicBattleSession Fixture(string[][] hands, int[]? hp = null)
    {
        var names = hands.SelectMany(h => h).Concat(Enumerable.Range(0, 20).Select(i => $"s{i}")).Distinct().ToArray();
        var cards = names.Select(id => new BattleCard(id, id[0] switch
        {
            'j' => BasicCardKind.Duel, 'x' => BasicCardKind.ExNihilo, 'n' => BasicCardKind.Nullification,
            'p' => BasicCardKind.Peach, 'd' => BasicCardKind.Dodge, _ => BasicCardKind.Slash
        }, "spade", 1)).ToArray();
        var state = BasicBattle.Create("tricks", Enumerable.Range(0, hands.Length)
            .Select(i => new CombatantSetup($"u{i}", $"c{i}", i % 2 == 0 ? "a" : "b")), cards, 42, 0);
        var assigned = hands.SelectMany(h => h).ToHashSet();
        state = state with { Combatants = state.Combatants.Select((u, i) => u with
            { Hp = hp?[i] ?? 4, Hand = hands[i].Select(id => cards.Single(c => c.InstanceId == id)).ToImmutableArray() }).ToImmutableArray(),
            DrawPile = cards.Where(c => !assigned.Contains(c.InstanceId)).ToImmutableArray() };
        return new(new(BasicBattleSession.EngineVersion, ContentHash, state, []));
    }

    private static void PassWindow(BasicBattleSession session)
    {
        while (!session.State.Battle.Frames.IsEmpty && session.State.Battle.Frames[^1].Kind == EffectKind.Nullification)
            Submit(session, Pass);
    }

    private static void Submit(BasicBattleSession session, BattleAction action)
    {
        var command = Command(session, action);
        var restored = Restore(session);
        Assert.True(session.Submit(command).Accepted);
        Assert.True(restored.Submit(command).Accepted);
        Assert.Equal(Hash(session), Hash(restored));
        Assert.True(restored.Submit(command).Duplicate);
        Assert.Equal(Hash(session), Hash(restored));
    }

    private static BattleCommand Command(BasicBattleSession session, BattleAction action)
    {
        var state = session.State.Battle;
        var d = state.PendingDecision!;
        return new($"trick.command.{state.StateVersion}", state.StateVersion, d.ControllerId, d.RequestId, d.ActorId, action);
    }
    private static void RejectUnchanged(BasicBattleSession session, BattleAction action)
    {
        var before = Hash(session);
        Assert.Equal("action.illegal", session.Submit(Command(session, action)).Code);
        Assert.Equal(before, Hash(session));
    }
    private static string Hash(BasicBattleSession session) => BasicBattleSnapshotCodec.StateHash(session.State);
    private static BasicBattleSession Restore(BasicBattleSession session)
        => new(BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(session.State), ContentHash));
    private static JsonNode Payload(BasicBattleSession session)
        => JsonNode.Parse(JsonNode.Parse(BasicBattleSnapshotCodec.Encode(session.State))!["payload"]!.GetValue<string>())!;
    private static BasicBattleSessionState DecodePayload(JsonNode payload)
    {
        var text = payload.ToJsonString();
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return BasicBattleSnapshotCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(new
            { formatId = "sanguo.basic-battle", formatVersion = 4, checksum, payload = text }), ContentHash);
    }
}
