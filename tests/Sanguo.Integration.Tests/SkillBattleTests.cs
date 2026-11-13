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

public class SkillBattleTests
{
    private static readonly string ContentHash = new('f', 64);
    private static readonly BattleAction Pass = new(BattleActionKind.Pass);

    [Theory]
    [InlineData("h0", "heart")]
    [InlineData("z0", "diamond")]
    public void WushengUsesRedPhysicalCardAsSlashWithoutChangingItsPrintedKind(string payment, string suit)
    {
        var session = Fixture([[payment, "s0"], ["d0"]], [BattleProfile.GuanyuStandard, BattleProfile.Generic]);
        Submit(session, new(BattleActionKind.Slash, payment, "u1", SkillId: BattleSkills.Wusheng));
        var battle = session.State.Battle;
        var actual = Assert.Single(battle.Processing);
        Assert.Equal(suit, actual.Suit);
        Assert.Equal(BasicCardKind.Dodge, actual.Kind);
        Assert.Equal(BattleSkills.Wusheng, battle.Frames[0].SkillId);
        Assert.Equal(1, battle.SlashesUsed);
        Assert.Equal(BattleSkills.Wusheng, BasicBattle.Observe(battle, "c1").Response!.SkillId);
        Submit(session, new(BattleActionKind.Dodge, "d0", "u1"));
        Assert.Equal(4, session.State.Battle.Combatants[1].Hp);
        Assert.Equal(BasicCardKind.Dodge, session.State.Battle.DiscardPile.Single(c => c.InstanceId == payment).Kind);
        Assert.DoesNotContain(BasicBattle.GetLegalActions(session.State.Battle), a => a.Kind == BattleActionKind.Slash);
    }

    [Fact]
    public void WushengWorksInBothDirectionsOfDuelWithoutSpendingActiveSlashAllowance()
    {
        var session = Fixture([["j0", "h0"], ["z0"]], [BattleProfile.GuanyuStandard, BattleProfile.GuanyuStandard]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        Submit(session, new(BattleActionKind.RespondSlash, "z0", "u0", SkillId: BattleSkills.Wusheng));
        Submit(session, new(BattleActionKind.RespondSlash, "h0", "u1", SkillId: BattleSkills.Wusheng));
        Submit(session, Pass);
        Assert.Equal(3, session.State.Battle.Combatants[1].Hp);
        Assert.Equal(0, session.State.Battle.SlashesUsed);
        Assert.Equal(2, session.State.Battle.Events.Count(e => e.Code == "skill.used" && e.RelatedCardId == BattleSkills.Wusheng));
    }

    [Theory]
    [InlineData("a0", BasicCardKind.Peach)]
    [InlineData("x0", BasicCardKind.ExNihilo)]
    [InlineData("n0", BasicCardKind.Nullification)]
    [InlineData("r0", BasicCardKind.Slash)]
    public void WushengCanConvertEverySupportedRedPaymentAndKeepsNativeActions(string card, BasicCardKind printedKind)
    {
        var session = Fixture([[card], []], [BattleProfile.GuanyuStandard, BattleProfile.CaocaoStandard]);
        Assert.Contains(new(BattleActionKind.Slash, card, "u1", SkillId: BattleSkills.Wusheng), BasicBattle.GetLegalActions(session.State.Battle));
        if (printedKind == BasicCardKind.ExNihilo)
            Assert.Contains(new(BattleActionKind.ExNihilo, card, "u0"), BasicBattle.GetLegalActions(session.State.Battle));
        Submit(session, new(BattleActionKind.Slash, card, "u1", SkillId: BattleSkills.Wusheng));
        Submit(session, Pass);
        Submit(session, Invoke(card, "u1"));
        Assert.Equal(printedKind, Assert.Single(session.State.Battle.Combatants[1].Hand).Kind);
    }

    [Fact]
    public void UnknownProfileCannotEnterBattleOrSupplyUnregisteredSkills()
    {
        Assert.Throws<ArgumentException>(() => BasicBattle.Create("invalid.profile",
            [new("u0", "c0", "a", Profile: (BattleProfile)99), new("u1", "c1", "b")], [], 42, 0));
        var session = Fixture([["s0"], []], [BattleProfile.Generic, BattleProfile.Generic]);
        Reject(session, new(BattleActionKind.Slash, "s0", "u1", SkillId: "skill.unknown"));
    }

    [Theory]
    [InlineData(BattleProfile.Generic, "h0", BattleActionKind.Slash)]
    [InlineData(BattleProfile.GuanyuStandard, "d0", BattleActionKind.Slash)]
    [InlineData(BattleProfile.GuanyuStandard, "h0", BattleActionKind.RespondSlash)]
    [InlineData(BattleProfile.GuanyuStandard, "h0", BattleActionKind.Dodge)]
    public void ConversionRequiresCorrectOwnerColorAndTiming(BattleProfile profile, string card, BattleActionKind kind)
    {
        var session = Fixture([[card], []], [profile, BattleProfile.Generic]);
        Reject(session, new(kind, card, "u1", SkillId: BattleSkills.Wusheng));
    }

    [Fact]
    public void WushengCannotBypassDistanceOrConcealExtraPayment()
    {
        var session = Fixture([["h0", "s0"], [], [], []],
            [BattleProfile.GuanyuStandard, BattleProfile.Generic, BattleProfile.Generic, BattleProfile.Generic]);
        Reject(session, new(BattleActionKind.Slash, "h0", "u2", SkillId: BattleSkills.Wusheng));
        Reject(session, new(BattleActionKind.Slash, "h0", "u1", SkillId: BattleSkills.Wusheng, CostCardId: "s0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JianxiongOffersOptionalGainBeforeOriginalCardIsDiscarded(bool invoke)
    {
        var session = Fixture([["s0"], []], [BattleProfile.Generic, BattleProfile.CaocaoStandard]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Assert.Equal(EffectKind.InvokeJianxiong, session.State.Battle.Frames[^1].Kind);
        Assert.Equal("u1", session.State.Battle.PendingDecision!.ActorId);
        Assert.Equal("s0", Assert.Single(session.State.Battle.Processing).InstanceId);
        var view = BasicBattle.Observe(session.State.Battle, "c1");
        Assert.Equal(BattleSkills.Jianxiong, view.Response!.SkillId);
        Submit(session, invoke ? Invoke("s0", "u1") : Pass);
        Assert.Empty(session.State.Battle.Processing);
        Assert.Empty(session.State.Battle.Frames);
        Assert.Equal(invoke ? new[] { "s0" } : [], session.State.Battle.Combatants[1].Hand.Select(c => c.InstanceId));
        Assert.Equal(invoke ? [] : new[] { "s0" }, session.State.Battle.DiscardPile.Select(c => c.InstanceId));
        Assert.Equal(3, session.State.Battle.Combatants[1].Hp);
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
    }

    [Theory]
    [InlineData(BattleActionKind.Duel, "j0", "u1")]
    [InlineData(BattleActionKind.Slash, "h0", "u1")]
    public void JianxiongGainsPhysicalDamageCardIncludingConvertedSlash(BattleActionKind kind, string card, string target)
    {
        var session = Fixture([[card], []], [BattleProfile.GuanyuStandard, BattleProfile.CaocaoStandard]);
        Submit(session, new(kind, card, target, SkillId: kind == BattleActionKind.Slash ? BattleSkills.Wusheng : null));
        if (kind == BattleActionKind.Duel) PassWindow(session);
        Submit(session, Pass);
        Submit(session, Invoke(card, "u1"));
        var gained = Assert.Single(session.State.Battle.Combatants[1].Hand);
        Assert.Equal(card, gained.InstanceId);
        Assert.Equal(kind == BattleActionKind.Slash ? BasicCardKind.Dodge : BasicCardKind.Duel, gained.Kind);
    }

    [Fact]
    public void DuelInitiatorCanTriggerJianxiongAndKeepOriginalTurn()
    {
        var session = Fixture([["j0"], ["s0"]], [BattleProfile.CaocaoStandard, BattleProfile.Generic]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        Submit(session, new(BattleActionKind.RespondSlash, "s0", "u0"));
        Submit(session, Pass);
        Submit(session, Invoke("j0", "u0"));
        Assert.Equal("j0", Assert.Single(session.State.Battle.Combatants[0].Hand).InstanceId);
        Assert.Equal("s0", Assert.Single(session.State.Battle.DiscardPile).InstanceId);
        Assert.Equal(1, session.State.Battle.TurnNumber);
        Assert.Equal("u0", session.State.Battle.PendingDecision!.ActorId);
    }

    [Fact]
    public void JianxiongWaitsForRescueAndTriggersOnlyAfterSurvival()
    {
        var session = Fixture([["s0", "p0"], []], [BattleProfile.Generic, BattleProfile.CaocaoStandard], hp: [4, 1]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Assert.Equal(new[] { EffectKind.FinishCard, EffectKind.AfterDamage, EffectKind.Rescue }, session.State.Battle.Frames.Select(f => f.Kind));
        Reject(session, Invoke("s0", "u1"));
        Submit(session, new(BattleActionKind.Peach, "p0", "u1"));
        Assert.Equal(EffectKind.InvokeJianxiong, session.State.Battle.Frames[^1].Kind);
        Submit(session, Invoke("s0", "u1"));
        Assert.Equal(1, session.State.Battle.Combatants[1].Hp);
        Assert.Equal(new[] { "p0" }, session.State.Battle.DiscardPile.Select(c => c.InstanceId));
    }

    [Fact]
    public void DeadJianxiongOwnerDoesNotActOrGainDamageCard()
    {
        var session = Fixture([["s0"], []], [BattleProfile.Generic, BattleProfile.CaocaoStandard], hp: [4, 1]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Submit(session, Pass);
        Submit(session, Pass);
        Assert.Equal(BattlePhase.Finished, session.State.Battle.Phase);
        Assert.False(session.State.Battle.Combatants[1].Alive);
        Assert.Equal("s0", Assert.Single(session.State.Battle.DiscardPile).InstanceId);
        Assert.DoesNotContain(session.State.Battle.Events, e => e.Code == "skill.jianxiong_gained");
    }

    [Fact]
    public void DodgedSlashDoesNotOfferJianxiongAndLordSkillRemainsInactive()
    {
        var session = Fixture([["s0"], ["d0"]], [BattleProfile.CaocaoStandard, BattleProfile.CaocaoStandard]);
        var skills = BasicBattle.Observe(session.State.Battle, "c0").Combatants[0].Skills;
        Assert.Equal(2, skills.Length);
        var hujia = Assert.Single(skills.Where(s => s.Definition.RuleId == BattleSkills.Hujia));
        Assert.False(hujia.Enabled);
        Assert.NotNull(hujia.InactiveReason);
        Assert.False(BattleSkills.Has(session.State.Battle.Combatants[0], BattleSkills.Hujia));
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Reject(session, new(BattleActionKind.InvokeSkill, TargetId: "u1", ResponseToCardId: "s0", SkillId: BattleSkills.Hujia));
        Submit(session, new(BattleActionKind.Dodge, "d0", "u1"));
        Assert.Empty(session.State.Battle.Frames);
        Assert.Equal(4, session.State.Battle.Combatants[1].Hp);
    }

    [Theory]
    [InlineData("wrong", "u1", BattleSkills.Jianxiong)]
    [InlineData("s0", "u0", BattleSkills.Jianxiong)]
    [InlineData("s0", "u1", BattleSkills.Hujia)]
    public void TriggerChoiceBindsSkillTargetAndCause(string cause, string target, string skill)
    {
        var session = Fixture([["s0"], []], [BattleProfile.Generic, BattleProfile.CaocaoStandard]);
        Reject(session, Invoke("s0", "u0"));
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Reject(session, new(BattleActionKind.InvokeSkill, TargetId: target, ResponseToCardId: cause, SkillId: skill));
        var command = Command(session, Invoke("s0", "u1"));
        var before = Hash(session);
        Assert.Equal("controller.denied", session.Submit(command with { ControllerId = "c0" }).Code);
        Assert.Equal("actor.denied", session.Submit(command with { ActorId = "u0" }).Code);
        Assert.Equal(before, Hash(session));
        Submit(session, command.Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FenjiPaysExtraHandCardAndDoesNotRefundOnDodge(bool dodge)
    {
        var session = Fixture([["s0", "h0"], ["d0"]], [BattleProfile.ProtagonistFenjiI, BattleProfile.Generic]);
        Submit(session, Boost("s0", "h0", "u1"));
        Assert.True(session.State.Battle.FenjiUsed);
        Assert.Equal(1, session.State.Battle.SlashesUsed);
        Assert.Equal("h0", Assert.Single(session.State.Battle.DiscardPile).InstanceId);
        Assert.Equal("s0", Assert.Single(session.State.Battle.Processing).InstanceId);
        Assert.Equal(1, BasicBattle.Observe(session.State.Battle, "c1").Response!.DamageBonus);
        Submit(session, dodge ? new(BattleActionKind.Dodge, "d0", "u1") : Pass);
        Assert.Equal(dodge ? 4 : 2, session.State.Battle.Combatants[1].Hp);
        Assert.Contains(session.State.Battle.DiscardPile, c => c.InstanceId == "h0");
        if (!dodge) Assert.Equal(2, Assert.Single(session.State.Battle.Events.Where(e => e.Code == "damage.dealt")).Amount);
    }

    [Theory]
    [InlineData(BattleProfile.Generic, "s0", "h0")]
    [InlineData(BattleProfile.ProtagonistFenjiI, "s0", "s0")]
    [InlineData(BattleProfile.ProtagonistFenjiI, "s0", "missing")]
    [InlineData(BattleProfile.ProtagonistFenjiI, "h0", "s0")]
    public void FenjiRejectsMissingSkillInvalidSlashOrCost(BattleProfile profile, string card, string cost)
    {
        var session = Fixture([["s0", "h0"], []], [profile, BattleProfile.Generic]);
        Reject(session, Boost(card, cost, "u1"));
        Reject(session, new(BattleActionKind.Slash, "s0", "u1", CostCardId: "h0"));
    }

    [Fact]
    public void FenjiCreatesRealNegativeHpRescueAndJianxiongContinuesAfterTwoPeaches()
    {
        var session = Fixture([["s0", "h0", "p0", "p1"], []],
            [BattleProfile.ProtagonistFenjiI, BattleProfile.CaocaoStandard], hp: [4, 1]);
        Submit(session, Boost("s0", "h0", "u1"));
        Submit(session, Pass);
        Assert.Equal(-1, session.State.Battle.Combatants[1].Hp);
        Assert.Equal(2, session.State.Battle.Frames[^1].Amount);
        Submit(session, new(BattleActionKind.Peach, "p0", "u1"));
        Assert.Equal(0, session.State.Battle.Combatants[1].Hp);
        Assert.Equal(EffectKind.Rescue, session.State.Battle.Frames[^1].Kind);
        Submit(session, new(BattleActionKind.Peach, "p1", "u1"));
        Submit(session, Invoke("s0", "u1"));
        Assert.Equal(1, session.State.Battle.Combatants[1].Hp);
        Assert.Equal(new[] { "h0", "p0", "p1" }, session.State.Battle.DiscardPile.Select(c => c.InstanceId));
    }

    [Fact]
    public void FenjiDoesNotIncreaseDuelDamageAndItsCounterResetsWithNewTurn()
    {
        var session = Fixture([["s0", "h0", "j0"], []], [BattleProfile.ProtagonistFenjiI, BattleProfile.Generic]);
        Submit(session, Boost("s0", "h0", "u1"));
        Submit(session, Pass);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        Submit(session, Pass);
        Assert.Equal(1, session.State.Battle.Combatants[1].Hp);
        Assert.Equal(new[] { 2, 1 }, session.State.Battle.Events.Where(e => e.Code == "damage.dealt").Select(e => e.Amount));
        Assert.True(session.State.Battle.FenjiUsed);
        Submit(session, new(BattleActionKind.EndPlay));
        Assert.False(session.State.Battle.FenjiUsed);
        Assert.Equal(0, session.State.Battle.SlashesUsed);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("conversion")]
    [InlineData("bonus")]
    [InlineData("trigger")]
    [InlineData("afterDamage")]
    [InlineData("count")]
    public void SnapshotsRejectForgedSkillOwnershipBonusAndTriggerContexts(string fault)
    {
        var session = Fixture([["s0", "h0"], []], [BattleProfile.ProtagonistFenjiI, BattleProfile.CaocaoStandard], hp: [4, 1]);
        Submit(session, Boost("s0", "h0", "u1"));
        if (fault is "afterDamage" or "trigger") Submit(session, Pass);
        var envelope = JsonNode.Parse(BasicBattleSnapshotCodec.Encode(session.State))!;
        var payload = JsonNode.Parse(envelope["payload"]!.GetValue<string>())!;
        var battle = payload["battle"]!;
        switch (fault)
        {
            case "profile": battle["combatants"]![0]!["profile"] = "generic"; break;
            case "conversion": battle["frames"]![0]!["skillId"] = BattleSkills.Wusheng; break;
            case "bonus": battle["frames"]![0]!["damageBonus"] = 5; break;
            case "trigger": battle["frames"]![2]!["skillId"] = BattleSkills.Jianxiong; break;
            case "afterDamage": battle["frames"]![1]!["amount"] = 1; break;
            case "count": battle["fenjiUsed"] = false; break;
        }
        var text = payload.ToJsonString();
        envelope["payload"] = text;
        envelope["checksum"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(
            Encoding.UTF8.GetBytes(envelope.ToJsonString()), ContentHash)).Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OldDevelopmentFormatsAreExplicitlyIncompatible(int version)
    {
        var session = Fixture([[], []], [BattleProfile.GuanyuStandard, BattleProfile.CaocaoStandard]);
        var envelope = JsonNode.Parse(BasicBattleSnapshotCodec.Encode(session.State))!;
        envelope["formatVersion"] = version;
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => BasicBattleSnapshotCodec.Decode(
            Encoding.UTF8.GetBytes(envelope.ToJsonString()), ContentHash)).Code);
    }

    [Fact]
    public void HundredSkillBattlesAcrossAllFormationSizesRestoreEveryDecision()
    {
        var formations = new HashSet<(int, int)>();
        var seen = new HashSet<string?>();
        for (ulong seed = 0; seed < 100; seed++)
        {
            var left = (int)(seed % 4) + 1;
            var right = (int)(seed / 4 % 4) + 1;
            formations.Add((left, right));
            var profiles = new[] { BattleProfile.GuanyuStandard, BattleProfile.CaocaoStandard, BattleProfile.ProtagonistFenjiI, BattleProfile.Generic };
            var session = BasicBattleSession.Create($"skills.{seed}", Enumerable.Range(0, left + right)
                .Select(i => new CombatantSetup($"u{i}", $"c{i}", i < left ? "a" : "b", Profile: profiles[(i + (int)seed) % 4])),
                Enumerable.Range(0, 64).Select(i => new BattleCard($"card.{i}", (i % 16) switch
                {
                    0 or 1 => BasicCardKind.Dodge, 2 => BasicCardKind.Peach, 3 or 4 => BasicCardKind.Duel,
                    5 => BasicCardKind.ExNihilo, 6 or 7 => BasicCardKind.Nullification,
                    8 => BasicCardKind.Dismantlement, 9 => BasicCardKind.Snatch, _ => BasicCardKind.Slash
                }, new[] { "heart", "spade", "diamond", "club" }[i % 4], i % 13 + 1)), ContentHash, seed);
            var steps = 0;
            while (session.State.Battle.PendingDecision is { } decision && steps++ < 4000)
            {
                var view = BasicBattle.Observe(session.State.Battle, decision.ControllerId);
                var actions = view.LegalActions;
                var actor = view.Combatants.Single(u => u.EntityId == decision.ActorId);
                bool Enemy(string id) => view.Combatants.Single(u => u.EntityId == id).TeamId != actor.TeamId;
                var action = actions.FirstOrDefault(a => a.Kind is BattleActionKind.InvokeSkill or BattleActionKind.Dodge or BattleActionKind.RespondSlash or BattleActionKind.SelectHandCard)
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Nullify && view.Response is { } r && Enemy(r.SourceId))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Peach && !Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.ExNihilo)
                    ?? actions.FirstOrDefault(a => a.Kind is BattleActionKind.Snatch or BattleActionKind.Dismantlement && Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Slash && a.SkillId is not null && Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind is BattleActionKind.Duel or BattleActionKind.Slash && Enemy(a.TargetId!))
                    ?? actions.FirstOrDefault(a => a.Kind is BattleActionKind.Pass or BattleActionKind.Discard)
                    ?? new(BattleActionKind.EndPlay);
                Submit(session, action);
                seen.Add(action.SkillId);
                BattleValidation.Validate(session.State.Battle);
            }
            Assert.True(steps < 4000, $"技能场景步数保护触发：种子{seed}，{left}对{right}");
            Assert.Equal(BattlePhase.Finished, session.State.Battle.Phase);
            Assert.NotNull(session.State.Battle.WinnerTeamId);
        }
        Assert.Equal(16, formations.Count);
        Assert.Contains(BattleSkills.Wusheng, seen);
        Assert.Contains(BattleSkills.Jianxiong, seen);
        Assert.Contains(BattleSkills.FenjiI, seen);
        Assert.DoesNotContain(BattleSkills.Hujia, seen);
    }

    private static BasicBattleSession Fixture(string[][] hands, BattleProfile[] profiles, int[]? hp = null)
    {
        var names = hands.SelectMany(h => h).Concat(Enumerable.Range(0, 20).Select(i => $"s{i}")).Distinct().ToArray();
        var cards = names.Select(id => new BattleCard(id, id[0] switch
        {
            'd' or 'h' or 'z' => BasicCardKind.Dodge, 'j' => BasicCardKind.Duel,
            'p' or 'a' => BasicCardKind.Peach, 'x' => BasicCardKind.ExNihilo,
            'n' => BasicCardKind.Nullification, _ => BasicCardKind.Slash
        }, id[0] switch { 'h' or 'a' or 'x' or 'n' or 'r' => "heart", 'z' => "diamond", _ => "spade" }, 1)).ToArray();
        var state = BasicBattle.Create("skills", Enumerable.Range(0, hands.Length)
            .Select(i => new CombatantSetup($"u{i}", $"c{i}", i % 2 == 0 ? "a" : "b", Profile: profiles[i])), cards, 42, 0);
        var assigned = hands.SelectMany(h => h).ToHashSet();
        state = state with
        {
            Combatants = state.Combatants.Select((u, i) => u with { Hp = hp?[i] ?? 4,
                Hand = hands[i].Select(id => cards.Single(c => c.InstanceId == id)).ToImmutableArray() }).ToImmutableArray(),
            DrawPile = cards.Where(c => !assigned.Contains(c.InstanceId)).ToImmutableArray()
        };
        return new(new(BasicBattleSession.EngineVersion, ContentHash, state, []));
    }

    private static BattleAction Invoke(string card, string target)
        => new(BattleActionKind.InvokeSkill, TargetId: target, ResponseToCardId: card, SkillId: BattleSkills.Jianxiong);
    private static BattleAction Boost(string card, string cost, string target)
        => new(BattleActionKind.Slash, card, target, SkillId: BattleSkills.FenjiI, CostCardId: cost);
    private static void PassWindow(BasicBattleSession session)
    {
        while (session.State.Battle.Frames.LastOrDefault()?.Kind == EffectKind.Nullification) Submit(session, Pass);
    }
    private static BattleCommand Command(BasicBattleSession session, BattleAction action)
    {
        var battle = session.State.Battle;
        var d = battle.PendingDecision!;
        return new($"skill.command.{battle.StateVersion}", battle.StateVersion, d.ControllerId, d.RequestId, d.ActorId, action);
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
