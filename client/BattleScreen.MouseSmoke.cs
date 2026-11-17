using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private void DriveAction(BattleAction action)
    {
        if (action.Kind == BattleActionKind.Discard)
        {
            SelectMouseCard(action.CardId!);
            foreach (var extra in _interaction.View.LegalActions.Where(a => a.Kind == BattleActionKind.Discard && a.CardId != action.CardId).Take(RequiredDiscards - 1)) SelectMouseCard(extra.CardId!);
            _confirm.EmitSignal(BaseButton.SignalName.Pressed); return;
        }
        if (action.Kind == BattleActionKind.SelectHandCard) { _choiceActions.Single(p => p.Value == action).Key.EmitSignal(BaseButton.SignalName.Pressed); return; }
        if (action.Kind == BattleActionKind.Pass) { _pass.EmitSignal(BaseButton.SignalName.Pressed); return; }
        if (action.Kind == BattleActionKind.EndPlay) { _end.EmitSignal(BaseButton.SignalName.Pressed); return; }
        if (action.SkillId is { } skill && _skillButtons.TryGetValue(skill, out var button)) button.EmitSignal(BaseButton.SignalName.Pressed);
        if (action.CardId is { } card) _handCards.Single(p => p.Value.InstanceId == card).Key.EmitSignal(BaseButton.SignalName.Pressed);
        if (action.CostCardId is { } cost) _handCards.Single(p => p.Value.InstanceId == cost).Key.EmitSignal(BaseButton.SignalName.Pressed);
        if (NeedsTarget(action)) _seatButtons[action.TargetId!].EmitSignal(BaseButton.SignalName.Pressed);
        if (_interaction.Selection != action) throw new InvalidOperationException("鼠标控件未生成正确动作：" + action);
        _confirm.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void RunSmoke(string[] args)
    {
        _aiTimer.Stop();
        if (args.Length != 3 || args[1] is not ("write" or "resume") || !Path.IsPathFullyQualified(args[2])) throw new ArgumentException("用法：--battle-smoke write|resume <绝对路径>");
        var path = Path.Combine(args[2], "client-battle.json"); var resume = args[1] == "resume";
        if (resume) { _interaction.Restore(_store.Load(path, DevelopmentBattleScenario.ContentHash).State); ClearSelection(); ResetPresentation(); Refresh("恢复烟测"); }
        var steps = 0; var saved = resume;
        while (_interaction.View.PendingDecision is not null)
        {
            if (++steps > 4000) throw new InvalidOperationException("对局步数保护。");
            if (!saved && steps >= 12) { _store.Save(path, _interaction.Snapshot); saved = true; }
            if (_interaction.WaitingForAi) { AdvanceAi(); } else DriveAction(BasicBattleAi.Choose(_interaction.View)!.Action);
        }
        if (!saved) throw new InvalidOperationException("未生成检查点。");
        File.WriteAllBytes(Path.Combine(args[2], resume ? "resumed-final.json" : "final.json"), BasicBattleSnapshotCodec.Encode(_interaction.Snapshot));
        GD.Print($"BATTLE_CLIENT_{(resume ? "RESUME" : "SMOKE")}_OK {BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)} steps={steps}"); GetTree().Quit();
    }

    private async Task MouseClick(Control control, bool hand = false)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var rect = control.GetGlobalRect(); var point = hand ? rect.Position + new Vector2(8, 30) : rect.GetCenter();
        var viewport = control.GetViewport();
        // Embedded windows receive real pointer events through the parent viewport's popup router.
        if (viewport is Window popup && popup != GetWindow())
        { point += popup.Position; viewport = GetViewport(); }
        if (!viewport.GetVisibleRect().HasPoint(point)) throw new InvalidOperationException("鼠标目标超出屏幕。" + control.Name);
        viewport.PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
        viewport.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        viewport.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task ChooseWithMouse(BattleAction action, bool cancel)
    {
        if (action.Kind == BattleActionKind.Discard)
        {
            var before = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            var ids = new[] { action.CardId! }.Concat(_interaction.View.LegalActions.Where(a => a.Kind == BattleActionKind.Discard && a.CardId != action.CardId)
                .Take(RequiredDiscards - 1).Select(a => a.CardId!)).ToArray();
            foreach (var id in ids) await MouseClick(_handCards.Single(p => p.Value.InstanceId == id).Key, true);
            if (_confirm.Disabled) throw new InvalidOperationException("选满弃牌后无法确认。");
            if (cancel)
            {
                await MouseClick(_cancel);
                if (_selectedDiscards.Count != 0 || BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) != before) throw new InvalidOperationException("取消弃牌改变了状态。");
                foreach (var id in ids) await MouseClick(_handCards.Single(p => p.Value.InstanceId == id).Key, true);
            }
            await MouseClick(_confirm); return;
        }
        if (action.Kind == BattleActionKind.SelectHandCard) { await MouseClick(_choiceActions.Single(p => p.Value == action).Key); return; }
        if (action.Kind == BattleActionKind.Pass) { await MouseClick(_pass); return; }
        if (action.Kind == BattleActionKind.EndPlay) { await MouseClick(_end); return; }
        if (action.SkillId is { } skill && _skillButtons.TryGetValue(skill, out var button)) await MouseClick(button);
        if (action.CardId is { } card)
        {
            await MouseClick(_handCards.Single(p => p.Value.InstanceId == card).Key, true);
            if (_selectedCard != card) throw new InvalidOperationException($"鼠标未选中手牌：{card} selected={_selectedCard} hover={GetViewport().GuiGetHoveredControl()?.Name}");
        }
        if (action.CostCardId is { } cost) await MouseClick(_handCards.Single(p => p.Value.InstanceId == cost).Key, true);
        if (NeedsTarget(action)) await MouseClick(_seatButtons[action.TargetId!]);
        if (_interaction.Selection != action) throw new InvalidOperationException($"鼠标点击未选中合法动作：{action} selectedTarget={_selectedTarget} hover={GetViewport().GuiGetHoveredControl()?.Name}");
        if (cancel && action.CardId is not null)
        {
            var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot); await MouseClick(_cancel);
            if (_interaction.Selection is not null || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("取消消耗了牌或次数。");
            await ChooseWithMouse(action, false); return;
        }
        await MouseClick(_confirm);
    }

    private async Task RunInteractionSmoke()
    {
        _aiTimer.Stop();
        try
        {
            var clicks = 0; var contexts = new HashSet<EffectKind>(); var checkedIgnore = false;
            foreach (var size in new[] { 2, 4 })
            {
                _interaction = new(DevelopmentBattleScenario.Create(42, size, size)); var reference = DevelopmentBattleScenario.Create(42, size, size);
                ClearSelection(); _ignoredTrick = null; _focusUnitId = "development.guanyu"; _logVersion = -1; ResetPresentation(); Refresh("鼠标整局回归");
                var steps = 0;
                while (_interaction.View.PendingDecision is not null)
                {
                    if (++steps > 4000) throw new InvalidOperationException("鼠标对局超限。");
                    if (_interaction.View.Response is { } response) contexts.Add(response.Kind);
                    if (!checkedIgnore && _ignore.Visible)
                    {
                        var checkpoint = _interaction.Snapshot;
                        var trick = _interaction.View.Response!.Trick!.CardId;
                        await MouseClick(_ignore);
                        if (_interaction.View.PendingDecision?.ControllerId == "player" && _interaction.View.Response is { Kind: EffectKind.Nullification } next && next.Trick?.CardId == trick)
                            throw new InvalidOperationException("本次锦囊仍反复询问。");
                        _interaction.Restore(checkpoint); ClearSelection(); _ignoredTrick = null; ResetPresentation(); Refresh("恢复无懈请求");
                        if (!_ignore.Visible) throw new InvalidOperationException("恢复后无懈请求未重新开放。");
                        checkedIgnore = true;
                    }
                    var before = _interaction.View.StateVersion;
                    if (_interaction.WaitingForAi) AdvanceAi();
                    else { await ChooseWithMouse(BasicBattleAi.Choose(_interaction.View)!.Action, clicks % 12 == 0); clicks++; }
                    if (_interaction.View.StateVersion <= before) throw new InvalidOperationException("鼠标点击未提交命令。");
                    foreach (var receipt in _interaction.Snapshot.Receipts.Skip(reference.State.Receipts.Length))
                        if (!reference.Submit(receipt.Command).Accepted) throw new InvalidOperationException("参照拒绝鼠标命令。");
                    if (BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) != BasicBattleSnapshotCodec.StateHash(reference.State)) throw new InvalidOperationException("鼠标对局与参照不同。");
                    if (_interaction.View.Response?.Kind == EffectKind.SelectHandCard && _interaction.View.PendingDecision?.ControllerId == "player"
                        && (!_choiceOverlay.Visible || _choiceActions.Count == 0)) throw new InvalidOperationException("拆顺未使用牌背弹层。");
                }
                if (_interaction.View.WinnerTeamId is null) throw new InvalidOperationException("未到终局。");
            }
            if (!checkedIgnore || clicks < 20 || !contexts.Contains(EffectKind.Nullification) || !contexts.Contains(EffectKind.SelectHandCard)) throw new InvalidOperationException("鼠标覆盖不足。");
            GD.Print($"BATTLE_MOUSE_SMOKE_OK games=2 decisions={clicks} contexts={string.Join(',', contexts.Order())}"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private async Task RunLayoutSmoke()
    {
        _aiTimer.Stop();
        try
        {
            var layouts = 0;
            foreach (var width in new[] { 960, 1280 })
            {
                GetWindow().Size = new Vector2I(width, width == 960 ? 600 : 720);
                for (var p = 1; p <= 4; p++) for (var e = 1; e <= 4; e++)
                {
                    _interaction = new(DevelopmentBattleScenario.Create(42, p, e)); _focusUnitId = "development.guanyu"; _logVersion = -1; ClearSelection(); ResetPresentation(); Refresh("布局");
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    var viewport = GetViewportRect(); var buttons = _seatButtons.Values.ToArray();
                    if (buttons.Length != p + e) throw new InvalidOperationException("座次遗漏。");
                    for (var i = 0; i < buttons.Length; i++)
                    {
                        if (!viewport.Encloses(buttons[i].GetGlobalRect())) throw new InvalidOperationException("角色框超出画面。");
                        for (var j = 0; j < i; j++) if (buttons[i].GetGlobalRect().Intersects(buttons[j].GetGlobalRect())) throw new InvalidOperationException("角色框重叠。");
                    }
                    if (!viewport.Encloses(_confirm.GetGlobalRect()) || !viewport.Encloses(_handScroll.GetGlobalRect())) throw new InvalidOperationException("手牌或操作区超界。");
                    layouts++;
                }
            }
            GD.Print($"BATTLE_LAYOUT_SMOKE_OK layouts={layouts}"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private async Task RunArtworkSmoke()
    {
        _aiTimer.Stop();
        try
        {
            foreach (var kind in Enum.GetValues<BasicCardKind>()) if (_art.CardFace(kind).GetSize() != new Vector2(186, 260)) throw new InvalidOperationException("卡面尺寸不符。");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (var (button, card) in _handCards)
            {
                button.EmitSignal(Control.SignalName.MouseEntered);
                if (!_cardDetail.Text.StartsWith(Card(card), StringComparison.Ordinal) || button.FocusMode != FocusModeEnum.All) throw new InvalidOperationException("牌面说明或可聚焦查看能力错误。");
            }
            GD.Print("BATTLE_ARTWORK_SMOKE_OK cards=8 portraits=2"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private async Task CapturePreview(string[] args)
    {
        _aiTimer.Stop();
        _fastSmoke = true;
        try
        {
            if (args.Length is not (2 or 4 or 5) || !Path.IsPathFullyQualified(args[1])) throw new ArgumentException("预览参数错误。");
            if (args.Length >= 4) { _playerCount.Selected = int.Parse(args[2]) - 1; _opponentCount.Selected = int.Parse(args[3]) - 1; NewBattle(); _aiTimer.Stop(); }
            var mode = args.Length == 5 ? args[4] : "idle";
            if (mode is "settlement" or "nullification" or "choice")
            {
                for (var i = 0; i < 4000; i++)
                {
                    if (_interaction.View.PendingDecision?.ControllerId == "player" && (mode == "choice" && _choiceOverlay.Visible
                        || mode == "nullification" && _interaction.View.Response?.Kind == EffectKind.Nullification || mode == "settlement" && _interaction.View.Processing.Length > 0)) break;
                    if (_interaction.View.PendingDecision is null) throw new InvalidOperationException("未找到预览状态。");
                    if (_interaction.WaitingForAi) AdvanceAi(); else DriveAction(BasicBattleAi.Choose(_interaction.View)!.Action);
                }
            }
            else if (mode == "unit") ShowUnitDetails(); else if (mode == "log") ShowLog();
            else if (mode == "settings") ShowBattleDialog(_settingsDialog);
            else if (mode == "hand") ShowHandInspection();
            else if (mode == "result")
            {
                for (var step = 0; _interaction.View.PendingDecision is not null; step++)
                {
                    if (step > 4000) throw new InvalidOperationException("终局预览超限。");
                    if (_interaction.WaitingForAi) AdvanceAi(); else DriveAction(BasicBattleAi.Choose(_interaction.View)!.Action);
                }
                ResetBattleMotion(); ShowBattleResult();
            }
            else if (mode == "selected")
            {
                var card = _handCards.First(p => !p.Key.Disabled && _interaction.View.LegalActions.Any(a => a.CardId == p.Value.InstanceId && a.SkillId is null && NeedsTarget(a)));
                card.Key.EmitSignal(BaseButton.SignalName.Pressed);
            }
            else if (mode is "target" or "target-animation")
            {
                var action = _interaction.View.LegalActions.First(a => a.CardId is not null && a.SkillId is null && NeedsTarget(a));
                SelectMouseCard(action.CardId!); SelectTarget(action.TargetId!);
            }
            else if (mode is "discard" or "discard-selected")
            {
                PrepareDiscardPreview();
                if (mode == "discard-selected") AutoSelectDiscards();
            }
            else if (mode is "hover" or "drag" or "cast-animation" or "impact") await PrepareMotionPreview(mode);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (mode is "target-animation" or "cast-animation")
            {
                var folder = Path.GetDirectoryName(args[1])!;
                for (var frame = 0; frame < 12; frame++)
                {
                    await ToSignal(GetTree().CreateTimer(.07), SceneTreeTimer.SignalName.Timeout);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    var prefix = mode == "cast-animation" ? "cast" : "target";
                    var frameError = GetViewport().GetTexture().GetImage().SavePng(Path.Combine(folder, $"{prefix}-frame-{frame:D2}.png"));
                    if (frameError != Error.Ok) throw new IOException("无法保存动画帧。");
                }
            }
            var error = GetViewport().GetTexture().GetImage().SavePng(args[1]); GD.Print("BATTLE_PREVIEW " + error); GetTree().Quit(error == Error.Ok ? 0 : 1);
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
    private void PrepareDiscardPreview()
    {
        var state = _interaction.Snapshot;
        var index = Array.FindIndex(state.Battle.Combatants.ToArray(), u => u.EntityId == state.Battle.PendingDecision!.ActorId);
        var actor = state.Battle.Combatants[index];
        _interaction.Restore(state with { Battle = state.Battle with { Combatants = state.Battle.Combatants.SetItem(index, actor with { Hp = 1 }) } });
        ClearSelection(); ResetPresentation(); Refresh("弃牌场景");
        SubmitImmediate(BattleActionKind.EndPlay);
    }

    private async Task RunDesktopSmoke()
    {
        _aiTimer.Stop();
        try
        {
            var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            foreach (var index in new[] { 1, 2, 0 })
            {
                await MouseClick(_sidebarTabs[index]);
                if (_sidebarTab != index || _sidebarContent.GetChildCount() == 0) throw new InvalidOperationException("侧栏切换失败。");
            }
            await MouseClick(_sidebarToggle);
            if (_sidebar.Visible || _table.AnchorRight < .95f) throw new InvalidOperationException("侧栏未收起。");
            await MouseClick(_sidebarToggle);
            if (!_sidebar.Visible || _table.AnchorRight != TableRight) throw new InvalidOperationException("侧栏未恢复。");
            var action = _interaction.View.LegalActions.First(a => a.CardId is not null && a.SkillId is null && NeedsTarget(a));
            await MouseClick(_handCards.Single(p => p.Value.InstanceId == action.CardId).Key, true);
            await MouseClick(_sortHand);
            if (_selectedCard != action.CardId || !_sortByKind) throw new InvalidOperationException("整理手牌丢失选择。");
            await MouseClick(_seatButtons[action.TargetId!]);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!_targetLine.HasLine || _targetLine.MouseFilter != MouseFilterEnum.Ignore) throw new InvalidOperationException("指向线未显示或阻挡输入。");
            await MouseClick(_cancel);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_targetLine.HasLine) throw new InvalidOperationException("取消后指向线未清理。");
            if (hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("查看或预选改变了战斗。");
            PrepareDiscardPreview();
            if (!ChoosingDiscards || RequiredDiscards != 5) throw new InvalidOperationException("弃牌预览应要求5张。");
            hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            var first = _handCards.First().Value.InstanceId;
            await MouseClick(_handCards.Single(p => p.Value.InstanceId == first).Key, true);
            if (_selectedDiscards.Count != 1 || !_confirm.Disabled) throw new InvalidOperationException("不足5张仍可确认。");
            await MouseClick(_handCards.Single(p => p.Value.InstanceId == first).Key, true);
            if (_selectedDiscards.Count != 0) throw new InvalidOperationException("未能取消单张选择。");
            await MouseClick(_autoSelect);
            if (_selectedDiscards.Count != 5 || _confirm.Disabled) throw new InvalidOperationException("一键选择数量错误。");
            var unselected = _handCards.First(p => !_selectedDiscards.Contains(p.Value.InstanceId)).Value.InstanceId;
            await MouseClick(_handCards.Single(p => p.Value.InstanceId == unselected).Key, true);
            if (_selectedDiscards.Count != 5 || _selectedDiscards.Contains(unselected)) throw new InvalidOperationException("允许超选弃牌。");
            await MouseClick(_cancel);
            if (_selectedDiscards.Count != 0 || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("取消或预选消耗了牌。");
            await MouseClick(_autoSelect);
            var expected = _selectedDiscards.ToArray();
            await MouseClick(_confirm);
            if (!expected.All(id => _interaction.Snapshot.Battle.DiscardPile.Any(c => c.InstanceId == id))
                || ChoosingDiscards || _selectedDiscards.Count != 0) throw new InvalidOperationException("批量弃牌未正确提交。");
            _targetLine.Play(new Vector2(20, 200), new Vector2(300, 20));
            await ToSignal(GetTree().CreateTimer(.12), SceneTreeTimer.SignalName.Timeout);
            if (!_targetLine.HasLine) throw new InvalidOperationException("已提交指向动画过早消失。");
            await ToSignal(GetTree().CreateTimer(.8), SceneTreeTimer.SignalName.Timeout);
            if (_targetLine.HasLine) throw new InvalidOperationException("已提交指向动画未淡出。");
            GD.Print("BATTLE_DESKTOP_SMOKE_OK discard=5 sidebar=3 targeting=animated sort=preserved"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
}
