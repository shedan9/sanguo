using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private async Task PrepareMotionPreview(string mode)
    {
        await MotionFrame();
        if (mode == "impact")
        {
            var sequence = _presentedSequence;
            for (var step = 0; step < 500; step++)
            {
                if (_interaction.View.PendingDecision is null) throw new InvalidOperationException("未找到伤害预览。");
                if (_interaction.WaitingForAi)
                {
                    // Explicit preview advances decisions without waiting for presentation pacing.
                    var result = _interaction.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action);
                    if (result is not { Accepted: true }) throw new InvalidOperationException("预览AI无法推进。");
                    ClearSelection(); Refresh("伤害预览");
                }
                else DriveAction(BasicBattleAi.Choose(_interaction.View)!.Action);
                if (_interaction.ReadLog(sequence).Any(e => e.Code == "damage.dealt")) break;
                ResetBattleMotion(); sequence = _presentedSequence;
            }
            await ToSignal(GetTree().CreateTimer(.34), SceneTreeTimer.SignalName.Timeout);
            return;
        }
        var action = _interaction.View.LegalActions.First(a => a.CardId is not null && a.SkillId is null && a.Kind == BattleActionKind.Slash);
        if (mode == "cast-animation") { DriveAction(action); return; }
        var button = _handCards.Single(p => p.Value.InstanceId == action.CardId).Key;
        var start = button.GetGlobalRect().Position + new Vector2(8, 35);
        MovePointer(start); await MotionFrame();
        if (mode == "hover")
        {
            await ToSignal(GetTree().CreateTimer(.20), SceneTreeTimer.SignalName.Timeout);
            return;
        }
        GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        await MotionFrame();
        MovePointer(SeatCenter(action.TargetId!) + new Vector2(0, 20));
        await MotionFrame();
    }

    private async Task MotionFrame()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void MovePointer(Vector2 point) => GetViewport().PushInput(new InputEventMouseMotion
        { Position = point, GlobalPosition = point }, true);

    private async Task DragWithMouse(string id, Vector2 destination)
    {
        await MotionFrame();
        var button = _handCards.Single(p => p.Value.InstanceId == id).Key;
        var start = button.GetGlobalRect().Position + new Vector2(8, 35);
        MovePointer(start); await MotionFrame();
        GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        await MotionFrame();
        MovePointer(start + new Vector2(0, -18)); await MotionFrame();
        if (!_dragging || _dragFace is null || _dragFace.MouseFilter != MouseFilterEnum.Ignore)
            throw new InvalidOperationException("真实鼠标拖动未生成可穿透浮牌。");
        MovePointer(destination); await MotionFrame();
        GetViewport().PushInput(new InputEventMouseButton { Position = destination, GlobalPosition = destination, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await MotionFrame();
        if (_dragging || _pointerCard is not null || _dropZone.Visible) throw new InvalidOperationException("松手后拖牌状态未清理。");
    }

    private void FreshMotionBattle(int size = 2)
    {
        _interaction = new(DevelopmentBattleScenario.Create(42, size, size));
        _focusUnitId = "development.guanyu"; ClearSelection(); _ignoredTrick = null; _logVersion = -1;
        ResetPresentation(); Refresh("动画交互回归");
    }

    private async Task RunMotionSmoke()
    {
        _aiTimer.Stop();
        try
        {
            FreshMotionBattle(); await MotionFrame();
            var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            var first = _handCards.First();
            var rect = first.Key.GetGlobalRect();
            MovePointer(rect.Position + new Vector2(8, 35)); await MotionFrame();
            await ToSignal(GetTree().CreateTimer(.20), SceneTreeTimer.SignalName.Timeout);
            if (_hoverFace is null || _hoverFace.Scale.X < 1.1f || _hoverFace.GlobalPosition.Y >= rect.Position.Y - 20)
                throw new InvalidOperationException("悬停没有明显抬升放大。");
            if (_hoverFace.MouseFilter != MouseFilterEnum.Ignore) throw new InvalidOperationException("悬停牌面阻挡鼠标。");
            MovePointer(new Vector2(15, 50)); await MotionFrame();
            if (_hoverFace is not null || first.Key.Modulate != Colors.White) throw new InvalidOperationException("移出后牌面未复位。");
            if (hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("悬停改变了规则状态。");

            // Disabled cards must still be movable; sorting cannot mutate the engine hand.
            var disabled = _handCards.First(p => p.Key.Disabled).Value.InstanceId;
            var right = _handCards.Last().Key.GetGlobalRect();
            await DragWithMouse(disabled, new Vector2(right.End.X - 3, right.GetCenter().Y));
            if (_handCards.OrderBy(p => p.Key.Position.X).Last().Value.InstanceId != disabled)
                throw new InvalidOperationException("不可出牌的手牌无法拖动排序。");
            if (hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("拖动排序改变了规则手牌。");

            var action = _interaction.View.LegalActions.First(a => a.CardId is not null && a.SkillId is null && NeedsTarget(a));
            await DragWithMouse(action.CardId!, new Vector2(20, 50));
            if (hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) || _interaction.Selection is not null)
                throw new InvalidOperationException("无效放置消耗牌或提交了选择。");
            await ToSignal(GetTree().CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
            await MouseClick(_handCards.Single(p => p.Value.InstanceId == action.CardId).Key, true);
            if (_selectedCard != action.CardId) throw new InvalidOperationException("拖动接入破坏了原点击选牌。");
            await MouseClick(_cancel);

            var flights = _flightCount;
            var version = _interaction.View.StateVersion;
            await DragWithMouse(action.CardId!, SeatCenter(action.TargetId!));
            if (_interaction.View.StateVersion <= version || _flightCount <= flights
                || !_interaction.Snapshot.Receipts.Any(r => r.Command.Action == action))
                throw new InvalidOperationException($"拖向合法目标未实际出牌或没有飞牌：version={version}->{_interaction.View.StateVersion} flights={flights}->{_flightCount} action={action}。");

            // Target-free and response cards share the drop zone and the same legal action contract.
            FreshMotionBattle();
            for (var step = 0; step < 500 && !_interaction.View.LegalActions.Any(a => a.Kind == BattleActionKind.ExNihilo && a.SkillId is null); step++)
            {
                if (_interaction.View.PendingDecision is null) throw new InvalidOperationException("未找到自用牌回归状态。");
                if (_interaction.WaitingForAi) AdvanceAi(); else DriveAction(BasicBattleAi.Choose(_interaction.View)!.Action);
            }
            await MotionFrame();
            var self = _interaction.View.LegalActions.First(a => a.Kind == BattleActionKind.ExNihilo && a.SkillId is null);
            version = _interaction.View.StateVersion; flights = _flightCount;
            await DragWithMouse(self.CardId!, _dropZone.GetGlobalRect().GetCenter());
            if (_interaction.View.StateVersion <= version || _flightCount <= flights)
                throw new InvalidOperationException("自用牌拖入出牌区未生效。");

            FreshMotionBattle(); await MotionFrame();
            var draggedResponse = false; var gotDamage = false; var impacts = _impactCount;
            for (var step = 0; step < 500 && _interaction.View.PendingDecision is not null; step++)
            {
                var next = _interaction.WaitingForAi ? null : BasicBattleAi.Choose(_interaction.View)!.Action;
                if (next is not null && _interaction.View.Response is not null && next.CardId is not null
                    && next.SkillId is null && !NeedsTarget(next) && next.Kind != BattleActionKind.Discard && !draggedResponse)
                {
                    version = _interaction.View.StateVersion;
                    await DragWithMouse(next.CardId, _dropZone.GetGlobalRect().GetCenter());
                    if (_interaction.View.StateVersion <= version) throw new InvalidOperationException("拖动响应牌没有提交。");
                    draggedResponse = true;
                }
                else if (_interaction.WaitingForAi) AdvanceAi();
                else DriveAction(next!);
                gotDamage |= _interaction.ReadLog().Any(e => e.Code == "damage.dealt");
                if (draggedResponse && gotDamage) break;
                await MotionFrame();
            }
            await ToSignal(GetTree().CreateTimer(.35), SceneTreeTimer.SignalName.Timeout);
            if (!draggedResponse || !gotDamage || _impactCount <= impacts) throw new InvalidOperationException("未覆盖真实响应拖牌和伤害粒子。");
            foreach (var child in _motionLayer.GetChildren().OfType<Control>())
                if (child.MouseFilter != MouseFilterEnum.Ignore) throw new InvalidOperationException("动画层存在拦截输入的节点。");

            foreach (var skill in new[] { BattleSkills.Wusheng, BattleSkills.FenjiI })
            {
                FreshMotionBattle();
                for (var step = 0; step < 500 && !_interaction.View.LegalActions.Any(a => a.SkillId == skill); step++)
                {
                    if (_interaction.View.PendingDecision is null) throw new InvalidOperationException("未找到技能拖牌状态：" + skill);
                    if (_interaction.WaitingForAi) AdvanceAi(); else DriveAction(BasicBattleAi.Choose(_interaction.View)!.Action);
                }
                await MotionFrame();
                var skillAction = _interaction.View.LegalActions.First(a => a.SkillId == skill);
                await MouseClick(_skillButtons[skill]);
                if (skillAction.CostCardId is { } cost)
                {
                    await MouseClick(_handCards.Single(p => p.Value.InstanceId == skillAction.CardId).Key, true);
                    await MouseClick(_handCards.Single(p => p.Value.InstanceId == cost).Key, true);
                }
                version = _interaction.View.StateVersion;
                await DragWithMouse(skillAction.CardId!, SeatCenter(skillAction.TargetId!));
                if (_interaction.View.StateVersion <= version || !_interaction.Snapshot.Receipts.Any(r => r.Command.Action == skillAction))
                    throw new InvalidOperationException("技能转换或额外成本拖牌失败：" + skill);
            }

            FreshMotionBattle(); await MotionFrame();
            PrepareDiscardPreview(); await MotionFrame();
            hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            await DragWithMouse(_handCards.First().Value.InstanceId, _dropZone.GetGlobalRect().GetCenter());
            if (hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) || _selectedDiscards.Count != 0)
                throw new InvalidOperationException("拖牌绕过了弃牌批量确认。");
            ResetPresentation(); await MotionFrame();
            if (_motionLayer.GetChildCount() != 1 || _hoverFace is not null || _dragFace is not null)
                throw new InvalidOperationException("重置后残留历史动画。");
            GD.Print($"BATTLE_MOTION_SMOKE_OK hover=lift drag=reorder,target,self,response skills=2 invalid=return damage=particles flights={_flightCount}");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
}
