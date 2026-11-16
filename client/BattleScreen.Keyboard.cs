using Godot;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private enum FocusArea { Default, Hand, Target, Option, Confirm, Processing }
    private sealed record FocusRequest(FocusArea Area, string? Id = null, BattleAction? Action = null);
    private Button _helpButton = null!;
    private AcceptDialog _helpDialog = null!;
    private long _focusRevision, _focusStateVersion = -1;
    private string? _focusRequestId;
    private FocusRequest? _dialogReturnFocus;
    private Control? _dialogReturnControl;
    private string? _dialogRequestId;
    private bool _automaticWaitingFocus;

    public override void _Input(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false } or InputEventMouseButton { Pressed: true })
            _automaticWaitingFocus = false;
        if (input is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false }
            && !BattleDialogVisible && _inspectionPanel.Visible)
        {
            _inspectionDismissed = true;
            _inspectionPanel.Hide();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildKeyboardHelp()
    {
        _helpDialog = new AcceptDialog { Title = "操作说明 · 键盘与鼠标", DialogText =
            "Tab：移到下一个可用控件；Shift+Tab：返回上一个。\n"
            + "Enter或空格：激活当前按钮。描边标明当前焦点。\n\n"
            + "选中手牌后，焦点移到合法目标；无目标时移到操作方案。\n"
            + "选好目标与技能成本后，核对预览，再按“确认操作”。\n"
            + "“取消选择”不消耗牌，焦点返回原手牌。\n"
            + "放弃响应、结束出牌和弃牌均在操作方案中选择，再确认。\n\n"
            + "悬停或聚焦卡面可展开详情，Esc收起；查看不出牌。\n"
            + "“手牌一览”可用方向键逐张查看，包括当前不可用的牌。\n"
            + "处理区卡面可聚焦查看；手牌、目标和方案随焦点滚动。\n"
            + "战斗记录可用Tab聚焦滚动条，按方向键上下查看历史。\n"
            + "战斗记录和本说明均可聚焦关闭按钮返回。\n"
            + "弹层内按Esc也可关闭，不会放弃战斗响应。\n"
            + "敌方行动时只显示公开信息，玩家响应不限时。" };
        AddChild(_helpDialog);
        _helpDialog.GetOkButton().Text = "关闭说明";
        _logDialog.GetOkButton().Text = "关闭记录";
        _helpDialog.DialogCloseOnEscape = true;
        _logDialog.DialogCloseOnEscape = true;
        var logScroll = _logText.GetVScrollBar();
        logScroll.FocusMode = FocusModeEnum.All;
        logScroll.TooltipText = "战斗记录滚动条：方向键上下查看历史。";
        logScroll.AddThemeStyleboxOverride("scroll_focus", new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = Gold,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2 });
        _helpDialog.Canceled += RestoreDialogFocus;
        _helpDialog.Confirmed += RestoreDialogFocus;
        _logDialog.Canceled += RestoreDialogFocus;
        _logDialog.Confirmed += RestoreDialogFocus;
    }

    private FocusRequest? CaptureBattleFocus(Control? owner)
    {
        if (owner is null) return null;
        if (owner is Button button)
        {
            if (_handCards.TryGetValue(button, out var card)) return new(FocusArea.Hand, card.InstanceId);
            if (_optionActions.TryGetValue(button, out var action)) return new(FocusArea.Option, Action: action);
            foreach (var seat in _seatButtons)
                if (seat.Value == button) return new(FocusArea.Target, seat.Key);
        }
        return _processingCards.TryGetValue(owner, out var processing) ? new(FocusArea.Processing, processing.Card.InstanceId) : null;
    }

    private static bool CanFocus(Control? control) => GodotObject.IsInstanceValid(control)
        && control!.IsInsideTree() && control.IsVisibleInTree() && control.FocusMode != FocusModeEnum.None
        && (control is not BaseButton button || !button.Disabled);

    private void ScheduleBattleFocus(FocusRequest request, Control? stableControl = null)
    {
        var revision = ++_focusRevision;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || revision != _focusRevision || BattleDialogVisible) return;
            var preserved = CanFocus(stableControl);
            var control = preserved ? stableControl : ResolveBattleFocus(request);
            _automaticWaitingFocus = !preserved && _interaction.WaitingForAi && control == _helpButton;
            control?.GrabFocus();
        }).CallDeferred();
    }

    private Control? ResolveBattleFocus(FocusRequest request)
    {
        Control? desired = request.Area switch
        {
            FocusArea.Hand => _handCards.FirstOrDefault(p => p.Value.InstanceId == request.Id && CanFocus(p.Key)).Key,
            FocusArea.Target => request.Id is not null ? _seatButtons.GetValueOrDefault(request.Id)
                : _seatButtons.Values.FirstOrDefault(CanFocus),
            FocusArea.Option => _optionActions.FirstOrDefault(p => p.Value == request.Action && CanFocus(p.Key)).Key,
            FocusArea.Confirm => _confirm,
            FocusArea.Processing => _processingCards.FirstOrDefault(p => p.Value.Card.InstanceId == request.Id).Key,
            _ => null
        };
        if (CanFocus(desired)) return desired;
        if (request.Area is FocusArea.Target or FocusArea.Option)
        {
            var option = _optionActions.Keys.FirstOrDefault(CanFocus);
            if (option is not null) return option;
        }
        return _handCards.Keys.FirstOrDefault(CanFocus) ?? _optionActions.Keys.FirstOrDefault(CanFocus) ?? _helpButton;
    }

    private async Task RevealBattleFocus(Control control)
    {
        // Containers can move after focus enters (for example when a response prompt wraps).
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(this) || !CanFocus(control) || GetViewport().GuiGetFocusOwner() != control || BattleDialogVisible) return;
        Control visible = control;
        for (var parent = control.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll)
            {
                scroll.EnsureControlVisible(visible);
                visible = scroll;
            }
    }

    private static IEnumerable<ScrollContainer> AncestorScrolls(Node node)
    {
        for (var parent = node.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) yield return scroll;
    }

    private void ShowBattleDialog(AcceptDialog dialog)
    {
        _inspectionPanel.Hide();
        var owner = GetViewport().GuiGetFocusOwner();
        _dialogReturnFocus = CaptureBattleFocus(owner);
        _dialogReturnControl = _dialogReturnFocus is null ? owner : null;
        _dialogRequestId = _interaction.View.PendingDecision?.RequestId;
        ++_focusRevision;
        dialog.PopupCentered(new Vector2I(850, 500));
        dialog.GetOkButton().GrabFocus();
    }

    private void RestoreDialogFocus()
    {
        var sameRequest = _dialogRequestId == _interaction.View.PendingDecision?.RequestId;
        ScheduleBattleFocus(sameRequest ? _dialogReturnFocus ?? new(FocusArea.Default) : new(FocusArea.Default), _dialogReturnControl);
        _dialogReturnControl = null; _dialogReturnFocus = null;
    }
}
