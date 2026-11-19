using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private void BuildProcessingArea(BattleView view)
    {
        var cards = BattleProcessingPresentation.Observe(view, _interaction.ReadLog());
        var panel = new PanelContainer { AnchorLeft = .23f, AnchorRight = .77f, AnchorTop = .5f, AnchorBottom = .5f,
            OffsetTop = -44, OffsetBottom = 44 };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("152723"),
            BorderColor = new Color("6C6048"), BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 });
        _processingPanel = panel;
        _table.AddChild(panel);
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8); panel.AddChild(row);
        var context = new VBoxContainer { CustomMinimumSize = new Vector2(148, 0) };
        row.AddChild(context);
        context.AddChild(Text(cards.IsEmpty ? "处理区 · 空" : $"处理区 · {cards.Length}张", 16, Gold));
        var state = view.WinnerTeamId is not null ? "本场战斗结束" : view.Response?.Kind switch
        {
            EffectKind.Nullification => $"无懈窗口 · 第{view.Response.ChainDepth}层",
            EffectKind.Dodge => "杀 · 等待闪响应", EffectKind.Duel => "决斗 · 等待杀响应",
            EffectKind.Rescue => "濒死 · 等待桃救援", EffectKind.InvokeJianxiong => "伤后 · 等待奸雄选择",
            EffectKind.SelectHandCard => "拆顺 · 等待暗牌选择", _ => "等待出牌"
        };
        context.AddChild(Text(state, 13));
        var relation = view.Response is { } response
            ? UnitName(view, response.Trick?.SourceId ?? response.SourceId) + " → " + UnitName(view, response.Trick?.TargetId ?? response.TargetId)
            : "选牌 → 目标 → 确认";
        var relationLabel = Text(relation, 12, Paper);
        relationLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        relationLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        relationLabel.TooltipText = relation;
        context.AddChild(relationLabel);
        if (cards.IsEmpty) return;
        var scroll = new ScrollContainer { FollowFocus = true, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        row.AddChild(scroll);
        var strip = new HBoxContainer(); strip.AddThemeConstantOverride("separation", 6); scroll.AddChild(strip);
        Control? current = null;
        foreach (var card in cards)
        {
            var tile = CreateProcessingCard(view, card);
            strip.AddChild(tile); _processingCards.Add(tile, card);
            if (card.AwaitingResponse) current = tile;
        }
        if (current is not null) scroll.CallDeferred("ensure_control_visible", current);
    }

    private Control CreateProcessingCard(BattleView view, ProcessingCardView item)
    {
        var height = GetViewportRect().Size.Y * .16f;
        var tile = new Control { CustomMinimumSize = new Vector2(height * .715f, height + 20), FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop };
        var marker = item.AwaitingResponse ? "待响应" : item.OriginalTrick ? "原锦囊" : "结算中";
        var title = Text(marker, 12, item.AwaitingResponse ? Gold : Paper);
        title.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide); title.OffsetBottom = 16;
        title.HorizontalAlignment = HorizontalAlignment.Center; title.MouseFilter = MouseFilterEnum.Ignore;
        tile.AddChild(title);
        var body = new Panel { AnchorTop = 0, AnchorBottom = 1, AnchorRight = 1, OffsetTop = 16,
            MouseFilter = MouseFilterEnum.Ignore };
        var border = new StyleBoxFlat { BgColor = Paper, BorderColor = Gold,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1 };
        body.AddThemeStyleboxOverride("panel", border); tile.AddChild(body);
        body.AddChild(CardArtwork(item.Card));
        if (item.EffectiveKind != item.Card.Kind)
        {
            var caption = DesktopLabel("作为【" + CardName(item.EffectiveKind) + "】", 12, Gold);
            var band = Picture(_art.Sprite("seat", "seat_opt_tips_bg"));
            Place(band, .02f, .78f, .98f, .98f); body.AddChild(band);
            caption.HorizontalAlignment = HorizontalAlignment.Center; Place(caption, 0, 0, 1, 1); band.AddChild(caption);
        }
        var details = $"{marker} · 原牌{Card(item.Card)} · 本次作为{CardName(item.EffectiveKind)}"
            + (item.SkillId is null ? "" : $"（{SkillName(item.SkillId)}）")
            + (item.SourceId is null ? "" : $" · {UnitName(view, item.SourceId)} → {UnitName(view, item.TargetId ?? item.SourceId)}")
            + (item.RespondsToCardId is null ? "" : " · 抵消" + Card(view.Processing.Single(c => c.InstanceId == item.RespondsToCardId)))
            + "。仅展示，不提交操作。";
        BindCardInspection(tile, item.Card, details);
        tile.FocusEntered += () =>
        {
            border.BorderWidthLeft = border.BorderWidthRight = border.BorderWidthTop = border.BorderWidthBottom = 3;
            _ = RevealBattleFocus(tile);
        };
        tile.FocusExited += () => border.BorderWidthLeft = border.BorderWidthRight = border.BorderWidthTop = border.BorderWidthBottom = 1;
        return tile;
    }

    private async Task RunProcessingSmoke()
    {
        _aiTimer.Stop();
        try
        {
            var inspected = 0;
            var kinds = new HashSet<EffectKind>();
            foreach (var width in new[] { 960, 1280 })
            {
                GetWindow().Size = new Vector2I(width, width == 960 ? 600 : 720);
                _interaction = new(DevelopmentBattleScenario.Create(42, 4, 4));
                var steps = 0;
                while (_interaction.View.PendingDecision is { } decision)
                {
                    if (++steps > 4000) throw new InvalidOperationException("处理区回归触发步数保护。");
                    var view = _interaction.View;
                    // Check every distinct response type and every nullification depth, not only the opening hand.
                    var key = view.Response?.Kind;
                    if (key is { } kind && (kinds.Add(kind) || kind == EffectKind.Nullification && view.Response!.ChainDepth > 0))
                    {
                        Refresh("处理区回归");
                        view = _interaction.View;
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        CheckProcessingLayout();
                        var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
                        var expected = BattleProcessingPresentation.Observe(view, _interaction.ReadLog());
                        if (!_processingCards.Values.SequenceEqual(expected)) throw new InvalidOperationException("处理区卡面与公开投影不一致。");
                        foreach (var (control, card) in _processingCards)
                        {
                            control.GrabFocus();
                            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                            if (!_cardDetail.Text.Contains(Card(card.Card), StringComparison.Ordinal)
                                || !control.GetParent().GetParent<ScrollContainer>().GetGlobalRect().Encloses(control.GetGlobalRect()))
                                throw new InvalidOperationException("处理区聚焦卡面或说明不可见。");
                            if (!_choiceOverlay.Visible) CheckCardInspection(control, card.Card);
                            else if (_inspectionPanel.Visible) throw new InvalidOperationException("卡面预览遮住暗牌选择弹层。");
                            inspected++;
                        }
                        if (hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot) || _interaction.Selection is not null)
                            throw new InvalidOperationException("查看处理区改变了战斗或选择。");
                        _interaction.Restore(BasicBattleSnapshotCodec.Decode(BasicBattleSnapshotCodec.Encode(_interaction.Snapshot), _interaction.Snapshot.ContentHash));
                        Refresh("处理区恢复回归");
                        if (!_processingCards.Values.SequenceEqual(expected)) throw new InvalidOperationException("恢复后处理区不一致。");
                    }
                    if (_interaction.WaitingForAi) _interaction.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action);
                    else
                    {
                        var action = BasicBattleAi.Choose(BasicBattle.Observe(_interaction.Snapshot.Battle, decision.ControllerId))!.Action;
                        if (!_interaction.Select(action) || !_interaction.Confirm().Accepted) throw new InvalidOperationException("处理区回归命令失败。");
                    }
                }
                Refresh("处理区终局回归");
                if (_processingCards.Count != 0) throw new InvalidOperationException("终局仍显示已结算牌。");
                kinds.Clear();
            }
            GD.Print($"BATTLE_PROCESSING_SMOKE_OK inspected={inspected}");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private void CheckProcessingLayout()
    {
        if (!_table.GetGlobalRect().Encloses(_processingPanel.GetGlobalRect())
            || _seatButtons.Values.Any(b => b.GetGlobalRect().Intersects(_processingPanel.GetGlobalRect())))
            throw new InvalidOperationException("处理区超出桌面或遮挡角色框。");
    }
}
