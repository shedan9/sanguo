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
    private HFlowContainer _hand = null!, _options = null!;
    private Control _table = null!;
    private Label _tableInfo = null!;
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
            BuildInterface();
            Refresh("选择手牌，再选择目标或技能方案，最后确认。取消选择不会消耗牌。");
            if (args.Contains("--battle-smoke")) { RunSmoke(args); return; }
            if (args.Contains("--battle-layout-smoke")) { _ = RunLayoutSmoke(); return; }
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
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 18);
        AddChild(margin);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        margin.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(column);
        var header = Flow(column);
        header.AddChild(Text("三国行纪 · 战斗桌面", 26, Gold));
        header.AddChild(Button("保存战斗", () => { _store.Save(SavePath, _interaction.Snapshot); _feedback.Text = "已保存当前决策点。"; }));
        header.AddChild(Button("恢复战斗", Restore));
        header.AddChild(Button("新建战斗", NewBattle));
        header.AddChild(Button("战斗记录", () => _logDialog.PopupCentered(new Vector2I(850, 500))));
        header.AddChild(Button("G0抽牌试验", () => GetTree().ChangeSceneToFile("res://scenes/Probe.tscn")));
        var setup = new HBoxContainer(); setup.AddThemeConstantOverride("separation", 12); column.AddChild(setup);
        var playerLabel = Text("我方人数", 16); playerLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        playerLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        setup.AddChild(playerLabel);
        _playerCount = TeamSizeOption(); setup.AddChild(_playerCount);
        var opponentLabel = Text("敌方人数", 16); opponentLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        opponentLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        setup.AddChild(opponentLabel);
        _opponentCount = TeamSizeOption(); setup.AddChild(_opponentCount);
        _status = Text("", 16); setup.AddChild(_status);
        _table = new Control { CustomMinimumSize = new Vector2(0, 320), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddChild(_table);
        _prompt = Text("", 22, Gold); column.AddChild(_prompt);
        _hand = Flow(column);
        var optionScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 50), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(optionScroll);
        _options = Flow(optionScroll);
        _preview = Text("", 16);
        var controls = Flow(column);
        _confirm = Button("确认操作", Confirm); controls.AddChild(_confirm);
        _cancel = Button("取消选择", () => { ClearSelection(); Refresh("已取消选择，战斗状态未改变。"); });
        controls.AddChild(_cancel);
        controls.AddChild(_preview);
        _feedback = Text("", 16); column.AddChild(_feedback);
        column.AddChild(Text("选牌 → 点目标角色框 → 选择技能或成本 → 确认。Tab切换，回车或空格选择，响应不限时。人数设置在新建时生效。", 14));
        GetViewport().GuiEmbedSubwindows = true;
        _logDialog = new AcceptDialog { Title = "战斗记录 · 仅公开信息" };
        AddChild(_logDialog);
        _logText = new RichTextLabel { ScrollFollowing = true, SelectionEnabled = true };
        _logText.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _logText.OffsetLeft = 16; _logText.OffsetTop = 16; _logText.OffsetRight = -16; _logText.OffsetBottom = -58;
        _logDialog.AddChild(_logText);
        _aiTimer = new Godot.Timer { WaitTime = 0.35, OneShot = false };
        AddChild(_aiTimer);
        _aiTimer.Timeout += AdvanceAi;
    }

    private void Refresh(string message)
    {
        var view = _interaction.View;
        Clear(_table); Clear(_hand); Clear(_options); _optionActions.Clear(); _seatButtons.Clear();
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
        _tableInfo = Text("", 17);
        _tableInfo.AnchorLeft = .25f; _tableInfo.AnchorRight = .75f;
        _tableInfo.AnchorTop = .37f; _tableInfo.AnchorBottom = .63f;
        _tableInfo.HorizontalAlignment = HorizontalAlignment.Center;
        _tableInfo.VerticalAlignment = VerticalAlignment.Center;
        _tableInfo.MouseFilter = MouseFilterEnum.Ignore;
        _table.AddChild(_tableInfo);
        foreach (var seat in BattleSeatLayout.Arrange(view, _focusUnitId))
        {
            var unit = view.Combatants.Single(u => u.EntityId == seat.EntityId);
            var targetable = view.LegalActions.Any(a => a.CardId == _selectedCard && a.TargetId == unit.EntityId
                && (a.CardId is not null || a.Kind == BattleActionKind.SelectHandCard));
            var marker = _selectedTarget == unit.EntityId ? " · 已选目标" : targetable ? " · 可选目标"
                : unit.EntityId == actorId ? " · 操作中" : unit.EntityId == view.ActiveUnitId ? " · 当前回合" : "";
            var skills = string.Join(" / ", unit.Skills.Select(s => s.Definition.Name));
            var button = Button($"{UnitName(view, unit.EntityId)} · {(unit.TeamId == "a" ? "我方" : "敌方")}\n"
                + (unit.Alive ? $"体力 {unit.Hp}/{unit.MaxHp} · 手牌 {unit.HandCount}" : "已阵亡")
                + $"\n座次{unit.SeatId + 1}{marker}" + (skills.Length > 0 ? "\n" + skills : ""), () => SelectTarget(unit.EntityId));
            button.CustomMinimumSize = new Vector2(158, 90);
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
            button.AnchorLeft = button.AnchorRight = (float)seat.X;
            button.AnchorTop = button.AnchorBottom = (float)seat.Y;
            button.OffsetLeft = -79; button.OffsetRight = 79; button.OffsetTop = -45; button.OffsetBottom = 45;
            _table.AddChild(button);
            _seatButtons.Add(unit.EntityId, button);
        }
        _prompt.Text = Prompt(view);
        UpdateLog(view);
        _tableInfo.Text = view.Processing.Length > 0 ? "处理区\n" + string.Join("、", view.Processing.Select(Card))
            : "三国行纪\n" + (_interaction.View.WinnerTeamId is null ? "以卡牌决胜" : "本场战斗结束");
        if (actorId is not null)
        {
            var actor = view.Combatants.Single(u => u.EntityId == actorId);
            foreach (var card in actor.VisibleHand)
            {
                var legal = view.LegalActions.Any(a => a.CardId == card.InstanceId);
                var button = Button(Card(card) + (card.InstanceId == _selectedCard ? " ✓" : ""), () =>
                {
                    ClearSelection(); _selectedCard = card.InstanceId;
                    Refresh("已选牌，请选择目标或技能方案。确认前可以取消或重选。");
                });
                button.CustomMinimumSize = new Vector2(122, 72);
                button.Disabled = !legal;
                button.TooltipText = legal ? "选择此牌后查看合法目标与技能方案。" : "当前时机无合法动作。";
                _hand.AddChild(button);
            }
            foreach (var action in view.LegalActions.Where(a => (a.CardId is null || a.CardId == _selectedCard)
                && (_selectedTarget is null || a.TargetId == _selectedTarget || a.TargetId is null)))
            {
                var button = Button(Describe(view, action), () =>
                {
                    if (!_interaction.Select(action)) { Refresh("请求已更新，请重新选择。"); return; }
                    _preview.Text = "待确认：" + Describe(view, action);
                    _confirm.Disabled = false; _cancel.Disabled = false;
                    _confirm.GrabFocus();
                });
                _optionActions.Add(button, action);
                _options.AddChild(button);
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
        Refresh($"已新建{_playerCount.Selected + 1}对{_opponentCount.Selected + 1}战斗，种子42。已切换至对应人数的独立存档槽，已有存档保留。");
        _aiTimer.Start();
    }

    private void ClearSelection()
    {
        _interaction.Cancel(); _selectedCard = null; _selectedTarget = null;
    }

    private void SelectTarget(string targetId)
    {
        var candidates = _interaction.View.LegalActions.Where(a => a.CardId == _selectedCard && a.TargetId == targetId
            && (a.CardId is not null || a.Kind == BattleActionKind.SelectHandCard)).ToArray();
        if (candidates.Length == 0) { Refresh("目标已失效，请重新选牌。"); return; }
        _interaction.Cancel(); _selectedTarget = targetId;
        Refresh("已选择目标；请核对操作或技能成本后确认。");
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
                throw new ArgumentException("预览用法：--battle-preview <PNG绝对路径> [我方人数 敌方人数 [log]]");
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
                if (args[4] != "log") throw new ArgumentException("未知预览模式。");
                _logDialog.PopupCentered(new Vector2I(850, 500));
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var error = GetViewport().GetTexture().GetImage().SavePng(args[1]);
            GD.Print("BATTLE_PREVIEW " + error);
            GetTree().Quit(error == Error.Ok ? 0 : 1);
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
}
