using Sanguo.Application;

namespace Sanguo.Infrastructure.Saves;

public sealed record SaveLoadResult(SessionState State, bool UsedBackup, string Message);

/// <summary>调用方在单写入队列的稳定决策点传入不可变快照；临时文件位于同一目录。</summary>
public sealed class FileSaveStore
{
    public void Save(string path, SessionState state)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = SnapshotCodec.Encode(state);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _ = SnapshotCodec.Decode(File.ReadAllBytes(temporary), state.ContentHash);
            if (File.Exists(path))
            {
                // 不允许损坏档覆盖有效备份；版本不兼容也不允许静默覆盖。
                try
                {
                    var previous = File.ReadAllBytes(path);
                    _ = SnapshotCodec.Decode(previous, state.ContentHash);
                    WriteBackup(path + ".bak", previous);
                }
                catch (SaveException ex) when (ex.Code == "save.corrupt") { }
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public SaveLoadResult Load(string path, string expectedContentHash)
    {
        try
        {
            return new(SnapshotCodec.Decode(File.ReadAllBytes(path), expectedContentHash), false, "已恢复当前存档。");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            || ex is SaveException { Code: "save.corrupt" })
        {
            try
            {
                var backup = SnapshotCodec.Decode(File.ReadAllBytes(path + ".bak"), expectedContentHash);
                return new(backup, true, "当前档缺失或损坏，已恢复上一份有效备份。");
            }
            catch (Exception backupError) when (backupError is IOException or SaveException)
            {
                throw new SaveException("save.unrecoverable", "当前档与备份均不可恢复，请保留文件并选择其他存档。", backupError);
            }
        }
    }

    private static void WriteBackup(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
