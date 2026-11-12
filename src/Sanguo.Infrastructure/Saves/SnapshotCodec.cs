using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sanguo.Application;

namespace Sanguo.Infrastructure.Saves;

public sealed class SaveException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed record SaveEnvelope(int SaveFormatVersion, string ChecksumAlgorithm, string Checksum, string Payload);

public static class SnapshotCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string StateHash(SessionState state) => Hash(JsonSerializer.Serialize(state, Options));

    public static byte[] Encode(SessionState state)
    {
        SessionValidation.Validate(state);
        var payload = JsonSerializer.Serialize(state, Options);
        return JsonSerializer.SerializeToUtf8Bytes(new SaveEnvelope(1, "SHA-256", Hash(payload), payload), Options);
    }

    public static SessionState Decode(byte[] bytes, string expectedContentHash)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SaveEnvelope>(bytes, Options)
                ?? throw new SaveException("save.corrupt", "存档信封为空。");
            if (envelope.SaveFormatVersion != 1 || envelope.ChecksumAlgorithm != "SHA-256")
                throw new SaveException("save.incompatible", "存档格式不兼容，请使用对应版本。");
            if (envelope.Payload is null || envelope.Checksum is null || envelope.Checksum.Length != 64
                || !envelope.Checksum.All(Uri.IsHexDigit)
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(envelope.Checksum), Convert.FromHexString(Hash(envelope.Payload))))
                throw new SaveException("save.corrupt", "存档校验失败，可尝试上一份有效备份。");
            var state = JsonSerializer.Deserialize<SessionState>(envelope.Payload, Options)
                ?? throw new SaveException("save.corrupt", "存档内容为空。");
            if (state.EngineVersion != ProbeSession.EngineVersion || state.RulesetId != Sanguo.BattleCore.Probe.DrawProbe.RulesetId
                || state.ContentHash != expectedContentHash)
                throw new SaveException("save.incompatible", "引擎、规则或内容摘要不匹配，请使用对应版本。");
            SessionValidation.Validate(state);
            return state;
        }
        catch (JsonException ex) { throw new SaveException("save.corrupt", "存档JSON损坏。", ex); }
        catch (ArgumentException ex) { throw new SaveException("save.corrupt", "存档状态无效。", ex); }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
