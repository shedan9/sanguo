using System.Collections.Immutable;
using System.Text.Json;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public class BasicBattleAiTests
{
    private static readonly string ContentHash = new('e', 64);
    private static readonly BattleAction Pass = new(BattleActionKind.Pass);

    [Fact]
    public void SpectatorAndFinishedViewsReturnNoChoice()
    {
        var session = Fixture([["s0"], []]);
        Assert.Null(BasicBattleAi.Choose(BasicBattle.Observe(session.State.Battle, "spectator")));
        var view = View(session);
        Assert.Null(BasicBattleAi.Choose(view with { PendingDecision = null, LegalActions = [] }));
    }

    [Fact]
    public void HealingPrecedesAttackAndAttacksAvoidAllies()
    {
        var session = Fixture([["p0", "s0"], [], [], []]);
        var state = session.State.Battle;
        session = new(session.State with { Battle = state with
        {
            Combatants = state.Combatants.SetItem(0, state.Combatants[0] with { Hp = 2 })
        } });
        Assert.Equal(BattleActionKind.Peach, Choose(session).Kind);
        Submit(session, Choose(session));
        var attack = Choose(session);
        Assert.Equal(BattleActionKind.Slash, attack.Kind);
        Assert.Equal("u1", attack.TargetId);
    }

    [Theory]
    [InlineData("s0", BattleActionKind.Slash)]
    [InlineData("j0", BattleActionKind.Duel)]
    [InlineData("b0", BattleActionKind.Dismantlement)]
    [InlineData("q0", BattleActionKind.Snatch)]
    public void OffensiveCardsNeverTargetAllies(string card, BattleActionKind kind)
    {
        var session = Fixture([[card, "s1"], ["s2"], ["s3"], ["s4"]]);
        var view = View(session);
        // 留下当前牌的全部合法目标与结束阶段，防止别的牌掩盖错误选择。
        view = view with { LegalActions = view.LegalActions.Where(a => a.CardId == card || a.Kind == BattleActionKind.EndPlay).ToImmutableArray() };
        var action = BasicBattleAi.Choose(view)!.Action;
        Assert.Equal(kind, action.Kind);
        Assert.Equal("b", view.Combatants.Single(u => u.EntityId == action.TargetId).TeamId);
    }

    [Fact]
    public void DodgeAndDuelResponsesUseLegalCards()
    {
        var session = Fixture([["s0"], ["d0"]]);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Assert.Equal(BattleActionKind.Dodge, Choose(session).Kind);
        session = Fixture([["j0"], ["s0"]]);
        Submit(session, new(BattleActionKind.Duel, "j0", "u1"));
        PassWindow(session);
        Assert.Equal(BattleActionKind.RespondSlash, Choose(session).Kind);
    }

    [Theory]
    [InlineData(true, BattleActionKind.Peach)]
    [InlineData(false, BattleActionKind.Pass)]
    public void RescueSpendsPeachOnlyOnOwnTeam(bool ally, BattleActionKind expected)
    {
        var session = Fixture([["p0"], ["s0"]]);
        var view = View(session);
        var target = view.Combatants[1] with { TeamId = ally ? "a" : "b", Hp = 0 };
        view = view with
        {
            Combatants = view.Combatants.SetItem(1, target),
            Response = new(EffectKind.Rescue, "u0", "u1", "s0", 0),
            LegalActions = [new(BattleActionKind.Peach, "p0", "u1"), Pass]
        };
        Assert.Equal(expected, BasicBattleAi.Choose(view)!.Action.Kind);
    }

    [Fact]
    public void DiscardKeepsHealingAndLastDodgeBeforeSurplusSlashes()
    {
        var session = Fixture([["p0", "d0", "s0", "s1"], []]);
        var view = View(session) with { Phase = BattlePhase.Discard,
            LegalActions = [new(BattleActionKind.Discard, "p0"), new(BattleActionKind.Discard, "d0"),
                new(BattleActionKind.Discard, "s0"), new(BattleActionKind.Discard, "s1")] };
        Assert.Equal("s0", BasicBattleAi.Choose(view)!.Action.CardId);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, BattleSkills.FenjiI)]
    public void FenjiUsesCheapCostForLethalDamageButAvoidsOverkill(int hp, string? skill)
    {
        var session = Fixture([["s0", "s1", "p0"], []], BattleProfile.ProtagonistFenjiI);
        var view = View(session);
        view = view with { Combatants = view.Combatants.SetItem(1, view.Combatants[1] with { Hp = hp }) };
        var action = BasicBattleAi.Choose(view)!.Action;
        Assert.Equal(BattleActionKind.Slash, action.Kind);
        Assert.Equal(skill, action.SkillId);
        if (skill is not null) Assert.StartsWith("s", action.CostCardId);
    }

    [Fact]
    public void WushengPreservesPeachWhenNaturalSlashExists()
    {
        var session = Fixture([["s0", "p0"], []], BattleProfile.GuanyuStandard);
        Assert.Equal("s0", Choose(session).CardId);
        session = Fixture([["p0"], []], BattleProfile.GuanyuStandard);
        Assert.Equal(BattleSkills.Wusheng, Choose(session).SkillId);
    }

    [Fact]
    public void JianxiongInvokesWithoutSpendingAnotherCard()
    {
        var session = Fixture([["s0"], []], targetProfile: BattleProfile.CaocaoStandard);
        Submit(session, new(BattleActionKind.Slash, "s0", "u1"));
        Submit(session, Pass);
        Assert.Equal(BattleActionKind.InvokeSkill, Choose(session).Kind);
    }

    [Fact]
    public void AvoidsDuelWithoutSlashAgainstNonemptyHand()
    {
        var session = Fixture([["j0"], ["d0"]]);
        Assert.Equal(BattleActionKind.EndPlay, Choose(session).Kind);
        session = Fixture([["j0"], []]);
        Assert.Equal(BattleActionKind.Duel, Choose(session).Kind);
    }

    [Theory]
    [InlineData("j0")]
    [InlineData("x0")]
    [InlineData("b0")]
    [InlineData("q0")]
    public void CounterChainUsesOriginalEffectAndParityThroughFourCounters(string card)
    {
        var session = Fixture([[card, "n0", "n2"], ["s0", "n1", "n3"]]);
        var kind = card[0] switch { 'j' => BattleActionKind.Duel, 'x' => BattleActionKind.ExNihilo,
            'b' => BattleActionKind.Dismantlement, _ => BattleActionKind.Snatch };
        var target = kind == BattleActionKind.ExNihilo ? "u0" : "u1";
        Submit(session, new(kind, card, target));
        for (var depth = 0; depth <= 4; depth++)
        {
            // 发起方保留己方有利锦囊；反制后恢复它。另一方做相反选择。
            var first = View(session);
            var trick = first.Response!.Trick!;
            Assert.Equal(("u0", target, card), (trick.SourceId, trick.TargetId, trick.CardId));
            Assert.Equal(depth, first.Response.ChainDepth);
            var choice = Choose(session);
            var expected = depth % 2 == 1 && depth < 4 ? BattleActionKind.Nullify : BattleActionKind.Pass;
            Assert.Equal(expected, choice.Kind);
            if (choice.Kind == BattleActionKind.Nullify) { Submit(session, choice); continue; }
            Submit(session, Pass);
            choice = Choose(session);
            Assert.Equal(depth < 4 ? BattleActionKind.Nullify : BattleActionKind.Pass, choice.Kind);
            if (depth < 4)
            {
                Assert.Equal(session.State.Battle.Frames[^1].CardId, choice.ResponseToCardId);
                Submit(session, choice);
            }
        }
    }

    [Fact]
    public void HiddenEnemyFacesAndFutureDeckDoNotChangeChoiceOrConsumeRandomness()
    {
        var session = Fixture([["s0", "j0", "b0"], ["s1", "d0"]]);
        var state = session.State.Battle;
        var changed = state with
        {
            Combatants = state.Combatants.SetItem(1, state.Combatants[1] with
            {
                Hand = state.Combatants[1].Hand.Select(c => c with { Kind = BasicCardKind.Peach, Suit = "heart", Rank = 13 }).Reverse().ToImmutableArray()
            }),
            DrawPile = state.DrawPile.Reverse().ToImmutableArray()
        };
        BattleValidation.Validate(changed);
        var before = BasicBattleSnapshotCodec.StateHash(session.State);
        var visible = View(session);
        var other = BasicBattle.Observe(changed, "c0");
        Assert.Equal(JsonSerializer.Serialize(visible), JsonSerializer.Serialize(other));
        Assert.Equal(BasicBattleAi.Choose(visible), BasicBattleAi.Choose(other));
        Assert.Equal(before, BasicBattleSnapshotCodec.StateHash(session.State));
        Assert.Equal(BasicBattleAi.Choose(visible), BasicBattleAi.Choose(visible with
        { LegalActions = visible.LegalActions.Reverse().ToImmutableArray() }));
    }

    [Fact]
    public void HiddenHandChoiceUsesStableSlotAndSurvivesRestore()
    {
        var session = Fixture([["q0"], ["p0", "s0"]]);
        Submit(session, new(BattleActionKind.Snatch, "q0", "u1"));
        PassWindow(session);
        var action = Choose(session);
        Assert.Equal(0, action.HandIndex);
        Assert.Null(action.CardId);
        Assert.Empty(View(session).Combatants[1].VisibleHand);
        var restored = new BasicBattleSession(BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(session.State), ContentHash));
        Assert.Equal(action, Choose(restored));
    }

    [Fact]
    public void AiBattlesAcross32SeedsAndAllFormationsRestoreEveryChoice()
    {
        var formations = new HashSet<(int, int)>();
        var kinds = new HashSet<BattleActionKind>();
        for (ulong seed = 0; seed < 32; seed++)
        {
            var left = (int)(seed % 4) + 1;
            var right = (int)(seed / 4 % 4) + 1;
            formations.Add((left, right));
            var session = BasicBattleSession.Create($"ai.{seed}", Enumerable.Range(0, left + right)
                .Select(i => new CombatantSetup($"u{i}", $"c{i}", i < left ? "a" : "b", Profile: (BattleProfile)(i % 4))),
                Enumerable.Range(0, 80).Select(i => new BattleCard($"card.{i:D3}", (i % 16) switch
                {
                    0 or 1 => BasicCardKind.Dodge, 2 => BasicCardKind.Peach, 3 or 4 => BasicCardKind.Duel,
                    5 => BasicCardKind.ExNihilo, 6 or 7 => BasicCardKind.Nullification,
                    8 => BasicCardKind.Dismantlement, 9 => BasicCardKind.Snatch, _ => BasicCardKind.Slash
                }, i % 2 == 0 ? "heart" : "spade", i % 13 + 1)), ContentHash, seed);
            var steps = 0;
            while (session.State.Battle.PendingDecision is not null && steps++ < 4000)
            {
                var restored = new BasicBattleSession(BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(session.State), ContentHash));
                var choice = BasicBattleAi.Choose(View(session))!;
                Assert.Equal(choice, BasicBattleAi.Choose(View(restored)));
                Assert.Contains(choice.Action, View(session).LegalActions);
                kinds.Add(choice.Action.Kind);
                var decision = session.State.Battle.PendingDecision;
                var command = new BattleCommand($"ai.{session.State.Battle.StateVersion}", session.State.Battle.StateVersion,
                    decision.ControllerId, decision.RequestId, decision.ActorId, choice.Action);
                Assert.True(session.Submit(command).Accepted);
                Assert.True(restored.Submit(command).Accepted);
                var hash = BasicBattleSnapshotCodec.StateHash(session.State);
                Assert.Equal(hash, BasicBattleSnapshotCodec.StateHash(restored.State));
                Assert.True(restored.Submit(command).Duplicate);
                Assert.Equal(hash, BasicBattleSnapshotCodec.StateHash(restored.State));
                var state = session.State.Battle;
                Assert.Equal(state.CardInventory.Order(), state.DrawPile.Concat(state.DiscardPile).Concat(state.Processing)
                    .Concat(state.Combatants.SelectMany(u => u.Hand)).Select(c => c.InstanceId).Order());
            }
            Assert.True(steps < 4000, $"AI步数保护：seed={seed}, {left}对{right}");
            Assert.Equal(BattlePhase.Finished, session.State.Battle.Phase);
            Assert.NotNull(session.State.Battle.WinnerTeamId);
        }
        Assert.Equal(16, formations.Count);
        foreach (var kind in Enum.GetValues<BattleActionKind>()) Assert.Contains(kind, kinds);
    }

    private static BasicBattleSession Fixture(string[][] hands, BattleProfile profile = BattleProfile.Generic,
        BattleProfile targetProfile = BattleProfile.Generic)
    {
        var names = hands.SelectMany(h => h).Concat(Enumerable.Range(10, 20).Select(i => $"s{i}")).Distinct().ToArray();
        var cards = names.Select(id => new BattleCard(id, id[0] switch
        {
            'd' => BasicCardKind.Dodge, 'p' => BasicCardKind.Peach, 'j' => BasicCardKind.Duel,
            'x' => BasicCardKind.ExNihilo, 'n' => BasicCardKind.Nullification,
            'b' => BasicCardKind.Dismantlement, 'q' => BasicCardKind.Snatch, _ => BasicCardKind.Slash
        }, id[0] == 'p' ? "heart" : "spade", 1)).ToArray();
        var state = BasicBattle.Create("ai.fixture", Enumerable.Range(0, hands.Length)
            .Select(i => new CombatantSetup($"u{i}", $"c{i}", i % 2 == 0 ? "a" : "b",
                Profile: i == 0 ? profile : targetProfile)), cards, 42, 0);
        var assigned = hands.SelectMany(h => h).ToHashSet();
        state = state with
        {
            Combatants = state.Combatants.Select((u, i) => u with
            { Hand = hands[i].Select(id => cards.Single(c => c.InstanceId == id)).ToImmutableArray() }).ToImmutableArray(),
            DrawPile = cards.Where(c => !assigned.Contains(c.InstanceId)).ToImmutableArray()
        };
        return new(new(BasicBattleSession.EngineVersion, ContentHash, state, []));
    }

    private static BattleView View(BasicBattleSession session)
        => BasicBattle.Observe(session.State.Battle, session.State.Battle.PendingDecision!.ControllerId);
    private static BattleAction Choose(BasicBattleSession session) => BasicBattleAi.Choose(View(session))!.Action;
    private static void Submit(BasicBattleSession session, BattleAction action)
    {
        var state = session.State.Battle;
        var decision = state.PendingDecision!;
        Assert.True(session.Submit(new($"cmd.{state.StateVersion}", state.StateVersion,
            decision.ControllerId, decision.RequestId, decision.ActorId, action)).Accepted);
    }
    private static void PassWindow(BasicBattleSession session)
    {
        while (session.State.Battle.Frames.LastOrDefault()?.Kind == EffectKind.Nullification) Submit(session, Pass);
    }
}
