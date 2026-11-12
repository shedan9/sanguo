using Sanguo.Application;

namespace Sanguo.Infrastructure.Saves;

/// <summary>仅操作专用验收目录；写入和恢复可在两个独立客户端进程中执行。</summary>
public static class G0AcceptanceProbe
{
    public static string Write(string directory, SessionState initial)
    {
        var session = Advance(initial, 3);
        var path = Path.Combine(directory, "acceptance-session.json");
        Directory.CreateDirectory(directory);
        // 不覆盖前一次验收或玩家存档。
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("验收写入需要一个空目录，请为本轮创建新目录。");
        new FileSaveStore().Save(path, session.State);
        return SnapshotCodec.StateHash(session.State);
    }

    public static string Resume(string directory, SessionState initial)
    {
        var store = new FileSaveStore();
        var path = Path.Combine(directory, "acceptance-session.json");
        var expected = Advance(initial, 3);
        var loaded = store.Load(path, initial.ContentHash);
        if (loaded.UsedBackup || SnapshotCodec.StateHash(loaded.State) != SnapshotCodec.StateHash(expected.State))
            throw new InvalidOperationException("跨进程恢复未读到预期的三张牌检查点。");
        var session = new ProbeSession(loaded.State);
        var command = Next(session);
        Submit(session, command);
        Submit(expected, command);
        var hash = SnapshotCodec.StateHash(expected.State);
        if (SnapshotCodec.StateHash(session.State) != hash)
            throw new InvalidOperationException("跨进程恢复后的下一步与连续运行不一致。");

        var duplicate = session.Submit(command);
        var conflict = session.Submit(command with { ControllerId = "other" });
        if (!duplicate.Accepted || !duplicate.Duplicate || conflict.Accepted
            || conflict.Code != "command.id_conflict" || SnapshotCodec.StateHash(session.State) != hash)
            throw new InvalidOperationException("恢复后的命令去重或冲突拒绝失败。");

        store.Save(path, session.State);
        var backup = File.ReadAllBytes(path + ".bak");
        // 仅损坏本轮专用测试档，验证Windows文件替换、备份和修复路径。
        File.WriteAllText(path, "{acceptance-corrupt");
        var fallback = store.Load(path, initial.ContentHash);
        if (!fallback.UsedBackup || SnapshotCodec.StateHash(fallback.State) != SnapshotCodec.StateHash(loaded.State))
            throw new InvalidOperationException("损坏存档未回退到上一有效检查点。");
        store.Save(path, fallback.State);
        if (!File.ReadAllBytes(path + ".bak").SequenceEqual(backup) || store.Load(path, initial.ContentHash).UsedBackup)
            throw new InvalidOperationException("修复存档后未保留有效备份。");
        var recovered = new ProbeSession(store.Load(path, initial.ContentHash).State);
        Submit(recovered, command);
        if (SnapshotCodec.StateHash(recovered.State) != hash)
            throw new InvalidOperationException("备份恢复后的下一步不一致。");
        store.Save(path, recovered.State);
        if (Directory.EnumerateFiles(directory, "*.tmp").Any())
            throw new InvalidOperationException("验收目录残留临时存档。");
        return hash;
    }

    private static ProbeSession Advance(SessionState initial, int steps)
    {
        if (initial.StateVersion != 0 || initial.Probe.DrawPile.Length < 4)
            throw new ArgumentException("验收需要至少四张牌的初始试验会话。", nameof(initial));
        var session = new ProbeSession(initial);
        for (var i = 0; i < steps; i++) Submit(session, Next(session));
        return session;
    }

    private static DrawProbeCommand Next(ProbeSession session)
        => new($"acceptance.draw.{session.State.StateVersion}", session.State.StateVersion,
            "player", session.State.Probe.PendingDecision!.RequestId);

    private static void Submit(ProbeSession session, DrawProbeCommand command)
    {
        var result = session.Submit(command);
        if (!result.Accepted || result.Duplicate) throw new InvalidOperationException(result.Message);
    }
}
