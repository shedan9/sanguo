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
            if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
                throw new ArgumentException("键盘烟测用法：--battle-keyboard-smoke <专用目录绝对路径>");
            Directory.CreateDirectory(args[1]);
            var kinds = new HashSet<EffectKind>();
            var actions = new HashSet<BattleActionKind>();
            var games = 0;
            foreach (var count in new[] { 2, 4 })
            {
                _keyboardSavePath = Path.Combine(args[1], $"keyboard-{count}v{count}.json");
                GetWindow().Size = count == 2 ? new Vector2I(960, 600) : new Vector2I(1280, 720);
                _interaction = new(DevelopmentBattleScenario.Create(42, count, count));
                ClearSelection(); _logVersion = -1; _focusUnitId = "development.guanyu";
                Refresh("键盘回归", new(FocusArea.Default));
                await KeyboardFrame();
                if (!_handCards.Keys.Any(b => b.HasFocus())) throw new InvalidOperationException("新请求没有定位到手牌。");
                var initialHash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
                var initial = _interaction.Snapshot;
                var sample = _interaction.View.LegalActions.First(a => a.Kind == BattleActionKind.Slash && a.SkillId == BattleSkills.Wusheng);
                await PreviewWithKeyboard(sample);
                await TapKeyboard(Key.Enter, echo: true);
                if (initialHash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("长按确认提交了未经重新按键的动作。");
                await NavigateWithKeyboard(_cancel); await TapKeyboard(Key.Space);
                if (_interaction.Selection is not null || initialHash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)
                    || !_handCards.Single(p => p.Value.InstanceId == sample.CardId).Key.HasFocus())
                    throw new InvalidOperationException("键盘取消扣牌或没有返回原牌。");

                await NavigateWithKeyboard(KeyboardButton("保存战斗")); await TapKeyboard(Key.Enter);
                if (!File.Exists(_keyboardSavePath) || initialHash != BasicBattleSnapshotCodec.StateHash(_store.Load(_keyboardSavePath, initial.ContentHash).State))
                    throw new InvalidOperationException("键盘保存未产生对应配置的专用存档。");
                await PreviewWithKeyboard(sample);
                await NavigateWithKeyboard(KeyboardButton("恢复战斗")); await TapKeyboard(Key.Enter);
                _aiTimer.Stop();
                if (_interaction.Selection is not null || initialHash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)
                    || !_handCards.Keys.Any(b => b.HasFocus())) throw new InvalidOperationException("键盘恢复未清理预览或定位当前请求：" + _feedback.Text);

                foreach (var name in new[] { "操作说明", "战斗记录" })
                {
                    var opener = KeyboardButton(name);
                    await NavigateWithKeyboard(opener); await TapKeyboard(Key.Space);
                    var dialog = name == "操作说明" ? _helpDialog : _logDialog;
                    if (!dialog.Visible || !dialog.GetOkButton().HasFocus()) throw new InvalidOperationException("弹层未获得键盘焦点。");
                    Refresh("弹层打开时的只读刷新"); await KeyboardFrame();
                    if (!dialog.GetOkButton().HasFocus()) throw new InvalidOperationException("桌面刷新抢走弹层焦点。");
                    await TapKeyboard(Key.Enter);
                    if (dialog.Visible || !opener.HasFocus() || initialHash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                        throw new InvalidOperationException("关闭弹层未归还焦点或改变了战斗。");
                    await TapKeyboard(Key.Enter); await TapKeyboard(Key.Escape);
                    if (dialog.Visible || !opener.HasFocus() || initialHash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                        throw new InvalidOperationException($"Esc关闭弹层未归还焦点或改变了战斗：{name} visible={dialog.Visible} focus={opener.HasFocus()}"
                            + " close=" + string.Join(",", InputMap.ActionGetEvents("ui_close_dialog").Select(e => e.AsText())));
                }

                // Exercise discard through real input, then restore the initial decision before the deterministic game.
                await PlayWithKeyboard(_interaction.View.LegalActions.Single(a => a.Kind == BattleActionKind.EndPlay));
                if (_interaction.View.Phase != BattlePhase.Discard) throw new InvalidOperationException("未进入弃牌键盘用例。");
                await PlayWithKeyboard(_interaction.View.LegalActions.First(a => a.Kind == BattleActionKind.Discard));
                actions.Add(BattleActionKind.Discard);
                await NavigateWithKeyboard(KeyboardButton("恢复战斗")); await TapKeyboard(Key.Enter);
                _aiTimer.Stop();
                if (BasicBattleSnapshotCodec.StateHash(initial) != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                    throw new InvalidOperationException("弃牌后键盘恢复结果不一致。");

                var steps = 0;
                while (_interaction.View.PendingDecision is not null)
                {
                    if (++steps > 4000) throw new InvalidOperationException("键盘回归触发步数保护。");
                    if (_interaction.WaitingForAi) { AdvanceAi(); await KeyboardFrame(); continue; }
                    if (_interaction.View.Response is { } response) kinds.Add(response.Kind);
                    var action = BasicBattleAi.Choose(_interaction.View)!.Action;
                    actions.Add(action.Kind);
                    await PlayWithKeyboard(action);
                }
                var expected = new BattleInteraction(DevelopmentBattleScenario.Create(42, count, count));
                var expectedSteps = 0;
                while (expected.View.PendingDecision is not null)
                {
                    if (++expectedSteps > 4000) throw new InvalidOperationException("连续对照触发步数保护。");
                    if (expected.WaitingForAi) expected.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action);
                    else { expected.Select(BasicBattleAi.Choose(expected.View)!.Action); expected.Confirm(); }
                }
                if (BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) != BasicBattleSnapshotCodec.StateHash(expected.Snapshot))
                    throw new InvalidOperationException("键盘整局与连续对照不一致。");
                var logOpener = KeyboardButton("战斗记录");
                await NavigateWithKeyboard(logOpener); await TapKeyboard(Key.Enter);
                var logScroll = _logText.GetVScrollBar();
                await NavigateWithKeyboard(logScroll);
                var bottom = logScroll.Value;
                await TapKeyboard(Key.Up);
                if (logScroll.Value >= bottom) throw new InvalidOperationException("无法用键盘滚动公开记录。");
                await NavigateWithKeyboard(_logDialog.GetOkButton()); await TapKeyboard(Key.Space);
                if (_logDialog.Visible || !logOpener.HasFocus()
                    || BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) != BasicBattleSnapshotCodec.StateHash(expected.Snapshot))
                    throw new InvalidOperationException("键盘查看历史记录改变了状态或没有归还焦点。");
                games++;
                GD.Print($"BATTLE_KEYBOARD_GAME_OK {count}v{count} steps={steps} taps={_keyboardTaps}");
            }
            // Cao Cao belongs to the AI in the default scene, so use a separate all-Slash fixture for his player prompt.
            _interaction = new(BasicBattleSession.Create("battle.keyboard-jianxiong",
                [new("keyboard.attacker", "opponent", "b"), new("keyboard.caocao", "player", "a", Profile: BattleProfile.CaocaoStandard)],
                Enumerable.Range(0, 24).Select(i => new BattleCard($"keyboard.slash.{i}", BasicCardKind.Slash, "spade", i % 13 + 1)),
                new string('a', 64), 42, 0));
            _focusUnitId = "keyboard.caocao"; _logVersion = -1; ClearSelection(); Refresh("奸雄键盘回归", new(FocusArea.Default));
            await KeyboardFrame();
            for (var step = 0; _interaction.View.Response?.Kind != EffectKind.InvokeJianxiong; step++)
            {
                if (step > 20) throw new InvalidOperationException("未进入奸雄键盘用例。");
                if (_interaction.WaitingForAi) { AdvanceAi(); await KeyboardFrame(); }
                else await PlayWithKeyboard(BasicBattleAi.Choose(_interaction.View)!.Action);
            }
            var invoke = _interaction.View.LegalActions.Single(a => a.Kind == BattleActionKind.InvokeSkill);
            await PlayWithKeyboard(invoke); actions.Add(invoke.Kind); kinds.Add(EffectKind.InvokeJianxiong);
            foreach (var kind in new[] { EffectKind.Dodge, EffectKind.Duel, EffectKind.Nullification, EffectKind.Rescue, EffectKind.SelectHandCard })
                if (!kinds.Contains(kind)) throw new InvalidOperationException("键盘回归缺少响应：" + kind);
            if (!actions.Contains(BattleActionKind.InvokeSkill)) throw new InvalidOperationException("键盘回归缺少奸雄询问。");
            GD.Print($"BATTLE_KEYBOARD_SMOKE_OK games={games} taps={_keyboardTaps} responses={kinds.Count} actions={actions.Count}");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private Button KeyboardButton(string text) => FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == text);

    private async Task PreviewWithKeyboard(BattleAction action)
    {
        var before = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
        if (action.CardId is { } id)
        {
            await NavigateWithKeyboard(_handCards.Single(p => p.Value.InstanceId == id).Key); await TapKeyboard(Key.Enter);
            if (_selectedCard != id) throw new InvalidOperationException("按键未选择手牌。");
        }
        if (action.TargetId is { } target && _seatButtons.TryGetValue(target, out var seat) && !seat.Disabled)
        { await NavigateWithKeyboard(seat); await TapKeyboard(Key.Enter); }
        if (_interaction.Selection != action)
        { await NavigateWithKeyboard(_optionActions.Single(p => p.Value == action).Key); await TapKeyboard(Key.Space); }
        if (_interaction.Selection != action || !_confirm.HasFocus() || before != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
            throw new InvalidOperationException("键盘预览改变了战斗、选错方案或未定位确认。");
    }

    private async Task PlayWithKeyboard(BattleAction action)
    {
        await PreviewWithKeyboard(action);
        var version = _interaction.View.StateVersion;
        await TapKeyboard(Key.Enter);
        if (_interaction.View.StateVersion != version + 1 || _interaction.Selection is not null)
            throw new InvalidOperationException($"一次确认未恰好提交一条命令：{action.Kind} {version}→{_interaction.View.StateVersion} / {_feedback.Text}");
        if (!CanFocus(GetViewport().GuiGetFocusOwner())) throw new InvalidOperationException("提交后键盘焦点丢失。");
    }

    private async Task NavigateWithKeyboard(Control target)
    {
        await KeyboardFrame();
        if (!CanFocus(target)) throw new InvalidOperationException("键盘目标不可聚焦。");
        var owner = KeyboardViewport().GuiGetFocusOwner() ?? throw new InvalidOperationException("键盘焦点丢失。");
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

    private static IEnumerable<ScrollContainer> AncestorScrolls(Node node)
    {
        for (var parent = node.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) yield return scroll;
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

    private Viewport KeyboardViewport() => _helpDialog.Visible ? _helpDialog : _logDialog.Visible ? _logDialog : GetViewport();

    private async Task KeyboardFrame()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
