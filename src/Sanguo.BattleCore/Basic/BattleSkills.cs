using System.Collections.Immutable;

namespace Sanguo.BattleCore.Basic;

public enum BattleProfile { Generic, GuanyuStandard, CaocaoStandard, ProtagonistFenjiI }
public enum SkillTiming { SlashPayment, AfterDamage, SlashUse, DodgeRequest }
public enum SkillUsageScope { Unlimited, PlayPhase }
public sealed record BattleSkillDefinition(string RuleId, string SourceId, string Name, string Text,
    SkillTiming Timing, SkillUsageScope UsageScope, bool RequiresLord = false);
public sealed record BattleSkillView(BattleSkillDefinition Definition, bool Enabled, string? InactiveReason);

/// <summary>编译期开发目录；与正式OL内容包隔离，不支持运行时拼装或双将。</summary>
public static class BattleSkills
{
    public const string Wusheng = "skill.development.guanyu.wusheng.v1";
    public const string Jianxiong = "skill.development.caocao.jianxiong.v1";
    public const string Hujia = "skill.development.caocao.hujia.v1";
    public const string FenjiI = "protagonist.fenji.tier1";

    private static readonly ImmutableArray<BattleSkillDefinition> Guanyu =
    [new(Wusheng, "ol.standard.guanyu", "武圣", "你可以将一张红色牌当【杀】使用或打出。",
        SkillTiming.SlashPayment, SkillUsageScope.Unlimited)];
    private static readonly ImmutableArray<BattleSkillDefinition> Caocao =
    [
        new(Jianxiong, "ol.standard.caocao", "奸雄", "当你受到伤害后，你可以获得造成此伤害的牌。",
            SkillTiming.AfterDamage, SkillUsageScope.Unlimited),
        new(Hujia, "ol.standard.caocao", "护驾", "主公技，其他魏势力角色可以在你需要时代替你使用或打出【闪】（视为由你使用或打出）。",
            SkillTiming.DodgeRequest, SkillUsageScope.Unlimited, RequiresLord: true)
    ];
    private static readonly ImmutableArray<BattleSkillDefinition> Fenji =
    [new(FenjiI, "protagonist.damage", "奋击I阶", "出牌阶段限一次，使用杀且最终仅有一个目标时，可额外弃置一张手牌；该次杀对该目标首次造成的伤害加1。",
        SkillTiming.SlashUse, SkillUsageScope.PlayPhase)];

    public static ImmutableArray<BattleSkillDefinition> For(BattleProfile profile) => profile switch
    {
        BattleProfile.Generic => [], BattleProfile.GuanyuStandard => Guanyu,
        BattleProfile.CaocaoStandard => Caocao, BattleProfile.ProtagonistFenjiI => Fenji,
        _ => throw new ArgumentException("未知开发角色配置。", nameof(profile))
    };

    // 当前仅公开阵营模式，无主公身份；不能以队首或先手位置激活主公技。
    public static bool Has(Combatant unit, string ruleId)
        => For(unit.Profile).Any(s => s.RuleId == ruleId && !s.RequiresLord);
    public static ImmutableArray<BattleSkillView> Observe(BattleProfile profile)
        => For(profile).Select(s => new BattleSkillView(s, !s.RequiresLord,
            s.RequiresLord ? "当前公开阵营模式没有主公身份。" : null)).ToImmutableArray();
    public static bool IsRed(BattleCard card) => card.Suit is "heart" or "diamond";
}
