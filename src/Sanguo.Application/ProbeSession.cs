using System.Collections.Immutable;
using Sanguo.BattleCore.Probe;
using Sanguo.CampaignCore;

namespace Sanguo.Application;

public sealed record DrawProbeCommand(string CommandId, long ExpectedStateVersion, string ControllerId, string RequestId);
public sealed record CommandReceipt(DrawProbeCommand Command, long CommittedVersion);
public sealed record SessionState(
    string EngineVersion,
    string RulesetId,
    string ContentHash,
    long StateVersion,
    CampaignState Campaign,
    DrawProbeState Probe,
    ImmutableArray<CommandReceipt> Receipts);
public sealed record CommandResult(bool Accepted, bool Duplicate, string Code, string Message, long StateVersion);

public sealed class ProbeSession
{
    public const string EngineVersion = "0.1.0-g0";
    private readonly object _gate = new();
    public SessionState State { get; private set; }

    public ProbeSession(SessionState state)
    {
        SessionValidation.Validate(state);
        State = state;
    }

    public static ProbeSession Create(string campaignId, Origin origin, IEnumerable<ProbeCard> cards, string contentHash, ulong seed)
        => new(new(EngineVersion, DrawProbe.RulesetId, contentHash, 0,
            CampaignState.Create(campaignId, origin), DrawProbe.Create(cards, seed), []));

    public CommandResult Submit(DrawProbeCommand command)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(command.CommandId)) return Reject("command.invalid", "命令ID不能为空。");
            var previous = State.Receipts.FirstOrDefault(r => r.Command.CommandId == command.CommandId);
            if (previous is not null)
                return previous.Command == command
                    ? new(true, true, "command.duplicate", "已提交，无需重复执行。", previous.CommittedVersion)
                    : Reject("command.id_conflict", "命令ID已用于其他输入，请刷新操作。");
            if (command.ExpectedStateVersion != State.StateVersion)
                return Reject("state.stale", "状态已更新，请重新选择。");
            var decision = State.Probe.PendingDecision;
            if (decision is null) return Reject("decision.none", "试验已结束，请新建会话。");
            if (command.ControllerId != decision.ControllerId)
                return Reject("controller.denied", "当前控制者没有操作权限。");
            if (command.RequestId != decision.RequestId)
                return Reject("decision.stale", "请求已失效，请重新选择。");
            var version = checked(State.StateVersion + 1);
            State = State with
            {
                StateVersion = version,
                Probe = DrawProbe.Draw(State.Probe, version),
                Receipts = State.Receipts.Add(new(command, version))
            };
            return new(true, false, "command.accepted", "已抽取一张试验牌。", version);
        }
    }

    private CommandResult Reject(string code, string message) => new(false, false, code, message, State.StateVersion);
}
