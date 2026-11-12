using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public sealed class G0AcceptanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sanguo-acceptance-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SeparateSessionsVerifyContinuationIdempotencyAndBackupRecovery()
    {
        var writeHash = G0AcceptanceProbe.Write(_directory, ProbeTests.Create().State);
        var resumeHash = G0AcceptanceProbe.Resume(_directory, ProbeTests.Create().State);
        Assert.NotEqual(writeHash, resumeHash);
        var loaded = new FileSaveStore().Load(Path.Combine(_directory, "acceptance-session.json"), ProbeTests.Create().State.ContentHash);
        Assert.False(loaded.UsedBackup);
        Assert.Equal(4, loaded.State.StateVersion);
        Assert.Equal(resumeHash, SnapshotCodec.StateHash(loaded.State));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void WriteRefusesAnExistingDirectoryWithFiles()
    {
        G0AcceptanceProbe.Write(_directory, ProbeTests.Create().State);
        var path = Path.Combine(_directory, "acceptance-session.json");
        var original = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => G0AcceptanceProbe.Write(_directory, ProbeTests.Create().State));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ResumeRejectsAnotherSeedBeforeChangingFiles()
    {
        G0AcceptanceProbe.Write(_directory, ProbeTests.Create().State);
        var path = Path.Combine(_directory, "acceptance-session.json");
        var original = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => G0AcceptanceProbe.Resume(_directory, ProbeTests.Create(99).State));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void ResumeRequiresTheWriteStage()
        => Assert.Equal("save.unrecoverable", Assert.Throws<SaveException>(() =>
            G0AcceptanceProbe.Resume(_directory, ProbeTests.Create().State)).Code);

    [Fact]
    public void ResumeDoesNotAcceptABackupAsTheOriginalCheckpoint()
    {
        G0AcceptanceProbe.Write(_directory, ProbeTests.Create().State);
        var path = Path.Combine(_directory, "acceptance-session.json");
        File.Move(path, path + ".bak");
        Assert.Throws<InvalidOperationException>(() => G0AcceptanceProbe.Resume(_directory, ProbeTests.Create().State));
        Assert.False(File.Exists(path));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
