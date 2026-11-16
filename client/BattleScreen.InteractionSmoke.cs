using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private async Task KeyStroke(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task FocusByTab(Control target)
    {
        var visited = new HashSet<Control>();
        while (GetViewport().GuiGetFocusOwner() != target)
        {
            await KeyStroke(Key.Tab);
            var focus = GetViewport().GuiGetFocusOwner();
            if (focus is null || !visited.Add(focus))
                throw new InvalidOperationException("Tab无法到达操作控件：" + target.Name);
        }
    }

    private async Task ChooseByKeyboard(BattleAction action)
    {
        if (action.CardId is { } cardId)
        {
            await FocusByTab(_handCards.Single(p => p.Value.InstanceId == cardId).Key);
            await KeyStroke(Key.Space);
            if (_selectedCard != cardId) throw new InvalidOperationException("键盘未选中指定手牌。");
        }
        if (action.TargetId is { } targetId && _seatButtons.TryGetValue(targetId, out var target) && !target.Disabled)
        {
            await FocusByTab(target);
            await KeyStroke(Key.Space);
            if (_selectedTarget != targetId) throw new InvalidOperationException("键盘未选中指定目标。");
        }
        if (_interaction.Selection != action)
        {
            await FocusByTab(_optionActions.Single(p => p.Value == action).Key);
            await KeyStroke(Key.Space);
        }
        if (_interaction.Selection != action || _confirm.Disabled)
            throw new InvalidOperationException("键盘选择与合法方案不一致。");
    }

    private async Task CheckInspection(bool log)
    {
        var header = GetChildren().OfType<MarginContainer>().Single().GetChild<VBoxContainer>(0).GetChild<HFlowContainer>(0);
        var button = header.GetChildren().OfType<Button>().Single(b => b.Text == (log ? "战斗记录" : "武将资料"));
        await FocusByTab(button);
        var before = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
        var selection = _interaction.Selection;
        await KeyStroke(Key.Space);
        if (!InspectionOpen) throw new InvalidOperationException("键盘未打开资料弹层。");
        if (!log)
        {
            foreach (var id in _inspectedUnits)
            {
                _unitPicker.Selected = _inspectedUnits.IndexOf(id); UpdateUnitDetails();
                var unit = _interaction.View.Combatants.Single(u => u.EntityId == id);
                if (!_unitText.Text.Contains($"手牌 {unit.HandCount}张", StringComparison.Ordinal)
                    || unit.Skills.Any(s => !_unitText.Text.Contains(s.Definition.Text, StringComparison.Ordinal))
                    || _interaction.Snapshot.Battle.CardInventory.Any(c => _unitText.Text.Contains(c, StringComparison.Ordinal)))
                    throw new InvalidOperationException("武将资料缺失技能或泄漏手牌实体。");
                if (unit.Profile == BattleProfile.CaocaoStandard && !_unitText.Text.Contains("没有主公身份", StringComparison.Ordinal))
                    throw new InvalidOperationException("护驾未说明未启用原因。");
            }
        }
        for (var i = 0; i < 3; i++) AdvanceAi();
        if (before != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) || selection != _interaction.Selection)
            throw new InvalidOperationException("查看资料或记录推进了战斗或改变了选择。");
        await KeyStroke(Key.Escape);
        if (InspectionOpen || GetViewport().GuiGetFocusOwner() != button)
            throw new InvalidOperationException($"关闭弹层未返回原键盘焦点：log={log} visible={InspectionOpen} focus={GetViewport().GuiGetFocusOwner()?.Name} expected={button.Name}。");
    }

    private async Task RunInteractionSmoke()
    {
        _aiTimer.Stop();
        try
        {
            var keyboardDecisions = 0;
            var contexts = new HashSet<EffectKind>();
            var checkedAiPause = false;
            foreach (var size in new[] { 2, 4 })
            {
                _interaction = new(DevelopmentBattleScenario.Create(42, size, size));
                var reference = new BattleInteraction(DevelopmentBattleScenario.Create(42, size, size));
                ClearSelection(); _focusUnitId = "development.guanyu"; _logVersion = -1; ResetPresentation();
                Refresh("完整键盘对局回归：Tab移动，空格选择，关闭弹层回到原操作。");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await CheckInspection(false);
                var steps = 0;
                while (_interaction.View.PendingDecision is not null)
                {
                    if (++steps > 4000) throw new InvalidOperationException("键盘对局触发步数保护。");
                    if (_interaction.View.Response is { } response) contexts.Add(response.Kind);
                    if (_interaction.WaitingForAi)
                    {
                        if (!checkedAiPause)
                        {
                            await CheckInspection(true); await CheckInspection(false);
                            checkedAiPause = true;
                        }
                        _interaction.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action);
                        reference.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action);
                        ClearSelection(); Refresh("敌方回归推进");
                    }
                    else
                    {
                        var action = BasicBattleAi.Choose(_interaction.View)!.Action;
                        var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
                        await ChooseByKeyboard(action);
                        if (keyboardDecisions % 10 == 0)
                        {
                            await CheckInspection(false);
                            await FocusByTab(_cancel); await KeyStroke(Key.Space);
                            if (_interaction.Selection is not null || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                                throw new InvalidOperationException("键盘取消改变了战斗状态。");
                            await ChooseByKeyboard(action);
                        }
                        await FocusByTab(_confirm); await KeyStroke(Key.Space);
                        reference.Select(action);
                        if (!reference.Confirm().Accepted) throw new InvalidOperationException("参照对局拒绝合法动作。");
                        keyboardDecisions++;
                        if (GetViewport().GuiGetFocusOwner() is not { } focus || !focus.IsInsideTree())
                            throw new InvalidOperationException("决策切换后键盘焦点丢失。");
                    }
                    if (BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) != BasicBattleSnapshotCodec.StateHash(reference.Snapshot))
                        throw new InvalidOperationException("键盘对局与参照对局不同。");
                    if (_publicCards.GetChildCount() != Math.Min(3, _interaction.View.Processing.Length > 0
                            ? _interaction.View.Processing.Length : _lastPublicCard is null ? 0 : 1))
                        throw new InvalidOperationException("中央处理区展示不匹配。");
                    if (steps == 30)
                    {
                        _interaction.Restore(BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(_interaction.Snapshot), _interaction.Snapshot.ContentHash));
                        ClearSelection(); ResetPresentation(); Refresh("恢复不重播旧结算");
                        if (_lastPublicCard is not null || _settlementText.Length > 0)
                            throw new InvalidOperationException("读档重播了历史结算。");
                    }
                }
                if (_interaction.View.WinnerTeamId is null) throw new InvalidOperationException("键盘对局未到达终局。");
            }
            if (!checkedAiPause || keyboardDecisions < 20 || !contexts.Contains(EffectKind.Nullification)
                || !contexts.Contains(EffectKind.Dodge) || !contexts.Contains(EffectKind.Rescue))
                throw new InvalidOperationException("键盘响应或AI暂停覆盖不足。");
            GD.Print($"BATTLE_INTERACTION_SMOKE_OK games=2 keyboard={keyboardDecisions} contexts={string.Join(',', contexts.Order())}");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
}
