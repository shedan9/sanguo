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

    public static BasicBattleSession Create(ulong seed = 42, int playerCount = 2, int opponentCount = 2)
        => BasicBattleSession.Create("battle.client-development", GetUnits(playerCount, opponentCount), Cards,
            HashFor(playerCount, opponentCount), seed);

    public static string HashFor(int playerCount, int opponentCount)
    {
        var units = GetUnits(playerCount, opponentCount);
        return playerCount == 2 && opponentCount == 2 ? ContentHash
            : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                Scenario = "client.development.v1", BasicBattle.RulesetId, Units = units, Cards,
                Skills = units.SelectMany(u => BattleSkills.For(u.Profile)).Distinct().ToArray()
            })));
    }

    private static CombatantSetup[] GetUnits(int playerCount, int opponentCount)
    {
        if (playerCount is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(playerCount));
        if (opponentCount is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(opponentCount));
        var units = new List<CombatantSetup>();
        for (var i = 0; i < Math.Max(playerCount, opponentCount); i++)
        {
            if (i < playerCount) units.Add(i < 2 ? Units[i * 2] : new($"generic.a{i + 1}", "player", "a"));
            if (i < opponentCount) units.Add(i < 2 ? Units[i * 2 + 1] : new($"generic.b{i + 1}", "opponent", "b"));
        }
        return units.ToArray();
    }
}
