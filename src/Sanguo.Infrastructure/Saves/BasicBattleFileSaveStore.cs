using Sanguo.Application;

namespace Sanguo.Infrastructure.Saves;

public sealed record BasicBattleLoadResult(BasicBattleSessionState State, bool UsedBackup, string Message);

/// <summary>开发战斗独立槽位；临时文件刷新及校验后替换，保留上一份有效备份。</summary>
public sealed class BasicBattleFileSaveStore
{
    public void Save(string path, BasicBattleSessionState state)
    {
        var bytes = BasicBattleSnapshotCodec.Encode(state);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteFlushed(temporary, bytes);
            _ = BasicBattleSnapshotCodec.Decode(File.ReadAllBytes(temporary), state.ContentHash);
            if (File.Exists(path))
            {
                try
                {
                    var previous = File.ReadAllBytes(path);
                    _ = BasicBattleSnapshotCodec.Decode(previous, state.ContentHash);
                    ReplaceBackup(path + ".bak", previous);
                }
                catch (SaveException ex) when (ex.Code == "save.corrupt") { }
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public BasicBattleLoadResult Load(string path, string expectedContentHash)
    {
        try
        {
            return new(BasicBattleSnapshotCodec.Decode(File.ReadAllBytes(path), expectedContentHash), false, "已恢复战斗决策点。");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            || ex is SaveException { Code: "save.corrupt" })
        {
            try
            {
                return new(BasicBattleSnapshotCodec.Decode(File.ReadAllBytes(path + ".bak"), expectedContentHash), true,
                    "当前战斗档缺失或损坏，已恢复上一份有效备份。");
            }
            catch (Exception backupError) when (backupError is IOException or SaveException)
            {
                throw new SaveException("save.unrecoverable", "战斗档与备份均不可恢复，请保留文件后新建战斗。", backupError);
            }
        }
    }

    private static void ReplaceBackup(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteFlushed(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteFlushed(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
