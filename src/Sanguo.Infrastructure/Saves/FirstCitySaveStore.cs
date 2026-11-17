using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sanguo.Application;

namespace Sanguo.Infrastructure.Saves;

public sealed record FirstCityEnvelope(string FormatId, int FormatVersion, string Checksum, string Payload);
public sealed record FirstCityLoadResult(FirstCitySessionState State, bool UsedBackup, string Message);

public static class FirstCitySnapshotCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    public static string StateHash(FirstCitySessionState state) => Hash(JsonSerializer.Serialize(state, Options));
    public static byte[] Encode(FirstCitySessionState state)
    {
        FirstCitySession.Validate(state);
        var payload = JsonSerializer.Serialize(state, Options);
        return JsonSerializer.SerializeToUtf8Bytes(new FirstCityEnvelope("sanguo.first-city", 1, Hash(payload), payload), Options);
    }
    public static FirstCitySessionState Decode(byte[] bytes)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<FirstCityEnvelope>(bytes, Options)
                ?? throw new SaveException("save.corrupt", "首城战役存档为空。");
            if (envelope.FormatId != "sanguo.first-city" || envelope.FormatVersion != 1)
                throw new SaveException("save.incompatible", "首城战役存档格式不兼容。");
            if (envelope.Checksum is null || envelope.Checksum.Length != 64 || !envelope.Checksum.All(Uri.IsHexDigit)
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(envelope.Checksum), Convert.FromHexString(Hash(envelope.Payload))))
                throw new SaveException("save.corrupt", "首城战役存档校验失败。");
            var state = JsonSerializer.Deserialize<FirstCitySessionState>(envelope.Payload, Options)
                ?? throw new SaveException("save.corrupt", "首城战役状态为空。");
            if (state.EngineVersion != FirstCitySession.EngineVersion || state.ContentHash != FirstCityScenario.ContentHash)
                throw new SaveException("save.incompatible", "首城战役引擎或内容版本不匹配。");
            FirstCitySession.Validate(state);
            return state;
        }
        catch (JsonException ex) { throw new SaveException("save.corrupt", "首城战役JSON无效。", ex); }
        catch (ArgumentException ex) { throw new SaveException("save.corrupt", "首城战役状态无效。", ex); }
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>战役单槽位，包含当前牌局；同目录原子替换、有效备份及损坏回退。</summary>
public sealed class FirstCitySaveStore
{
    public void Save(string path, FirstCitySessionState state)
    {
        var bytes = FirstCitySnapshotCodec.Encode(state);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Write(temporary, bytes);
            _ = FirstCitySnapshotCodec.Decode(File.ReadAllBytes(temporary));
            if (File.Exists(path))
            {
                try
                {
                    var previous = File.ReadAllBytes(path);
                    _ = FirstCitySnapshotCodec.Decode(previous);
                    var backupTemp = temporary + ".bak";
                    try { Write(backupTemp, previous); File.Move(backupTemp, path + ".bak", overwrite: true); }
                    finally { if (File.Exists(backupTemp)) File.Delete(backupTemp); }
                }
                catch (SaveException ex) when (ex.Code == "save.corrupt") { }
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public FirstCityLoadResult Load(string path)
    {
        try { return new(FirstCitySnapshotCodec.Decode(File.ReadAllBytes(path)), false, "已恢复战役与当前牌局。"); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException || ex is SaveException { Code: "save.corrupt" })
        {
            try { return new(FirstCitySnapshotCodec.Decode(File.ReadAllBytes(path + ".bak")), true, "主档缺失或损坏，已恢复上一份有效战役备份。"); }
            catch (Exception backupError) when (backupError is IOException or SaveException)
            { throw new SaveException("save.unrecoverable", "战役主档与备份均不可恢复，请保留文件后新建战役。", backupError); }
        }
    }
    private static void Write(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
