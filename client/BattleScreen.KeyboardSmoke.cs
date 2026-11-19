using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private string? _keyboardSavePath;
    private int _keyboardTaps;

    private async Task RunKeyboardSmoke(string[] args)
    {
        _aiTimer.Stop();
        try
        {
            if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) throw new ArgumentException("请指定键盘回归专用目录。");
            Directory.CreateDirectory(args[1]);
            var games = 0; var decisions = 0;
            foreach (var count in new[] { 2, 4 })
            {
                FreshMotionBattle(count);
                _keyboardSavePath = Path.Combine(args[1], $"keyboard-{count}.json");
                Refresh("键盘整局回归", new(FocusArea.Default)); await KeyboardFrame();
                var initial = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
                SaveBattle(); Restore(); _aiTimer.Stop(); await KeyboardFrame();
                if (initial != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("键盘场景恢复不一致。");
                var sample = _interaction.View.LegalActions.First(a => a.CardId is not null && a.SkillId is null && NeedsTarget(a));
                await PreviewWithKeyboard(sample);
                await NavigateWithKeyboard(_cancel); await TapKeyboard(Key.Space);
                if (_selectedCard is not null || initial != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("键盘取消提交了规则命令。");
                foreach (var opener in new[] { _helpButton, _inspectButton, _unitButton, _settingsButton })
                {
                    await NavigateWithKeyboard(opener); await TapKeyboard(Key.Enter); await TapKeyboard(Key.Escape);
                    if (BattleDialogVisible || !opener.HasFocus()) throw new InvalidOperationException("查看弹层未返回原焦点。");
                }
                for (var step = 0; _interaction.View.PendingDecision is not null; step++)
                {
                    if (step > 4000) throw new InvalidOperationException("键盘整局超限。");
                    if (_interaction.WaitingForAi) { AdvanceAi(); await KeyboardFrame(); continue; }
                    var action = BasicBattleAi.Choose(_interaction.View)!.Action;
                    var version = _interaction.View.StateVersion;
                    await PlayWithKeyboard(action);
                    if (_interaction.View.StateVersion <= version) throw new InvalidOperationException("键盘确认未推进战斗。");
                    decisions++;
                }
                if (_interaction.View.WinnerTeamId is null) throw new InvalidOperationException("键盘对局未终局。");
                games++;
            }
            GD.Print($"BATTLE_KEYBOARD_SMOKE_OK games={games} taps={_keyboardTaps} decisions={decisions}"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private Button KeyboardButton(string text) => FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == text);

    private async Task PreviewWithKeyboard(BattleAction action)
    {
        var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
        if (action.SkillId is { } skill && _selectedSkill != skill)
        { await NavigateWithKeyboard(_skillButtons[skill]); await TapKeyboard(Key.Enter); }
        if (action.CardId is { } id)
        { await NavigateWithKeyboard(_handCards.Single(p => p.Value.InstanceId == id).Key); await TapKeyboard(Key.Enter); }
        if (action.CostCardId is { } cost)
        { await NavigateWithKeyboard(_handCards.Single(p => p.Value.InstanceId == cost).Key); await TapKeyboard(Key.Enter); }
        if (NeedsTarget(action))
        { await NavigateWithKeyboard(_seatButtons[action.TargetId!]); await TapKeyboard(Key.Enter); }
        if (_interaction.Selection != action || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
            throw new InvalidOperationException("键盘预选提交了状态或选错动作。");
        await NavigateWithKeyboard(_confirm);
    }

    private async Task PlayWithKeyboard(BattleAction action)
    {
        if (action.Kind == BattleActionKind.Discard)
        {
            var ids = _interaction.View.LegalActions.Where(a => a.Kind == BattleActionKind.Discard).Take(RequiredDiscards).Select(a => a.CardId!).ToArray();
            foreach (var id in ids)
            { await NavigateWithKeyboard(_handCards.Single(p => p.Value.InstanceId == id).Key); await TapKeyboard(Key.Enter); }
            await NavigateWithKeyboard(_confirm); await TapKeyboard(Key.Enter); return;
        }
        if (action.Kind is BattleActionKind.Pass or BattleActionKind.EndPlay or BattleActionKind.SelectHandCard)
        {
            var button = action.Kind == BattleActionKind.Pass ? _pass : action.Kind == BattleActionKind.EndPlay ? _end
                : _choiceActions.Single(p => p.Value == action).Key;
            if (action.Kind == BattleActionKind.SelectHandCard) await WaitForHandChoice(button);
            await NavigateWithKeyboard(button); await TapKeyboard(Key.Enter); return;
        }
        if (action.Kind != BattleActionKind.InvokeSkill) await PreviewWithKeyboard(action);
        else await NavigateWithKeyboard(_confirm);
        await TapKeyboard(Key.Enter);
    }

    private async Task NavigateWithKeyboard(Control target)
    {
        await KeyboardFrame();
        if (!CanFocus(target)) throw new InvalidOperationException("键盘目标不可聚焦。");
        if (KeyboardViewport().GuiGetFocusOwner() is null) await TapKeyboard(Key.Tab);
        var owner = KeyboardViewport().GuiGetFocusOwner() ?? throw new InvalidOperationException("Tab无法建立键盘焦点。");
        int Distance(bool reverse)
        {
            var next = owner;
            for (var i = 0; i < 256; i++)
            {
                if (next == target) return i;
                next = reverse ? next.FindPrevValidFocus() : next.FindNextValidFocus();
                if (next is null || next == owner) break;
            }
            return int.MaxValue;
        }
        var forward = Distance(false); var backward = Distance(true);
        var distance = Math.Min(forward, backward);
        if (distance == int.MaxValue) throw new InvalidOperationException("Tab无法到达目标。");
        for (var i = 0; i < distance; i++) await TapKeyboard(Key.Tab, shift: backward < forward);
        if (!target.HasFocus()) throw new InvalidOperationException("Tab焦点顺序与控件树不一致。");
        foreach (var ancestor in AncestorScrolls(target))
            if (!ancestor.GetGlobalRect().Encloses(target.GetGlobalRect())) throw new InvalidOperationException("键盘焦点被滚动区裁切："
                + (target is Button b ? b.Text : target.Name) + " / " + target.GetGlobalRect() + " / " + ancestor.GetGlobalRect()
                + " / version=" + _interaction.View.StateVersion);
    }

    private async Task TapKeyboard(Key key, bool shift = false, bool echo = false)
    {
        var windowId = GetWindow().GetWindowId();
        Input.ParseInputEvent(new InputEventKey { WindowId = windowId, Keycode = key, Pressed = true, ShiftPressed = shift, Echo = echo });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventKey { WindowId = windowId, Keycode = key, Pressed = false, ShiftPressed = shift });
        await KeyboardFrame();
        _keyboardTaps++;
    }

    private Viewport KeyboardViewport() => _handDialog.Visible ? _handDialog : _helpDialog.Visible ? _helpDialog : _logDialog.Visible ? _logDialog : GetViewport();

    private async Task KeyboardFrame()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
