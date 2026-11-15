using System.Security.Cryptography;
using System.Text.Json;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Application;

/// <summary>客户端合成开发场景，不加载正式OL人物或实体牌内容包。</summary>
public static class DevelopmentBattleScenario
{
    private static readonly CombatantSetup[] Units =
    [
        new("development.guanyu", "player", "a", Profile: BattleProfile.GuanyuStandard),
        new("development.caocao", "opponent", "b", Profile: BattleProfile.CaocaoStandard),
        new("development.protagonist", "player", "a", Profile: BattleProfile.ProtagonistFenjiI),
        new("generic.b2", "opponent", "b")
    ];
    private static readonly BattleCard[] Cards = Enumerable.Range(0, 64).Select(i => new BattleCard(
        $"client.development.{i:D3}", (i % 16) switch
        {
            0 or 1 => BasicCardKind.Dodge, 2 => BasicCardKind.Peach,
            3 or 4 => BasicCardKind.Duel, 5 => BasicCardKind.ExNihilo,
            6 or 7 => BasicCardKind.Nullification, 8 => BasicCardKind.Dismantlement,
            9 => BasicCardKind.Snatch, _ => BasicCardKind.Slash
        }, new[] { "spade", "heart", "club", "diamond" }[i % 4], i % 13 + 1)).ToArray();

    public static string ContentHash { get; } = Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Scenario = "client.development.v1", BasicBattle.RulesetId,
            Units, Cards, Skills = Units.SelectMany(u => BattleSkills.For(u.Profile)).Distinct().ToArray() })));

    public static BasicBattleSession Create(ulong seed = 42)
        => BasicBattleSession.Create("battle.client-development", Units, Cards, ContentHash, seed);
}
