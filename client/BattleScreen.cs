using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen : Control
{
    public FirstCitySession? CampaignSession { get; set; }
    public Action? LeaveCampaignBattle { get; set; }
    public Action? SaveCampaign { get; set; }
    public Action? LoadCampaign { get; set; }
    private static readonly Color Paper = new("EDE3CF"), Gold = new("C3A66A"), Ink = new("252B29");
    private BattleInteraction _interaction = null!;
    private readonly BasicBattleFileSaveStore _store = new();
    private Label _status = null!, _prompt = null!, _feedback = null!, _preview = null!;
    private HBoxContainer _hand = null!;
    private HFlowContainer _options = null!;
    private DevelopmentBattleArtwork _art = null!;
    private Label _cardDetail = null!;
    private readonly Dictionary<Button, BattleCard> _handCards = new();
    private Control _table = null!;
    private Control _processingPanel = null!;
    private readonly Dictionary<Control, ProcessingCardView> _processingCards = new();
    private OptionButton _playerCount = null!, _opponentCount = null!;
    private AcceptDialog _logDialog = null!;
    private RichTextLabel _logText = null!;
    private long _logVersion = -1;
    private Button _confirm = null!, _cancel = null!;
    private Godot.Timer _aiTimer = null!;
    private string? _selectedCard;
    private string? _selectedTarget;
    private string _focusUnitId = "development.guanyu";
    private readonly Dictionary<string, Button> _seatButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<Button, BattleAction> _optionActions = new();
    private int _aiSteps;
    private string SavePath
    {
        get
        {
            if (_keyboardSavePath is { } smokePath) return smokePath;
            var units = _interaction.View.Combatants;
            var players = units.Count(u => u.TeamId == "a");
            var opponents = units.Count(u => u.TeamId == "b");
            return ProjectSettings.GlobalizePath(players == 2 && opponents == 2 ? "user://battle-development/session.json"
                : $"user://battle-development/{players}v{opponents}/session.json");
        }
    }

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        if (args.Any(a => a == "--smoke" || a.StartsWith("--acceptance-", StringComparison.Ordinal)))
        {
            var probe = GD.Load<PackedScene>("res://scenes/Probe.tscn").Instantiate();
            GetTree().Root.CallDeferred(Node.MethodName.AddChild, probe);
            QueueFree();
            return;
        }
        if (CampaignSession is null && (args.Contains("--campaign-smoke") || args.Contains("--campaign-preview")))
        {
            Callable.From(() => GetTree().ChangeSceneToFile("res://scenes/Campaign.tscn")).CallDeferred();
            return;
        }
        try
        {
            _interaction = new(CampaignSession?.ActiveBattle ?? DevelopmentBattleScenario.Create());
            _focusUnitId = _interaction.View.Combatants.First(u => u.TeamId == "a").EntityId;
            _art = new DevelopmentBattleArtwork();
            BuildInterface();
            LoadPresentationSettings(args.Any(a => a.StartsWith("--battle-", StringComparison.Ordinal)) || args.Contains("--campaign-smoke"));
            Refresh("选择手牌，再选择目标或技能方案，最后确认。取消选择不会消耗牌。");
            if (args.Contains("--battle-smoke")) { RunSmoke(args); return; }
            if (args.Contains("--battle-layout-smoke")) { _ = RunLayoutSmoke(); return; }
            if (args.Contains("--battle-artwork-smoke")) { _ = RunArtworkSmoke(); return; }
            if (args.Contains("--battle-processing-smoke")) { _ = RunProcessingSmoke(); return; }
            if (args.Contains("--battle-inspection-smoke")) { _ = RunInspectionSmoke(); return; }
            if (args.Contains("--battle-keyboard-smoke")) { _ = RunKeyboardSmoke(args); return; }
            if (args.Contains("--battle-presentation-smoke")) { _ = RunPresentationSmoke(); return; }
            if (args.Contains("--battle-preview")) { _ = CapturePreview(args); return; }
            _aiTimer.Start();
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
            if (args.Any(a => a.StartsWith("--battle-", StringComparison.Ordinal))) { GetTree().Quit(1); return; }
            AddChild(Text("战斗初始化失败：" + ex.Message));
        }
    }

    private void BuildInterface()
    {
        Theme = new Theme
        {
            DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC"] },
            DefaultFontSize = 18
        };
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 12);
        AddChild(margin);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);
        margin.AddChild(root);
        var header = Flow(root);
        header.AddChild(Text("三国行纪 · 战斗桌面", 26, Gold));
        if (CampaignSession is null)
        {
            header.AddChild(Button("保存战斗", () => { _store.Save(SavePath, _interaction.Snapshot); _feedback.Text = "已保存当前决策点。"; }));
            header.AddChild(Button("恢复战斗", Restore));
            header.AddChild(Button("新建战斗", NewBattle));
            header.AddChild(Button("首城行程", () => GetTree().ChangeSceneToFile("res://scenes/Campaign.tscn")));
        }
        else
        {
            header.AddChild(Button("保存行程", () => { SaveCampaign?.Invoke(); _feedback.Text = "已保存行程与当前牌局。"; }));
            header.AddChild(Button("恢复行程", () => LoadCampaign?.Invoke()));
            header.AddChild(Button("返回行程", () => { _aiTimer.Stop(); LeaveCampaignBattle?.Invoke(); }));
        }
        header.AddChild(Button("战斗记录", () => ShowBattleDialog(_logDialog)));
        _inspectButton = Button("手牌一览", ShowHandInspection); header.AddChild(_inspectButton);
        if (CampaignSession is null) header.AddChild(Button("G0抽牌试验", () => GetTree().ChangeSceneToFile("res://scenes/Probe.tscn")));
        var setup = new HBoxContainer(); setup.AddThemeConstantOverride("separation", 12); root.AddChild(setup);
        var playerLabel = Text("我方人数", 16); playerLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        playerLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        setup.AddChild(playerLabel);
        _playerCount = TeamSizeOption(); setup.AddChild(_playerCount);
        var opponentLabel = Text("敌方人数", 16); opponentLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        opponentLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        setup.AddChild(opponentLabel);
        _opponentCount = TeamSizeOption(); setup.AddChild(_opponentCount);
        if (CampaignSession is not null)
            foreach (var control in setup.GetChildren().OfType<Control>()) control.Hide();
        _status = Text("", 16); setup.AddChild(_status);
        var scroll = new ScrollContainer { FollowFocus = true, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(column);
        _table = new Control { CustomMinimumSize = new Vector2(0, 288), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddChild(_table);
        var promptRow = new HBoxContainer(); promptRow.AddThemeConstantOverride("separation", 10); column.AddChild(promptRow);
        _prompt = Text("", 22, Gold); promptRow.AddChild(_prompt);
        _skills = Flow(promptRow); _skills.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        var handRow = new HBoxContainer(); handRow.AddThemeConstantOverride("separation", 10); column.AddChild(handRow);
        BuildReservedZones(handRow);
        var handScroll = new ScrollContainer { FollowFocus = true, CustomMinimumSize = new Vector2(0, 122),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        handRow.AddChild(handScroll);
        _hand = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _hand.AddThemeConstantOverride("separation", 8); handScroll.AddChild(_hand);
        var optionScroll = new ScrollContainer { FollowFocus = true, CustomMinimumSize = new Vector2(0, 50), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(optionScroll);
        _options = Flow(optionScroll);
        _cardDetail = Text(CardDetailHint, 14);
        _cardDetail.CustomMinimumSize = new Vector2(0, 20);
        _cardDetail.MaxLinesVisible = 1;
        _cardDetail.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        root.AddChild(_cardDetail);
        _preview = Text("", 16);
        var controls = Flow(root);
        _confirm = Button("确认操作", Confirm); controls.AddChild(_confirm);
        _cancel = Button("取消选择", () =>
        {
            var card = _selectedCard;
            ClearSelection(); Refresh("已取消选择，战斗状态未改变。", new(FocusArea.Hand, card));
        });
        controls.AddChild(_cancel);
        _art.StyleDecisionButton(_confirm, true);
        _art.StyleDecisionButton(_cancel, false);
        controls.AddChild(_preview);
        _helpButton = Button("操作说明", () => ShowBattleDialog(_helpDialog)); controls.AddChild(_helpButton);
        _unitButton = Button("角色资料", ShowUnitDetails); controls.AddChild(_unitButton);
        _settingsButton = Button("界面设置", () => ShowBattleDialog(_settingsDialog)); controls.AddChild(_settingsButton);
        _feedback = Text("", 14); root.AddChild(_feedback);
        GetViewport().GuiEmbedSubwindows = true;
        _logDialog = new AcceptDialog { Title = "战斗记录 · 仅公开信息" };
        AddChild(_logDialog);
        _logText = new RichTextLabel { ScrollFollowing = true, SelectionEnabled = true };
        _logText.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _logText.OffsetLeft = 16; _logText.OffsetTop = 16; _logText.OffsetRight = -16; _logText.OffsetBottom = -58;
        _logDialog.AddChild(_logText);
        BuildKeyboardHelp();
        BuildCardInspection();
        BuildPresentationDialogs();
        _aiTimer = new Godot.Timer { WaitTime = 0.35, OneShot = false };
        AddChild(_aiTimer);
        _aiTimer.Timeout += AdvanceAi;
    }

    private void Refresh(string message, FocusRequest? focus = null)
    {
        ClearCardInspection();
        var view = _interaction.View;
        var previousOwner = GetViewport().GuiGetFocusOwner();
        var previousFocus = CaptureBattleFocus(previousOwner);
        var automaticWait = previousOwner == _helpButton && _automaticWaitingFocus;
        var sameRequest = _focusRequestId == view.PendingDecision?.RequestId && _focusStateVersion == view.StateVersion;
        _focusRequestId = view.PendingDecision?.RequestId; _focusStateVersion = view.StateVersion;
        Clear(_table); Clear(_hand); Clear(_options); Clear(_skills); _skillButtons.Clear(); _optionActions.Clear(); _seatButtons.Clear(); _handCards.Clear(); _processingCards.Clear(); _handInspectionDetails.Clear();
        _skills.Hide(); _skills.CustomMinimumSize = Vector2.Zero;
        _options.GetParent<ScrollContainer>().CustomMinimumSize = new Vector2(0, view.Response?.Kind == EffectKind.SelectHandCard ? 86 : 50);
        _cardDetail.Text = CardDetailHint;
        _status.Text = $"当前{view.Combatants.Count(u => u.TeamId == "a")}对{view.Combatants.Count(u => u.TeamId == "b")} · 第 {view.TurnNumber} 回合 · {UnitName(view, view.ActiveUnitId)}    牌堆 {view.DrawCount} · 弃牌 {view.DiscardPile.Length}    "
            + (view.WinnerTeamId is { } winner ? (winner == "a" ? "我方获胜" : "敌方获胜") : "战斗进行中");
        var actorId = view.PendingDecision?.ControllerId == "player" ? view.PendingDecision.ActorId : null;
        if (actorId is not null) _focusUnitId = actorId;
        var tableBackground = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        tableBackground.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        tableBackground.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("1B302C"), BorderColor = new Color("6C6048"),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 38, CornerRadiusTopRight = 38, CornerRadiusBottomLeft = 38, CornerRadiusBottomRight = 38 });
        _table.AddChild(tableBackground);
        BuildTargetLink(view);
        BuildProcessingArea(view);
        foreach (var seat in BattleSeatLayout.Arrange(view, _focusUnitId))
        {
            var unit = view.Combatants.Single(u => u.EntityId == seat.EntityId);
            var targetable = view.LegalActions.Any(a => MatchesSkill(a) && a.CardId == _selectedCard && a.TargetId == unit.EntityId
                && (a.CardId is not null || a.Kind == BattleActionKind.SelectHandCard));
            var marker = _selectedTarget == unit.EntityId ? " · 已选目标" : targetable ? " · 可选目标"
                : unit.EntityId == actorId ? " · 操作中" : unit.EntityId == view.ActiveUnitId ? " · 当前回合" : "";
            var skills = string.Join(" / ", unit.Skills.Select(s => s.Definition.Name));
            var button = Button("", () => SelectTarget(unit.EntityId));
            button.CustomMinimumSize = new Vector2(178, 90);
            button.AddThemeFontSizeOverride("font_size", 15);
            var style = new StyleBoxFlat
            {
                BgColor = new Color(unit.TeamId == "a" ? "29483F" : "583932"),
                BorderColor = _selectedTarget == unit.EntityId || unit.EntityId == actorId ? Gold : targetable ? new Color("C8D9BD") : new Color("66685B"),
                BorderWidthLeft = targetable ? 3 : 1, BorderWidthRight = targetable ? 3 : 1,
                BorderWidthTop = targetable ? 3 : 1, BorderWidthBottom = targetable ? 3 : 1
            };
            button.AddThemeStyleboxOverride("normal", style);
            button.AddThemeStyleboxOverride("disabled", style);
            button.AddThemeColorOverride("font_color", new Color("F3EDDF"));
            button.AddThemeColorOverride("font_disabled_color", new Color("DED8C9"));
            button.TooltipText = string.Join("\n", unit.Skills.Select(s => s.Definition.Name + "：" + s.Definition.Text
                + (s.Enabled ? "" : "（" + s.InactiveReason + "）")));
            button.Disabled = !targetable;
            var portrait = _art.Portrait(unit.Profile);
            var portraitPanel = new Control { Position = new Vector2(4, 4), Size = new Vector2(70, 82), MouseFilter = MouseFilterEnum.Ignore, ClipContents = true };
            button.AddChild(portraitPanel);
            if (portrait is not null)
            {
                var picture = Picture(portrait, TextureRect.StretchModeEnum.KeepAspectCovered);
                picture.Modulate = unit.Alive ? Colors.White : new Color(.35f, .35f, .35f);
                portraitPanel.AddChild(picture);
            }
            else
            {
                var placeholder = Text(unit.Profile == BattleProfile.ProtagonistFenjiI ? "乡勇" : "乡兵", 20, Gold);
                placeholder.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                placeholder.HorizontalAlignment = HorizontalAlignment.Center;
                placeholder.VerticalAlignment = VerticalAlignment.Center;
                placeholder.MouseFilter = MouseFilterEnum.Ignore;
                portraitPanel.AddChild(placeholder);
            }
            portraitPanel.AddChild(Picture(_art.SeatFrame));
            for (var hp = 0; hp < unit.MaxHp; hp++)
            {
                var blood = new TextureRect { Texture = hp >= unit.Hp ? _art.BloodEmpty : unit.Hp <= 1 ? _art.BloodRed
                        : unit.Hp <= 2 ? _art.BloodYellow : _art.BloodGreen,
                    Position = new Vector2(58, 4 + hp * 13), Size = new Vector2(10, 12),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore };
                portraitPanel.AddChild(blood);
            }
            var seatInfo = new VBoxContainer { Position = new Vector2(78, 4), Size = new Vector2(96, 82) };
            seatInfo.AddThemeConstantOverride("separation", 2);
            button.AddChild(seatInfo);
            var info = Text(UnitName(view, unit.EntityId) + "\n"
                + (unit.Alive ? $"体力{unit.Hp}/{unit.MaxHp} 手牌{unit.HandCount}" : "已阵亡")
                + $"\n{(unit.TeamId == "a" ? "我" : "敌")}{unit.SeatId + 1} · "
                + (marker.Length > 0 ? marker.Trim(' ', '·').Replace("当前回合", "回合中", StringComparison.Ordinal) : skills), 13);
            info.AutowrapMode = TextServer.AutowrapMode.Off;
            info.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            info.MouseFilter = MouseFilterEnum.Ignore;
            info.SizeFlagsVertical = SizeFlags.ExpandFill;
            seatInfo.AddChild(info);
            var zones = Text("装备 — · 判定 —", 12, Paper);
            zones.AutowrapMode = TextServer.AutowrapMode.Off;
            zones.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            zones.MouseFilter = MouseFilterEnum.Ignore;
            seatInfo.AddChild(zones);
            button.TooltipText += "\n装备区与判定区尚未接入；当前拆顺只选择手牌。";
            button.AnchorLeft = button.AnchorRight = (float)seat.X;
            button.AnchorTop = button.AnchorBottom = (float)seat.Y;
            button.OffsetLeft = -89; button.OffsetRight = 89; button.OffsetTop = -45; button.OffsetBottom = 45;
            _table.AddChild(button);
            _seatButtons.Add(unit.EntityId, button);
        }
        _prompt.Text = CampaignSession is not null && view.WinnerTeamId is not null
            ? "本场战斗已结束，请点“返回行程”记录战果。" : Prompt(view, _selectedCard is not null);
        UpdateLog(view);
        if (actorId is not null)
        {
            var actor = view.Combatants.Single(u => u.EntityId == actorId);
            foreach (var card in actor.VisibleHand)
            {
                var legal = view.LegalActions.Any(a => a.CardId == card.InstanceId && MatchesSkill(a));
                var button = CreateCardButton(card, legal, () => SelectCard(card.InstanceId));
                _hand.AddChild(button);
                _handCards.Add(button, card);
            }
            BuildSkillButtons(actor.Skills, view);
            foreach (var action in view.LegalActions.Where(a => MatchesSkill(a) && (a.CardId is null || a.CardId == _selectedCard)
                && (_selectedTarget is null || a.TargetId == _selectedTarget || a.TargetId is null)))
            {
                var button = Button(Describe(view, action), () =>
                {
                    if (!_interaction.Select(action)) { Refresh("请求已更新，请重新选择。"); return; }
                    _preview.Text = "待确认：" + Describe(view, action);
                    _confirm.Disabled = false; _cancel.Disabled = false;
                    ScheduleBattleFocus(new(FocusArea.Confirm));
                });
                _optionActions.Add(button, action);
                _options.AddChild(button);
                if (action.Kind == BattleActionKind.SelectHandCard) StyleHiddenHand(button, action.HandIndex!.Value);
            }
        }
        else
        {
            _hand.AddChild(Text(view.WinnerTeamId is null ? "等待敌方操作；敌方手牌只显示数量。" : "战斗结束，可新建或恢复战斗。"));
        }
        _preview.Text = _interaction.Selection is { } selection ? "待确认：" + Describe(view, selection) : "尚未选择操作。";
        _confirm.Disabled = _interaction.Selection is null;
        _cancel.Disabled = _interaction.Selection is null && _selectedCard is null && _selectedTarget is null;
        _feedback.Text = message;
        UpdateHandInspection();
        UpdateUnitDetails();
        UpdatePresentationFeedback(view);
        ScheduleBattleFocus(focus ?? (sameRequest ? previousFocus : null) ?? new(FocusArea.Default),
            focus is null && previousFocus is null && !automaticWait ? previousOwner : null);
    }

    private void Confirm()
    {
        var view = _interaction.View;
        var selection = _interaction.Selection;
        var result = _interaction.Confirm();
        ClearSelection();
        Refresh(result.Accepted && selection is not null
            ? UnitName(view, view.PendingDecision!.ActorId) + "：" + Describe(view, selection) + "。" + Outcome(view, _interaction.View) : result.Message,
            new(FocusArea.Default));
    }

    private void AdvanceAi()
    {
        if (!_interaction.WaitingForAi) return;
        try
        {
            if (++_aiSteps > 4000) throw new InvalidOperationException("AI步数保护触发，请保存战斗以便排查。");
            var before = _interaction.View;
            var result = _interaction.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action)!;
            if (!result.Accepted) throw new InvalidOperationException(result.Message);
            ClearSelection();
            Refresh(UnitName(before, before.PendingDecision!.ActorId) + "已操作。" + Outcome(before, _interaction.View));
        }
        catch (Exception ex)
        {
            _aiTimer.Stop();
            _feedback.Text = "AI暂停：" + ex.Message;
            GD.PushError(ex.ToString());
        }
    }

    private void Restore()
    {
        var loaded = _store.Load(SavePath, _interaction.Snapshot.ContentHash);
        _interaction.Restore(loaded.State);
        ClearSelection(); _aiSteps = 0; _logVersion = -1;
        Refresh(loaded.Message, new(FocusArea.Default));
        _aiTimer.Start();
    }

    private static OptionButton TeamSizeOption()
    {
        var option = new OptionButton { CustomMinimumSize = new Vector2(70, 32) };
        for (var i = 1; i <= 4; i++) option.AddItem(i.ToString(), i);
        option.Selected = 1;
        return option;
    }

    private void NewBattle()
    {
        _interaction = new(DevelopmentBattleScenario.Create(42, _playerCount.Selected + 1, _opponentCount.Selected + 1));
        ClearSelection(); _aiSteps = 0; _logVersion = -1; _focusUnitId = "development.guanyu";
        Refresh($"已新建{_playerCount.Selected + 1}对{_opponentCount.Selected + 1}战斗，种子42。已切换至对应人数的独立存档槽，已有存档保留。", new(FocusArea.Default));
        _aiTimer.Start();
    }

    private void ClearSelection()
    {
        _interaction.Cancel(); _selectedCard = null; _selectedTarget = null; _selectedSkill = null;
    }

    private void SelectTarget(string targetId)
    {
        var candidates = _interaction.View.LegalActions.Where(a => a.CardId == _selectedCard && a.TargetId == targetId
            && MatchesSkill(a) && (a.CardId is not null || a.Kind == BattleActionKind.SelectHandCard)).ToArray();
        if (candidates.Length == 0) { Refresh("目标已失效，请重新选牌。"); return; }
        _interaction.Cancel(); _selectedTarget = targetId;
        Refresh("已选择目标；请核对操作或技能成本后确认。", new(FocusArea.Option));
        if (candidates.Length == 1)
            _optionActions.Single(p => p.Value == candidates[0]).Key.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void UpdateLog(BattleView view)
    {
        if (_logVersion == view.StateVersion) return;
        _logText.Text = string.Join("\n", _interaction.ReadLog().Select(e => $"{e.Sequence:D3}  {DescribeEvent(view, e)}"));
        _logVersion = view.StateVersion;
    }

    private static string DescribeEvent(BattleView view, PublicBattleEvent e)
    {
        var actor = UnitName(view, e.ActorId);
        var target = e.TargetId is null ? "" : UnitName(view, e.TargetId);
        var card = e.Card is null ? "" : Card(e.Card);
        return e.Code switch
        {
            "card.drawn" => $"{actor}摸1张牌（暗牌）",
            "card.snatched" => $"{actor}获得{target}的1张手牌（暗牌）",
            "turn.started" => $"{actor}的回合开始", "turn.ended" => $"{actor}的回合结束",
            "play.ended" => $"{actor}结束出牌", "response.passed" => $"{actor}放弃本次响应",
            "slash.used" => $"{actor}对{target}使用杀：{card}",
            "slash.dodged" => $"{actor}使用闪，避开本次杀：{card}",
            "duel.used" => $"{actor}对{target}使用决斗：{card}",
            "duel.slash_responded" => $"{actor}在决斗中向{target}打出杀：{card}",
            "exnihilo.used" => $"{actor}使用无中生有：{card}",
            "dismantlement.used" => $"{actor}对{target}使用过河拆桥：{card}",
            "snatch.used" => $"{actor}对{target}使用顺手牵羊：{card}",
            "nullification.used" => $"{actor}使用{card}，抵消{(e.RelatedCard is null ? "当前锦囊" : Card(e.RelatedCard))}",
            "nullification.window_closed" => $"无懈窗口结束，共{e.Amount}层反制",
            "trick.negated" => $"对{target}的{card}被抵消",
            "trick.no_target_cards" => $"{target}已无手牌，本次拆顺结束",
            "card.dismantled" => $"{actor}弃置{target}的{card}",
            "card.discarded" => $"{actor}将{card}置入弃牌堆",
            "damage.dealt" => $"{actor}对{target}造成{e.Amount}点伤害：{card}",
            "hp.recovered" => $"{actor}使用{card}，{target}回复{e.Amount}点体力",
            "dying.started" => $"{actor}进入濒死，等待救援", "dying.saved" => $"{actor}脱离濒死",
            "unit.died" => $"{actor}阵亡，{e.Amount}张手牌置入弃牌堆",
            "skill.used" => $"{actor}发动{SkillName(e.RelatedCardId)}：{card}",
            "skill.declined" => $"{actor}放弃发动{SkillName(e.RelatedCardId)}",
            "skill.jianxiong_gained" => $"{actor}发动奸雄，获得伤害牌{card}",
            "card.finished_after_gain" => $"{card}已被获得，本次使用结算结束",
            "deck.reshuffled" => $"弃牌堆{e.Amount}张牌重新洗入牌堆",
            "draw.exhausted" => $"{actor}摸牌不足，仍缺{e.Amount}张",
            "battle.finished" => e.TargetId == "a" ? "战斗结束，我方获胜" : "战斗结束，敌方获胜",
            _ => "公开结算完成"
        };
    }

    private static string SkillName(string? id) => id switch
    { BattleSkills.Wusheng => "武圣", BattleSkills.FenjiI => "奋击I阶", BattleSkills.Jianxiong => "奸雄", _ => "技能" };

    private static string Outcome(BattleView before, BattleView after)
        => string.Join("；", after.Combatants.Select(u =>
        {
            var old = before.Combatants.Single(p => p.EntityId == u.EntityId);
            return old.Alive && !u.Alive ? UnitName(after, u.EntityId) + "阵亡"
                : old.Hp != u.Hp ? $"{UnitName(after, u.EntityId)}体力 {old.Hp}→{u.Hp}" : "";
        }).Where(s => s.Length > 0));

    private static string Prompt(BattleView view, bool cardSelected = false)
    {
        if (view.WinnerTeamId is not null) return "本场战斗已结束。";
        var who = UnitName(view, view.PendingDecision!.ActorId);
        var prefix = view.PendingDecision.ControllerId == "player" ? "正在操作 " : "敌方正在操作 ";
        var reason = view.Response switch
        {
            { Kind: EffectKind.Dodge } r => $"响应{ UnitName(view, r.SourceId) }的杀：使用闪或放弃（伤害{1 + r.DamageBonus}）",
            { Kind: EffectKind.Duel } r => $"决斗响应：向{ UnitName(view, r.SourceId) }打出杀或放弃",
            { Kind: EffectKind.Rescue } r => $"救援{UnitName(view, r.TargetId)}：体力{view.Combatants.Single(u => u.EntityId == r.TargetId).Hp}，"
                + $"还需回复{Math.Max(1, 1 - view.Combatants.Single(u => u.EntityId == r.TargetId).Hp)}点，使用桃或放弃",
            { Kind: EffectKind.Nullification } r => $"无懈窗口 · 第{r.ChainDepth}层 · 当前抵消对象 {Card(view.Processing.Single(c => c.InstanceId == r.CardId))}"
                + (r.Trick is { } t ? $"\n原锦囊{CardName(t.CardKind)}：{UnitName(view, t.SourceId)} → {UnitName(view, t.TargetId)}" : ""),
            { Kind: EffectKind.SelectHandCard } r => $"选择{UnitName(view, r.TargetId)}的一张暗手牌（只显示槽位）",
            { Kind: EffectKind.InvokeJianxiong } => "伤后询问：发动奸雄获得伤害牌，或放弃",
            _ => view.Phase == BattlePhase.Discard
                ? $"弃牌阶段：还需弃{Math.Max(0, view.Combatants.Single(u => u.EntityId == view.PendingDecision.ActorId).HandCount - Math.Max(0, view.Combatants.Single(u => u.EntityId == view.PendingDecision.ActorId).Hp))}张 · 本次已选{(cardSelected ? 1 : 0)}/1张"
                : "出牌阶段：选择牌与目标，或结束出牌"
        };
        return prefix + who + " · " + reason;
    }

    private static string Describe(BattleView view, BattleAction action)
    {
        var label = action.Kind switch
        {
            BattleActionKind.Slash => "使用杀", BattleActionKind.Dodge => "打出闪", BattleActionKind.Peach => "使用桃",
            BattleActionKind.Duel => "使用决斗", BattleActionKind.ExNihilo => "使用无中生有",
            BattleActionKind.Nullify => "使用无懈可击", BattleActionKind.RespondSlash => "打出杀",
            BattleActionKind.Dismantlement => "使用过河拆桥", BattleActionKind.Snatch => "使用顺手牵羊",
            BattleActionKind.Discard => "弃置此牌", BattleActionKind.EndPlay => "结束出牌阶段",
            BattleActionKind.Pass => "放弃本次响应", BattleActionKind.InvokeSkill => "发动奸雄",
            BattleActionKind.SelectHandCard => $"暗手牌 #{action.HandIndex + 1}", _ => action.Kind.ToString()
        };
        if (action.SkillId is { } skill) label += skill == BattleSkills.Wusheng ? "（武圣）" : skill == BattleSkills.FenjiI ? "（奋击I阶）" : "";
        if (action.CostCardId is { } cost)
            label += " 弃" + Card(view.Combatants.SelectMany(u => u.VisibleHand).Single(c => c.InstanceId == cost));
        if (action.TargetId is { } target) label += " → " + UnitName(view, target);
        return label;
    }

    private static string UnitName(BattleView view, string id) => id switch
    {
        "protagonist" => "主角", "companion.volunteer" => "同行乡勇", "guard.1" => "守军一", "guard.2" => "守军二",
        _ => BattleUnitName(view, id)
    };
    private static string BattleUnitName(BattleView view, string id) => view.Combatants.FirstOrDefault(u => u.EntityId == id)?.Profile switch
    {
        BattleProfile.GuanyuStandard => "关羽", BattleProfile.CaocaoStandard => "曹操",
        BattleProfile.ProtagonistFenjiI => "乡勇", _ => view.Combatants.FirstOrDefault(u => u.EntityId == id) is { } unit
            ? (unit.TeamId == "a" ? "我方乡兵" : "敌方乡兵") + (unit.SeatId + 1) : id
    };
    private static string CardName(BasicCardKind kind) => kind switch
    {
        BasicCardKind.Slash => "杀", BasicCardKind.Dodge => "闪", BasicCardKind.Peach => "桃", BasicCardKind.Duel => "决斗",
        BasicCardKind.ExNihilo => "无中生有", BasicCardKind.Nullification => "无懈可击",
        BasicCardKind.Dismantlement => "过河拆桥", BasicCardKind.Snatch => "顺手牵羊", _ => "未知牌"
    };
    private static string Card(BattleCard card) => (card.Suit switch
    { "spade" => "♠", "heart" => "♥", "club" => "♣", "diamond" => "♦", _ => "" }) + card.Rank + " " + CardName(card.Kind);

    private static TextureRect Picture(Texture2D texture, TextureRect.StretchModeEnum stretch = TextureRect.StretchModeEnum.Scale)
    {
        var picture = new TextureRect { Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = stretch, MouseFilter = MouseFilterEnum.Ignore };
        picture.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return picture;
    }

    private Button CreateCardButton(BattleCard card, bool legal, Action select)
    {
        var selected = card.InstanceId == _selectedCard;
        var button = Button("", select);
        button.CustomMinimumSize = new Vector2(82, 112);
        button.Disabled = !legal;
        var border = new StyleBoxFlat { BgColor = new Color("3C3B32"), BorderColor = selected ? Gold : new Color("777264"),
            BorderWidthLeft = selected ? 3 : 1, BorderWidthRight = selected ? 3 : 1,
            BorderWidthTop = selected ? 3 : 1, BorderWidthBottom = selected ? 3 : 1 };
        button.AddThemeStyleboxOverride("normal", border);
        button.AddThemeStyleboxOverride("disabled", border);
        var content = new Control { MouseFilter = MouseFilterEnum.Ignore, Modulate = legal ? Colors.White : new Color(.48f, .48f, .48f) };
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        content.OffsetLeft = 5; content.OffsetRight = -5; content.OffsetTop = 6; content.OffsetBottom = -6;
        button.AddChild(content);
        content.AddChild(Picture(_art.CardFace(card.Kind)));
        var captionBackground = new ColorRect { Color = Paper, MouseFilter = MouseFilterEnum.Ignore };
        captionBackground.AnchorTop = 1; captionBackground.AnchorBottom = 1; captionBackground.AnchorRight = 1;
        captionBackground.OffsetTop = -23;
        content.AddChild(captionBackground);
        var caption = Text(CardName(card.Kind) + (selected ? " ✓" : ""), 12, Ink);
        caption.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.MouseFilter = MouseFilterEnum.Ignore; captionBackground.AddChild(caption);
        var rank = Text((card.Suit switch { "spade" => "♠", "heart" => "♥", "club" => "♣", "diamond" => "♦", _ => "?" })
            + "\n" + (card.Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => card.Rank.ToString() }), 16,
            card.Suit is "heart" or "diamond" ? new Color("A51F20") : Ink);
        rank.Position = new Vector2(3, 2); rank.Size = new Vector2(24, 44);
        rank.HorizontalAlignment = HorizontalAlignment.Center; rank.MouseFilter = MouseFilterEnum.Ignore;
        var rankBackground = new ColorRect { Position = rank.Position, Size = rank.Size, Color = Paper, MouseFilter = MouseFilterEnum.Ignore };
        content.AddChild(rankBackground); content.AddChild(rank);
        var details = Card(card) + " · " + (card.Kind switch
        {
            BasicCardKind.Slash => "对合法目标使用，造成伤害；也可响应决斗。",
            BasicCardKind.Dodge => "响应杀，抵消本次杀。",
            BasicCardKind.Peach => "受伤时自疗，或在濒死请求中救援，回复1点体力。",
            BasicCardKind.Duel => "与目标轮流打出杀，先放弃者受到伤害。",
            BasicCardKind.ExNihilo => "对自己使用，摸2张牌。",
            BasicCardKind.Nullification => "抵消当前锦囊效果或当前无懈；可连续反制。",
            BasicCardKind.Dismantlement => "弃置目标1张暗手牌（开发范围仅手牌区）。",
            BasicCardKind.Snatch => "获得距离内目标1张暗手牌（开发范围仅手牌区）。",
            _ => ""
        }) + (legal ? " 可选择；具体目标、转换技能与成本以合法方案为准。"
            : _selectedSkill is not null ? " 当前技能筛选下不可用；取消技能筛选可查看其他合法方案。" : " 当前无合法动作。");
        _handInspectionDetails[button] = details;
        BindCardInspection(button, card, details);
        var hovering = false;
        void UpdateHighlight()
        {
            var highlight = !_reduceMotion && (hovering || button.HasFocus());
            content.OffsetTop = highlight ? 2 : 6; content.OffsetBottom = highlight ? -10 : -6;
        }
        button.MouseEntered += () => { hovering = true; UpdateHighlight(); };
        button.MouseExited += () => { hovering = false; UpdateHighlight(); };
        button.FocusEntered += UpdateHighlight;
        button.FocusExited += UpdateHighlight;
        return button;
    }

    private static Label Text(string value, int size = 18, Color? color = null)
    {
        var label = new Label { Text = value, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? new Color("F3EDDF"));
        return label;
    }
    private static HFlowContainer Flow(Node parent)
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 10); flow.AddThemeConstantOverride("v_separation", 10);
        parent.AddChild(flow); return flow;
    }
    private Button Button(string label, Action action)
    {
        var button = new Button { Text = label, CustomMinimumSize = new Vector2(130, 42), FocusMode = FocusModeEnum.All };
        button.AddThemeColorOverride("font_color", Ink);
        button.AddThemeColorOverride("font_hover_color", Ink);
        button.AddThemeColorOverride("font_pressed_color", Ink);
        button.AddThemeColorOverride("font_focus_color", Ink);
        button.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Paper, ContentMarginLeft = 12, ContentMarginRight = 12 });
        button.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = Gold });
        button.AddThemeStyleboxOverride("focus", new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = Gold,
            BorderWidthLeft = 3, BorderWidthRight = 3, BorderWidthTop = 3, BorderWidthBottom = 3 });
        button.FocusEntered += () => _ = RevealBattleFocus(button);
        button.Pressed += () =>
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SaveException or ArgumentException)
            { _feedback.Text = ex.Message; }
        };
        return button;
    }
    private static void Clear(Node parent)
    {
        foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); }
    }

    private void RunSmoke(string[] args)
    {
        _aiTimer.Stop();
        if (args.Length != 1 && (args.Length != 3 || args[1] is not ("write" or "resume") || !Path.IsPathFullyQualified(args[2])))
            throw new ArgumentException("烟测用法：--battle-smoke [write|resume <专用目录绝对路径>]");
        var path = args.Length == 3 ? Path.Combine(Path.GetFullPath(args[2]), "client-battle.json")
            : ProjectSettings.GlobalizePath("user://battle-development/smoke.json");
        var resume = args.Contains("resume");
        if (resume)
        {
            _interaction.Restore(_store.Load(path, DevelopmentBattleScenario.ContentHash).State);
            Refresh("已恢复烟测决策点。");
        }
        var steps = 0;
        while (_interaction.View.PendingDecision is not null)
        {
            if (++steps > 4000) throw new InvalidOperationException("客户端烟测步数保护触发。");
            if (!resume && steps == 12)
            {
                _store.Save(path, _interaction.Snapshot);
                var restored = _store.Load(path, DevelopmentBattleScenario.ContentHash).State;
                if (BasicBattleSnapshotCodec.StateHash(restored) != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                    throw new InvalidOperationException("客户端存档恢复不一致。");
                _interaction.Restore(restored);
            }
            if (_interaction.WaitingForAi)
            {
                if (!_interaction.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action)!.Accepted)
                    throw new InvalidOperationException("客户端AI提交失败。");
            }
            else
            {
                var action = BasicBattleAi.Choose(_interaction.View)!.Action;
                // 使用真实界面按钮走选牌、选择方案与确认；也检查取消不改变状态。
                if (action.CardId is { } cardId)
                {
                    var cardIndex = _interaction.View.Combatants.Single(u => u.EntityId == _interaction.View.PendingDecision!.ActorId)
                        .VisibleHand.Select((c, i) => (c, i)).Single(p => p.c.InstanceId == cardId).i;
                    _hand.GetChild<Button>(cardIndex).EmitSignal(BaseButton.SignalName.Pressed);
                }
                if (action.TargetId is { } targetId && _seatButtons.TryGetValue(targetId, out var targetButton) && !targetButton.Disabled)
                    targetButton.EmitSignal(BaseButton.SignalName.Pressed);
                var option = _optionActions.Single(p => p.Value == action).Key;
                option.EmitSignal(BaseButton.SignalName.Pressed);
                if (_interaction.Selection != action) throw new InvalidOperationException("界面选择与合法动作不一致。");
                var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
                _cancel.EmitSignal(BaseButton.SignalName.Pressed);
                if (_interaction.Selection is not null || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                    throw new InvalidOperationException("取消选择改变了战斗。");
                // 取消会清理卡牌过滤，再按原牌恢复方案。
                _selectedCard = action.CardId; Refresh("烟测选择");
                _optionActions.Single(p => p.Value == action).Key.EmitSignal(BaseButton.SignalName.Pressed);
                var version = _interaction.View.StateVersion;
                _confirm.EmitSignal(BaseButton.SignalName.Pressed);
                _confirm.EmitSignal(BaseButton.SignalName.Pressed);
                if (_interaction.View.StateVersion != version + 1) throw new InvalidOperationException("界面重复确认未去重。");
            }
            Refresh("烟测运行");
        }
        var finalHash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
        if (args.Length == 3)
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, resume ? "resumed-final.json" : "final.json"), BasicBattleSnapshotCodec.Encode(_interaction.Snapshot));
        GD.Print($"BATTLE_CLIENT_{(resume ? "RESUME" : "SMOKE")}_OK {finalHash} steps={steps}");
        GetTree().Quit();
    }

    private async Task RunLayoutSmoke()
    {
        _aiTimer.Stop();
        try
        {
            var layouts = 0;
            foreach (var width in new[] { 960, 1280 })
            {
                var saveSlots = new HashSet<string>(StringComparer.Ordinal);
                GetWindow().Size = new Vector2I(width, width == 960 ? 600 : 720);
                for (var playerCount = 1; playerCount <= 4; playerCount++)
                for (var opponentCount = 1; opponentCount <= 4; opponentCount++)
                {
                    _interaction = new(DevelopmentBattleScenario.Create(42, playerCount, opponentCount));
                    _focusUnitId = "development.guanyu"; _logVersion = -1; ClearSelection();
                    Refresh("桌面布局回归");
                    if (!saveSlots.Add(SavePath)) throw new InvalidOperationException("不同人数共用了存档槽。");
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    if (_seatButtons.Count != playerCount + opponentCount) throw new InvalidOperationException("角色框数量不匹配。");
                    var bounds = new Rect2(Vector2.Zero, _table.Size);
                    CheckProcessingLayout();
                    var buttons = _seatButtons.Values.ToArray();
                    for (var i = 0; i < buttons.Length; i++)
                    {
                        var rect = buttons[i].GetRect();
                        if (!bounds.Encloses(rect)) throw new InvalidOperationException("角色框超出桌面。");
                        for (var j = 0; j < i; j++)
                            if (rect.Intersects(buttons[j].GetRect())) throw new InvalidOperationException("角色框重叠。");
                    }
                    var action = _interaction.View.LegalActions.FirstOrDefault(a => a.CardId is not null && a.TargetId is not null);
                    if (action is not null)
                    {
                        _selectedCard = action.CardId; Refresh("目标回归");
                        var before = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
                        var target = _seatButtons[action.TargetId!];
                        if (target.Disabled) throw new InvalidOperationException("合法目标未启用。");
                        target.EmitSignal(BaseButton.SignalName.Pressed);
                        if (_selectedTarget != action.TargetId) throw new InvalidOperationException("目标选择丢失。");
                        _cancel.EmitSignal(BaseButton.SignalName.Pressed);
                        if (before != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot)) throw new InvalidOperationException("目标取消改变了状态。");
                    }
                    layouts++;
                }
            }
            var logHash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            _logDialog.PopupCentered(new Vector2I(850, 500));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (string.IsNullOrWhiteSpace(_logText.Text) || logHash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                throw new InvalidOperationException("查看战斗记录改变了状态或记录为空。");
            _logDialog.Hide();
            GD.Print($"BATTLE_LAYOUT_SMOKE_OK layouts={layouts}");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private async Task CapturePreview(string[] args)
    {
        try
        {
            if (args.Length is not (2 or 4 or 5) || !Path.IsPathFullyQualified(args[1]))
                throw new ArgumentException("预览用法：--battle-preview <PNG绝对路径> [我方人数 敌方人数 [log|selected|processing|conversion|keyboard|hand]]");
            if (args.Length >= 4)
            {
                var players = int.Parse(args[2]); var opponents = int.Parse(args[3]);
                _ = DevelopmentBattleScenario.HashFor(players, opponents);
                _playerCount.Selected = players - 1; _opponentCount.Selected = opponents - 1;
                NewBattle();
            }
            _aiTimer.Stop();
            if (args.Length == 5)
            {
                if (args[4] == "log") _logDialog.PopupCentered(new Vector2I(850, 500));
                else if (args[4] == "keyboard") ShowBattleDialog(_helpDialog);
                else if (args[4] == "hand") ShowHandInspection();
                else if (args[4] == "settings") ShowBattleDialog(_settingsDialog);
                else if (args[4] == "units") ShowUnitDetails();
                else if (args[4] == "selected")
                {
                    _handCards.First(p => !p.Key.Disabled && _interaction.View.LegalActions.Any(a => a.CardId == p.Value.InstanceId && a.TargetId is not null))
                        .Key.EmitSignal(BaseButton.SignalName.Pressed);
                    _handCards.First(p => p.Value.InstanceId == _selectedCard).Key.EmitSignal(Control.SignalName.MouseEntered);
                }
                else if (args[4] == "conversion")
                {
                    var action = _interaction.View.LegalActions.First(a => a.SkillId == BattleSkills.Wusheng);
                    _interaction.Select(action); _interaction.Confirm(); Refresh("武圣转换牌处理区预览");
                    _processingCards.Keys.First().EmitSignal(Control.SignalName.MouseEntered);
                }
                else if (args[4] == "processing")
                {
                    var steps = 0;
                    while (_interaction.View.Response is not { Kind: EffectKind.Nullification, ChainDepth: > 0 })
                    {
                        if (++steps > 4000 || _interaction.View.PendingDecision is null) throw new InvalidOperationException("未找到反无懈预览点。");
                        if (_interaction.WaitingForAi) _interaction.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action);
                        else { _interaction.Select(BasicBattleAi.Choose(_interaction.View)!.Action); _interaction.Confirm(); }
                    }
                    Refresh("反无懈处理区预览");
                }
                else throw new ArgumentException("未知预览模式。");
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var error = GetViewport().GetTexture().GetImage().SavePng(args[1]);
            GD.Print("BATTLE_PREVIEW " + error);
            GetTree().Quit(error == Error.Ok ? 0 : 1);
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }

    private async Task RunArtworkSmoke()
    {
        _aiTimer.Stop();
        try
        {
            foreach (var kind in Enum.GetValues<BasicCardKind>())
                if (_art.CardFace(kind).GetSize() != new Vector2(186, 260)) throw new InvalidOperationException("卡面尺寸错误：" + kind);
            if (_art.Guanyu.GetSize() != new Vector2(750, 950) || _art.Caocao.GetSize() != new Vector2(750, 950)
                || _art.SeatFrame.GetSize() != new Vector2(304, 356) || _art.Portrait(BattleProfile.Generic) is not null
                || _art.Portrait(BattleProfile.ProtagonistFenjiI) is not null)
                throw new InvalidOperationException("人物图像映射错误。");
            var inspected = 0;
            foreach (var width in new[] { 960, 1280 })
            {
                GetWindow().Size = new Vector2I(width, width == 960 ? 600 : 720);
                Refresh("开发素材交互回归");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var viewport = new Rect2(Vector2.Zero, GetViewportRect().Size);
                if (!viewport.Encloses(_confirm.GetGlobalRect()) || !viewport.Encloses(_cancel.GetGlobalRect()))
                    throw new InvalidOperationException("确认或取消按钮超出窗口。");
                var before = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
                var actor = _interaction.View.Combatants.Single(u => u.EntityId == _interaction.View.PendingDecision!.ActorId);
                if (!_handCards.Values.SequenceEqual(actor.VisibleHand)) throw new InvalidOperationException("手牌图像未绑定当前操作角色。");
                foreach (var (button, card) in _handCards)
                {
                    var body = _table.GetParent().GetParent<ScrollContainer>();
                    if (!body.GetGlobalRect().Encloses(button.GetGlobalRect()))
                        throw new InvalidOperationException("初始手牌卡面被桌面滚动区裁切。");
                    button.EmitSignal(Control.SignalName.MouseEntered);
                    if (!_cardDetail.Text.StartsWith(Card(card), StringComparison.Ordinal)) throw new InvalidOperationException("悬停说明与牌面不一致。");
                    button.EmitSignal(Control.SignalName.MouseExited);
                    if (!button.Disabled)
                    {
                        button.GrabFocus();
                        if (!_cardDetail.Text.StartsWith(Card(card), StringComparison.Ordinal)) throw new InvalidOperationException("键盘聚焦未显示牌信息。");
                        button.ReleaseFocus();
                    }
                    inspected++;
                }
                if (_interaction.Selection is not null || before != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                    throw new InvalidOperationException("查看卡面改变了选择或战斗状态。");
            }
            // Synthetic display-only copies exercise keyboard access beyond the horizontal viewport.
            var hash = BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot);
            var sample = _handCards.Values.First();
            for (var i = 0; i < 24; i++) _hand.AddChild(CreateCardButton(sample, true, () => { }));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _hand.GetChild<Button>(-1).GrabFocus();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var handScroll = _hand.GetParent<ScrollContainer>();
            if (handScroll.ScrollHorizontal <= 0 || !handScroll.GetGlobalRect().Encloses(_hand.GetChild<Button>(-1).GetGlobalRect())
                || hash != BasicBattleSnapshotCodec.StateHash(_interaction.Snapshot))
                throw new InvalidOperationException("长手牌键盘聚焦没有滚动到可见区域，或改变了战斗状态。");
            GD.Print($"BATTLE_ARTWORK_SMOKE_OK cards=8 portraits=2 inspected={inspected}");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
}
