using Godot;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private Control _selfSeat = null!, _skills = null!, _choiceOverlay = null!;
    private ScrollContainer _handScroll = null!;
    private HBoxContainer _choiceCards = null!;
    private RichTextLabel _sideLog = null!;
    private Button _pass = null!, _end = null!, _ignore = null!;
    private AcceptDialog _setupDialog = null!;
    private readonly Dictionary<string, Button> _skillButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<Button, BattleAction> _choiceActions = new();
    private string? _selectedSkill, _costCard, _ignoredTrick;
    private bool _refreshing;
    private const string MouseHint = "悬停查看牌面；点击手牌，再点击发光目标。";

    private static void Place(Control node, float left, float top, float right, float bottom)
    {
        node.AnchorLeft = left; node.AnchorTop = top; node.AnchorRight = right; node.AnchorBottom = bottom;
        node.OffsetLeft = node.OffsetTop = node.OffsetRight = node.OffsetBottom = 0;
    }

    private static void Box(Control node, string color, string border = "655C48", int width = 1)
        => node.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(color), BorderColor = new Color(border),
            BorderWidthLeft = width, BorderWidthRight = width, BorderWidthTop = width, BorderWidthBottom = width });

    private void BuildInterface()
    {
        Theme = new Theme { DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC"] }, DefaultFontSize = 16 };
        var background = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(background, 0, 0, 1, 1); Box(background, "242723"); AddChild(background);
        var title = Text("三国行纪", 21, Gold); Place(title, .012f, .008f, .14f, .055f); AddChild(title);
        _status = Text("", 13); Place(_status, .15f, .012f, .60f, .055f); AddChild(_status);
        var menu = new HBoxContainer(); Place(menu, .62f, .006f, .992f, .052f); AddChild(menu);
        foreach (var (name, action) in new (string, Action)[] { ("保存", SaveBattle), ("读档", Restore),
            ("新局", () => _setupDialog.PopupCentered(new Vector2I(360, 140))), ("记录", ShowLog), ("资料", ShowUnitDetails) })
        {
            var button = Button(name, action); button.CustomMinimumSize = new Vector2(60, 28); button.AddThemeFontSizeOverride("font_size", 14); menu.AddChild(button);
        }
        _table = new Control(); Place(_table, .006f, .065f, .806f, .695f); AddChild(_table);
        var side = new Panel(); Place(side, .815f, .065f, .994f, .705f); Box(side, "191D1B"); AddChild(side);
        var logTitle = Text("战斗记录", 15, Gold); Place(logTitle, .05f, .01f, .95f, .07f); side.AddChild(logTitle);
        _sideLog = new RichTextLabel { ScrollFollowing = true, SelectionEnabled = true, FocusMode = FocusModeEnum.None };
        Place(_sideLog, .05f, .08f, .95f, .99f); _sideLog.AddThemeFontSizeOverride("normal_font_size", 13); side.AddChild(_sideLog);
        var bottom = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(bottom, 0, .76f, 1, 1); Box(bottom, "101614"); AddChild(bottom);
        var equipment = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(equipment, .008f, .79f, .145f, .99f); Box(equipment, "1F2421"); AddChild(equipment);
        var equipText = Text("装备区\n武器　—　防具　—\n坐骑　—\n\n判定区　—", 13, new Color("8C8A7E")); Place(equipText, .05f, .04f, .95f, .96f); equipment.AddChild(equipText);
        equipment.TooltipText = "当前开发规则尚未接入装备与判定牌。";
        _handScroll = new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, FocusMode = FocusModeEnum.None };
        Place(_handScroll, .16f, .785f, .80f, .995f); AddChild(_handScroll);
        _hand = new Control(); _handScroll.AddChild(_hand);
        _selfSeat = new Control(); Place(_selfSeat, .855f, .735f, .99f, .995f); AddChild(_selfSeat);
        _skills = new HBoxContainer(); Place(_skills, .82f, .707f, .994f, .752f); AddChild(_skills);
        _prompt = Text("", 17, Gold); _prompt.HorizontalAlignment = HorizontalAlignment.Center;
        Place(_prompt, .16f, .692f, .80f, .727f); AddChild(_prompt);
        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; Place(controls, .20f, .731f, .77f, .778f); AddChild(controls);
        _confirm = Button("确定", Confirm); _cancel = Button("取消", CancelMouseSelection);
        _pass = Button("不出", () => SubmitImmediate(BattleActionKind.Pass));
        _end = Button("结束", () => SubmitImmediate(BattleActionKind.EndPlay));
        _ignore = Button("本次锦囊不再询问", IgnoreCurrentTrick);
        foreach (var button in new[] { _confirm, _cancel, _pass, _end, _ignore })
        { button.CustomMinimumSize = new Vector2(button == _ignore ? 150 : 80, 30); button.AddThemeFontSizeOverride("font_size", 15); controls.AddChild(button); }
        _art.StyleDecisionButton(_confirm, true); _art.StyleDecisionButton(_cancel, false);
        _preview = Text("", 13); _preview.Visible = false; AddChild(_preview);
        _cardDetail = Text(MouseHint, 12); Place(_cardDetail, .012f, .943f, .145f, .995f); _cardDetail.Visible = false; AddChild(_cardDetail);
        _feedback = Text("", 12); Place(_feedback, .16f, .665f, .80f, .69f); _feedback.MaxLinesVisible = 1;
        _feedback.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; AddChild(_feedback);
        GetViewport().GuiEmbedSubwindows = true;
        _logDialog = new AcceptDialog { Title = "战斗记录", OkButtonText = "关闭" }; AddChild(_logDialog);
        _logText = new RichTextLabel { ScrollFollowing = true, SelectionEnabled = true }; Place(_logText, .03f, .03f, .97f, .85f); _logDialog.AddChild(_logText);
        BuildPresentationDialogs();
        _setupDialog = new AcceptDialog { Title = "新建战斗", OkButtonText = "开始" }; AddChild(_setupDialog);
        var setup = new HBoxContainer(); Place(setup, .05f, .1f, .95f, .55f); _setupDialog.AddChild(setup);
        setup.AddChild(Text("我方")); _playerCount = TeamSizeOption(); _playerCount.FocusMode = FocusModeEnum.None; setup.AddChild(_playerCount);
        setup.AddChild(Text("敌方")); _opponentCount = TeamSizeOption(); _opponentCount.FocusMode = FocusModeEnum.None; setup.AddChild(_opponentCount);
        _setupDialog.Confirmed += NewBattle;
        var probe = Button("G0抽牌试验", () => GetTree().ChangeSceneToFile("res://scenes/Probe.tscn"));
        probe.CustomMinimumSize = new Vector2(120, 26); Place(probe, .05f, .57f, .50f, .77f); _setupDialog.AddChild(probe);
        _choiceOverlay = new Control { MouseFilter = MouseFilterEnum.Stop }; Place(_choiceOverlay, 0, 0, 1, 1); AddChild(_choiceOverlay);
        _choiceOverlay.Visible = false;
        _aiTimer = new Godot.Timer { WaitTime = .45, OneShot = false }; AddChild(_aiTimer); _aiTimer.Timeout += AdvanceAi;
    }

    private void SaveBattle() { _store.Save(SavePath, _interaction.Snapshot); _feedback.Text = "已保存当前牌局。"; }

    private void Refresh(string message)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            SkipUnavailableNullifications();
            var view = _interaction.View;
            Clear(_table); Clear(_selfSeat); Clear(_hand); Clear(_skills);
            _seatButtons.Clear(); _handCards.Clear(); _skillButtons.Clear();
            _status.Text = $"{view.Combatants.Count(u => u.TeamId == "a")}对{view.Combatants.Count(u => u.TeamId == "b")}　{UnitName(view, view.ActiveUnitId)}的回合　牌堆{view.DrawCount} / 弃牌{view.DiscardPile.Length}";
            var actorId = view.PendingDecision?.ControllerId == "player" ? view.PendingDecision.ActorId : null;
            if (actorId is not null) _focusUnitId = actorId;
            var focus = view.Combatants.Single(u => u.EntityId == _focusUnitId);
            var units = view.Combatants.OrderBy(u => (u.SeatId - focus.SeatId + view.Combatants.Length) % view.Combatants.Length).ToArray();
            var anchors = new[] { (.08f, .65f), (.08f, .20f), (.30f, .20f), (.50f, .20f), (.70f, .20f), (.92f, .20f), (.92f, .65f) };
            for (var i = 1; i < units.Length; i++)
            {
                var slot = units.Length == 2 ? 3 : units.Length == 3 ? (i == 1 ? 0 : 6)
                    : units.Length == 4 ? new[] { 0, 3, 6 }[i - 1] : (i - 1) * 7 / (units.Length - 1);
                var button = GeneralButton(view, units[i], actorId);
                var (x, y) = anchors[slot]; button.AnchorLeft = button.AnchorRight = x; button.AnchorTop = button.AnchorBottom = y;
                var height = Math.Min(158, GetViewportRect().Size.Y * .22f); var width = height * .79f;
                button.OffsetLeft = -width / 2; button.OffsetRight = width / 2; button.OffsetTop = -height / 2; button.OffsetBottom = height / 2;
                _table.AddChild(button); _seatButtons.Add(units[i].EntityId, button);
            }
            var self = GeneralButton(view, focus, actorId); Place(self, 0, 0, 1, 1); _selfSeat.AddChild(self); _seatButtons.Add(focus.EntityId, self);
            UpdateLog(view); _sideLog.Text = _logText.Text; UpdatePresentation(view);
            RenderSkills(view, actorId); RenderMouseHand(view, actorId); ResolveMouseSelection(view);
            _prompt.Text = MousePrompt(view);
            if (view.Response is null && actorId is not null)
                _prompt.Text = _selectedSkill switch
                {
                    BattleSkills.Wusheng => "武圣：选择一张红色手牌当【杀】，再选择目标",
                    BattleSkills.FenjiI => _selectedCard is null ? "奋击：先选择【杀】，再选择额外弃置的手牌与目标" : _costCard is null ? "奋击：再选择一张要弃置的手牌" : "奋击：请选择目标，然后确定",
                    _ => _prompt.Text
                };
            _confirm.Disabled = _interaction.Selection is null;
            _cancel.Disabled = _selectedCard is null && _selectedSkill is null;
            _pass.Visible = view.LegalActions.Any(a => a.Kind == BattleActionKind.Pass);
            _pass.Text = view.Response?.Kind == EffectKind.InvokeJianxiong ? "不发动" : "不出";
            _end.Visible = view.LegalActions.Any(a => a.Kind == BattleActionKind.EndPlay);
            _ignore.Visible = view.Response?.Kind == EffectKind.Nullification && view.LegalActions.Any(a => a.Kind == BattleActionKind.Nullify);
            _confirm.Text = view.Response?.Kind == EffectKind.InvokeJianxiong ? "发动" : "确定";
            _preview.Text = _interaction.Selection is { } action ? Describe(view, action) : "";
            _feedback.Text = _settlementText.Length > 0 ? _settlementText : message; _feedback.TooltipText = message + "\n" + _settlementText;
            RenderHandChoice(view);
        }
        finally { _refreshing = false; }
    }

    private Button GeneralButton(BattleView view, CombatantView unit, string? actor)
    {
        var targetable = CandidateActions(view).Any(a => a.TargetId == unit.EntityId && NeedsTarget(a));
        var selected = _selectedTarget == unit.EntityId;
        var button = Button("", () => SelectTarget(unit.EntityId)); button.CustomMinimumSize = Vector2.Zero; button.Disabled = !targetable;
        var border = new StyleBoxFlat { BgColor = new Color(unit.TeamId == "a" ? "263B35" : "402D28"), BorderColor = selected || targetable ? Gold : new Color("69604D"),
            BorderWidthLeft = selected ? 3 : 1, BorderWidthRight = selected ? 3 : 1, BorderWidthTop = selected ? 3 : 1, BorderWidthBottom = selected ? 3 : 1 };
        button.AddThemeStyleboxOverride("normal", border); button.AddThemeStyleboxOverride("disabled", border);
        var portrait = _art.Portrait(unit.Profile);
        if (portrait is not null)
        {
            var picture = Picture(portrait, TextureRect.StretchModeEnum.KeepAspectCovered); Place(picture, .04f, .04f, .93f, .89f);
            picture.Modulate = unit.Alive ? Colors.White : new Color(.35f, .35f, .35f); button.AddChild(picture);
        }
        else
        {
            var name = Text(unit.Profile == BattleProfile.ProtagonistFenjiI ? "乡勇" : "乡兵", 25, Gold); Place(name, .1f, .25f, .9f, .65f);
            name.HorizontalAlignment = HorizontalAlignment.Center; name.MouseFilter = MouseFilterEnum.Ignore; button.AddChild(name);
        }
        var frame = Picture(_art.SeatFrame); button.AddChild(frame);
        var nameplate = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(nameplate, .05f, .02f, .95f, .18f); Box(nameplate, "191D1BE8", "655C48", 0); button.AddChild(nameplate);
        var title = Text(UnitName(view, unit.EntityId), 14); Place(title, .05f, .02f, .95f, .18f); title.MouseFilter = MouseFilterEnum.Ignore; button.AddChild(title);
        var hp = Text(unit.Alive ? $"{unit.Hp}/{unit.MaxHp}" : "阵亡", 13, unit.Alive ? new Color("B5D191") : Gold); Place(hp, .68f, .72f, .97f, .86f); hp.MouseFilter = MouseFilterEnum.Ignore; button.AddChild(hp);
        var info = Text($"{(unit.TeamId == "a" ? "友" : "敌")} {unit.SeatId + 1}　手牌 {unit.HandCount}", 12); Place(info, .04f, .88f, .98f, 1); info.MouseFilter = MouseFilterEnum.Ignore; button.AddChild(info);
        var marker = selected ? "已选" : targetable ? "可选" : actor == unit.EntityId ? "响应" : view.ActiveUnitId == unit.EntityId ? "回合" : "";
        var mark = Text(marker, 13, Gold); Place(mark, .04f, .68f, .6f, .87f); mark.MouseFilter = MouseFilterEnum.Ignore; button.AddChild(mark);
        button.TooltipText = string.Join("\n", unit.Skills.Select(s => s.Definition.Name + "：" + s.Definition.Text));
        return button;
    }

    private void RenderSkills(BattleView view, string? actorId)
    {
        if (actorId is null) return;
        foreach (var skill in view.Combatants.Single(u => u.EntityId == actorId).Skills)
        {
            if (skill.Definition.RuleId is not (BattleSkills.Wusheng or BattleSkills.FenjiI)) continue;
            var id = skill.Definition.RuleId;
            var button = Button(skill.Definition.Name + (_selectedSkill == id ? " ✓" : ""), () =>
            {
                var chosen = _selectedSkill != id; ClearSelection(); if (chosen) _selectedSkill = id; Refresh("请选择技能需要的牌。");
            });
            button.CustomMinimumSize = new Vector2(72, 28); button.AddThemeFontSizeOverride("font_size", 14);
            button.Disabled = !view.LegalActions.Any(a => a.SkillId == id); button.TooltipText = skill.Definition.Text;
            _skills.AddChild(button); _skillButtons.Add(id, button);
        }
    }

    private IEnumerable<BattleAction> CandidateActions(BattleView view)
        => view.LegalActions.Where(a => a.CardId == _selectedCard && a.SkillId == _selectedSkill
            && (_costCard is null || a.CostCardId == _costCard));

    private static bool NeedsTarget(BattleAction action) => action.Kind is BattleActionKind.Slash or BattleActionKind.Duel or BattleActionKind.Dismantlement or BattleActionKind.Snatch;

    private void RenderMouseHand(BattleView view, string? actorId)
    {
        if (actorId is null) return;
        var hand = view.Combatants.Single(u => u.EntityId == actorId).VisibleHand;
        var height = Math.Min(140, GetViewportRect().Size.Y * .19f); var width = height * .715f;
        var available = GetViewportRect().Size.X * .64f;
        var step = hand.Length <= 1 ? width : Math.Clamp((available - width) / Math.Max(1, hand.Length - 1), 32, width + 4);
        _hand.CustomMinimumSize = new Vector2(Math.Max(available, step * Math.Max(0, hand.Length - 1) + width), height + 14);
        for (var i = 0; i < hand.Length; i++)
        {
            var card = hand[i];
            var legal = view.LegalActions.Any(a => a.SkillId == _selectedSkill && (a.CardId == card.InstanceId ||
                _selectedSkill == BattleSkills.FenjiI && a.CardId == _selectedCard && a.CostCardId == card.InstanceId));
            var button = CreateCardButton(card, legal, () => SelectMouseCard(card.InstanceId));
            button.CustomMinimumSize = Vector2.Zero; button.Position = new Vector2(i * step, card.InstanceId == _selectedCard || card.InstanceId == _costCard ? 0 : 12); button.Size = new Vector2(width, height);
            _hand.AddChild(button); _handCards.Add(button, card);
        }
    }

    private void SelectMouseCard(string id)
    {
        _interaction.Cancel();
        if (_selectedSkill == BattleSkills.FenjiI && _selectedCard is not null && id != _selectedCard
            && _interaction.View.LegalActions.Any(a => a.CardId == _selectedCard && a.CostCardId == id && a.SkillId == _selectedSkill))
            _costCard = _costCard == id ? null : id;
        else
        {
            _selectedCard = _selectedCard == id ? null : id; _selectedTarget = null; _costCard = null;
        }
        Refresh("已选牌，请点击目标；需要额外成本时再点一张手牌。");
    }

    private void SelectTarget(string targetId)
    {
        if (!CandidateActions(_interaction.View).Any(a => a.TargetId == targetId && NeedsTarget(a))) return;
        _interaction.Cancel(); _selectedTarget = _selectedTarget == targetId ? null : targetId; Refresh("已选择目标。");
    }

    private void ResolveMouseSelection(BattleView view)
    {
        _interaction.Cancel();
        if (view.Response?.Kind == EffectKind.InvokeJianxiong)
        { var invoke = view.LegalActions.FirstOrDefault(a => a.Kind == BattleActionKind.InvokeSkill); if (invoke is not null) _interaction.Select(invoke); return; }
        var candidates = CandidateActions(view).Where(a => a.CardId is not null
            && (!NeedsTarget(a) || a.TargetId == _selectedTarget)
            && (a.CostCardId is null || a.CostCardId == _costCard)).ToArray();
        if (candidates.Length == 1) _interaction.Select(candidates[0]);
    }

    private void CancelMouseSelection() { ClearSelection(); Refresh("已取消选择。 "); }

    private void SubmitImmediate(BattleActionKind kind)
    {
        var action = _interaction.View.LegalActions.SingleOrDefault(a => a.Kind == kind);
        if (action is null) return;
        _interaction.Select(action); Confirm();
    }

    private void IgnoreCurrentTrick()
    {
        if (_interaction.View.Response is not { Kind: EffectKind.Nullification } response) return;
        _ignoredTrick = response.Trick?.CardId ?? response.CardId;
        SubmitImmediate(BattleActionKind.Pass);
    }

    private void SkipUnavailableNullifications()
    {
        if (_ignoredTrick is not null && !_interaction.View.Processing.Any(c => c.InstanceId == _ignoredTrick)) _ignoredTrick = null;
        for (var i = 0; i < 128; i++)
        {
            var view = _interaction.View;
            if (view.Response is not { Kind: EffectKind.Nullification } response) return;
            if (_interaction.WaitingForAi)
            {
                _interaction.AdvanceAi(v => Sanguo.AI.BasicBattleAi.Choose(v)?.Action);
                ClearSelection(); continue;
            }
            if (view.PendingDecision?.ControllerId != "player") return;
            if (view.LegalActions.Any(a => a.Kind == BattleActionKind.Nullify) && _ignoredTrick != (response.Trick?.CardId ?? response.CardId)) return;
            var pass = view.LegalActions.Single(a => a.Kind == BattleActionKind.Pass);
            _interaction.Select(pass);
            if (!_interaction.Confirm().Accepted) throw new InvalidOperationException("无懈空询问推进失败。");
            ClearSelection();
        }
        throw new InvalidOperationException("无懈空询问超过人数保护。");
    }

    private static string MousePrompt(BattleView view)
    {
        if (view.WinnerTeamId is { } winner) return winner == "a" ? "胜利" : "失败";
        if (view.PendingDecision?.ControllerId != "player") return "等待对方行动…";
        var name = UnitName(view, view.PendingDecision.ActorId);
        return name + " · " + (view.Response switch
        {
            { Kind: EffectKind.Nullification } r => $"是否使用【无懈可击】抵消【{CardName(view.Processing.Single(c => c.InstanceId == r.CardId).Kind)}】？",
            { Kind: EffectKind.Dodge } => "请打出一张【闪】",
            { Kind: EffectKind.Duel } => "决斗：请打出一张【杀】",
            { Kind: EffectKind.Rescue } r => $"是否对{UnitName(view, r.TargetId)}使用【桃】？",
            { Kind: EffectKind.SelectHandCard } => "请选择目标的一张手牌",
            { Kind: EffectKind.InvokeJianxiong } => "是否发动【奸雄】？",
            _ => view.Phase == BattlePhase.Discard ? "请弃置一张手牌" : "出牌阶段"
        });
    }

    private void RenderHandChoice(BattleView view)
    {
        Clear(_choiceOverlay); _choiceActions.Clear();
        _choiceOverlay.Visible = view.PendingDecision?.ControllerId == "player" && view.Response?.Kind == EffectKind.SelectHandCard;
        if (!_choiceOverlay.Visible) return;
        var shade = new ColorRect { Color = new Color(0, 0, 0, .62f), MouseFilter = MouseFilterEnum.Stop }; Place(shade, 0, 0, 1, 1); _choiceOverlay.AddChild(shade);
        var panel = new Panel(); Place(panel, .20f, .23f, .80f, .65f); Box(panel, "252820", "BA9C60", 2); _choiceOverlay.AddChild(panel);
        var target = UnitName(view, view.Response!.TargetId);
        var title = Text("选择 " + target + " 的牌", 22, Gold); Place(title, .04f, .04f, .96f, .18f); panel.AddChild(title);
        var label = Text("手牌区", 14); Place(label, .04f, .20f, .18f, .30f); panel.AddChild(label);
        var scroll = new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled }; Place(scroll, .04f, .31f, .96f, .86f); panel.AddChild(scroll);
        _choiceCards = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = SizeFlags.ExpandFill }; _choiceCards.AddThemeConstantOverride("separation", 12); scroll.AddChild(_choiceCards);
        foreach (var action in view.LegalActions.Where(a => a.Kind == BattleActionKind.SelectHandCard))
        {
            var button = Button("", () => { _interaction.Select(action); Confirm(); }); button.CustomMinimumSize = new Vector2(76, 106);
            button.TooltipText = "选择一张手牌";
            var back = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(back, .06f, .04f, .94f, .96f); Box(back, "24362E", "C8AF70", 2); button.AddChild(back);
            var glyph = Text("三\n国\n杀", 20, Gold); Place(glyph, .15f, .10f, .85f, .90f); glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.MouseFilter = MouseFilterEnum.Ignore; button.AddChild(glyph);
            _choiceCards.AddChild(button); _choiceActions.Add(button, action);
        }
        var save = Button("保存牌局", SaveBattle); save.CustomMinimumSize = new Vector2(100, 26); Place(save, .77f, .88f, .96f, .98f); panel.AddChild(save);
        var note = Text("点击一张牌背即选取；此处不展示牌面。", 12); Place(note, .04f, .88f, .75f, .98f); panel.AddChild(note);
    }
}
