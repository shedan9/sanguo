namespace Sanguo.BattleCore.Basic;

public static partial class BasicBattle
{
    private static BattleState PaySlashSkill(BattleState state, string actorId, BattleAction action)
    {
        if (action.SkillId == BattleSkills.FenjiI)
        {
            state = FinishPayment(Pay(state, actorId, action.CostCardId!), action.CostCardId!, actorId);
            state = state with { FenjiUsed = true };
        }
        return action.SkillId is null ? state : LogSkill(state, actorId, action);
    }

    private static BattleState LogSkill(BattleState state, string actorId, BattleAction action)
        => Log(state, "skill.used", actorId, action.TargetId, action.CardId, relatedCardId: action.SkillId);

    private static BattleState ResolveAfterDamage(BattleState state, EffectFrame damage)
    {
        var target = Unit(state, damage.TargetId);
        // 濒死流程先完成；死亡者不能再以普通技能行动权获取伤害牌。
        if (target.Alive && target.Hp > 0 && BattleSkills.Has(target, BattleSkills.Jianxiong)
            && state.Processing.Any(c => c.InstanceId == damage.CardId))
            return Push(state, damage with { Kind = EffectKind.InvokeJianxiong, SkillId = BattleSkills.Jianxiong });
        return state;
    }

    private static BattleState AcquireDamageCard(BattleState state, string actorId)
    {
        var frame = state.Frames[^1];
        var card = state.Processing.Single(c => c.InstanceId == frame.CardId);
        var actor = Unit(state, actorId);
        state = SetUnit(Pop(state) with { Processing = state.Processing.Remove(card) }, actor with { Hand = actor.Hand.Add(card) });
        return Log(state, "skill.jianxiong_gained", actorId, frame.SourceId, card.InstanceId,
            relatedCardId: BattleSkills.Jianxiong);
    }
}
