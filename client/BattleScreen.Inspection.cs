using Godot;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private const string CardDetailHint = "悬停或用Tab聚焦卡面展开详情；手牌一览可查看全部当前手牌。";
    private sealed record CardInspection(Control Owner, BattleCard Card, string Details);
    private PanelContainer _inspectionPanel = null!, _handInspectionContent = null!;
    private CardInspection? _hoverInspection, _focusInspection;
    private bool _inspectionDismissed;
    private Button _inspectButton = null!;
    private AcceptDialog _handDialog = null!;
    private OptionButton _handChoice = null!;
    private string? _inspectedActor;
    private readonly List<(BattleCard Card, string Details)> _inspectionHand = [];
    private bool BattleDialogVisible => _helpDialog.Visible || _logDialog.Visible || _handDialog.Visible;

    private void BuildCardInspection()
    {
        // This display layer never captures pointer input or keyboard focus.
        var layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);
        _inspectionPanel = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _inspectionPanel.Theme = Theme;
        StyleInspectionPanel(_inspectionPanel);
        layer.AddChild(_inspectionPanel);
        _handDialog = new AcceptDialog { Title = "手牌一览 · 只读查看", DialogCloseOnEscape = true };
        AddChild(_handDialog);
        var column = new VBoxContainer();
        column.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        column.OffsetLeft = 16; column.OffsetTop = 16; column.OffsetRight = -16; column.OffsetBottom = -58;
        column.AddThemeConstantOverride("separation", 12);
        _handDialog.AddChild(column);
        _handChoice = new OptionButton { CustomMinimumSize = new Vector2(0, 42) };
        column.AddChild(_handChoice);
        _handChoice.ItemSelected += _ => RenderHandInspection();
        _handInspectionContent = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        StyleInspectionPanel(_handInspectionContent);
        column.AddChild(_handInspectionContent);
        _handDialog.GetOkButton().Text = "返回战斗";
        _handDialog.Confirmed += RestoreDialogFocus;
        _handDialog.Canceled += RestoreDialogFocus;
    }

    private static void StyleInspectionPanel(PanelContainer panel)
        => panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("172822"), BorderColor = Gold,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 12, ContentMarginBottom = 12
        });

    private void BindCardInspection(Control owner, BattleCard card, string details)
    {
        var entry = new CardInspection(owner, card, details);
        owner.MouseEntered += () => { _hoverInspection = entry; _inspectionDismissed = false; UpdateCardInspection(); };
        owner.MouseExited += () => { if (_hoverInspection == entry) _hoverInspection = null; UpdateCardInspection(); };
        owner.FocusEntered += () => { _focusInspection = entry; _inspectionDismissed = false; UpdateCardInspection(); };
        owner.FocusExited += () => { if (_focusInspection == entry) _focusInspection = null; UpdateCardInspection(); };
    }

    private void ClearCardInspection()
    {
        _hoverInspection = _focusInspection = null;
        _inspectionDismissed = false;
        _inspectionPanel.Hide();
    }

    private void UpdateCardInspection()
    {
        var entry = _hoverInspection ?? _focusInspection;
        _inspectionPanel.Hide();
        _cardDetail.Text = entry?.Details ?? CardDetailHint;
        if (entry is null || _inspectionDismissed || BattleDialogVisible) return;
        Clear(_inspectionPanel);
        _inspectionPanel.AddChild(InspectionContent(entry.Card, entry.Details, 300, "Esc收起 · 查看不提交操作"));
        _inspectionPanel.ResetSize();
        _inspectionPanel.Show();
        PositionCardInspection();
    }

    private Control InspectionContent(BattleCard card, string details, float textWidth, string hint, bool expanded = false)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 14);
        var face = new Panel { CustomMinimumSize = expanded ? new Vector2(186, 260) : new Vector2(112, 156), MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkBegin };
        row.AddChild(face);
        face.AddChild(Picture(_art.CardFace(card.Kind)));
        var rankBackground = new ColorRect { Color = Paper, Position = new Vector2(3, 3), Size = new Vector2(32, 48),
            MouseFilter = MouseFilterEnum.Ignore };
        face.AddChild(rankBackground);
        var rank = Text(Card(card).Split(' ')[0], 16, card.Suit is "heart" or "diamond" ? new Color("A51F20") : Ink);
        rank.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); rank.AutowrapMode = TextServer.AutowrapMode.Off;
        rank.HorizontalAlignment = HorizontalAlignment.Center; rank.MouseFilter = MouseFilterEnum.Ignore;
        rankBackground.AddChild(rank);
        var caption = new ColorRect { Color = Paper, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1,
            OffsetTop = -26, MouseFilter = MouseFilterEnum.Ignore };
        face.AddChild(caption);
        var identity = Text(Card(card), 15, card.Suit is "heart" or "diamond" ? new Color("A51F20") : Ink);
        identity.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        identity.HorizontalAlignment = HorizontalAlignment.Center; identity.MouseFilter = MouseFilterEnum.Ignore;
        caption.AddChild(identity);
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(textWidth, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 8); row.AddChild(column);
        foreach (var (value, size, color) in new[] { (Card(card), 20, Gold), (details, expanded ? 18 : 16, Paper), (hint, 13, Gold) })
        {
            var label = Text(value, size, color); label.Size = new Vector2(textWidth, 0); label.MouseFilter = MouseFilterEnum.Ignore;
            column.AddChild(label);
        }
        return row;
    }

    public override void _Process(double delta)
    {
        if (_inspectionPanel is not null && (_hoverInspection ?? _focusInspection) is not null && !_inspectionDismissed)
            PositionCardInspection();
    }

    private void PositionCardInspection()
    {
        var entry = _hoverInspection ?? _focusInspection;
        if (entry is null || !GodotObject.IsInstanceValid(entry.Owner) || !entry.Owner.IsVisibleInTree() || _inspectionDismissed || BattleDialogVisible)
        { _inspectionPanel.Hide(); return; }
        var source = entry.Owner.GetGlobalRect();
        if (AncestorScrolls(entry.Owner).Any(s => !s.GetGlobalRect().Intersects(source)))
        { _inspectionPanel.Hide(); return; }
        _inspectionPanel.Show();
        var size = _inspectionPanel.Size;
        var bounds = GetViewportRect().Size;
        var x = Math.Clamp(source.GetCenter().X - size.X / 2, 8, Math.Max(8, bounds.X - size.X - 8));
        var y = source.Position.Y - size.Y - 10;
        if (y < 8) y = source.End.Y + 10;
        if (y + size.Y > bounds.Y - 8)
        {
            // A middle-of-window source may have more space beside it than above or below it.
            x = source.GetCenter().X < bounds.X / 2 ? source.End.X + 10 : source.Position.X - size.X - 10;
            x = Math.Clamp(x, 8, Math.Max(8, bounds.X - size.X - 8));
            y = Math.Clamp(source.Position.Y, 8, Math.Max(8, bounds.Y - size.Y - 8));
        }
        _inspectionPanel.Position = new Vector2(x, y);
    }

    private void ShowHandInspection()
    {
        UpdateHandInspection();
        ShowBattleDialog(_handDialog);
        if (!_handChoice.Disabled) _handChoice.GrabFocus();
    }

    private void UpdateHandInspection()
    {
        var view = _interaction.View;
        var actor = view.PendingDecision is { ControllerId: "player" } request
            ? view.Combatants.Single(u => u.EntityId == request.ActorId) : null;
        var oldId = actor?.EntityId == _inspectedActor && _handChoice.Selected >= 0 && _handChoice.Selected < _inspectionHand.Count
            ? _inspectionHand[_handChoice.Selected].Card.InstanceId : null;
        _inspectedActor = actor?.EntityId;
        _inspectionHand.Clear(); _handChoice.Clear();
        foreach (var (button, card) in _handCards)
        {
            var legal = !button.Disabled;
            // Only the current player's controller view supplied these cards; never reconstruct hidden hands.
            var details = _handInspectionDetails[button];
            _inspectionHand.Add((card, details));
            _handChoice.AddItem(Card(card) + (legal ? " · 可用" : " · 当前不可用"));
        }
        _handChoice.Disabled = _inspectionHand.Count == 0;
        _handChoice.Selected = _inspectionHand.Count == 0 ? -1 : Math.Max(0, _inspectionHand.FindIndex(p => p.Card.InstanceId == oldId));
        _handDialog.Title = actor is null ? "手牌一览 · 当前无玩家请求" : "手牌一览 · " + UnitName(view, actor.EntityId) + " · 只读查看";
        RenderHandInspection();
    }

    private readonly Dictionary<Button, string> _handInspectionDetails = new();

    private void RenderHandInspection()
    {
        Clear(_handInspectionContent);
        if (_handChoice.Selected >= 0 && _handChoice.Selected < _inspectionHand.Count)
        {
            var (card, details) = _inspectionHand[_handChoice.Selected];
            _handInspectionContent.AddChild(InspectionContent(card, details, 420, "仅查看当前操作角色手牌；返回后选牌并确认操作。", expanded: true));
        }
        else _handInspectionContent.AddChild(Text("当前没有可查看的玩家手牌。敌方手牌仅显示数量。"));
    }
}
