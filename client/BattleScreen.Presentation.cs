using System.Collections.Immutable;
using Godot;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private const string PresentationSettingsPath = "user://battle-interface.cfg";
    private static readonly int[] InterfaceScales = [100, 125, 150, 200];
    private Button _unitButton = null!, _settingsButton = null!;
    private AcceptDialog _unitDialog = null!, _settingsDialog = null!;
    private OptionButton _unitChoice = null!, _scaleChoice = null!;
    private RichTextLabel _unitText = null!;
    private Label _scaleExplanation = null!;
    private CheckButton _motionChoice = null!, _doubleClickChoice = null!;
    private bool _reduceMotion, _doubleClickConfirm = true, _presentationTesting, _presentationConnected;
    private int _requestedScale = 100;
    private bool _applyingScale, _leavingBattle;
    private long _presentationSequence = -1;
    private string? _presentationBattle;
    private readonly List<Control> _reservedSlots = [];

    private bool MatchesSkill(BattleAction action) => _selectedSkill is null || action.SkillId == _selectedSkill
        || action.Kind is BattleActionKind.Pass or BattleActionKind.EndPlay;

    private void BuildTargetLink(BattleView view)
    {
        var from = _selectedTarget is not null ? view.PendingDecision?.ActorId : view.Response?.SourceId;
        var to = _selectedTarget ?? view.Response?.TargetId;
        if (from is null || to is null || from == to) return;
        var seats = BattleSeatLayout.Arrange(view, _focusUnitId);
        var source = seats.Single(s => s.EntityId == from); var target = seats.Single(s => s.EntityId == to);
        var line = new BattleTargetLink { Source = new Vector2((float)source.X, (float)source.Y),
            Target = new Vector2((float)target.X, (float)target.Y), MouseFilter = MouseFilterEnum.Ignore };
        line.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _table.AddChild(line);
    }

    private void SelectCard(string id)
    {
        if (_selectedCard != id) SelectMouseCard(id);
    }

    private void BuildSkillButtons(ImmutableArray<BattleSkillView> skills, BattleView view)
    {
        foreach (var skill in skills)
        {
            var id = skill.Definition.RuleId;
            var available = view.LegalActions.Any(a => a.SkillId == id);
            var button = Button(skill.Definition.Name + (_selectedSkill == id ? " · 已选" : available ? " · 可发动" : " · 未到时机"), () =>
            {
                var selected = _selectedSkill == id ? null : id;
                ClearSelection(); _selectedSkill = selected;
                Refresh(selected is null ? "已返回全部合法操作。" : "已选择" + skill.Definition.Name + "，请选择合法手牌或发动方案。", new(FocusArea.Default));
            });
            button.Disabled = !available;
            button.TooltipText = skill.Definition.Name + "：" + skill.Definition.Text
                + (skill.Enabled ? "" : "\n" + skill.InactiveReason);
            button.CustomMinimumSize = new Vector2(110, 30);
            button.AddThemeFontSizeOverride("font_size", 14);
            _skills.AddChild(button); _skillButtons.Add(id, button);
        }
        _skills.Visible = _skillButtons.Count > 0;
        _skills.CustomMinimumSize = new Vector2(_skillButtons.Count * 140, 0);
    }

    private void BuildReservedZones(HBoxContainer parent)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(112, 112), MouseFilter = MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("252B29"),
            BorderColor = new Color("6C6048"), BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 4, ContentMarginBottom = 4 });
        parent.AddChild(panel);
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 1); panel.AddChild(column);
        foreach (var name in new[] { "武器", "防具", "进攻坐骑", "防御坐骑", "判定区" })
        {
            var slot = Text(name + "  —", 12, Paper);
            slot.AutowrapMode = TextServer.AutowrapMode.Off;
            slot.TooltipText = name + "预留区：本场牌组不包含装备牌或延时锦囊，不能选择或支付此区域。";
            slot.MouseFilter = MouseFilterEnum.Stop;
            column.AddChild(slot); _reservedSlots.Add(slot);
        }
    }

    private static void StyleHiddenHand(Button button, int index)
    {
        button.Text = $"暗手牌 {index + 1}\n选择此牌背";
        button.CustomMinimumSize = new Vector2(86, 74);
        button.AddThemeFontSizeOverride("font_size", 14);
        button.AddThemeColorOverride("font_color", Paper);
        button.AddThemeColorOverride("font_hover_color", Paper);
        button.AddThemeColorOverride("font_focus_color", Paper);
        button.AddThemeColorOverride("font_pressed_color", Paper);
        foreach (var state in new[] { "normal", "hover", "pressed" })
            button.AddThemeStyleboxOverride(state, new StyleBoxFlat { BgColor = new Color(state == "normal" ? "35473F" : "536451"),
                BorderColor = Gold, BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
                ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 8, ContentMarginBottom = 8 });
        button.TooltipText = "只选择一个手牌槽位，牌面在提交前保持隐藏。取消当前选择不会放弃本次选牌请求。";
    }

    private void BuildPresentationDialogs()
    {
        _unitDialog = new AcceptDialog { Title = "角色资料 · 公开信息", DialogCloseOnEscape = true, WrapControls = false };
        AddChild(_unitDialog);
        var units = DialogColumn(_unitDialog);
        _unitChoice = new OptionButton { CustomMinimumSize = new Vector2(0, 42) };
        units.AddChild(_unitChoice);
        _unitText = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        units.AddChild(_unitText);
        _unitText.GetVScrollBar().FocusMode = FocusModeEnum.All;
        _unitChoice.ItemSelected += _ => RenderUnitDetails();
        _unitDialog.GetOkButton().Text = "返回战斗";
        _unitDialog.Canceled += RestoreDialogFocus; _unitDialog.Confirmed += RestoreDialogFocus;

        _settingsDialog = new AcceptDialog { Title = "界面设置", DialogCloseOnEscape = true, WrapControls = false };
        AddChild(_settingsDialog);
        var settings = DialogColumn(_settingsDialog);
        settings.AddChild(Text("界面缩放", 20, Gold));
        _scaleChoice = new OptionButton { CustomMinimumSize = new Vector2(0, 42) };
        foreach (var percent in InterfaceScales) _scaleChoice.AddItem(percent + "%");
        _scaleChoice.AddItem("跟随系统"); settings.AddChild(_scaleChoice);
        _scaleChoice.ItemSelected += index =>
        {
            _requestedScale = index < InterfaceScales.Length ? InterfaceScales[index] : 0;
            ApplyInterfaceScale(); SavePresentationSettings();
        };
        _scaleExplanation = Text("", 16); settings.AddChild(_scaleExplanation);
        _motionChoice = new CheckButton { Text = "减少动态：停用卡牌抬升、飞牌和命中粒子", CustomMinimumSize = new Vector2(0, 44) };
        _motionChoice.Toggled += value => { _reduceMotion = value; SavePresentationSettings(); Refresh("已更新动态设置，选牌与响应保持不变。"); };
        settings.AddChild(_motionChoice);
        _doubleClickChoice = new CheckButton { Text = "双击确认已明确的牌或目标", CustomMinimumSize = new Vector2(0, 44), ButtonPressed = true };
        _doubleClickChoice.Toggled += value => { _doubleClickConfirm = value; SavePresentationSettings(); };
        settings.AddChild(_doubleClickChoice);
        settings.AddChild(Text("双击遇到多个目标或技能成本时仍需选择；右键取消当前选择。Esc先收起卡面预览，再取消选择。\n确认、放弃响应和结束出牌均保留可见按钮；战斗提示始终显示文字。", 16));
        _settingsDialog.GetOkButton().Text = "返回战斗";
        _settingsDialog.Canceled += RestoreDialogFocus; _settingsDialog.Confirmed += RestoreDialogFocus;
        GetWindow().SizeChanged += ApplyInterfaceScale;
        _presentationConnected = true;
    }

    private static VBoxContainer DialogColumn(AcceptDialog dialog)
    {
        var column = new VBoxContainer(); column.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        column.OffsetLeft = 20; column.OffsetRight = -20; column.OffsetTop = 16; column.OffsetBottom = -64;
        column.AddThemeConstantOverride("separation", 10); dialog.AddChild(column);
        return column;
    }

    private void ShowUnitDetails()
    {
        UpdateUnitDetails(); ShowBattleDialog(_unitDialog); _unitChoice.GrabFocus();
    }

    private void UpdateUnitDetails()
    {
        var oldId = _unitChoice.Selected >= 0 ? _unitChoice.GetItemMetadata(_unitChoice.Selected).AsString() : null;
        _unitChoice.Clear();
        foreach (var unit in _interaction.View.Combatants.OrderBy(u => u.SeatId))
        {
            _unitChoice.AddItem($"{unit.SeatId + 1}号位 · {UnitName(_interaction.View, unit.EntityId)} · {(unit.TeamId == "a" ? "我方" : "敌方")}");
            _unitChoice.SetItemMetadata(_unitChoice.ItemCount - 1, unit.EntityId);
        }
        var units = _interaction.View.Combatants.OrderBy(u => u.SeatId).ToArray();
        var id = oldId ?? _interaction.View.PendingDecision?.ActorId;
        _unitChoice.Selected = Math.Max(0, Array.FindIndex(units, u => u.EntityId == id));
        RenderUnitDetails();
    }

    private void RenderUnitDetails()
    {
        if (_unitChoice.Selected < 0) return;
        var view = _interaction.View;
        var unit = view.Combatants.Single(u => u.EntityId == _unitChoice.GetItemMetadata(_unitChoice.Selected).AsString());
        var skills = unit.Skills.Select(s => s.Definition.Name + "：" + s.Definition.Text
            + "\n当前状态：" + (s.Enabled ? "已启用，具体发动时机以当前合法操作为准。" : s.InactiveReason));
        _unitText.Text = $"{UnitName(view, unit.EntityId)}\n{(unit.Alive ? "存活" : "已阵亡")} · 体力 {unit.Hp}/{unit.MaxHp} · 手牌 {unit.HandCount}张\n"
            + (unit.EntityId == view.PendingDecision?.ActorId ? "当前响应或出牌角色\n" : "")
            + (unit.EntityId == view.ActiveUnitId ? "当前回合角色\n" : "")
            + "\n" + (unit.Skills.IsEmpty ? "无武将技能" : string.Join("\n\n", skills))
            + "\n\n装备区：武器 — / 防具 — / 进攻坐骑 — / 防御坐骑 —\n判定区：—\n本场牌组不含装备牌或延时锦囊，预留区不可操作。"
            + "\n\n查看资料不会切换操作权，也不会展示其他角色的暗手牌。";
    }

    private void LoadPresentationSettings(bool testing)
    {
        _presentationTesting = testing;
        var config = new ConfigFile();
        if (!testing && config.Load(PresentationSettingsPath) == Error.Ok)
        {
            var scale = config.GetValue("interface", "scale", 100).AsInt32();
            _requestedScale = scale == 0 || InterfaceScales.Contains(scale) ? scale : 100;
            _reduceMotion = config.GetValue("interface", "reduce_motion", false).AsBool();
            _doubleClickConfirm = config.GetValue("interface", "double_click", true).AsBool();
        }
        _scaleChoice.Selected = _requestedScale == 0 ? InterfaceScales.Length : Array.IndexOf(InterfaceScales, _requestedScale);
        _motionChoice.SetPressedNoSignal(_reduceMotion); _doubleClickChoice.SetPressedNoSignal(_doubleClickConfirm);
        ApplyInterfaceScale();
    }

    private void ApplyInterfaceScale()
    {
        if (_scaleExplanation is null || _applyingScale || _leavingBattle) return;
        _applyingScale = true;
        try
        {
            var window = GetWindow();
            if (DisplayServer.GetName() == "headless" && window.Size.X < 960) window.Size = new Vector2I(1280, 720);
            var requested = _requestedScale == 0 ? Math.Max(1, DisplayServer.ScreenGetScale()) : _requestedScale / 100f;
            // Keep a complete 1280x720 logical desktop even in a small physical window.
            var fit = Math.Min(window.Size.X / 1280f, window.Size.Y / 720f);
            window.ContentScaleSize = Vector2I.Zero;
            window.ContentScaleFactor = Math.Max(.5f, Math.Min(requested, fit));
            _scaleExplanation.Text = $"当前显示比例 {window.ContentScaleFactor:P0}。窗口空间不足时自动适配，保持卡面和底栏完整；放大窗口可使用所选比例。";
            if (_settingsDialog.Visible) _settingsDialog.Size = DialogSize();
        }
        finally { _applyingScale = false; }
        if (_aiTimer is not null) Refresh("界面比例已更新。");
    }

    private void SavePresentationSettings()
    {
        if (_presentationTesting) return;
        var config = new ConfigFile();
        config.SetValue("interface", "scale", _requestedScale);
        config.SetValue("interface", "reduce_motion", _reduceMotion);
        config.SetValue("interface", "double_click", _doubleClickConfirm);
        if (config.Save(PresentationSettingsPath) != Error.Ok) _feedback.Text = "界面设置本次已生效，但未能保存到本机。";
    }

    private Vector2I DialogSize() => new((int)Math.Min(850, GetViewportRect().Size.X - 48), (int)Math.Min(540, GetViewportRect().Size.Y - 48));

    private void UpdatePresentationFeedback(BattleView view)
    {
        var events = _interaction.ReadLog();
        if (_presentationBattle != view.BattleId || events.LastOrDefault()?.Sequence < _presentationSequence)
            _presentationSequence = -1;
        _presentationBattle = view.BattleId;
        var latest = events.LastOrDefault(e => e.Sequence > _presentationSequence
            && e.Code is "damage.dealt" or "hp.recovered" or "unit.died" or "skill.used" or "skill.jianxiong_gained" or "battle.finished");
        if (latest is not null) _feedback.Text += "  " + DescribeEvent(view, latest);
        if (!events.IsEmpty) _presentationSequence = events[^1].Sequence;
    }

    private bool HandleBattlePointer(InputEventMouseButton input)
    {
        if (!input.Pressed || BattleDialogVisible || _choiceOverlay.Visible) return false;
        if (input.ButtonIndex == MouseButton.Right)
        {
            if (!_cancel.Disabled) _cancel.EmitSignal(BaseButton.SignalName.Pressed);
            return true;
        }
        if (!_doubleClickConfirm || input.ButtonIndex != MouseButton.Left || !input.DoubleClick) return false;
        if (ChoosingDiscards) return false;
        // Later siblings paint on top when a large hand overlaps.
        var card = _handCards.Reverse().FirstOrDefault(p => p.Key.GetGlobalRect().HasPoint(input.Position)
            && AncestorScrolls(p.Key).All(s => s.GetGlobalRect().HasPoint(input.Position)));
        if (card.Key is not null)
        {
            if (card.Key.Disabled) return true;
            // A second click must never guess a target or cost. Preserve the current card's chosen target.
            if (_selectedCard != card.Value.InstanceId) SelectCard(card.Value.InstanceId);
            var actions = _interaction.View.LegalActions.Where(a => a.CardId == card.Value.InstanceId && a.SkillId == _selectedSkill
                && (a.CostCardId is null || a.CostCardId == _costCard)
                && (_interaction.View.Response is not null || (_selectedTarget is null
                    ? a.TargetId is null || a.TargetId == _interaction.View.PendingDecision?.ActorId : a.TargetId == _selectedTarget))).ToArray();
            if (actions.Length == 1 && _interaction.Select(actions[0])) Confirm();
            else _feedback.Text = "请先选择目标或技能成本，再确认操作。";
            return true;
        }
        var target = _seatButtons.FirstOrDefault(p => !p.Value.Disabled && p.Value.GetGlobalRect().HasPoint(input.Position));
        if (target.Value is null) return false;
        if (_selectedTarget != target.Key) SelectTarget(target.Key);
        if (_interaction.Selection is not null) Confirm();
        return true;
    }

    public override void _ExitTree()
    {
        _leavingBattle = true;
        if (_motionConnected) { GetWindow().FocusExited -= OnBattleWindowFocusExited; _motionConnected = false; }
        if (!_presentationConnected) return;
        GetWindow().SizeChanged -= ApplyInterfaceScale;
        _presentationConnected = false;
        GetWindow().ContentScaleFactor = 1;
        GetWindow().ContentScaleSize = new Vector2I(1280, 720);
    }
}
