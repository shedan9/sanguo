using Sanguo.BattleCore.Basic;

namespace Sanguo.AI;

public sealed record BattleAiChoice(BattleAction Action, int Score, string ReasonCode, string PolicyId);

/// <summary>只接收控制者视图；整数评分与稳定排序，不读取完整状态或消费战斗随机流。</summary>
public static class BasicBattleAi
{
    public const string PolicyId = "ai.basic-development.v1";

    public static BattleAiChoice? Choose(BattleView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.PendingDecision is null || view.LegalActions.IsDefaultOrEmpty) return null;
        var actor = view.Combatants.Single(u => u.EntityId == view.PendingDecision.ActorId);
        return view.LegalActions.Select(action => Evaluate(view, actor, action))
            .OrderByDescending(choice => choice.Score)
            .ThenBy(choice => choice.Action.Kind)
            .ThenBy(choice => choice.Action.TargetId is { } id
                ? view.Combatants.Single(u => u.EntityId == id).SeatId : -1)
            .ThenBy(choice => choice.Action.HandIndex ?? -1)
            .ThenBy(choice => choice.Action.CardId, StringComparer.Ordinal)
            .ThenBy(choice => choice.Action.SkillId, StringComparer.Ordinal)
            .ThenBy(choice => choice.Action.CostCardId, StringComparer.Ordinal)
            .First();
    }

    private static BattleAiChoice Evaluate(BattleView view, CombatantView actor, BattleAction action)
    {
        var target = view.Combatants.FirstOrDefault(u => u.EntityId == action.TargetId);
        var enemy = target is not null && target.TeamId != actor.TeamId;
        var cost = action.CardId is null ? 0 : KeepValue(actor, action.CardId);
        var pressure = target is null ? 0 : Math.Max(0, 5 - target.Hp) * 10;
        var (score, reason) = action.Kind switch
        {
            BattleActionKind.InvokeSkill => (1000, "skill.acquire"),
            BattleActionKind.Dodge => (800 - cost, "response.dodge"),
            BattleActionKind.RespondSlash => (750 - cost, "response.slash"),
            BattleActionKind.Peach when !enemy && view.Response?.Kind == EffectKind.Rescue
                => (1200 - cost, "rescue.ally"),
            BattleActionKind.Peach when !enemy => (700 + (actor.MaxHp - actor.Hp) * 10 - cost, "recover.self"),
            BattleActionKind.ExNihilo => (650, "draw.cards"),
            BattleActionKind.Snatch when enemy => (520 + pressure, "disrupt.snatch"),
            BattleActionKind.Dismantlement when enemy => (430 + pressure, "disrupt.dismantle"),
            BattleActionKind.Slash when enemy => (SlashScore(actor, target!, action) - cost, "attack.slash"),
            BattleActionKind.Duel when enemy => (DuelScore(actor, target!, action) - cost, "attack.duel"),
            BattleActionKind.Nullify => (ShouldNullify(view, actor) ? 500 - cost : -1000, "response.counter"),
            // 不根据暗牌牌面评分；固定选最低合法槽位。
            BattleActionKind.SelectHandCard => (100, "select.hidden_slot"),
            BattleActionKind.Discard => (-cost, "discard.preserve_resources"),
            BattleActionKind.Pass => (0, "response.pass"),
            BattleActionKind.EndPlay => (0, "phase.end"),
            _ => (-1000, "avoid.friendly_harm")
        };
        return new(action, score, reason, PolicyId);
    }

    private static int SlashScore(CombatantView actor, CombatantView target, BattleAction action)
    {
        var damage = action.SkillId == BattleSkills.FenjiI ? 2 : 1;
        var score = 450 + Math.Max(0, 5 - target.Hp) * 10 + (target.Hp <= damage ? 120 : 0);
        if (action.CostCardId is { } cost)
            score += (target.Hp > 1 ? 55 : -100) - KeepValue(actor, cost);
        return score;
    }

    private static int DuelScore(CombatantView actor, CombatantView target, BattleAction action)
    {
        var slashes = actor.VisibleHand.Count(c => c.InstanceId != action.CardId
            && (c.Kind == BasicCardKind.Slash || actor.Profile == BattleProfile.GuanyuStandard && BattleSkills.IsRed(c)));
        // 对手只使用公开手牌数；没有可响应杀时，避免主动进入有手牌对手的决斗。
        return slashes == 0 && target.HandCount > 0 ? -100
            : 360 + Math.Max(0, 5 - target.Hp) * 10 + (target.HandCount == 0 ? 100 : 0);
    }

    private static bool ShouldNullify(BattleView view, CombatantView actor)
    {
        if (view.Response is not { Kind: EffectKind.Nullification, Trick: { } trick } response) return false;
        var sourceAlly = view.Combatants.Single(u => u.EntityId == trick.SourceId).TeamId == actor.TeamId;
        var targetAlly = view.Combatants.Single(u => u.EntityId == trick.TargetId).TeamId == actor.TeamId;
        var benefit = trick.CardKind switch
        {
            BasicCardKind.ExNihilo => targetAlly ? 1 : -1,
            BasicCardKind.Duel or BasicCardKind.Dismantlement => targetAlly ? -1 : 1,
            BasicCardKind.Snatch => (sourceAlly ? 1 : -1) + (targetAlly ? -1 : 1),
            _ => 0
        };
        // 偶数层原锦囊有效，奇数层原锦囊无效；反制必须改善己方结果。
        return response.ChainDepth % 2 == 0 ? benefit < 0 : benefit > 0;
    }

    private static int KeepValue(CombatantView actor, string cardId)
    {
        var card = actor.VisibleHand.Single(c => c.InstanceId == cardId);
        var count = actor.VisibleHand.Count(c => c.Kind == card.Kind);
        return card.Kind switch
        {
            BasicCardKind.Peach => 100,
            BasicCardKind.ExNihilo => 90,
            BasicCardKind.Dodge => count > 2 ? 20 : 75,
            BasicCardKind.Nullification => count > 1 ? 30 : 65,
            BasicCardKind.Snatch => 70,
            BasicCardKind.Dismantlement => 55,
            BasicCardKind.Slash => count > 1 ? 25 : 45,
            _ => 40
        };
    }
}
