using Godot;
using Sanguo.Application;
using Sanguo.CampaignCore;
using Sanguo.CampaignCore.FirstCity;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

/// <summary>首城叙事与编队入口；战斗沿用已有桌面，存档包含同一活动牌局。</summary>
public partial class CampaignScreen : Control
{
    private static readonly Color Ink = new("182D29"), Surface = new("29433B"), Gold = new("DEC68D"), Paper = new("F3EDDF");
    private FirstCitySession? _session;
    private readonly FirstCitySaveStore _store = new();
    private MarginContainer _margin = null!;
    private VBoxContainer _body = null!;
    private OptionButton _origin = null!;
    private Label _feedback = null!;
    private ConfirmationDialog _newDialog = null!, _leaveDialog = null!;
    private readonly Dictionary<string, CheckButton> _deployment = new();
    private BattleScreen? _battleScreen;
    private string? _smokeDirectory;
    private string Slot(Origin origin) => _smokeDirectory is { } directory ? Path.Combine(directory, origin + ".json")
        : ProjectSettings.GlobalizePath($"user://campaign-development/{origin.ToString().ToLowerInvariant()}/session.json");

    public override void _Ready()
    {
        GetViewport().GuiEmbedSubwindows = true;
        Theme = new Theme { DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC"] }, DefaultFontSize = 18 };
        var background = new ColorRect { Color = Ink, MouseFilter = MouseFilterEnum.Ignore };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(background);
        _margin = new MarginContainer(); _margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) _margin.AddThemeConstantOverride("margin_" + side, 24);
        AddChild(_margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 16); _margin.AddChild(root);
        var header = new HFlowContainer(); root.AddChild(header);
        header.AddChild(Text("三国行纪  /  首城行程", 28, Gold));
        header.AddChild(MakeButton("自由战斗", () =>
        {
            if (_session is null) GetTree().ChangeSceneToFile("res://scenes/Battle.tscn");
            else _leaveDialog.PopupCentered();
        }));
        var tools = new HFlowContainer(); tools.AddThemeConstantOverride("h_separation", 12); root.AddChild(tools);
        tools.AddChild(Text("新建或恢复的出身", 16));
        _origin = new OptionButton { CustomMinimumSize = new(180, 44) };
        foreach (var origin in Enum.GetValues<Origin>()) _origin.AddItem(FirstCityStory.OriginName(origin), (int)origin);
        tools.AddChild(_origin);
        tools.AddChild(MakeButton("新行程", () => { if (_session is null) BeginRun(); else _newDialog.PopupCentered(); }));
        tools.AddChild(MakeButton("恢复行程", () => LoadRun((Origin)_origin.Selected)));
        tools.AddChild(MakeButton("保存行程", SaveRun));
        var scroll = new ScrollContainer { FollowFocus = true, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(scroll);
        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 16); scroll.AddChild(_body);
        _feedback = Text("Tab切换控件，Enter或空格确认。行程与自由战斗使用独立存档。", 16); root.AddChild(_feedback);
        _newDialog = new ConfirmationDialog { Title = "开始新行程", DialogText = "当前未保存的进度将离开。已有存档保留，直到你再次保存行程。", OkButtonText = "开始新行程", CancelButtonText = "返回" };
        AddChild(_newDialog); _newDialog.Confirmed += BeginRun;
        _leaveDialog = new ConfirmationDialog { Title = "离开当前行程", DialogText = "离开将丢失尚未保存的进度。需要保留时，请返回并先保存行程。", OkButtonText = "离开行程", CancelButtonText = "返回" };
        AddChild(_leaveDialog); _leaveDialog.Confirmed += () => GetTree().ChangeSceneToFile("res://scenes/Battle.tscn");
        Render();
        var args = OS.GetCmdlineUserArgs();
        if (args.Contains("--campaign-smoke")) { _ = RunCampaignSmoke(args); return; }
        if (args.Contains("--campaign-preview")) _ = CaptureCampaignPreview(args);
    }

    private void BeginRun()
    {
        _session = FirstCitySession.Create("campaign.first-city", (Origin)_origin.Selected);
        Render("已开始新行程。剧情选择将被记录；战斗失败可以重新编队挑战。");
    }
    private void Render(string? message = null)
    {
        foreach (var child in _body.GetChildren()) { _body.RemoveChild(child); child.QueueFree(); }
        _deployment.Clear();
        if (_session is null)
        {
            _body.AddChild(Text("从无名之辈，到一城之主", 36, Gold));
            _body.AddChild(Text("选一种出身，经历故乡撤离、驿路通行，再挑战柳平外城与内城。你的选择会留在行程里；只有牌局胜利才能取得城池。", 22));
            foreach (var origin in Enum.GetValues<Origin>())
            {
                var story = FirstCityStory.Event(origin, 0);
                _body.AddChild(Text(FirstCityStory.OriginName(origin) + "  /  " + story.Title, 22, Gold));
                _body.AddChild(Text(story.Text));
            }
            _body.AddChild(Text("首城开发版：三线剧情已可推进；乡勇启用奋击I阶，学子与农户暂用无技能模板。对手均为通用守军。", 16));
            return;
        }
        var p = _session.Progress;
        _body.AddChild(Text(FirstCityStory.OriginName(p.Origin) + "的行程", 28, Gold));
        var rail = new HFlowContainer(); rail.AddThemeConstantOverride("h_separation", 20); _body.AddChild(rail);
        for (var i = 0; i < 4; i++) rail.AddChild(Text((p.Stage > i ? "已过  " : p.Stage == i ? "当前  " : "待行  ") + FirstCityStory.EncounterName(i), 18,
            p.Stage == i ? Gold : Paper));
        _body.AddChild(Text("永久名册：" + string.Join("、", p.Roster.Select(MemberName)) + "    领地：" + (_session.OwnsFirstCity ? "柳平城" : "尚无"), 16));
        if (p.Stage < 4)
        {
            var story = FirstCityStory.Event(p.Origin, Math.Min(p.Stage, 2));
            _body.AddChild(Text(p.Stage == 3 ? "外城已破，内城未定" : story.Title, 32, Gold));
            _body.AddChild(Text(p.Stage == 3 ? "外城胜利已被记录。内城守军仍在，重整队伍后迎战；此时柳平城尚未归属你。" : story.Text, 22));
            if (p.Choices.FirstOrDefault(c => c.Stage == Math.Min(p.Stage, 2)) is { } chosen)
                _body.AddChild(Text("你已决定：" + story.Choices.Single(c => c.Id == chosen.ChoiceId).Text, 18, Gold));
        }
        if (p.Stage == 1 && p.Phase is CampaignPhase.Choice or CampaignPhase.Deployment && !p.Roster.Contains(FirstCityStory.Volunteer))
        {
            _body.AddChild(Text("驿亭的乡勇愿意同行。邀请后永久加入名册；你也可以独自继续，主线无需招募前置。", 18));
            _body.AddChild(MakeButton("邀请乡勇同行", () => Act("recruit", FirstCityStory.Volunteer)));
        }
        switch (p.Phase)
        {
            case CampaignPhase.Choice:
                foreach (var choice in FirstCityStory.Event(p.Origin, p.Stage).Choices)
                    _body.AddChild(MakeButton(choice.Text, () => Act("choose", choice.Id), true));
                break;
            case CampaignPhase.Deployment:
                _body.AddChild(Text("出战编队  /  选择至少一人；主角可以不上场", 18));
                var roster = new HFlowContainer(); _body.AddChild(roster);
                foreach (var id in p.Roster)
                {
                    var check = new CheckButton { Text = MemberName(id), ButtonPressed = true, CustomMinimumSize = new(180, 44) };
                    roster.AddChild(check); _deployment.Add(id, check);
                }
                _body.AddChild(Text($"{FirstCityStory.EncounterName(p.Stage)}，第{p.Attempt}次挑战。每场重新发牌和恢复体力；两阶段攻城是两场独立牌局。", 16));
                _body.AddChild(MakeButton("进入牌局", () =>
                {
                    Act("start", string.Join('|', _deployment.Where(pair => pair.Value.ButtonPressed).Select(pair => pair.Key)));
                    OpenBattle();
                }, true));
                break;
            case CampaignPhase.Battle:
                _body.AddChild(MakeButton("继续牌局", OpenBattle, true));
                _body.AddChild(Text("当前牌局已暂停在决策点，可以保存行程后再继续。", 16));
                break;
            case CampaignPhase.Defeat:
                _body.AddChild(Text("本次挑战失利。剧情选择与永久名册保留；重整后以新的固定牌序再战。", 20));
                _body.AddChild(MakeButton("重新编队挑战", () => Act("retry"), true));
                break;
            case CampaignPhase.Complete:
                _body.AddChild(Text("柳平城，终于有了归处", 36, Gold));
                _body.AddChild(Text(FirstCityStory.Epilogue(p), 22));
                foreach (var choice in p.Choices)
                    _body.AddChild(Text(FirstCityStory.Event(p.Origin, choice.Stage).Choices.Single(c => c.Id == choice.ChoiceId).Consequence));
                _body.AddChild(Text("首城行程完成。四场胜利已记录，柳平城归属你。可以保存此刻的行程。", 20, Gold));
                break;
        }
        _body.AddChild(Text("战果记录：" + (_session.Snapshot.Results.IsEmpty ? "尚无" : string.Join("；", _session.Snapshot.Results.Select(r =>
            FirstCityStory.EncounterName(r.Stage) + $"第{r.Attempt}次" + (r.FinalState.Battle.WinnerTeamId == "a" ? "胜利" : "失利")))), 16));
        if (message is not null) _feedback.Text = message;
        Callable.From(() =>
        {
            if (_margin.IsVisibleInTree()) _body.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault()?.GrabFocus();
        }).CallDeferred();
    }
    private void Act(string kind, string? value = null)
    {
        var session = _session ?? throw new ArgumentException("请先开始新行程。");
        var result = session.Submit(new($"campaign.command.{session.Version}", session.Version, kind, value));
        if (!result.Accepted) throw new ArgumentException(result.Message);
        Render(result.Message);
    }
    private void SaveRun()
    {
        if (_session is null) throw new ArgumentException("请先开始新行程。");
        _store.Save(Slot(_session.Progress.Origin), _session.Snapshot);
        _feedback.Text = "已保存行程与当前牌局。";
    }
    private void LoadRun(Origin origin)
    {
        var loaded = _store.Load(Slot(origin));
        if (loaded.State.Progress.Origin != origin) throw new SaveException("save.incompatible", "出身与存档槽不匹配。");
        var restored = new FirstCitySession(loaded.State);
        CloseBattle(); _session = restored; _origin.Selected = (int)origin; Render(loaded.Message);
    }
    private void OpenBattle()
    {
        if (_session?.ActiveBattle is null) throw new ArgumentException("没有可继续的牌局。");
        _margin.Hide();
        _battleScreen = GD.Load<PackedScene>("res://scenes/Battle.tscn").Instantiate<BattleScreen>();
        _battleScreen.CampaignSession = _session;
        _battleScreen.LeaveCampaignBattle = () =>
        {
            CloseBattle();
            if (_session.ActiveBattle?.State.Battle.WinnerTeamId is not null) Act("resolve");
            else Render("牌局已暂停，可以继续或保存行程。");
        };
        _battleScreen.SaveCampaign = SaveRun;
        _battleScreen.LoadCampaign = () => LoadRun(_session.Progress.Origin);
        AddChild(_battleScreen);
    }
    private void CloseBattle()
    {
        if (_battleScreen is not null) { RemoveChild(_battleScreen); _battleScreen.QueueFree(); _battleScreen = null; }
        _margin.Show();
    }
    private static string MemberName(string id) => id == FirstCityStory.Protagonist ? "主角" : "同行乡勇";
    private static Label Text(string text, int size = 18, Color? color = null)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", size); label.AddThemeColorOverride("font_color", color ?? Paper);
        return label;
    }
    private Button MakeButton(string text, Action action, bool primary = false)
    {
        var button = new Button { Text = text, CustomMinimumSize = new(140, 44), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        button.AddThemeColorOverride("font_color", primary ? Ink : Paper);
        button.AddThemeColorOverride("font_focus_color", primary ? Ink : Paper);
        button.AddThemeColorOverride("font_hover_color", Ink); button.AddThemeColorOverride("font_pressed_color", Ink);
        button.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = primary ? Gold : Surface,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 10 });
        button.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = Gold, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 10 });
        button.AddThemeStyleboxOverride("focus", new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = Paper,
            BorderWidthLeft = 3, BorderWidthRight = 3, BorderWidthTop = 3, BorderWidthBottom = 3 });
        button.Pressed += () =>
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SaveException)
            { _feedback.Text = ex.Message; }
        };
        return button;
    }
}
