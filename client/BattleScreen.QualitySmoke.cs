using System.Collections.Immutable;
using Godot;
using Sanguo.AI;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private async Task RunQualitySmoke()
    {
        _aiTimer.Stop();
        try
        {
            FreshMotionBattle(); await MotionFrame();
            var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            var card = _handCards.First(p => p.Key.Disabled).Value.InstanceId;
            var end = _handCards.Last().Key.GetGlobalRect();
            await DragWithMouse(card, end.End - new Vector2(3, 35));
            var order = _handCards.OrderBy(p => p.Key.Position.X).Select(p => p.Value.InstanceId).ToArray();
            Clear(_hand); _handCards.Clear(); RenderMouseHand(_interaction.View, "development.protagonist");
            Refresh("友军手牌排序恢复"); await MotionFrame();
            if (!_handCards.OrderBy(p => p.Key.Position.X).Select(p => p.Value.InstanceId).SequenceEqual(order)
                || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("切换友军丢失手牌排序或改变状态。");

            // Display-only oversized hand: no fake card enters the core or a save file.
            var view = _interaction.View;
            var samples = Enumerable.Range(0, 40).Select(i => new BattleCard($"quality.display.{i}", BasicCardKind.Slash, "heart", i % 13 + 1)).ToImmutableArray();
            var units = view.Combatants.Select(u => u.EntityId == "development.guanyu" ? u with { VisibleHand = samples, HandCount = samples.Length } : u).ToImmutableArray();
            Clear(_hand); _handCards.Clear(); RenderMouseHand(view with { Combatants = units }, "development.guanyu"); await MotionFrame();
            var first = _handCards.First().Key;
            var start = first.GetGlobalRect().Position + new Vector2(8, 35);
            MovePointer(start);
            GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            MovePointer(start - new Vector2(0, 18)); await MotionFrame();
            MovePointer(_handScroll.GetGlobalRect().End - new Vector2(3, 45)); await MotionFrame();
            await ToSignal(GetTree().CreateTimer(.25), SceneTreeTimer.SignalName.Timeout);
            if (!_dragging || _handScroll.ScrollHorizontal <= 0) throw new InvalidOperationException("拖牌到边缘未自动滚动。");
            OnBattleWindowFocusExited(); await MotionFrame();
            if (_pointerCard is not null || _dragFace is not null || _dropZone.Visible || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                throw new InvalidOperationException("失焦后拖牌未清理或改变状态。");

            foreach (var escape in new[] { false, true })
            {
                await MotionFrame();
                var button = _handCards.First().Key;
                start = button.GetGlobalRect().Position + new Vector2(8, 35);
                MovePointer(start);
                GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start, ButtonIndex = MouseButton.Left, Pressed = true }, true);
                MovePointer(start - new Vector2(0, 30)); await MotionFrame();
                if (!_dragging) throw new InvalidOperationException("取消测试未进入拖牌。");
                if (escape) GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true }, true);
                else GetViewport().PushInput(new InputEventMouseButton { Position = start, ButtonIndex = MouseButton.Right, Pressed = true }, true);
                GetViewport().PushInput(new InputEventMouseButton { Position = start, ButtonIndex = MouseButton.Left, Pressed = false }, true);
                await MotionFrame();
                if (_pointerCard is not null || _selectedCard is not null || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                    throw new InvalidOperationException("拖牌右键或Esc取消后误选牌或改变状态。");
            }

            FreshMotionBattle(); await MotionFrame();
            for (var step = 0; _interaction.View.PendingDecision is not null; step++)
            {
                if (step > 4000) throw new InvalidOperationException("终局复查超限。");
                if (_interaction.WaitingForAi) AdvanceAi(); else DriveAction(BasicBattleAi.Choose(_interaction.View)!.Action);
            }
            ResetBattleMotion(); await MotionFrame();
            hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            ShowBattleResult(); await MotionFrame();
            if (!_resultDialog.Visible || !_resultSummary.Text.Contains("我方造成伤害", StringComparison.Ordinal)
                || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("终局未显示真实结算或查看改变了状态。");
            if (DisplayServer.GetName() != "headless" && (!GetViewportRect().Encloses(new Rect2(_resultDialog.Position, _resultDialog.Size))
                || !_resultDialog.GetVisibleRect().Encloses(_resultContinue.GetGlobalRect())))
                throw new InvalidOperationException($"结算弹层或继续按钮被窗口裁切：root={GetViewportRect()} dialog={_resultDialog.Position}/{_resultDialog.Size} local={_resultDialog.GetVisibleRect()} button={_resultContinue.GetGlobalRect()}");
            // The headless display reports native popup windows as 1x1; real rendering tests the mouse path.
            if (DisplayServer.GetName() == "headless") _resultContinue.EmitSignal(BaseButton.SignalName.Pressed);
            else await MouseClick(_resultContinue);
            if (_resultDialog.Visible || !_setupDialog.Visible) throw new InvalidOperationException("终局无法开始新牌局。");
            _setupDialog.Hide(); NewBattle(); _aiTimer.Stop(); await MotionFrame();
            if (_resultShown || _resultDialog.Visible || _interaction.View.WinnerTeamId is not null) throw new InvalidOperationException("新牌局残留旧终局。");
            GD.Print("BATTLE_QUALITY_SMOKE_OK order=per-ally overflow=scroll focus=cancel result=continue"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
}
