using System.Collections.Immutable;
using Sanguo.Application;
using Sanguo.BattleCore.Probe;
using Sanguo.BattleCore.Randomness;
using Sanguo.CampaignCore;
using Sanguo.Infrastructure.Saves;
using Xunit;

namespace Sanguo.Integration.Tests;

public class ProbeTests
{
    private static readonly string ContentHash = new('a', 64);

    [Fact]
    public void Pcg32MatchesUpstreamSeed42Stream54Vector()
    {
        var random = new Pcg32(42, 54);
        uint[] expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];
        Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(_ => random.NextUInt32()));
        Assert.Equal(6UL, random.Capture().DrawCount);
    }

    [Fact]
    public void RandomSnapshotResumesIncludingBoundedSamples()
    {
        var original = new Pcg32(1234, 54);
        for (var i = 0; i < 17; i++) original.NextInt(1000000001);
        var restored = new Pcg32(original.Capture());
        for (var i = 0; i < 1000; i++) Assert.Equal(original.NextInt(1000000001), restored.NextInt(1000000001));
        Assert.Equal(original.Capture(), restored.Capture());
    }

    [Fact]
    public void InvalidRandomBoundDoesNotConsumeRandomness()
    {
        var random = new Pcg32(42, 54);
        var before = random.Capture();
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(0));
        Assert.Equal(before, random.Capture());
        Assert.Throws<ArgumentException>(() => new Pcg32(before with { Increment = 2 }));
    }

    [Fact]
    public void ResumeAtEveryDecisionFor100SeedsMatchesContinuousRunAndConservesCards()
    {
        for (ulong seed = 0; seed < 100; seed++)
        {
            var session = Create(seed);
            var originalIds = session.State.Probe.DrawPile.Select(c => c.InstanceId).Order().ToArray();
            while (session.State.Probe.PendingDecision is not null)
            {
                var restored = new ProbeSession(SnapshotCodec.Decode(SnapshotCodec.Encode(session.State), ContentHash));
                var command = NextCommand(session);
                Assert.True(session.Submit(command).Accepted);
                Assert.True(restored.Submit(command).Accepted);
                Assert.Equal(SnapshotCodec.StateHash(session.State), SnapshotCodec.StateHash(restored.State));
                Assert.Equal(originalIds, session.State.Probe.DrawPile.Concat(session.State.Probe.Hand).Select(c => c.InstanceId).Order());
            }
            Assert.Equal(8, session.State.StateVersion);
            Assert.Equal("decision.none", session.Submit(new("extra", 8, "player", "none")).Code);
        }
    }

    [Theory]
    [InlineData(-1, "player", "probe.draw.0", "state.stale")]
    [InlineData(0, "enemy", "probe.draw.0", "controller.denied")]
    [InlineData(0, "player", "old-request", "decision.stale")]
    public void InvalidCommandsLeaveFullStateAndRandomnessUntouched(long version, string controller, string request, string code)
    {
        var session = Create();
        var before = SnapshotCodec.StateHash(session.State);
        var result = session.Submit(new("invalid", version, controller, request));
        Assert.False(result.Accepted);
        Assert.Equal(code, result.Code);
        Assert.Equal(before, SnapshotCodec.StateHash(session.State));
    }

    [Fact]
    public void AcceptedCommandIsIdempotentAfterSaveRestoreAndConflictsAreRejected()
    {
        var session = Create();
        var command = NextCommand(session);
        Assert.True(session.Submit(command).Accepted);
        var restored = new ProbeSession(SnapshotCodec.Decode(SnapshotCodec.Encode(session.State), ContentHash));
        var before = SnapshotCodec.StateHash(restored.State);
        var repeated = restored.Submit(command);
        Assert.True(repeated.Accepted && repeated.Duplicate);
        Assert.Equal("command.id_conflict", restored.Submit(command with { ControllerId = "enemy" }).Code);
        Assert.Equal(before, SnapshotCodec.StateHash(restored.State));
    }

    [Theory]
    [InlineData(Origin.Damage)]
    [InlineData(Origin.Control)]
    [InlineData(Origin.Tank)]
    public void CampaignOriginAndLargeRosterSurviveSnapshots(Origin origin)
    {
        var state = Create().State;
        state = state with { Campaign = CampaignState.Create("campaign.origin", origin) with
            { Roster = Enumerable.Range(0, 200).Select(i => $"synthetic.{i}").ToImmutableArray() } };
        var restored = SnapshotCodec.Decode(SnapshotCodec.Encode(state), ContentHash);
        Assert.Equal(origin, restored.Campaign.Origin);
        Assert.Equal(200, restored.Campaign.Roster.Length);
        Assert.Equal(state.Campaign.Roster.ToArray(), restored.Campaign.Roster.ToArray());
    }

    [Fact]
    public void SnapshotRejectsTamperingAndIncompatibleContent()
    {
        var bytes = SnapshotCodec.Encode(Create().State);
        var modified = System.Text.Encoding.UTF8.GetString(bytes).Replace("campaign.test", "campaign.evil", StringComparison.Ordinal);
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => SnapshotCodec.Decode(System.Text.Encoding.UTF8.GetBytes(modified), ContentHash)).Code);
        Assert.Equal("save.incompatible", Assert.Throws<SaveException>(() => SnapshotCodec.Decode(bytes, new string('b', 64))).Code);
        Assert.Equal("save.corrupt", Assert.Throws<SaveException>(() => SnapshotCodec.Decode("{"u8.ToArray(), ContentHash)).Code);
    }

    [Fact]
    public void SnapshotRejectsInvalidDecisionAndDuplicatedPhysicalCards()
    {
        var state = Create().State;
        Assert.Throws<ArgumentException>(() => SnapshotCodec.Encode(state with
            { Probe = state.Probe with { PendingDecision = null } }));
        Assert.Throws<ArgumentException>(() => SnapshotCodec.Encode(state with
            { Probe = state.Probe with { DrawPile = state.Probe.DrawPile.Add(state.Probe.DrawPile[0]) } }));
    }

    [Fact]
    public void ConcurrentDuplicateCommandsCommitOnce()
    {
        var session = Create();
        var command = NextCommand(session);
        Parallel.For(0, 20, _ => Assert.True(session.Submit(command).Accepted));
        Assert.Equal(1, session.State.StateVersion);
        Assert.Single(session.State.Probe.Hand);
        Assert.Single(session.State.Receipts);
    }

    internal static ProbeSession Create(ulong seed = 42)
        => ProbeSession.Create("campaign.test", Origin.Damage,
            Enumerable.Range(1, 8).Select(i => new ProbeCard($"card.{i}", "probe.slash", "spade", i)), ContentHash, seed);

    internal static DrawProbeCommand NextCommand(ProbeSession session)
        => new($"command.{session.State.StateVersion}", session.State.StateVersion, "player", session.State.Probe.PendingDecision!.RequestId);
}
