using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private async Task RunPresentationSmoke()
    {
        _aiTimer.Stop();
        try
        {
            var layouts = 0;
            foreach (var size in new[] { new Vector2I(960, 600), new Vector2I(1280, 720), new Vector2I(1920, 1080), new Vector2I(2560, 1440) })
            foreach (var scale in InterfaceScales)
            {
                GetWindow().Size = size; _requestedScale = scale; ApplyInterfaceScale();
                for (var players = 1; players <= 4; players++)
                for (var opponents = 1; opponents <= 4; opponents++)
                {
                    _interaction = new(DevelopmentBattleScenario.Create(42, players, opponents));
                    ClearSelection(); Refresh("缩放与预留区域回归"); await KeyboardFrame();
                    var viewport = GetViewportRect();
                    if (!viewport.Encloses(_confirm.GetGlobalRect()) || !viewport.Encloses(_cancel.GetGlobalRect())
                        || !viewport.Encloses(_settingsButton.GetGlobalRect()) || !viewport.Encloses(_unitButton.GetGlobalRect()))
                        throw new InvalidOperationException($"缩放{scale}窗口{size}底栏被裁切：{viewport} / {_settingsButton.GetGlobalRect()}");
                    CheckProcessingLayout();
                    if (_reservedSlots.Count != 5 || _reservedSlots.Any(s => s.FocusMode != FocusModeEnum.None))
                        throw new InvalidOperationException("装备与判定预留区缺失或成为可提交动作。");
                    var body = _table.GetParent().GetParent<ScrollContainer>();
                    if (_handCards.Keys.Any(c => !body.GetGlobalRect().Encloses(c.GetGlobalRect())))
                        throw new InvalidOperationException($"缩放{scale}窗口{size}初始卡面被裁切。");
                    layouts++;
                }
                foreach (var dialog in new[] { _settingsDialog, _unitDialog })
                {
                    ShowBattleDialog(dialog); await KeyboardFrame();
                    var rect = new Rect2(dialog.Position, dialog.Size);
                    // The dummy display server reports 1x1 native popup sizes. The rendered run checks
                    // actual popup geometry; the headless run still exercises focus and read-only behavior.
                    if (DisplayServer.GetName() != "headless" && (!GetViewportRect().Encloses(rect)
                        || !new Rect2(Vector2.Zero, dialog.Size).Encloses(dialog.GetOkButton().GetGlobalRect())))
                        throw new InvalidOperationException($"缩放{scale}窗口{size}弹层或返回按钮被裁切：{dialog.Title} {rect} min={dialog.MinSize}");
                    dialog.Hide();
                }
            }
            GetWindow().Size = new Vector2I(1280, 720); _requestedScale = 100; ApplyInterfaceScale();
            _interaction = new(DevelopmentBattleScenario.Create(42, 4, 4)); ClearSelection(); Refresh("技能筛选回归"); await KeyboardFrame();
            var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            _skillButtons[BattleSkills.Wusheng].EmitSignal(BaseButton.SignalName.Pressed); await KeyboardFrame();
            if (_selectedSkill != BattleSkills.Wusheng || _handCards.Any(p => !p.Key.Disabled
                && !_interaction.View.LegalActions.Any(a => a.CardId == p.Value.InstanceId && a.SkillId == BattleSkills.Wusheng)))
                throw new InvalidOperationException("武圣按钮未按合法动作筛选手牌。");
            var red = _handCards.First(p => !p.Key.Disabled);
            red.Key.EmitSignal(BaseButton.SignalName.Pressed); await KeyboardFrame();
            if (_selectedSkill != BattleSkills.Wusheng || _optionActions.Values.Any(a => a.CardId is not null && a.SkillId != BattleSkills.Wusheng))
                throw new InvalidOperationException("技能选牌后混入其他方案。");
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = new Vector2(4, 4) }, true);
            await KeyboardFrame();
            if (_selectedSkill is not null || _selectedCard is not null || _interaction.Selection is not null
                || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("右键取消改变战斗或未清理技能。");

            // Inspect every public unit with actual keyboard opening/closing; no hidden hand reconstruction.
            await NavigateWithKeyboard(_unitButton); await TapKeyboard(Key.Enter);
            for (var i = 0; i < _unitChoice.ItemCount; i++)
            {
                _unitChoice.Selected = i; _unitChoice.EmitSignal(OptionButton.SignalName.ItemSelected, i);
                if (_interaction.Snapshot.Battle.Combatants.SelectMany(u => u.Hand).Any(c => _unitText.Text.Contains(c.InstanceId, StringComparison.Ordinal))
                    || !_unitText.Text.Contains("判定区", StringComparison.Ordinal)) throw new InvalidOperationException("角色资料暗牌泄漏或区域说明缺失。");
            }
            await TapKeyboard(Key.Escape);
            if (_unitDialog.Visible || !_unitButton.HasFocus() || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                throw new InvalidOperationException("角色查看改变状态或未归还焦点。");
            await NavigateWithKeyboard(_settingsButton); await TapKeyboard(Key.Space);
            _motionChoice.SetPressedNoSignal(true); _motionChoice.EmitSignal(BaseButton.SignalName.Toggled, true);
            await KeyboardFrame(); await TapKeyboard(Key.Escape);
            if (!_reduceMotion || !_settingsButton.HasFocus()) throw new InvalidOperationException("减少动态或设置弹层焦点错误。");
            var first = _handCards.First(p => !p.Key.Disabled);
            first.Key.EmitSignal(Control.SignalName.MouseEntered);
            if (first.Key.GetChild<Control>(0).OffsetTop != 6) throw new InvalidOperationException("减少动态仍移动卡面。");
            await TapKeyboard(Key.Escape); // dismiss inspection first
            first.Key.EmitSignal(BaseButton.SignalName.Pressed); await KeyboardFrame();
            await TapKeyboard(Key.Escape); await TapKeyboard(Key.Escape);
            if (_selectedCard is not null || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                throw new InvalidOperationException("Esc取消影响规则状态。");

            // Ambiguous target/cost never auto-submits on double click; a unique response can submit.
            var withTarget = _handCards.First(p => !p.Key.Disabled && _interaction.View.LegalActions.Any(a => a.CardId == p.Value.InstanceId && a.TargetId != _interaction.View.PendingDecision!.ActorId && a.TargetId is not null));
            await DoubleClickPresentation(withTarget.Key);
            if (hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("双击手牌擅自猜选目标。");
            var ambiguous = _interaction.View.LegalActions.Where(a => a.CardId == _selectedCard && a.TargetId is not null)
                .GroupBy(a => a.TargetId).FirstOrDefault(g => g.Count() > 1);
            if (ambiguous is not null)
            {
                await DoubleClickPresentation(_seatButtons[ambiguous.Key!]);
                if (_interaction.Selection is not null || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                    throw new InvalidOperationException("双击目标擅自猜选技能或成本。");
            }
            ClearSelection(); Refresh("双击响应回归");
            var steps = 0; var responses = 0; var hiddenChoices = 0;
            while (_interaction.View.PendingDecision is not null)
            {
                if (++steps > 4000) throw new InvalidOperationException("展示回归步数保护。");
                if (_interaction.WaitingForAi) _interaction.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action);
                else
                {
                    var view = _interaction.View;
                    var action = BasicBattleAi.Choose(view)!.Action;
                    if (view.Response?.Kind == EffectKind.SelectHandCard)
                    {
                        if (_optionActions.Any(p => p.Value.Kind == BattleActionKind.SelectHandCard && !p.Key.Text.Contains("牌背", StringComparison.Ordinal)))
                            throw new InvalidOperationException("暗手牌未以牌背选项展示。");
                        hiddenChoices++;
                    }
                    if (view.Response is not null && action.CardId is { } id && view.LegalActions.Count(a => a.CardId == id) == 1)
                    {
                        var button = _handCards.Single(p => p.Value.InstanceId == id).Key;
                        var before = view.StateVersion;
                        if (responses == 0)
                        {
                            _doubleClickChoice.SetPressedNoSignal(false); _doubleClickChoice.EmitSignal(BaseButton.SignalName.Toggled, false);
                            await DoubleClickPresentation(button);
                            if (_interaction.View.StateVersion != before) throw new InvalidOperationException("关闭双击确认后仍提交了响应。");
                            _doubleClickChoice.SetPressedNoSignal(true); _doubleClickChoice.EmitSignal(BaseButton.SignalName.Toggled, true);
                            button = _handCards.Single(p => p.Value.InstanceId == id).Key;
                        }
                        await DoubleClickPresentation(button);
                        if (_interaction.View.StateVersion != before + 1) throw new InvalidOperationException("双击明确响应未准确提交一次。");
                        responses++;
                    }
                    else { _interaction.Select(action); _interaction.Confirm(); }
                }
                ClearSelection(); Refresh("展示整局回归"); await KeyboardFrame();
            }
            if (responses == 0 || hiddenChoices == 0) throw new InvalidOperationException("展示回归缺少真实响应或暗牌选择。");
            GD.Print($"BATTLE_PRESENTATION_SMOKE_OK layouts={layouts} responses={responses} hiddenChoices={hiddenChoices} rendered={DisplayServer.GetName() != "headless"}");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private async Task DoubleClickPresentation(Control control)
    {
        foreach (var scroll in AncestorScrolls(control)) scroll.EnsureControlVisible(control);
        await KeyboardFrame();
        var point = control.GetGlobalRect().GetCenter();
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = true, Position = point, GlobalPosition = point }, true);
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point }, true);
        await KeyboardFrame();
    }
}
