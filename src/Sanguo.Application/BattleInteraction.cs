using Sanguo.BattleCore.Basic;

namespace Sanguo.Application;

/// <summary>预览不提交命令；新请求、恢复和提交均清空选择，AI每次仅推进一个决策。</summary>
public sealed class BattleInteraction
{
    private BasicBattleSession _session;
    private long _selectionVersion = -1;
    public string PlayerControllerId { get; }
    public BattleAction? Selection { get; private set; }
    public BattleView View => BasicBattle.Observe(_session.State.Battle, PlayerControllerId);
    public bool WaitingForAi => View.PendingDecision is { } d && d.ControllerId != PlayerControllerId;
    public BasicBattleSessionState Snapshot => _session.State;

    public BattleInteraction(BasicBattleSession session, string playerControllerId = "player")
    {
        _session = session;
        PlayerControllerId = playerControllerId;
    }

    public bool Select(BattleAction action)
    {
        var view = View;
        if (!view.LegalActions.Contains(action)) return false;
        Selection = action;
        _selectionVersion = view.StateVersion;
        return true;
    }

    public void Cancel() { Selection = null; _selectionVersion = -1; }

    public System.Collections.Immutable.ImmutableArray<PublicBattleEvent> ReadLog(long afterSequence = 0)
        => PublicBattleLog.Observe(_session.State.Battle, afterSequence);

    public CommandResult Confirm()
    {
        if (Selection is not { } action)
            return new(false, false, "selection.none", "请先选择牌、目标或响应动作。", View.StateVersion);
        var version = _selectionVersion;
        Cancel();
        var view = View;
        if (version != view.StateVersion)
            return new(false, false, "selection.stale", "请求已更新，请重新选择。", view.StateVersion);
        return Submit(action, view);
    }

    public CommandResult? AdvanceAi(Func<BattleView, BattleAction?> choose)
    {
        if (!WaitingForAi) return null;
        var view = BasicBattle.Observe(_session.State.Battle, View.PendingDecision!.ControllerId);
        var action = choose(view) ?? throw new InvalidOperationException("AI没有返回动作。");
        if (!view.LegalActions.Contains(action)) throw new InvalidOperationException("AI返回非法动作。");
        Cancel();
        return Submit(action, view);
    }

    public void Restore(BasicBattleSessionState state)
    {
        if (state.ContentHash != _session.State.ContentHash)
            throw new ArgumentException("恢复场景与当前战斗不匹配。");
        var restored = new BasicBattleSession(state);
        _session = restored;
        Cancel();
    }

    private CommandResult Submit(BattleAction action, BattleView view)
    {
        var decision = view.PendingDecision!;
        return _session.Submit(new($"client.command.{view.StateVersion}", view.StateVersion,
            decision.ControllerId, decision.RequestId, decision.ActorId, action));
    }
}
