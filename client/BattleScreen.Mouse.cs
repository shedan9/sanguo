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
    private const string MouseHint = "悬停放大牌面；拖动整理手牌，拖向发光目标出牌；也可点击选择后确定。";

    private static void Place(Control node, float left, float top, float right, float bottom)
    {
        node.AnchorLeft = left; node.AnchorTop = top; node.AnchorRight = right; node.AnchorBottom = bottom;
        node.OffsetLeft = node.OffsetTop = node.OffsetRight = node.OffsetBottom = 0;
    }

    private static void Box(Control node, string color, string border = "655C48", int width = 1)
        => node.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(color), BorderColor = new Color(border),
            BorderWidthLeft = width, BorderWidthRight = width, BorderWidthTop = width, BorderWidthBottom = width });

    private void BuildInterface() => BuildScreenshotInterface();

    private void SaveBattle()
    {
        if (CampaignSession is not null) { SaveCampaign?.Invoke(); _feedback.Text = "已保存行程与当前牌局。"; return; }
        _store.Save(SavePath, _interaction.Snapshot); _feedback.Text = "已保存当前牌局。";
    }

    private void Refresh(string message, FocusRequest? focusRequest = null)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var owner = GetViewport().GuiGetFocusOwner();
            var previousFocus = CaptureBattleFocus(owner);
            var hadFocus = CanFocus(owner);
            ClearCardInspection();
            SkipUnavailableNullifications();
            var view = _interaction.View;
            _targetLine.ReduceMotion = _reduceMotion;
            CaptureHandOrigins();
            ResetHandMotion();
            Clear(_table); Clear(_selfSeat); Clear(_hand); Clear(_skills);
            _seatButtons.Clear(); _handCards.Clear(); _skillButtons.Clear(); _processingCards.Clear(); _handInspectionDetails.Clear();
            _status.Text = $"单机对战 [{view.Combatants.Count(u => u.TeamId == "a")} 对 {view.Combatants.Count(u => u.TeamId == "b")}]　{UnitName(view, view.ActiveUnitId)}的回合";
            if (_discardVersion != view.StateVersion) { _selectedDiscards.Clear(); _discardVersion = view.StateVersion; }
            var actorId = view.PendingDecision?.ControllerId == "player" ? view.PendingDecision.ActorId : null;
            if (actorId is not null) _focusUnitId = actorId;
            var focus = view.Combatants.Single(u => u.EntityId == _focusUnitId);
            var units = view.Combatants.OrderBy(u => (u.SeatId - focus.SeatId + view.Combatants.Length) % view.Combatants.Length).ToArray();
            var anchors = new[] { (.081f, .79f), (.081f, .33f), (.315f, .18f), (.50f, .18f), (.685f, .18f), (.919f, .33f), (.919f, .79f) };
            for (var i = 1; i < units.Length; i++)
            {
                var slot = units.Length == 2 ? 3 : units.Length == 3 ? (i == 1 ? 0 : 6)
                    : units.Length == 4 ? new[] { 0, 3, 6 }[i - 1] : (i - 1) * 7 / (units.Length - 1);
                var button = GeneralButton(view, units[i], actorId);
                var (x, y) = anchors[6 - slot]; button.AnchorLeft = button.AnchorRight = x; button.AnchorTop = button.AnchorBottom = y;
                var height = GetViewportRect().Size.Y * .235f; var width = height * .85f;
                button.OffsetLeft = -width / 2; button.OffsetRight = width / 2; button.OffsetTop = -height / 2; button.OffsetBottom = height / 2;
                _table.AddChild(button); _seatButtons.Add(units[i].EntityId, button);
            }
            var self = GeneralButton(view, focus, actorId); Place(self, 0, 0, 1, 1); _selfSeat.AddChild(self); _seatButtons.Add(focus.EntityId, self);
            UpdateLog(view); _sideLog.Text = _logText.Text; UpdatePresentation(view);
            RenderSkills(view, actorId); RenderMouseHand(view, actorId); ResolveMouseSelection(view);
            _prompt.Text = MousePrompt(view);
            if (CampaignSession is not null && view.WinnerTeamId is not null) _prompt.Text += " · 请返回行程记录战果";
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
            UpdateDesktopChrome(view);
            RenderHandChoice(view);
            if (_handDialog.Visible) UpdateHandInspection();
            if (focusRequest is not null || hadFocus)
                ScheduleBattleFocus(focusRequest ?? (_interaction.Selection is not null ? new(FocusArea.Confirm) : previousFocus ?? new(FocusArea.Default)), previousFocus is null ? owner : null);
        }
        finally { _refreshing = false; }
    }

    private Button GeneralButton(BattleView view, CombatantView unit, string? actor)
    {
        var targetable = CandidateActions(view).Any(a => a.TargetId == unit.EntityId && NeedsTarget(a));
        var selected = _selectedTarget == unit.EntityId;
        var button = Button("", () => SelectTarget(unit.EntityId)); button.CustomMinimumSize = Vector2.Zero; button.Disabled = !targetable;
        var active = view.ActiveUnitId == unit.EntityId && unit.Alive;
        var highlight = selected ? new Color("FF4D2D") : active ? new Color("91E34D") : targetable ? Gold : new Color("69604D");
        var border = new StyleBoxFlat { BgColor = new Color("2C2823"), BorderColor = highlight,
            BorderWidthLeft = selected || active ? 3 : 1, BorderWidthRight = selected || active ? 3 : 1,
            BorderWidthTop = selected || active ? 3 : 1, BorderWidthBottom = selected || active ? 3 : 1,
            ShadowColor = new Color(highlight, selected || active ? .75f : 0), ShadowSize = selected || active ? 8 : 0 };
        button.AddThemeStyleboxOverride("normal", border); button.AddThemeStyleboxOverride("disabled", border);
        var hover = (StyleBoxFlat)border.Duplicate(); hover.BorderColor = Gold; hover.ShadowColor = new Color(Gold, .6f); hover.ShadowSize = 6;
        button.AddThemeStyleboxOverride("hover", hover); button.AddThemeStyleboxOverride("pressed", hover);
        var frame = Picture(_art.Sprite("seatbottom", "other_general_border3")); button.AddChild(frame);
        var portrait = _art.Portrait(unit.Profile);
        if (portrait is not null)
        {
            var picture = Picture(portrait, TextureRect.StretchModeEnum.KeepAspectCovered); Place(picture, .065f, .06f, .935f, .94f);
            if (!unit.Alive) picture.Material = new ShaderMaterial { Shader = new Shader { Code = """
                shader_type canvas_item;
                void fragment() {
                    vec4 pixel = texture(TEXTURE, UV);
                    float gray = dot(pixel.rgb, vec3(0.299, 0.587, 0.114));
                    COLOR = vec4(vec3(gray * 0.72), pixel.a);
                }
                """ } };
            button.AddChild(picture);
        }
        else
        {
            var backdrop = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(backdrop, .065f, .06f, .935f, .94f);
            Box(backdrop, unit.TeamId == "a" ? "354039" : "493C31", "6D624C"); button.AddChild(backdrop);
            var name = Text(unit.Profile == BattleProfile.ProtagonistFenjiI ? "乡\n勇" : "乡\n兵", 30, new Color("B5A075")); Place(name, .2f, .20f, .8f, .78f);
            name.HorizontalAlignment = HorizontalAlignment.Center; name.MouseFilter = MouseFilterEnum.Ignore; button.AddChild(name);
        }
        var nameplate = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(nameplate, .05f, .02f, .95f, .18f); Box(nameplate, "191D1BE8", "655C48", 0); button.AddChild(nameplate);
        var title = DesktopLabel(UnitName(view, unit.EntityId), 12); Place(title, .20f, .025f, .96f, .14f); button.AddChild(title);
        var faction = Picture(_art.Sprite("seat", unit.TeamId == "a" ? "seat_country_shu" : "seat_country_wei"), TextureRect.StretchModeEnum.KeepAspectCentered);
        Place(faction, -.035f, .035f, .18f, .22f); button.AddChild(faction);
        for (var i = 0; i < unit.MaxHp; i++)
        {
            var life = Picture(_art.Sprite("seat", i >= unit.Hp ? "blood_empty_s" : unit.Hp <= 1 ? "blood_red_s" : unit.Hp == 2 ? "blood_yellow_s" : "blood_green_s"), TextureRect.StretchModeEnum.KeepAspectCentered);
            Place(life, .87f, .9f - (unit.MaxHp - i) * .097f, 1f, .9f - (unit.MaxHp - i - 1) * .097f); button.AddChild(life);
        }
        var count = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(count, -.025f, .83f, .2f, .96f);
        Box(count, unit.TeamId == "a" ? "2B643B" : "4A6174", "B1A78E"); button.AddChild(count);
        var info = DesktopLabel(unit.HandCount.ToString(), 13); info.HorizontalAlignment = HorizontalAlignment.Center; Place(info, 0, 0, 1, 1); count.AddChild(info);
        var seat = DesktopLabel(new[] { "一", "二", "三", "四", "五", "六", "七", "八" }[unit.SeatId], 13, new Color("C4B28C"));
        seat.HorizontalAlignment = HorizontalAlignment.Center; Place(seat, .3f, 1.01f, .7f, 1.12f); button.AddChild(seat);
        var marker = selected ? "已选" : targetable ? "可选" : actor == unit.EntityId ? "响应" : view.ActiveUnitId == unit.EntityId ? "回合" : "";
        if (!unit.Alive) marker = "阵亡";
        var mark = DesktopLabel(marker, 13, !unit.Alive ? new Color("E45C46") : Gold); Place(mark, .22f, .82f, .85f, .95f); button.AddChild(mark);
        if (!unit.Alive)
        {
            var stamp = new Panel { MouseFilter = MouseFilterEnum.Ignore }; Place(stamp, .22f, .34f, .76f, .72f);
            Box(stamp, "20000028", "B82016", 3); button.AddChild(stamp);
            var dead = DesktopLabel("阵\n亡", 24, new Color("D72D20")); dead.HorizontalAlignment = HorizontalAlignment.Center;
            Place(dead, 0, 0, 1, 1); stamp.AddChild(dead);
        }
        button.TooltipText = string.Join("\n", unit.Skills.Select(s => s.Definition.Name + "：" + s.Definition.Text));
        button.MouseEntered += () => { if (targetable) _hoverTarget = unit.EntityId; };
        button.MouseExited += () => { if (_hoverTarget == unit.EntityId) _hoverTarget = null; };
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
            DarkButton(button); button.CustomMinimumSize = new Vector2(72, 28); button.AddThemeFontSizeOverride("font_size", 14);
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
        var hand = view.Combatants.Single(u => u.EntityId == (actorId ?? _focusUnitId)).VisibleHand.AsEnumerable();
        if (_sortByKind) hand = hand.OrderBy(c => c.Kind).ThenBy(c => c.Suit, StringComparer.Ordinal).ThenBy(c => c.Rank);
        var cards = hand.ToArray();
        var owner = actorId ?? _focusUnitId;
        if (_handOrderOwner != owner)
        {
            if (_handOrderOwner is { } previousOwner) _ordersByOwner[previousOwner] = [.. _handOrder];
            _handOrder.Clear();
            if (_ordersByOwner.TryGetValue(owner, out var previousOrder)) _handOrder.AddRange(previousOrder);
            _handOrderOwner = owner;
        }
        _handOrder.RemoveAll(id => !cards.Any(c => c.InstanceId == id));
        foreach (var card in cards) if (!_handOrder.Contains(card.InstanceId)) _handOrder.Add(card.InstanceId);
        if (!_sortByKind) cards = cards.OrderBy(c => _handOrder.IndexOf(c.InstanceId)).ToArray();
        // Reserve scrollbar space even when the hand overflows; the row must fit above the window edge.
        var height = Math.Max(72, GetViewportRect().Size.Y * .191f - 28); var width = height * .715f;
        var available = GetViewportRect().Size.X * .65f;
        var step = cards.Length <= 1 ? width : Math.Clamp((available - width) / Math.Max(1, cards.Length - 1), Math.Min(28, width), width + 3);
        _hand.CustomMinimumSize = new Vector2(step * Math.Max(0, cards.Length - 1) + width, height + 10);
        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            var legal = view.LegalActions.Any(a => a.SkillId == _selectedSkill && (a.CardId == card.InstanceId ||
                _selectedSkill == BattleSkills.FenjiI && a.CardId == _selectedCard && a.CostCardId == card.InstanceId));
            var button = CreateCardButton(card, legal, () => SelectMouseCard(card.InstanceId));
            button.CustomMinimumSize = Vector2.Zero; button.Position = new Vector2(i * step, card.InstanceId == _selectedCard || card.InstanceId == _costCard || _selectedDiscards.Contains(card.InstanceId) ? 0 : 9); button.Size = new Vector2(width, height);
            _hand.AddChild(button); _handCards.Add(button, card);
            BindHandMotion(button, card);
        }
    }

    private void SelectMouseCard(string id)
    {
        _interaction.Cancel();
        if (ChoosingDiscards)
        {
            if (!_interaction.View.LegalActions.Any(a => a.Kind == BattleActionKind.Discard && a.CardId == id)) return;
            if (!_selectedDiscards.Remove(id) && _selectedDiscards.Count < RequiredDiscards) _selectedDiscards.Add(id);
            Refresh("点击手牌调整弃牌选择；选满所需数量后按确定。"); return;
        }
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

    private void CancelMouseSelection() { var card = _selectedCard; ClearSelection(); _targetLine.ClearLine(); Refresh("已取消选择。", new(FocusArea.Hand, card)); }

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
