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
    private HFlowContainer _units = null!, _hand = null!, _options = null!;
    private Button _confirm = null!, _cancel = null!;
    private Godot.Timer _aiTimer = null!;
    private string? _selectedCard;
    private readonly Dictionary<Button, BattleAction> _optionActions = new();
    private int _aiSteps;
    private string SavePath => ProjectSettings.GlobalizePath("user://battle-development/session.json");

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
            if (args.Contains("--battle-preview")) _ = CapturePreview(args);
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
        column.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(column);
        var header = Flow(column);
        header.AddChild(Text("三国行纪 · 战斗开发原型", 28, Gold));
        header.AddChild(Button("保存战斗", () => { _store.Save(SavePath, _interaction.Snapshot); _feedback.Text = "已保存当前决策点。"; }));
        header.AddChild(Button("恢复战斗", Restore));
        header.AddChild(Button("新建战斗", () =>
        {
            _interaction = new(DevelopmentBattleScenario.Create());
            _aiSteps = 0;
            _selectedCard = null;
            Refresh("已用种子42新建2对2战斗，手动存档保留。");
            _aiTimer.Start();
        }));
        header.AddChild(Button("G0抽牌试验", () => GetTree().ChangeSceneToFile("res://scenes/Probe.tscn")));
        _status = Text(""); column.AddChild(_status);
        _units = Flow(column);
        _prompt = Text("", 22, Gold); column.AddChild(_prompt);
        _hand = Flow(column);
        _options = Flow(column);
        _preview = Text(""); column.AddChild(_preview);
        var controls = Flow(column);
        _confirm = Button("确认操作", Confirm); controls.AddChild(_confirm);
        _cancel = Button("取消选择", () => { _interaction.Cancel(); _selectedCard = null; Refresh("已取消选择，战斗状态未改变。"); });
        controls.AddChild(_cancel);
        _feedback = Text(""); column.AddChild(_feedback);
        column.AddChild(Text("开发范围：八种牌、手牌拆顺、武圣／奸雄／奋击I阶。Tab切换，回车或空格选择。响应不限时。\n当前使用文字卡牌与角色框；OL视觉还原、装备、判定和正式人物内容仍待接入。", 16));
        _aiTimer = new Godot.Timer { WaitTime = 0.35, OneShot = false };
        AddChild(_aiTimer);
        _aiTimer.Timeout += AdvanceAi;
    }

    private void Refresh(string message)
    {
        var view = _interaction.View;
        Clear(_units); Clear(_hand); Clear(_options); _optionActions.Clear();
        _status.Text = $"第 {view.TurnNumber} 回合 · {UnitName(view, view.ActiveUnitId)}    牌堆 {view.DrawCount} · 弃牌 {view.DiscardPile.Length}    "
            + (view.WinnerTeamId is { } winner ? (winner == "a" ? "我方获胜" : "敌方获胜") : "战斗进行中");
        var actorId = view.PendingDecision?.ControllerId == "player" ? view.PendingDecision.ActorId : null;
        // 操作单位优先显示；规则座次始终以SeatId呈现，不因显示排序改变。
        foreach (var unit in view.Combatants.OrderBy(u => u.EntityId == actorId ? 0 : 1).ThenBy(u => u.SeatId))
        {
            var panel = new PanelContainer { CustomMinimumSize = new Vector2(235, 96), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(unit.TeamId == "a" ? "233D38" : "482E2D"),
                BorderColor = unit.EntityId == actorId ? Gold : new Color("626359"),
                BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
                ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8
            });
            var skills = string.Join(" · ", unit.Skills.Select(s => s.Definition.Name + (s.Enabled ? "" : "（无主公身份）")));
            panel.AddChild(Text($"座次{unit.SeatId + 1}  {UnitName(view, unit.EntityId)}  {(unit.TeamId == "a" ? "我方" : "敌方")}"
                + $"{(unit.EntityId == actorId ? " · 操作中" : unit.EntityId == view.ActiveUnitId ? " · 当前回合" : "")}\n"
                + (unit.Alive ? $"体力 {unit.Hp}/{unit.MaxHp}    手牌 {unit.HandCount}" : "已阵亡")
                + (skills.Length > 0 ? "\n" + skills : ""), 17));
            _units.AddChild(panel);
        }
        _prompt.Text = Prompt(view);
        if (view.Processing.Length > 0)
            _prompt.Text += "\n处理区：" + string.Join("、", view.Processing.Select(Card));
        if (actorId is not null)
        {
            var actor = view.Combatants.Single(u => u.EntityId == actorId);
            foreach (var card in actor.VisibleHand)
            {
                var legal = view.LegalActions.Any(a => a.CardId == card.InstanceId);
                var button = Button(Card(card) + (card.InstanceId == _selectedCard ? " ✓" : ""), () =>
                {
                    _interaction.Cancel(); _selectedCard = card.InstanceId;
                    Refresh("已选牌，请选择目标或技能方案。确认前可以取消或重选。");
                });
                button.CustomMinimumSize = new Vector2(122, 72);
                button.Disabled = !legal;
                button.TooltipText = legal ? "选择此牌后查看合法目标与技能方案。" : "当前时机无合法动作。";
                _hand.AddChild(button);
            }
            foreach (var action in view.LegalActions.Where(a => a.CardId is null || a.CardId == _selectedCard))
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
        _cancel.Disabled = _interaction.Selection is null && _selectedCard is null;
        _feedback.Text = message;
    }

    private void Confirm()
    {
        var view = _interaction.View;
        var selection = _interaction.Selection;
        var result = _interaction.Confirm();
        _selectedCard = null;
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
            _selectedCard = null;
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
        var loaded = _store.Load(SavePath, DevelopmentBattleScenario.ContentHash);
        _interaction.Restore(loaded.State);
        _selectedCard = null; _aiSteps = 0;
        Refresh(loaded.Message);
        _aiTimer.Start();
    }

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
            { Kind: EffectKind.Nullification } r => $"无懈窗口 · 第{r.ChainDepth}层 · 当前抵消对象 {r.CardId}"
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
        BattleProfile.ProtagonistFenjiI => "乡勇", _ => "敌方乡兵"
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

    private async Task CapturePreview(string[] args)
    {
        _aiTimer.Stop();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var path = args.Last();
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print("BATTLE_PREVIEW " + error);
        GetTree().Quit(error == Error.Ok ? 0 : 1);
    }
}
