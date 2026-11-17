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
                for (var p = 1; p <= 4; p++) for (var e = 1; e <= 4; e++)
                {
                    _interaction = new(DevelopmentBattleScenario.Create(42, p, e)); _focusUnitId = "development.guanyu";
                    ClearSelection(); ResetPresentation(); Refresh("完整桌面适配回归"); await KeyboardFrame();
                    var viewport = GetViewportRect();
                    foreach (var control in new Control[] { _confirm, _cancel, _settingsButton, _inspectButton, _unitButton, _handScroll })
                        if (!viewport.Encloses(control.GetGlobalRect())) throw new InvalidOperationException($"窗口{size}缩放{scale}控件越界：{control.Name} {control.GetGlobalRect()} viewport={viewport}");
                    CheckProcessingLayout();
                    var seats = _seatButtons.Values.ToArray();
                    for (var i = 0; i < seats.Length; i++)
                        for (var j = 0; j < i; j++)
                            if (seats[i].GetGlobalRect().Intersects(seats[j].GetGlobalRect())) throw new InvalidOperationException("角色框重叠。");
                    if (_reservedSlots.Count != 5) throw new InvalidOperationException("装备或判定空态丢失。");
                    layouts++;
                }
                foreach (var dialog in new[] { _settingsDialog, _unitDialog, _handDialog })
                {
                    if (dialog == _unitDialog) UpdateUnitDetails();
                    if (dialog == _handDialog) UpdateHandInspection();
                    ShowBattleDialog(dialog); await KeyboardFrame();
                    if (DisplayServer.GetName() != "headless" && !GetViewportRect().Encloses(new Rect2(dialog.Position, dialog.Size)))
                        throw new InvalidOperationException("真实渲染弹层越界：" + dialog.Title);
                    dialog.Hide();
                }
            }
            GetWindow().Size = new Vector2I(1280, 720); _requestedScale = 100; ApplyInterfaceScale();
            FreshMotionBattle(); await KeyboardFrame();
            var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            _skillButtons[BattleSkills.Wusheng].EmitSignal(BaseButton.SignalName.Pressed); await KeyboardFrame();
            if (_handCards.Any(p => !p.Key.Disabled && !_interaction.View.LegalActions.Any(a => a.CardId == p.Value.InstanceId && a.SkillId == BattleSkills.Wusheng)))
                throw new InvalidOperationException("技能筛选混入非法牌。");
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = new Vector2(4, 4) }, true);
            await KeyboardFrame();
            if (_selectedSkill is not null || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("右键取消改变战斗。");
            _motionChoice.SetPressedNoSignal(true); _motionChoice.EmitSignal(BaseButton.SignalName.Toggled, true);
            var sample = _handCards.First(); sample.Key.EmitSignal(Control.SignalName.MouseEntered); await KeyboardFrame();
            if (_hoverFace is not null) throw new InvalidOperationException("减少动态仍抬升卡面。");
            sample.Key.EmitSignal(Control.SignalName.MouseExited);
            var flights = _flightCount; var impacts = _impactCount;
            DriveAction(_interaction.View.LegalActions.First(a => a.Kind == BattleActionKind.Slash && a.SkillId is null));
            await ToSignal(GetTree().CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
            if (_flightCount != flights || _impactCount != impacts) throw new InvalidOperationException("减少动态仍播放飞牌或粒子。");
            _motionChoice.SetPressedNoSignal(false); _motionChoice.EmitSignal(BaseButton.SignalName.Toggled, false);
            FreshMotionBattle(); await KeyboardFrame();
            var targeted = _handCards.First(p => !p.Key.Disabled && _interaction.View.LegalActions.Any(a => a.CardId == p.Value.InstanceId && a.SkillId is null && NeedsTarget(a)));
            await DoubleClickPresentation(targeted.Key);
            if (hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("双击擅自选择目标。");
            CancelMouseSelection();
            var responses = 0; var hiddenChoices = 0;
            for (var step = 0; _interaction.View.PendingDecision is not null; step++)
            {
                if (step > 4000) throw new InvalidOperationException("展示整局回归超限。");
                if (_interaction.WaitingForAi) AdvanceAi();
                else
                {
                    var action = BasicBattleAi.Choose(_interaction.View)!.Action;
                    if (action.Kind == BattleActionKind.SelectHandCard)
                    {
                        if (!_choiceOverlay.Visible || _choiceActions.Keys.Any(b => b.GetChildren().OfType<TextureRect>().Any()))
                            throw new InvalidOperationException("暗牌弹层泄漏牌面。");
                        hiddenChoices++;
                    }
                    if (_interaction.View.Response is not null && action.CardId is { } id && action.SkillId is null)
                    {
                        var before = _interaction.View.StateVersion;
                        await DoubleClickPresentation(_handCards.Single(p => p.Value.InstanceId == id).Key);
                        if (_interaction.View.StateVersion <= before) throw new InvalidOperationException("双击响应未生效。");
                        responses++;
                    }
                    else DriveAction(action);
                }
                await KeyboardFrame();
            }
            if (responses == 0 || hiddenChoices == 0) throw new InvalidOperationException("未覆盖双击响应与暗牌选择。");
            GD.Print($"BATTLE_PRESENTATION_SMOKE_OK layouts={layouts} responses={responses} hiddenChoices={hiddenChoices}"); GetTree().Quit();
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
