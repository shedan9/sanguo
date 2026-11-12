using Sanguo.Application;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class FileSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sanguo-save-test-" + Guid.NewGuid().ToString("N"));
    private string SavePath => Path.Combine(_directory, "slot-1.json");

    [Fact]
    public void DamagedPrimaryFallsBackAndResavingPreservesValidBackup()
    {
        var store = new FileSaveStore();
        var session = ProbeTests.Create();
        store.Save(SavePath, session.State);
        var firstHash = SnapshotCodec.StateHash(session.State);
        session.Submit(ProbeTests.NextCommand(session));
        store.Save(SavePath, session.State);
        Assert.False(store.Load(SavePath, session.State.ContentHash).UsedBackup);
        File.WriteAllText(SavePath, "{broken");
        var fallback = store.Load(SavePath, session.State.ContentHash);
        Assert.True(fallback.UsedBackup);
        Assert.Equal(firstHash, SnapshotCodec.StateHash(fallback.State));
        var backupBytes = File.ReadAllBytes(SavePath + ".bak");
        store.Save(SavePath, fallback.State);
        Assert.Equal(backupBytes, File.ReadAllBytes(SavePath + ".bak"));
        Assert.False(store.Load(SavePath, session.State.ContentHash).UsedBackup);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void IncompatibleSaveIsNotSilentlyOverwrittenOrLoadedFromOldBackup()
    {
        var store = new FileSaveStore();
        var original = ProbeTests.Create().State;
        store.Save(SavePath, original);
        var bytes = File.ReadAllBytes(SavePath);
        var changed = original with { ContentHash = new string('b', 64) };
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => store.Save(SavePath, changed)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(SavePath));
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => store.Load(SavePath, changed.ContentHash)).Code);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void InvalidNewSnapshotLeavesPreviousSaveUntouched()
    {
        var store = new FileSaveStore();
        var state = ProbeTests.Create().State;
        store.Save(SavePath, state);
        var bytes = File.ReadAllBytes(SavePath);
        Assert.Throws<ArgumentException>(() => store.Save(SavePath, state with { StateVersion = 99 }));
        Assert.Equal(bytes, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void MissingOrBothDamagedSlotsReturnActionableFailure()
    {
        var store = new FileSaveStore();
        Assert.Equal("save.unrecoverable", Assert.Throws<SaveException>(() => store.Load(SavePath, new string('a', 64))).Code);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SavePath, "bad");
        File.WriteAllText(SavePath + ".bak", "bad");
        Assert.Equal("save.unrecoverable", Assert.Throws<SaveException>(() => store.Load(SavePath, new string('a', 64))).Code);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
