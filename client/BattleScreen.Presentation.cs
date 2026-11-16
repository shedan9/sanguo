using Godot;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private AcceptDialog _unitDialog = null!;
    private OptionButton _unitPicker = null!;
    private RichTextLabel _unitText = null!;
    private readonly List<string> _inspectedUnits = [];
    private long _presentedSequence;
    private BattleCard? _lastPublicCard;
    private string _settlementText = "";
    private HBoxContainer _publicCards = null!;
    private Label _resolution = null!;
    private bool InspectionOpen => _logDialog.Visible || _unitDialog.Visible || _setupDialog.Visible;

    private void BuildPresentationDialogs()
    {
        _unitDialog = new AcceptDialog { Title = "武将资料 · 公开信息", Exclusive = true, OkButtonText = "关闭" };
        AddChild(_unitDialog);
        var column = new VBoxContainer();
        column.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        column.OffsetLeft = 16; column.OffsetTop = 16; column.OffsetRight = -16; column.OffsetBottom = -58;
        _unitDialog.AddChild(column);
        _unitPicker = new OptionButton { CustomMinimumSize = new Vector2(0, 36), FocusMode = FocusModeEnum.None };
        column.AddChild(_unitPicker);
        _unitPicker.ItemSelected += _ => UpdateUnitDetails();
        _unitText = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        column.AddChild(_unitText);
        _logDialog.Exclusive = true;
    }

    private void ShowLog()
    {
        _logDialog.PopupCentered(new Vector2I(850, 440));
    }

    private void ShowUnitDetails()
    {
        _inspectedUnits.Clear(); _unitPicker.Clear();
        var view = _interaction.View;
        foreach (var unit in view.Combatants.OrderBy(u => u.SeatId))
        {
            _inspectedUnits.Add(unit.EntityId);
            _unitPicker.AddItem($"{UnitName(view, unit.EntityId)} · 座次{unit.SeatId + 1}" + (unit.Alive ? "" : " · 已阵亡"));
        }
        _unitPicker.Selected = Math.Max(0, _inspectedUnits.IndexOf(view.PendingDecision?.ActorId ?? view.ActiveUnitId));
        UpdateUnitDetails();
        _unitDialog.PopupCentered(new Vector2I(680, 420));
    }

    private void UpdateUnitDetails()
    {
        var view = _interaction.View;
        var unit = view.Combatants.Single(u => u.EntityId == _inspectedUnits[_unitPicker.Selected]);
        var active = view.PendingDecision?.ActorId == unit.EntityId;
        var lines = new List<string>
        {
            $"{UnitName(view, unit.EntityId)} · {(unit.TeamId == "a" ? "我方" : "敌方")} · 座次{unit.SeatId + 1}",
            $"体力 {unit.Hp}/{unit.MaxHp} · 手牌 {unit.HandCount}张 · {(unit.Alive ? "存活" : "已阵亡")}",
            active ? "当前请求由此角色响应。" : "当前没有此角色的操作请求。",
            "查看资料期间暂停敌方推进；关闭后继续当前请求。", ""
        };
        foreach (var skill in unit.Skills)
        {
            lines.Add(skill.Definition.Name + "：" + skill.Definition.Text);
            lines.Add(!skill.Enabled ? "未启用：" + skill.InactiveReason
                : !unit.Alive ? "当前已阵亡。"
                : view.LegalActions.Any(a => a.SkillId == skill.Definition.RuleId) && active ? "当前有合法发动方案，可关闭资料后选择。"
                : "具体发动条件与成本以当前操作区的合法方案为准。");
            if (skill.Definition.RuleId == BattleSkills.Wusheng) lines.Add("开发范围：仅支持红色手牌，尚不支持装备区成本。");
            lines.Add("");
        }
        if (unit.Skills.Length == 0) lines.Add("通用角色，无武将技能。");
        _unitText.Text = string.Join("\n", lines);
    }

    private void ResetPresentation()
    {
        // Restoring a decision never replays historical damage, skills or card reveals.
        _presentedSequence = _interaction.ReadLog().LastOrDefault()?.Sequence ?? 0;
        _lastPublicCard = null; _settlementText = "";
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
        }
        var center = new HBoxContainer { AnchorLeft = .25f, AnchorRight = .75f, AnchorTop = .43f, AnchorBottom = .70f,
            Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        center.AddThemeConstantOverride("separation", 8);
        _table.AddChild(center);
        _publicCards = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.AddChild(_publicCards);
        // Processing cards are public. Never derive a face from private hands or internal events.
        var cards = view.Processing.Length > 0 ? view.Processing.TakeLast(3).ToArray()
            : _lastPublicCard is { } recent ? new[] { recent } : [];
        foreach (var card in cards)
        {
            var panel = new Control { CustomMinimumSize = new Vector2(70, 115), MouseFilter = MouseFilterEnum.Pass,
                TooltipText = Card(card) };
            var face = Picture(_art.CardFace(card.Kind));
            face.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
            face.Size = new Vector2(70, 98);
            panel.AddChild(face);
            var rank = Text(Card(card).Split(' ')[0], 12, Paper);
            rank.Position = new Vector2(0, 98); rank.Size = new Vector2(70, 18);
            rank.AutowrapMode = TextServer.AutowrapMode.Off;
            rank.MouseFilter = MouseFilterEnum.Ignore;
            panel.AddChild(rank); _publicCards.AddChild(panel);
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
        center.AddChild(_resolution);
    }
}
