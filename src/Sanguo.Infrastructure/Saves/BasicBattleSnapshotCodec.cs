using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Infrastructure.Saves;

public sealed record BasicBattleEnvelope(string FormatId, int FormatVersion, string Checksum, string Payload);

/// <summary>基础战斗开发快照，与已交付G0存档分开版本化，不承担自动迁移。</summary>
public static class BasicBattleSnapshotCodec
{
    private const string FormatId = "sanguo.basic-battle";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string StateHash(BasicBattleSessionState state) => Hash(JsonSerializer.Serialize(state, Options));

    public static byte[] Encode(BasicBattleSessionState state)
    {
        BasicBattleSession.Validate(state);
        var payload = JsonSerializer.Serialize(state, Options);
        return JsonSerializer.SerializeToUtf8Bytes(new BasicBattleEnvelope(FormatId, 2, Hash(payload), payload), Options);
    }

    public static BasicBattleSessionState Decode(byte[] bytes, string expectedContentHash)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<BasicBattleEnvelope>(bytes, Options)
                ?? throw new SaveException("save.corrupt", "基础战斗快照为空。");
            if (envelope.FormatId != FormatId || envelope.FormatVersion != 2)
                throw new SaveException("save.incompatible", "基础战斗快照格式不兼容。");
            if (envelope.Payload is null || envelope.Checksum is null || envelope.Checksum.Length != 64
                || !envelope.Checksum.All(Uri.IsHexDigit)
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(envelope.Checksum), Convert.FromHexString(Hash(envelope.Payload))))
                throw new SaveException("save.corrupt", "基础战斗快照校验失败。");
            var state = JsonSerializer.Deserialize<BasicBattleSessionState>(envelope.Payload, Options)
                ?? throw new SaveException("save.corrupt", "基础战斗状态为空。");
            if (state.EngineVersion != BasicBattleSession.EngineVersion || state.ContentHash != expectedContentHash
                || state.Battle is not { RulesetId: BasicBattle.RulesetId })
                throw new SaveException("save.incompatible", "基础战斗引擎、规则或内容摘要不匹配。");
            BasicBattleSession.Validate(state);
            return state;
        }
        catch (JsonException ex) { throw new SaveException("save.corrupt", "基础战斗快照JSON损坏。", ex); }
        catch (ArgumentException ex) { throw new SaveException("save.corrupt", "基础战斗快照状态无效。", ex); }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
