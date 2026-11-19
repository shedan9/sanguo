using Godot;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private Control _motionLayer = null!;
    private Panel _dropZone = null!;
    private Label _dropHint = null!;
    private Panel? _hoverFace, _dragFace;
    private Button? _hoverButton, _pointerButton;
    private Tween? _hoverTween;
    private BattleCard? _pointerCard;
    private Vector2 _pointerStart, _pointerOffset;
    private Vector2 _dragPoint;
    private bool _motionConnected;
    private bool _dragging;
    private string? _dragTarget, _handOrderOwner;
    private long _pointerVersion;
    private ulong _motionUntil;
    private readonly List<string> _handOrder = [];
    private readonly Dictionary<string, List<string>> _ordersByOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect2> _handOrigins = new(StringComparer.Ordinal);
    private int _flightCount, _impactCount;
    private bool MotionBusy => _pointerCard is not null || Time.GetTicksMsec() < _motionUntil;

    private void BuildMotionLayer()
    {
        _motionLayer = new Control { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 20 };
        Place(_motionLayer, 0, 0, 1, 1); AddChild(_motionLayer);
        _dropZone = new Panel { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        Place(_dropZone, .27f, .43f, .67f, .62f); Box(_dropZone, "392F20CC", "D4B66D", 2);
        if (_dropZone.GetThemeStylebox("panel") is StyleBoxFlat dropStyle)
        {
            dropStyle.CornerRadiusTopLeft = dropStyle.CornerRadiusTopRight = dropStyle.CornerRadiusBottomLeft = dropStyle.CornerRadiusBottomRight = 9;
            dropStyle.ShadowColor = new Color(0, 0, 0, .3f); dropStyle.ShadowSize = 12;
        }
        _motionLayer.AddChild(_dropZone);
        _dropHint = DesktopLabel("拖至此处出牌", 18, Gold);
        _dropHint.HorizontalAlignment = HorizontalAlignment.Center;
        Place(_dropHint, .03f, .1f, .97f, .9f); _dropZone.AddChild(_dropHint);
        GetWindow().FocusExited += OnBattleWindowFocusExited;
        _motionConnected = true;
    }

    private void OnBattleWindowFocusExited()
    {
        var wasDragging = _dragging; ResetHandMotion();
        if (wasDragging && IsInsideTree()) Refresh("已收回手牌。");
    }

    private Panel MotionFace(BattleCard? card, Vector2 size)
    {
        var face = new Panel { Size = size, MouseFilter = MouseFilterEnum.Ignore, PivotOffset = size / 2 };
        var style = new StyleBoxFlat { BgColor = new Color("262118"), BorderColor = Gold,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            ShadowColor = new Color(0, 0, 0, .65f), ShadowSize = 14, ShadowOffset = new Vector2(0, 6),
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
        face.AddThemeStyleboxOverride("panel", style);
        if (card is not null)
        {
            var picture = CardArtwork(card);
            picture.OffsetLeft = picture.OffsetTop = 3; picture.OffsetRight = picture.OffsetBottom = -3;
            face.AddChild(picture);
        }
        else
        {
            var back = Picture(_art.Sprite("seatbottom", "card_normal"));
            Place(back, .01f, .01f, .99f, .99f); face.AddChild(back);
        }
        _motionLayer.AddChild(face);
        return face;
    }

    private void BindHandMotion(Button button, BattleCard card)
    {
        button.MouseDefaultCursorShape = CursorShape.Drag;
        button.MouseEntered += () => LiftHandCard(button, card);
        button.MouseExited += () => LowerHandCard(button);
        button.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse
                || InspectionOpen || _choiceOverlay.Visible) return;
            LowerHandCard(_hoverButton);
            _pointerButton = button; _pointerCard = card; _pointerStart = button.GetGlobalTransform() * mouse.Position;
            _pointerOffset = _pointerStart - button.GlobalPosition; _pointerVersion = _interaction.View.StateVersion;
        };
    }

    private void LiftHandCard(Button button, BattleCard card)
    {
        if (_reduceMotion || _dragging || _pointerCard is not null || InspectionOpen || _choiceOverlay.Visible || _hoverButton == button) return;
        LowerHandCard(_hoverButton);
        _hoverButton = button;
        _hoverFace = MotionFace(card, button.Size);
        _hoverFace.GlobalPosition = button.GlobalPosition;
        if (button.Disabled) _hoverFace.Modulate = new Color("A89F96");
        button.Modulate = new Color(1, 1, 1, .18f);
        var face = _hoverFace;
        _hoverTween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _hoverTween.TweenProperty(face, "position", face.Position + new Vector2(0, -32), .16);
        _hoverTween.TweenProperty(face, "scale", Vector2.One * 1.14f, .16);
    }

    private void LowerHandCard(Button? button)
    {
        if (button is null || button != _hoverButton) return;
        _hoverTween?.Kill();
        if (IsInstanceValid(button)) button.Modulate = Colors.White;
        if (IsInstanceValid(_hoverFace)) _hoverFace!.QueueFree();
        _hoverFace = null; _hoverButton = null;
    }

    private void CaptureHandOrigins()
    {
        _handOrigins.Clear();
        foreach (var (button, card) in _handCards)
            if (IsInstanceValid(button)) _handOrigins[card.InstanceId] = button.GetGlobalRect();
        if (_dragging && _pointerCard is { } dragged && IsInstanceValid(_dragFace))
            _handOrigins[dragged.InstanceId] = _dragFace!.GetGlobalRect();
    }

    private void ResetHandMotion()
    {
        LowerHandCard(_hoverButton);
        if (IsInstanceValid(_pointerButton)) _pointerButton!.Modulate = Colors.White;
        if (IsInstanceValid(_dragFace)) _dragFace!.QueueFree();
        _pointerButton = null; _pointerCard = null; _dragFace = null; _dragging = false; _dragTarget = null;
        if (IsInstanceValid(_dropZone)) _dropZone.Visible = false;
    }

    private void ResetBattleMotion()
    {
        ResetHandMotion(); _motionUntil = 0; _handOrigins.Clear(); _handOrder.Clear(); _handOrderOwner = null; _ordersByOwner.Clear();
        if (IsInstanceValid(_motionLayer))
            foreach (var child in _motionLayer.GetChildren()) if (child != _dropZone) { _motionLayer.RemoveChild(child); child.QueueFree(); }
    }

    public override void _Input(InputEvent input)
    {
        if (_pointerCard is null) { HandleKeyboardInput(input); return; }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }
            or InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            ResetHandMotion(); Refresh("已收回手牌。"); GetViewport().SetInputAsHandled(); return;
        }
        if (InspectionOpen || _choiceOverlay.Visible || _pointerVersion != _interaction.View.StateVersion)
        { ResetHandMotion(); return; }
        if (input is InputEventMouseMotion motion)
        {
            if (!_dragging && motion.Position.DistanceTo(_pointerStart) >= 9)
            {
                _dragging = true;
                _dragFace = MotionFace(_pointerCard, _pointerButton!.Size);
                _dragFace.Scale = Vector2.One * 1.08f;
                _pointerButton.Modulate = new Color(1, 1, 1, .18f);
            }
            if (!_dragging) return;
            _dragFace!.GlobalPosition = motion.Position - _pointerOffset;
            _dragPoint = motion.Position;
            _dragFace.Rotation = Mathf.Clamp((motion.Position.X - _pointerStart.X) / 1800, -.12f, .12f);
            UpdateDragTarget(motion.Position);
            GetViewport().SetInputAsHandled();
        }
        else if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mouse)
        {
            if (_dragging) { FinishCardDrag(mouse.Position); GetViewport().SetInputAsHandled(); }
            else { _pointerButton = null; _pointerCard = null; }
        }
    }

    private BattleAction[] DragActions() => _interaction.View.LegalActions.Where(a => a.CardId == _pointerCard?.InstanceId
        && a.SkillId == _selectedSkill && (a.CostCardId is null || a.CostCardId == _costCard)).ToArray();

    private void UpdateDragTarget(Vector2 point)
    {
        var actions = DragActions();
        _dragTarget = _seatButtons.FirstOrDefault(p => p.Value.GetGlobalRect().HasPoint(point)
            && actions.Any(a => NeedsTarget(a) && a.TargetId == p.Key)).Key;
        _dropZone.Visible = !_handScroll.GetGlobalRect().HasPoint(point);
        var direct = actions.Any(a => !NeedsTarget(a) && a.Kind != BattleActionKind.Discard);
        if (actions.Any(a => NeedsTarget(a))) Place(_dropZone, .31f, .41f, .65f, .49f);
        else Place(_dropZone, .27f, .43f, .67f, .62f);
        var valid = _dragTarget is not null || direct && _dropZone.GetGlobalRect().HasPoint(point);
        _dropHint.Text = ChoosingDiscards ? "弃牌请点击多选后确定" : _dragTarget is not null ? "松手 · 对目标出牌"
            : actions.Any(a => NeedsTarget(a)) ? "拖向角色框 · 松手出牌" : direct ? "拖至此处 · 松手出牌" : "当前无法打出 · 松手收回";
        _dropHint.Modulate = valid ? new Color("FFF0B8") : Paper;
        if (_dragFace?.GetThemeStylebox("panel") is StyleBoxFlat style)
            style.BorderColor = valid ? new Color("F8D778") : new Color("BBA780");
        foreach (var (id, seat) in _seatButtons)
        {
            var legal = actions.Any(a => NeedsTarget(a) && a.TargetId == id);
            if (seat.GetThemeStylebox(seat.Disabled ? "disabled" : "normal") is StyleBoxFlat border)
            {
                border.ShadowSize = legal ? 12 : 0;
                border.ShadowColor = new Color(id == _dragTarget ? "FF6946" : "F5CB68", .75f);
                if (legal) border.BorderColor = new Color(id == _dragTarget ? "FF6946" : "F5CB68");
            }
        }
    }

    private void FinishCardDrag(Vector2 point)
    {
        UpdateDragTarget(point);
        var card = _pointerCard!;
        var source = _pointerButton!.GetGlobalRect();
        var face = _dragFace!;
        var target = _dragTarget;
        var action = DragActions().SingleOrDefault(a => NeedsTarget(a) ? target is not null && a.TargetId == target
            : a.Kind != BattleActionKind.Discard && _dropZone.GetGlobalRect().HasPoint(point));
        // Reordering is a view preference. The rule hand, random stream and pending selection stay unchanged.
        if (_handScroll.GetGlobalRect().HasPoint(point))
        {
            var ordered = _handCards.OrderBy(p => p.Key.Position.X).Select(p => p.Value.InstanceId).ToList();
            var index = _handCards.Count(p => p.Value.InstanceId != card.InstanceId && point.X > p.Key.GetGlobalRect().GetCenter().X);
            ordered.Remove(card.InstanceId); ordered.Insert(Math.Min(index, ordered.Count), card.InstanceId);
            _handOrder.Clear(); _handOrder.AddRange(ordered); _sortByKind = false; _sortHand.Text = "整理手牌";
            ResetHandMotion(); Refresh("已调整手牌顺序。"); return;
        }
        if (action is not null && !ChoosingDiscards)
        {
            _interaction.Select(action);
            Confirm(); // Refresh captures the dragged card's current position before clearing its ghost.
            return;
        }
        // Invalid drops consume no command. Return the floating card to the original hand slot.
        _dragFace = null;
        ResetHandMotion();
        var tween = CreateTween().BindNode(face).SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(face, "global_position", source.Position, .22);
        tween.TweenProperty(face, "scale", Vector2.One, .22);
        tween.TweenProperty(face, "rotation", 0f, .22);
        tween.Chain().TweenCallback(Callable.From(face.QueueFree));
        Refresh(ChoosingDiscards ? "弃牌请点击多选后确定。" : "此处不能出牌，已收回手牌。");
    }

    private Vector2 SeatCenter(string id) => _seatButtons.TryGetValue(id, out var seat)
        ? seat.GetGlobalRect().GetCenter() : _table.GetGlobalRect().GetCenter();

    private void FlyCard(BattleCard? card, Vector2 from, Vector2 to, int index = 0, bool drawing = false)
    {
        if (_reduceMotion) return;
        _flightCount++;
        _motionUntil = Math.Max(_motionUntil, Time.GetTicksMsec() + 820 + (ulong)Math.Max(0, index) * 45);
        var height = GetViewportRect().Size.Y * .16f;
        var face = MotionFace(card, new Vector2(height * .715f, height));
        face.GlobalPosition = from - face.Size / 2;
        face.Scale = Vector2.One * (drawing ? .55f : .95f); face.Rotation = -.13f;
        var tween = CreateTween().BindNode(face);
        tween.TweenInterval(index * .045);
        tween.SetParallel();
        tween.TweenProperty(face, "global_position", to - face.Size / 2, .32).SetTrans(Tween.TransitionType.Quart).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(face, "scale", Vector2.One * (drawing ? .45f : 1.12f), .32);
        tween.TweenProperty(face, "rotation", .025f * (index % 3 - 1), .32);
        tween.Chain().TweenInterval(drawing ? .02 : .18);
        tween.Chain().TweenProperty(face, "modulate:a", 0f, .20);
        tween.Parallel().TweenProperty(face, "scale", Vector2.One * (drawing ? .25f : .92f), .20);
        tween.Chain().TweenCallback(Callable.From(face.QueueFree));
    }

    private void FloatFeedback(string text, Vector2 point, Color color, bool impact = false, bool slash = false, double delay = .22)
    {
        _motionUntil = Math.Max(_motionUntil, Time.GetTicksMsec() + (ulong)((delay + .95) * 1000));
        var label = DesktopLabel(text, impact ? 36 : 23, color);
        label.HorizontalAlignment = HorizontalAlignment.Center; label.Size = new Vector2(300, 65);
        label.GlobalPosition = point - new Vector2(150, 30); label.Modulate = new Color(1, 1, 1, 0);
        _motionLayer.AddChild(label); label.GlobalPosition = point - new Vector2(150, 30);
        var tween = CreateTween().BindNode(label); tween.TweenInterval(delay);
        tween.TweenCallback(Callable.From(() =>
        {
            if (_reduceMotion || !impact || !IsInstanceValid(_motionLayer)) return;
            var effect = new BattleImpactEffect { Position = point, Tint = color, Slash = slash };
            _motionLayer.AddChild(effect); _impactCount++;
        }));
        tween.TweenProperty(label, "modulate:a", 1f, .10);
        tween.SetParallel();
        tween.TweenProperty(label, "position:y", label.Position.Y - (_reduceMotion ? 0 : 44), .75).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(label, "modulate:a", 0f, .3).SetDelay(.52);
        tween.Chain().TweenCallback(Callable.From(label.QueueFree));
    }

    private void PlayBattleMotion(BattleView view, IEnumerable<PublicBattleEvent> events)
    {
        var batch = events.ToArray();
        var revealed = new HashSet<string>(StringComparer.Ordinal);
        var center = _table.GetGlobalRect().Position + _table.Size * new Vector2(.5f, .57f);
        foreach (var e in batch)
        {
            if (e.Card is { } card && e.Code is "slash.used" or "slash.dodged" or "duel.used" or "exnihilo.used"
                or "dismantlement.used" or "snatch.used" or "duel.slash_responded" or "nullification.used" or "hp.recovered" or "card.discarded")
            {
                if (revealed.Add(card.InstanceId))
                {
                    var origin = _handOrigins.TryGetValue(card.InstanceId, out var rect) ? rect.GetCenter() : SeatCenter(e.ActorId);
                    FlyCard(card, origin, center + new Vector2((revealed.Count - 1) * 19, 0), revealed.Count - 1);
                }
            }
            switch (e.Code)
            {
                case "damage.dealt":
                    FloatFeedback($"−{e.Amount}", SeatCenter(e.TargetId!), new Color("FF6A4F"), true, true); break;
                case "hp.recovered":
                    FloatFeedback($"+{e.Amount}  回复", SeatCenter(e.TargetId ?? e.ActorId), new Color("A8F0A1"), true); break;
                case "slash.dodged":
                    FloatFeedback("闪 · 躲避", SeatCenter(e.ActorId), new Color("ADDCF2"), true); break;
                case "trick.negated":
                    FloatFeedback("锦囊抵消", SeatCenter(e.TargetId ?? e.ActorId), new Color("B9B9F5"), true); break;
                case "skill.used": case "skill.jianxiong_gained":
                    FloatFeedback(SkillName(e.RelatedCardId), SeatCenter(e.ActorId), new Color("FFE09A")); break;
                case "turn.started":
                    FloatFeedback(UnitName(view, e.ActorId) + " · 回合开始", center - new Vector2(0, 70), Gold, delay: 0); break;
                case "dying.started":
                    FloatFeedback("濒死 · 等待救援", SeatCenter(e.ActorId), new Color("FF9A75")); break;
                case "unit.died":
                    FloatFeedback("阵亡", SeatCenter(e.ActorId), new Color("FF7259"), true); break;
                case "battle.finished":
                    FloatFeedback(e.TargetId == "a" ? "胜  利" : "败  北", center - new Vector2(0, 80), Gold, true); break;
            }
        }
        foreach (var draws in batch.Where(e => e.Code == "card.drawn").GroupBy(e => e.ActorId))
        {
            var destination = SeatCenter(draws.Key);
            var deck = _table.GetGlobalRect().Position + _table.Size * new Vector2(.82f, .12f);
            for (var i = 0; i < Math.Min(4, draws.Count()); i++) FlyCard(null, deck, destination, i, drawing: true);
            FloatFeedback($"摸牌 +{draws.Count()}", destination, Gold, delay: .1);
        }
        if (!_reduceMotion && revealed.Count > 0)
        {
            _publicCards.Modulate = new Color(1, 1, 1, 0);
            var tween = CreateTween().BindNode(_publicCards); tween.TweenInterval(.24);
            tween.TweenProperty(_publicCards, "modulate:a", 1f, .16);
        }
    }

    private void UpdateHandMotion(double delta)
    {
        if (_hoverButton is { } hovered && (!_handScroll.GetGlobalRect().Intersects(hovered.GetGlobalRect()) || InspectionOpen))
            LowerHandCard(hovered);
        if (!_dragging) return;
        var rect = _handScroll.GetGlobalRect();
        if (_dragPoint.Y < rect.Position.Y - 24 || _dragPoint.Y > rect.End.Y + 24) return;
        var direction = _dragPoint.X < rect.Position.X + 28 ? -1 : _dragPoint.X > rect.End.X - 28 ? 1 : 0;
        if (direction != 0) _handScroll.ScrollHorizontal += direction * Math.Max(1, (int)(640 * delta));
    }
}
