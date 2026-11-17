using Godot;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class BattleScreen : Control
{
    private static readonly Color Paper = new("EDE3CF"), Gold = new("C3A66A"), Ink = new("252B29");
    private BattleInteraction _interaction = null!;
    private readonly BasicBattleFileSaveStore _store = new();
    private Label _status = null!, _prompt = null!, _feedback = null!, _preview = null!;
    private Control _hand = null!;
    private DevelopmentBattleArtwork _art = null!;
    private Label _cardDetail = null!;
    private readonly Dictionary<Button, BattleCard> _handCards = new();
    private Control _table = null!;
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
    private int _aiSteps;
    private string SavePath
    {
        get
        {
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
        try
        {
            _interaction = new(DevelopmentBattleScenario.Create());
            _art = new DevelopmentBattleArtwork();
            BuildInterface();
            Refresh("点击手牌和目标，再点确定；取消选择不会消耗牌。");
            if (args.Contains("--battle-smoke")) { RunSmoke(args); return; }
            if (args.Contains("--battle-layout-smoke")) { _ = RunLayoutSmoke(); return; }
            if (args.Contains("--battle-artwork-smoke")) { _ = RunArtworkSmoke(); return; }
            if (args.Contains("--battle-interaction-smoke")) { _ = RunInteractionSmoke(); return; }
            if (args.Contains("--battle-desktop-smoke")) { _ = RunDesktopSmoke(); return; }
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

    private void Confirm()
    {
        var view = _interaction.View;
        var selection = _interaction.Selection;
        var result = _interaction.Confirm();
        ClearSelection();
        Refresh(result.Accepted && selection is not null
            ? UnitName(view, view.PendingDecision!.ActorId) + "：" + Describe(view, selection) + "。" + Outcome(view, _interaction.View) : result.Message);
    }

    private void AdvanceAi()
    {
        if (!_interaction.WaitingForAi || InspectionOpen) return;
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
        ResetPresentation(); _ignoredTrick = null;
        Refresh(loaded.Message);
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
        ResetPresentation(); _ignoredTrick = null;
        Refresh($"已新建{_playerCount.Selected + 1}对{_opponentCount.Selected + 1}战斗，种子42。已切换至对应人数的独立存档槽，已有存档保留。");
        _aiTimer.Start();
    }

    private void ClearSelection()
    {
        _interaction.Cancel(); _selectedCard = null; _selectedTarget = null; _costCard = null; _selectedSkill = null;
        _selectedDiscards.Clear(); _discardVersion = -1;
        _hoverTarget = null;
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

    private static string Prompt(BattleView view)
    {
        if (view.WinnerTeamId is not null) return "本场战斗已结束。";
        var who = UnitName(view, view.PendingDecision!.ActorId);
        var prefix = view.PendingDecision.ControllerId == "player" ? "正在操作 " : "敌方正在操作 ";
        var reason = view.Response switch
        {
            { Kind: EffectKind.Dodge } r => $"响应{ UnitName(view, r.SourceId) }的杀：使用闪或放弃（伤害{1 + r.DamageBonus}）",
            { Kind: EffectKind.Duel } r => $"决斗响应：向{ UnitName(view, r.SourceId) }打出杀或放弃",
            { Kind: EffectKind.Rescue } r => $"救援{UnitName(view, r.TargetId)}：体力{view.Combatants.Single(u => u.EntityId == r.TargetId).Hp}，使用桃或放弃",
            { Kind: EffectKind.Nullification } r => $"无懈窗口 · 第{r.ChainDepth}层 · 当前抵消对象 {Card(view.Processing.Single(c => c.InstanceId == r.CardId))}"
                + (r.Trick is { } t ? $"\n原锦囊{CardName(t.CardKind)}：{UnitName(view, t.SourceId)} → {UnitName(view, t.TargetId)}" : ""),
            { Kind: EffectKind.SelectHandCard } r => $"选择{UnitName(view, r.TargetId)}的一张暗手牌（只显示槽位）",
            { Kind: EffectKind.InvokeJianxiong } => "伤后询问：发动奸雄获得伤害牌，或放弃",
            _ => view.Phase == BattlePhase.Discard ? "弃牌阶段：选择一张手牌弃置，直到不超过体力值" : "出牌阶段：选择牌与目标，或结束出牌"
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

    private static string UnitName(BattleView view, string id) => view.Combatants.FirstOrDefault(u => u.EntityId == id)?.Profile switch
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
        var selected = card.InstanceId == _selectedCard || card.InstanceId == _costCard || _selectedDiscards.Contains(card.InstanceId);
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
        content.OffsetLeft = 2; content.OffsetRight = -2; content.OffsetTop = 2; content.OffsetBottom = -2;
        button.AddChild(content);
        content.AddChild(Picture(_art.CardFace(card.Kind)));
        var captionBackground = new ColorRect { Color = new Color("211B16D9"), MouseFilter = MouseFilterEnum.Ignore };
        captionBackground.AnchorTop = 1; captionBackground.AnchorBottom = 1; captionBackground.AnchorRight = 1;
        captionBackground.OffsetTop = -18;
        content.AddChild(captionBackground);
        var caption = Text(CardName(card.Kind) + (selected ? " ✓" : ""), 11, Paper);
        caption.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.MouseFilter = MouseFilterEnum.Ignore; captionBackground.AddChild(caption);
        var rank = Text((card.Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => card.Rank.ToString() })
            + "\n" + (card.Suit switch { "spade" => "♠", "heart" => "♥", "club" => "♣", "diamond" => "♦", _ => "?" }), 14,
            card.Suit is "heart" or "diamond" ? new Color("A51F20") : Ink);
        rank.Position = new Vector2(1, 1); rank.Size = new Vector2(20, 37);
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
        }) + (legal ? " 可选择；具体目标、转换技能与成本以合法方案为准。" : " 当前无合法动作。");
        button.TooltipText = details;
        var hovering = false;
        void UpdateHighlight()
        {
            var highlight = hovering || selected;
            content.OffsetTop = highlight ? 0 : 2; content.OffsetBottom = highlight ? -4 : -2;
            _cardDetail.Text = highlight ? details : "悬停查看牌面；点击手牌，再点击发光目标。";
        }
        button.MouseEntered += () => { hovering = true; UpdateHighlight(); };
        button.MouseExited += () => { hovering = false; UpdateHighlight(); };
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
        var button = new Button { Text = label, CustomMinimumSize = new Vector2(130, 42), FocusMode = FocusModeEnum.None };
        button.AddThemeColorOverride("font_color", Ink);
        button.AddThemeColorOverride("font_hover_color", Ink);
        button.AddThemeColorOverride("font_pressed_color", Ink);
        button.AddThemeColorOverride("font_focus_color", Ink);
        button.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Paper, ContentMarginLeft = 12, ContentMarginRight = 12 });
        button.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = Gold });
        button.AddThemeStyleboxOverride("focus", new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = Gold,
            BorderWidthLeft = 3, BorderWidthRight = 3, BorderWidthTop = 3, BorderWidthBottom = 3 });
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

}
