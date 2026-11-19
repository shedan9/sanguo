using Godot;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private long _presentedSequence;
    private BattleCard? _lastPublicCard;
    private string _settlementText = "";
    private readonly Dictionary<string, Control> _publicFaces = new(StringComparer.Ordinal);
    private HBoxContainer _publicCards = null!;
    private Label _resolution = null!;
    private bool InspectionOpen => BattleDialogVisible || _setupDialog.Visible;
    private void ShowLog() => ShowBattleDialog(_logDialog);

    private void ResetPresentation()
    {
        // Restoring a decision never replays historical damage, skills or card reveals.
        _presentedSequence = _interaction.ReadLog().LastOrDefault()?.Sequence ?? 0;
        _lastPublicCard = null; _settlementText = "";
        _targetLine?.ClearLine();
        ResetBattleMotion();
        _resultShown = false;
        _resultDialog?.Hide();
    }

    private void UpdatePresentation(BattleView view)
    {
        var events = _interaction.ReadLog(_presentedSequence);
        if (events.Length > 0)
        {
            _presentedSequence = events[^1].Sequence;
            _lastPublicCard = events.LastOrDefault(e => e.Card is not null)?.Card;
            var visible = events.Where(e => e.Code is not ("card.drawn" or "card.discarded" or "turn.ended"
                or "play.ended" or "response.passed" or "nullification.window_closed" or "card.finished_after_gain")).ToArray();
            _settlementText = string.Join("；", visible.TakeLast(3).Select(e => DescribeEvent(view, e)));
            var directed = events.LastOrDefault(e => e.Code is "slash.used" or "duel.used" or "dismantlement.used" or "snatch.used");
            if (directed is not null) PlayPublicTargetLine(directed.ActorId, directed.TargetId);
        }
        var center = new VBoxContainer { AnchorLeft = .28f, AnchorRight = .72f, AnchorTop = .42f, AnchorBottom = .78f,
            Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        center.AddThemeConstantOverride("separation", 8);
        _table.AddChild(center);
        _processingPanel = center;
        var scroll = new ScrollContainer { FollowFocus = true, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, GetViewportRect().Size.Y * .16f + 24), MouseFilter = MouseFilterEnum.Pass };
        center.AddChild(scroll);
        _publicCards = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        _publicCards.SizeFlagsHorizontal = SizeFlags.ExpandFill; scroll.AddChild(_publicCards);
        // Processing cards are public. Never derive a face from private hands or internal events.
        var cards = view.Processing.Length > 0 ? view.Processing.ToArray()
            : _lastPublicCard is { } recent ? new[] { recent } : [];
        if (view.Processing.Length > 0)
        {
            foreach (var item in BattleProcessingPresentation.Observe(view, _interaction.ReadLog()))
            {
                var tile = CreateProcessingCard(view, item); _publicCards.AddChild(tile); _processingCards.Add(tile, item);
                if (item.AwaitingResponse) scroll.CallDeferred("ensure_control_visible", tile);
            }
        }
        else foreach (var card in cards)
        {
            var height = GetViewportRect().Size.Y * .16f;
            var width = height * .715f;
            var panel = new Control { CustomMinimumSize = new Vector2(width, height + 18), MouseFilter = MouseFilterEnum.Pass,
                TooltipText = Card(card) };
            var face = CardArtwork(card);
            face.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
            face.Size = new Vector2(width, height);
            panel.AddChild(face);
            _publicFaces[card.InstanceId] = face;
            _publicCards.AddChild(panel);
            BindCardInspection(panel, card, Card(card) + " · 最近公开的牌，当前不在处理区。");
        }
        _resolution = Text(view.Processing.Length > 0 ? $"正在结算 · 共{view.Processing.Length}张" : cards.Length > 0 ? "最近公开牌" : "等待出牌", 13, Gold);
        _resolution.MaxLinesVisible = 4;
        _resolution.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _resolution.CustomMinimumSize = new Vector2(180, 0);
        _resolution.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        if (view.Response is { } r)
            _resolution.Text += $"\n{UnitName(view, r.SourceId)} → {UnitName(view, r.TargetId)}"
                + (r.Kind == EffectKind.Nullification ? $"\n反制链：{r.ChainDepth}层" : "");
        else if (view.WinnerTeamId is { } winner) _resolution.Text += winner == "a" ? "\n我方获胜" : "\n敌方获胜";
        else _resolution.Text += "\n" + UnitName(view, view.ActiveUnitId) + "的回合";
        _resolution.TooltipText = _resolution.Text;
        _resolution.HorizontalAlignment = HorizontalAlignment.Center;
        _resolution.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _resolution.Visible = cards.Length > 0 || view.WinnerTeamId is not null;
        center.AddChild(_resolution);
        if (events.Length > 0) PlayBattleMotion(view, events);
    }
}
