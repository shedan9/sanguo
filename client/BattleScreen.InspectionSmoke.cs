using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private async Task RunInspectionSmoke()
    {
        _aiTimer.Stop();
        try
        {
            var inspected = 0;
            foreach (var window in new[] { new Vector2I(960, 600), new Vector2I(1280, 720), new Vector2I(1920, 1080) })
            {
                GetWindow().Size = window;
                _interaction = new(DevelopmentBattleScenario.Create(42, 4, 4));
                ClearSelection(); _logVersion = -1;
                Refresh("卡牌详情回归");
                // Enter before the first container layout, as can happen when restoring keyboard focus.
                var earlyCard = _handCards.First();
                earlyCard.Key.EmitSignal(Control.SignalName.MouseEntered);
                if (_inspectionPanel.Visible) throw new InvalidOperationException("快速经过手牌时过早弹出详情。");
                await ToSignal(GetTree().CreateTimer(HoverDetailDelay + .06), SceneTreeTimer.SignalName.Timeout);
                await KeyboardFrame();
                CheckCardInspection(earlyCard.Key, earlyCard.Value);
                earlyCard.Key.EmitSignal(Control.SignalName.MouseExited);
                var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
                foreach (var (button, card) in _handCards)
                {
                    await MoveInspectionPointer(button.GetGlobalRect().GetCenter());
                    CheckCardInspection(button, card);
                    inspected++;
                }
                var disabled = _handCards.First(p => p.Key.Disabled);
                await MoveInspectionPointer(disabled.Key.GetGlobalRect().GetCenter());
                if (!_cardDetail.Text.Contains("当前无合法动作", StringComparison.Ordinal)) throw new InvalidOperationException("灰置牌未显示不可用说明。");
                await TapKeyboard(Key.Escape);
                if (_inspectionPanel.Visible) throw new InvalidOperationException("Esc未收起悬停预览。");
                await KeyboardFrame();
                if (_inspectionPanel.Visible) throw new InvalidOperationException("Esc收起后预览自动重现。");
                await MoveInspectionPointer(new Vector2(4, 4));
                var enabled = _handCards.First(p => !p.Key.Disabled);
                await NavigateWithKeyboard(_inspectButton);
                await NavigateWithKeyboard(enabled.Key);
                CheckCardInspection(enabled.Key, enabled.Value);
                await TapKeyboard(Key.Escape);
                if (_inspectionPanel.Visible || !enabled.Key.HasFocus()) throw new InvalidOperationException("收起预览改变了手牌焦点。");
                await NavigateWithKeyboard(_inspectButton); await TapKeyboard(Key.Enter);
                if (!_handDialog.Visible || !_handChoice.HasFocus() || _inspectionPanel.Visible
                    || _inspectionHand.Count != _handCards.Count || _handChoice.ItemCount != _handCards.Count)
                    throw new InvalidOperationException("手牌一览未显示当前完整手牌或遮挡弹层。");
                CheckInspectionText(_handInspectionContent);
                var index = _handChoice.Selected;
                await TapKeyboard(Key.Enter); await TapKeyboard(Key.Down); await TapKeyboard(Key.Enter);
                if (_handChoice.Selected == index) throw new InvalidOperationException("无法用键盘切换查看手牌。");
                var browsed = _inspectionHand[_handChoice.Selected];
                if (!InspectionLabels(_handInspectionContent).Any(l => l.Text == browsed.Details)) throw new InvalidOperationException("手牌一览内容未随选择更新。");
                _handChoice.Selected = _inspectionHand.FindIndex(p => p.Card == disabled.Value);
                _handChoice.EmitSignal(OptionButton.SignalName.ItemSelected, _handChoice.Selected);
                if (!InspectionLabels(_handInspectionContent).Any(l => l.Text.Contains("当前无合法动作", StringComparison.Ordinal)))
                    throw new InvalidOperationException("手牌一览未包含灰置牌完整说明。");
                await KeyboardFrame(); CheckInspectionText(_handInspectionContent);
                await TapKeyboard(Key.Escape);
                if (_handDialog.Visible || !_inspectButton.HasFocus()) throw new InvalidOperationException("关闭手牌一览未归还焦点。");

                // A pending selection survives the read-only browser; the browser never confirms it.
                var action = _interaction.View.LegalActions.First(a => a.CardId is not null && a.SkillId is null);
                SelectMouseCard(action.CardId!);
                if (NeedsTarget(action)) SelectTarget(action.TargetId!);
                await KeyboardFrame();
                await NavigateWithKeyboard(_inspectButton); await TapKeyboard(Key.Space); await TapKeyboard(Key.Escape);
                if (_interaction.Selection != action || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                    throw new InvalidOperationException("查看卡面提交或取消了预览操作。");
                _interaction.Cancel();

                // Refresh while the browser is open must remove the previous actor's hand.
                ShowHandInspection();
                while (!_interaction.WaitingForAi && _interaction.View.PendingDecision is not null)
                {
                    _interaction.Select(_interaction.View.LegalActions.FirstOrDefault(a => a.Kind == BattleActionKind.EndPlay)
                        ?? BasicBattleAi.Choose(_interaction.View)!.Action);
                    if (!_interaction.Confirm().Accepted) throw new InvalidOperationException("卡牌详情场景推进失败。");
                }
                Refresh("敌方请求隔离回归"); await KeyboardFrame();
                if (_inspectionHand.Count != 0 || _handChoice.ItemCount != 0 || !_handChoice.Disabled || _inspectionPanel.Visible)
                    throw new InvalidOperationException("敌方请求仍显示上一位玩家手牌。");
                await TapKeyboard(Key.Escape);

                // Exercise every card face and screen-edge placement using display-only samples.
                _interaction = new(DevelopmentBattleScenario.Create()); ClearSelection(); Refresh("边界回归"); await KeyboardFrame();
                await MoveInspectionPointer(new Vector2(4, 4));
                var sampleIndex = _handCards.Count;
                var samples = new List<(Button Control, BattleCard Card)>();
                foreach (var kind in Enum.GetValues<BasicCardKind>())
                {
                    var sample = new BattleCard("inspection.display." + kind, kind, "diamond", 13);
                    var control = CreateCardButton(sample, true, () => { }); _hand.AddChild(control);
                    control.Position = new Vector2(sampleIndex++ * 88, 0); control.Size = new Vector2(82, 112);
                    samples.Add((control, sample));
                }
                _hand.CustomMinimumSize = new Vector2(sampleIndex * 88, 122);
                await KeyboardFrame(); await KeyboardFrame();
                foreach (var (control, sample) in samples)
                {
                    control.GrabFocus(); await KeyboardFrame(); await KeyboardFrame();
                    CheckCardInspection(control, sample); inspected++;
                }
                if (BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) != BasicBattleSnapshotCodec.StateHash(DevelopmentBattleScenario.Create().State))
                    throw new InvalidOperationException("显示用卡面改变了真实战斗。");
                // A source scrolled out of view cannot leave a detached overlay behind.
                var handScroll = _hand.GetParent<ScrollContainer>();
                var sampleCard = _handCards.Values.First();
                for (var i = 0; i < 24; i++)
                {
                    var extra = CreateCardButton(sampleCard, true, () => { }); _hand.AddChild(extra);
                    extra.Position = new Vector2(sampleIndex++ * 88, 0); extra.Size = new Vector2(82, 112);
                }
                _hand.CustomMinimumSize = new Vector2(sampleIndex * 88, 122);
                _hand.GetChild<Button>(-1).GrabFocus(); await KeyboardFrame(); await KeyboardFrame();
                handScroll.ScrollHorizontal = 0; await KeyboardFrame();
                if (_inspectionPanel.Visible) throw new InvalidOperationException("滚出视口的卡牌残留预览。");
                Refresh("刷新清理回归");
                if (_inspectionPanel.Visible) throw new InvalidOperationException("刷新未清除旧卡面预览。");
            }
            GD.Print($"BATTLE_INSPECTION_SMOKE_OK windows=3 inspected={inspected}");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private async Task MoveInspectionPointer(Vector2 position)
    {
        GetViewport().PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position }, true);
        await ToSignal(GetTree().CreateTimer(HoverDetailDelay + .06), SceneTreeTimer.SignalName.Timeout);
        await KeyboardFrame();
    }

    private void CheckCardInspection(Control owner, BattleCard card)
    {
        if (!_inspectionPanel.Visible || !InspectionLabels(_inspectionPanel).Any(l => l.Text == Card(card))
            || !new Rect2(Vector2.Zero, GetViewportRect().Size).Encloses(_inspectionPanel.GetGlobalRect())
            || _inspectionPanel.GetGlobalRect().Intersects(owner.GetGlobalRect())
            || owner.GetParent() == _hand && _inspectionPanel.GetGlobalRect().Intersects(_decisionControls.GetGlobalRect())
            || InspectionControls(_inspectionPanel).Any(c => c.MouseFilter != MouseFilterEnum.Ignore || c.FocusMode != FocusModeEnum.None))
            throw new InvalidOperationException($"卡牌预览内容、边界或输入穿透错误：{Card(card)} owner={owner.GetGlobalRect()} preview={_inspectionPanel.GetGlobalRect()} visible={_inspectionPanel.Visible} focus={owner.HasFocus()} scrolls="
                + string.Join(" / ", AncestorScrolls(owner).Select(s => $"{s.GetGlobalRect()} h={s.ScrollHorizontal} max={s.GetHScrollBar().MaxValue} page={s.GetHScrollBar().Page}")));
        CheckInspectionText(_inspectionPanel);
    }

    private static void CheckInspectionText(Control content)
    {
        foreach (var label in InspectionLabels(content))
            if (!content.GetGlobalRect().Encloses(label.GetGlobalRect()) || label.GetLineCount() > label.GetVisibleLineCount())
                throw new InvalidOperationException("卡牌完整说明被裁切：" + label.Text);
    }

    private static IEnumerable<Control> InspectionControls(Node root)
    {
        if (root is Control control) yield return control;
        foreach (var child in root.GetChildren())
            foreach (var nested in InspectionControls(child)) yield return nested;
    }
    private static IEnumerable<Label> InspectionLabels(Node root) => InspectionControls(root).OfType<Label>();
}
