using Godot;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private Panel _sidebar = null!;
    private Control _sidebarContent = null!, _decisionControls = null!, _progress = null!, _progressFill = null!;
    private Label _round = null!, _phaseLabel = null!, _handCount = null!;
    private Button _autoSelect = null!, _sortHand = null!, _sidebarToggle = null!;
    private readonly List<Button> _sidebarTabs = [];
    private readonly HashSet<string> _selectedDiscards = new(StringComparer.Ordinal);
    private int _sidebarTab = 1;
    private bool _sidebarCollapsed, _sortByKind;
    private long _discardVersion = -1;
    private BattleTargetLine _targetLine = null!;
    private string? _hoverTarget;
    private const float TableRight = .826f;

    private bool ChoosingDiscards => _interaction.View is { Phase: BattlePhase.Discard, Response: null,
        PendingDecision.ControllerId: "player" };
    private int RequiredDiscards
    {
        get
        {
            if (!ChoosingDiscards) return 0;
            var actor = _interaction.View.Combatants.Single(u => u.EntityId == _interaction.View.PendingDecision!.ActorId);
            return Math.Max(0, actor.HandCount - actor.Hp);
        }
    }

    private static void DarkButton(Button button, bool accent = false)
    {
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled" })
        {
            var style = new StyleBoxFlat
            {
                BgColor = new Color(state == "hover" ? "64513A" : state == "pressed" ? "211B15" : accent ? "644029" : "302A23"),
                BorderColor = new Color(state == "hover" ? "D5BC80" : "82745B"),
                BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
                ContentMarginLeft = 6, ContentMarginRight = 6
            };
            button.AddThemeStyleboxOverride(state, style);
        }
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color" })
            button.AddThemeColorOverride(state, Paper);
        button.AddThemeColorOverride("font_disabled_color", new Color("837A68"));
        button.AddThemeFontSizeOverride("font_size", 14);
        button.CustomMinimumSize = Vector2.Zero;
    }

    private Label DesktopLabel(string value, int fontSize = 14, Color? color = null)
    {
        var label = Text(value, fontSize, color);
        label.MouseFilter = MouseFilterEnum.Ignore;
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, .9f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        return label;
    }

    private void BuildScreenshotInterface()
    {
        Theme = new Theme { DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC"] }, DefaultFontSize = 15 };
        var background = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, Color = Colors.White,
            Material = new ShaderMaterial { Shader = new Shader { Code = """
                shader_type canvas_item;
                float grain(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
                void fragment() {
                    vec2 p = UV - vec2(0.43, 0.40);
                    float vignette = clamp(1.0 - length(p * vec2(1.2, 1.0)) * 1.25, 0.0, 1.0);
                    float mottling = grain(floor(UV * vec2(900.0, 600.0))) * 0.035;
                    float veins = sin(UV.x * 70.0 + sin(UV.y * 24.0) * 0.8) * 0.008;
                    vec3 bronze = mix(vec3(0.18, 0.14, 0.10), vec3(0.57, 0.47, 0.34), vignette);
                    COLOR = vec4(bronze + mottling + veins, 1.0);
                }
                """ } } };
        Place(background, 0, 0, 1, 1); AddChild(background);
        var engraving = new BattleTableBackdrop(); Place(engraving, .02f, .04f, TableRight, .80f); AddChild(engraving);
        var top = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(top, 0, 0, 1, .035f); Box(top, "221F1B", "837256"); AddChild(top);
        var title = DesktopLabel("三国行纪", 19, Gold); Place(title, .012f, 0, .15f, .035f); AddChild(title);
        _status = DesktopLabel("", 12); Place(_status, .15f, 0, .65f, .035f); AddChild(_status);
        var menu = new HBoxContainer(); Place(menu, .49f, .001f, .992f, .033f); AddChild(menu);
        var entries = new List<(string Name, Action Action)> { (CampaignSession is null ? "保存" : "保存行程", SaveBattle),
            (CampaignSession is null ? "读档" : "恢复行程", Restore),
            (CampaignSession is null ? "新局" : "返回行程", () =>
            { if (CampaignSession is null) _setupDialog.PopupCentered(new Vector2I(420, 200)); else { _aiTimer.Stop(); LeaveCampaignBattle?.Invoke(); } }),
            ("记录", ShowLog), ("资料", ShowUnitDetails), ("手牌", ShowHandInspection),
            ("设置", () => ShowBattleDialog(_settingsDialog)), ("说明", () => ShowBattleDialog(_helpDialog)) };
        foreach (var (name, action) in entries)
        {
            var button = Button(name, action); DarkButton(button); button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", 12); menu.AddChild(button);
            if (name == "手牌") _inspectButton = button;
            if (name == "资料") _unitButton = button;
            if (name == "设置") _settingsButton = button;
            if (name == "说明") _helpButton = button;
        }
        _table = new Control { MouseFilter = MouseFilterEnum.Ignore }; Place(_table, .005f, .045f, TableRight, .714f); AddChild(_table);
        _round = DesktopLabel("", 13, Gold); _round.HorizontalAlignment = HorizontalAlignment.Right;
        Place(_round, .68f, .048f, .815f, .105f); AddChild(_round);
        BuildSidebar();
        var bottom = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(bottom, 0, .80f, 1, 1); Box(bottom, "16130FD9", "5D5140"); AddChild(bottom);
        BuildEquipmentSlots();
        _handScroll = new ScrollContainer { FollowFocus = true, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, FocusMode = FocusModeEnum.None };
        Place(_handScroll, .125f, .804f, .775f, .995f); AddChild(_handScroll);
        _hand = new Control(); _handScroll.AddChild(_hand);
        _selfSeat = new Control { MouseFilter = MouseFilterEnum.Ignore }; Place(_selfSeat, .865f, .767f, .992f, .995f); AddChild(_selfSeat);
        _skills = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; Place(_skills, .79f, .858f, .86f, .993f); AddChild(_skills);
        _phaseLabel = DesktopLabel("", 12, new Color("B8D99A")); Place(_phaseLabel, .125f, .774f, .26f, .802f); AddChild(_phaseLabel);
        _handCount = DesktopLabel("", 12, Gold); Place(_handCount, .78f, .774f, .86f, .802f); AddChild(_handCount);
        _sortHand = Button("整理手牌", ToggleHandSort); DarkButton(_sortHand);
        Place(_sortHand, .78f, .805f, .86f, .845f); AddChild(_sortHand);
        _prompt = DesktopLabel("", 19); _prompt.HorizontalAlignment = HorizontalAlignment.Center;
        Place(_prompt, .18f, .677f, .73f, .718f); AddChild(_prompt);
        _progress = new Control { MouseFilter = MouseFilterEnum.Ignore }; Place(_progress, .275f, .722f, .585f, .733f); AddChild(_progress);
        _progress.AddChild(Picture(_art.Sprite("selfseat", "selfSeatCountdownBg")));
        _progressFill = Picture(_art.Sprite("selfseat", "selfSeatCountdownBar")); Place(_progressFill, .01f, .2f, .99f, .8f); _progress.AddChild(_progressFill);
        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; _decisionControls = controls;
        controls.AddThemeConstantOverride("separation", 18); Place(controls, .19f, .744f, .73f, .789f); AddChild(controls);
        _autoSelect = Button("一键选择", AutoSelectDiscards);
        _confirm = Button("确定", ConfirmDesktopSelection); _cancel = Button("取消", CancelMouseSelection);
        _pass = Button("不出", () => SubmitImmediate(BattleActionKind.Pass));
        _end = Button("结束出牌", () => SubmitImmediate(BattleActionKind.EndPlay));
        _ignore = Button("本次锦囊不再询问", IgnoreCurrentTrick);
        foreach (var button in new[] { _autoSelect, _confirm, _cancel, _pass, _end, _ignore })
        {
            DarkButton(button, button == _autoSelect); button.CustomMinimumSize = new Vector2(button == _ignore ? 156 : 88, 31);
            controls.AddChild(button);
        }
        _art.StyleDecisionButton(_confirm, true); _art.StyleDecisionButton(_cancel, false);
        _preview = Text(""); _preview.Visible = false; AddChild(_preview);
        _cardDetail = DesktopLabel(MouseHint, 12, new Color("D1C1A0"));
        _cardDetail.MaxLinesVisible = 1; _cardDetail.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        Place(_cardDetail, .15f, .61f, .77f, .64f); AddChild(_cardDetail);
        _feedback = DesktopLabel("", 12, new Color("D7C8A9")); Place(_feedback, .19f, .643f, .73f, .673f);
        _feedback.MaxLinesVisible = 1; _feedback.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; AddChild(_feedback);
        BuildDesktopDialogs();
        _targetLine = new BattleTargetLine { Preview = PreviewTargetLine }; Place(_targetLine, 0, 0, 1, 1); AddChild(_targetLine);
        BuildMotionLayer();
        _choiceOverlay = new Control { MouseFilter = MouseFilterEnum.Stop, ZIndex = 50 }; Place(_choiceOverlay, 0, 0, 1, 1); AddChild(_choiceOverlay); _choiceOverlay.Visible = false;
        _aiTimer = new Godot.Timer { WaitTime = .45, OneShot = false }; AddChild(_aiTimer); _aiTimer.Timeout += AdvanceAi;
        Resized += () => { if (_interaction is not null && _aiTimer is not null && !_applyingScale && !_leavingBattle) Refresh(""); };
    }

    private void BuildEquipmentSlots()
    {
        var equipment = new Control { MouseFilter = MouseFilterEnum.Pass,
            TooltipText = "当前开发规则尚未接入装备与判定牌。" }; Place(equipment, .006f, .806f, .116f, .992f); AddChild(equipment);
        var slots = new[] { ("equip_wuqi", "武器"), ("equip_fangju", "防具"),
            ("equip_jiayima", "+1 坐骑"), ("equip_jianyima", "−1 坐骑"), ("", "判定区") };
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(slot, 0, i * .20f, 1, i * .20f + .18f);
            Box(slot, "211E1B", "827761"); equipment.AddChild(slot);
            _reservedSlots.Add(slot);
            if (slots[i].Item1.Length > 0)
            {
                var icon = Picture(_art.Sprite("seat", slots[i].Item1), TextureRect.StretchModeEnum.KeepAspectCentered);
                Place(icon, .025f, .1f, .22f, .9f); icon.Modulate = new Color(.7f, .65f, .55f); slot.AddChild(icon);
            }
            var label = DesktopLabel(slots[i].Item2 + "　—", 12, new Color("938A77")); Place(label, .24f, 0, 1, 1); slot.AddChild(label);
        }
    }

    private void BuildDesktopDialogs()
    {
        GetViewport().GuiEmbedSubwindows = true;
        Theme.SetStylebox("panel", "AcceptDialog", new StyleBoxFlat { BgColor = new Color("25231F"), BorderColor = Gold,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1 });
        _logDialog = new AcceptDialog { Title = "战斗记录", OkButtonText = "关闭" }; AddChild(_logDialog);
        _logText = new RichTextLabel { ScrollFollowing = true, SelectionEnabled = true }; Place(_logText, .03f, .03f, .97f, .85f); _logDialog.AddChild(_logText);
        BuildPresentationDialogs();
        BuildKeyboardHelp(); BuildCardInspection();
        BuildResultDialog();
        foreach (var dialog in new[] { _resultDialog, _unitDialog, _settingsDialog, _handDialog, _helpDialog, _logDialog })
        { DarkButton(dialog.GetOkButton()); dialog.GetOkButton().CustomMinimumSize = new Vector2(110, 32); }
        _logDialog.Exclusive = _unitDialog.Exclusive = _settingsDialog.Exclusive = _helpDialog.Exclusive = _handDialog.Exclusive = true;
        _setupDialog = new AcceptDialog { Title = "新建战斗", OkButtonText = "开始" }; AddChild(_setupDialog);
        var setup = new HBoxContainer(); Place(setup, .05f, .1f, .95f, .55f); _setupDialog.AddChild(setup);
        setup.AddChild(Text("我方")); _playerCount = TeamSizeOption(); _playerCount.FocusMode = FocusModeEnum.None; setup.AddChild(_playerCount);
        setup.AddChild(Text("敌方")); _opponentCount = TeamSizeOption(); _opponentCount.FocusMode = FocusModeEnum.None; setup.AddChild(_opponentCount);
        _setupDialog.Confirmed += NewBattle;
        var probe = Button("G0抽牌试验", () => GetTree().ChangeSceneToFile("res://scenes/Probe.tscn")); DarkButton(probe);
        Place(probe, .05f, .57f, .50f, .77f); _setupDialog.AddChild(probe);
        var campaign = Button("首城行程", () => { _setupDialog.Hide(); GetTree().ChangeSceneToFile("res://scenes/Campaign.tscn"); }); DarkButton(campaign);
        Place(campaign, .53f, .57f, .95f, .77f); _setupDialog.AddChild(campaign);
    }

    private void BuildSidebar()
    {
        _sidebar = new Panel(); Place(_sidebar, .836f, .04f, .995f, .759f); Box(_sidebar, "211C15E8", "7B6A4D"); AddChild(_sidebar);
        var ornament = Picture(_art.Sprite("gameBase", "game_right_top_bg")); _sidebar.AddChild(ornament); ornament.Modulate = new Color(1, 1, 1, .3f);
        var tabs = new HBoxContainer(); tabs.AddThemeConstantOverride("separation", 2); Place(tabs, .025f, .012f, .975f, .068f); _sidebar.AddChild(tabs);
        foreach (var name in new[] { "牌局", "记录", "武将" })
        {
            var index = _sidebarTabs.Count;
            var tab = Button(name, () => { _sidebarTab = index; UpdateDesktopChrome(_interaction.View); }); DarkButton(tab);
            tab.SizeFlagsHorizontal = SizeFlags.ExpandFill; tabs.AddChild(tab); _sidebarTabs.Add(tab);
        }
        _sidebarContent = new Control(); Place(_sidebarContent, .045f, .09f, .955f, .57f); _sidebar.AddChild(_sidebarContent);
        var divider = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(divider, .025f, .59f, .975f, .594f); Box(divider, "A38C5B"); _sidebar.AddChild(divider);
        var logTitle = DesktopLabel("战斗消息", 12, Gold); Place(logTitle, .05f, .605f, .95f, .65f); _sidebar.AddChild(logTitle);
        _sideLog = new RichTextLabel { ScrollFollowing = true, SelectionEnabled = true, FocusMode = FocusModeEnum.None };
        _sideLog.AddThemeFontSizeOverride("normal_font_size", 12); _sideLog.AddThemeColorOverride("default_color", new Color("D5C796"));
        Place(_sideLog, .05f, .66f, .95f, .975f); _sidebar.AddChild(_sideLog);
        _sidebarToggle = Button("›", ToggleSidebar); DarkButton(_sidebarToggle);
        Place(_sidebarToggle, .823f, .42f, .836f, .468f); _sidebarToggle.TooltipText = "收起或展开侧栏"; AddChild(_sidebarToggle);
    }

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed; _sidebar.Visible = !_sidebarCollapsed;
        _sidebarToggle.Text = _sidebarCollapsed ? "‹" : "›";
        Place(_sidebarToggle, _sidebarCollapsed ? .982f : .823f, .42f, _sidebarCollapsed ? .997f : .836f, .468f);
        Place(_table, .005f, .045f, _sidebarCollapsed ? .977f : TableRight, .714f);
        Refresh("");
    }

    private void UpdateDesktopChrome(BattleView view)
    {
        _round.Text = $"第 {view.TurnNumber} 回合\n剩余牌 {view.DrawCount}";
        _phaseLabel.Text = view.WinnerTeamId is not null ? "战斗结束" : view.Response is not null ? "响应阶段"
            : view.Phase == BattlePhase.Discard ? "弃牌阶段" : "出牌阶段";
        var focus = view.Combatants.Single(u => u.EntityId == _focusUnitId);
        _handCount.Text = $"手牌 {focus.HandCount} / {focus.Hp}";
        _autoSelect.Visible = ChoosingDiscards;
        _progress.Visible = ChoosingDiscards;
        if (ChoosingDiscards)
        {
            _prompt.Text = $"您需要弃置 {RequiredDiscards} 张卡牌，请选牌（{_selectedDiscards.Count}/{RequiredDiscards}）";
            _confirm.Disabled = RequiredDiscards == 0 || _selectedDiscards.Count != RequiredDiscards;
            _cancel.Disabled = _selectedDiscards.Count == 0;
            _progressFill.AnchorRight = .01f + .98f * _selectedDiscards.Count / Math.Max(1f, RequiredDiscards);
            _progressFill.Visible = _selectedDiscards.Count > 0;
        }
        Clear(_sidebarContent);
        for (var i = 0; i < _sidebarTabs.Count; i++) _sidebarTabs[i].Modulate = i == _sidebarTab ? new Color("F7D997") : new Color("B1A58F");
        if (_sidebarTab == 1)
        {
            var history = new RichTextLabel { Text = _logText.Text, ScrollFollowing = true, SelectionEnabled = true, FocusMode = FocusModeEnum.None };
            history.AddThemeColorOverride("default_color", new Color("D5C16E"));
            history.AddThemeFontSizeOverride("normal_font_size", 12); Place(history, 0, 0, 1, 1); _sidebarContent.AddChild(history);
        }
        else if (_sidebarTab == 2)
        {
            var list = new VBoxContainer(); Place(list, 0, 0, 1, 1); _sidebarContent.AddChild(list);
            foreach (var unit in view.Combatants.OrderBy(u => u.SeatId))
            {
                var id = unit.EntityId;
                var button = Button($"{unit.SeatId + 1}　{UnitName(view, id)}　{unit.Hp}/{unit.MaxHp}", () =>
                { ShowUnitDetails(); _unitChoice.Selected = view.Combatants.OrderBy(u => u.SeatId).Select(u => u.EntityId).ToList().IndexOf(id); RenderUnitDetails(); });
                DarkButton(button); button.AddThemeFontSizeOverride("font_size", 12); button.CustomMinimumSize = new Vector2(0, 25); list.AddChild(button);
            }
        }
        else
        {
            var title = DesktopLabel("单机对战", 18, Gold); title.HorizontalAlignment = HorizontalAlignment.Center; Place(title, 0, .02f, 1, .15f); _sidebarContent.AddChild(title);
            var label = DesktopLabel($"我方 {view.Combatants.Count(u => u.TeamId == "a" && u.Alive)} 人  ·  敌方 {view.Combatants.Count(u => u.TeamId == "b" && u.Alive)} 人\n\n"
                + $"{UnitName(view, view.ActiveUnitId)}的回合\n牌堆 {view.DrawCount}　弃牌 {view.DiscardPile.Length}\n\n"
                + "悬停放大，拖动整理手牌。\n拖向发光目标即可出牌；\n自用或响应牌拖入出牌区。\n也可点牌、点目标后确定。\n\n弃牌时可多选，或一键选择。", 13, new Color("C7BA9D"));
            Place(label, .04f, .19f, .96f, .98f); _sidebarContent.AddChild(label);
        }
    }

    private void ToggleHandSort()
    {
        _sortByKind = !_sortByKind; _sortHand.Text = _sortByKind ? "原始顺序" : "整理手牌";
        if (!_sortByKind) _handOrder.Clear();
        Refresh(_sortByKind ? "手牌已按牌名、花色、点数排列。" : "已恢复手牌原始顺序。");
    }

    private void AutoSelectDiscards()
    {
        if (!ChoosingDiscards) return;
        _selectedDiscards.Clear();
        foreach (var action in _interaction.View.LegalActions.Where(a => a.Kind == BattleActionKind.Discard).Take(RequiredDiscards))
            _selectedDiscards.Add(action.CardId!);
        Refresh("已选择需要弃置的牌，可点击手牌调整，确定后才弃置。");
    }

    private void ConfirmDesktopSelection()
    {
        if (!ChoosingDiscards)
        {
            var line = PreviewTargetLine();
            Confirm();
            if (line is { } shot) _targetLine.Play(shot.From, shot.To);
            return;
        }
        if (_selectedDiscards.Count != RequiredDiscards || RequiredDiscards == 0) return;
        var view = _interaction.View;
        var actor = view.PendingDecision!.ActorId;
        var ids = _selectedDiscards.ToArray();
        if (ids.Any(id => !view.LegalActions.Any(a => a.Kind == BattleActionKind.Discard && a.CardId == id))) return;
        foreach (var id in ids)
        {
            var current = _interaction.View;
            if (current.Phase != BattlePhase.Discard || current.PendingDecision?.ActorId != actor) throw new InvalidOperationException("弃牌请求意外改变。");
            var action = current.LegalActions.Single(a => a.Kind == BattleActionKind.Discard && a.CardId == id);
            _interaction.Select(action);
            var result = _interaction.Confirm();
            if (!result.Accepted) throw new InvalidOperationException(result.Message);
        }
        ClearSelection(); Refresh($"已弃置{ids.Length}张牌。");
    }

    private (Vector2 From, Vector2 To)? PreviewTargetLine()
    {
        if (_dragging && _dragTarget is { } dragTarget && _seatButtons.TryGetValue(dragTarget, out var dragSeat))
            return (_dragFace!.GetGlobalRect().GetCenter(), dragSeat.GetGlobalRect().GetCenter());
        var target = _selectedTarget ?? _hoverTarget;
        if (_selectedCard is null || target is null || !CandidateActions(_interaction.View).Any(a => a.TargetId == target && NeedsTarget(a))
            || !_seatButtons.TryGetValue(target, out var seat)) return null;
        var card = _handCards.FirstOrDefault(p => p.Value.InstanceId == _selectedCard).Key;
        if (card is null || !IsInstanceValid(card)) return null;
        var rect = card.GetGlobalRect();
        return (rect.Position + rect.Size * new Vector2(.5f, .80f), seat.GetGlobalRect().GetCenter());
    }

    private void PlayPublicTargetLine(BattleView view, string source, string? target)
    {
        if (target is null || source == target || !_seatButtons.TryGetValue(source, out var from) || !_seatButtons.TryGetValue(target, out var to)) return;
        _targetLine.Play(from.GetGlobalRect().GetCenter(), to.GetGlobalRect().GetCenter());
    }
}
