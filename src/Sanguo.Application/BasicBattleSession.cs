using System.Collections.Immutable;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Application;

public sealed record BattleCommand(string CommandId, long ExpectedStateVersion, string ControllerId,
    string RequestId, string ActorId, BattleAction Action);
public sealed record BattleCommandReceipt(BattleCommand Command, long CommittedVersion);
public sealed record BasicBattleSessionState(string EngineVersion, string ContentHash, BattleState Battle,
    ImmutableArray<BattleCommandReceipt> Receipts);

/// <summary>独立于G0抽牌会话，基础战斗尚不产生战役奖励或占城事件。</summary>
public sealed class BasicBattleSession
{
    public const string EngineVersion = "0.5.0-basic-development";
    private readonly object _gate = new();
    public BasicBattleSessionState State { get; private set; }

    public BasicBattleSession(BasicBattleSessionState state)
    {
        Validate(state);
        State = state;
    }

    public static BasicBattleSession Create(string battleId, IEnumerable<CombatantSetup> units,
        IEnumerable<BattleCard> cards, string contentHash, ulong seed, int initialHandSize = 4)
        => new(new(EngineVersion, contentHash, BasicBattle.Create(battleId, units, cards, seed, initialHandSize), []));

    public CommandResult Submit(BattleCommand command)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(command.CommandId)) return Reject("command.invalid", "命令ID不能为空。");
            var previous = State.Receipts.FirstOrDefault(r => r.Command.CommandId == command.CommandId);
            if (previous is not null)
                return previous.Command == command
                    ? new(true, true, "command.duplicate", "已提交，无需重复执行。", previous.CommittedVersion)
                    : Reject("command.id_conflict", "命令ID已被其他输入使用。");
            var battle = State.Battle;
            if (command.ExpectedStateVersion != battle.StateVersion) return Reject("state.stale", "状态已更新，请重新选择。");
            var decision = battle.PendingDecision;
            if (decision is null) return Reject("decision.none", "战斗已结束。");
            if (command.ControllerId != decision.ControllerId) return Reject("controller.denied", "当前控制者没有操作权限。");
            if (command.RequestId != decision.RequestId) return Reject("decision.stale", "请求已失效，请重新选择。");
            if (command.ActorId != decision.ActorId) return Reject("actor.denied", "当前操作单位不匹配。");
            if (!BasicBattle.GetLegalActions(battle).Contains(command.Action)) return Reject("action.illegal", "当前动作、目标或支付牌不合法。");
            var next = BasicBattle.Apply(battle, command.Action);
            BattleValidation.Validate(next);
            State = State with { Battle = next, Receipts = State.Receipts.Add(new(command, next.StateVersion)) };
            return new(true, false, "command.accepted", "战斗操作已提交。", next.StateVersion);
        }
    }

    public static void Validate(BasicBattleSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.EngineVersion != EngineVersion || state.ContentHash is null || state.ContentHash.Length != 64
            || !state.ContentHash.All(Uri.IsHexDigit) || state.Receipts.IsDefault)
            throw new ArgumentException("基础战斗会话版本、内容摘要或去重记录无效。");
        BattleValidation.Validate(state.Battle);
        if (state.Receipts.Length != state.Battle.StateVersion) throw new ArgumentException("命令记录与状态版本不一致。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < state.Receipts.Length; i++)
        {
            var receipt = state.Receipts[i];
            if (receipt?.Command is not { } command || string.IsNullOrWhiteSpace(command.CommandId)
                || !ids.Add(command.CommandId) || receipt.CommittedVersion != i + 1L || command.ExpectedStateVersion != i
                || command.RequestId != $"{state.Battle.BattleId}.decision.{i}"
                || !state.Battle.Combatants.Any(u => u.EntityId == command.ActorId && u.ControllerId == command.ControllerId)
                || command.Action is null || !Enum.IsDefined(command.Action.Kind))
                throw new ArgumentException("命令去重记录无效。");
        }
    }

    private CommandResult Reject(string code, string message) => new(false, false, code, message, State.Battle.StateVersion);
}
