using Godot;
using Sanguo.Application;
using Sanguo.CampaignCore;
using Sanguo.Content;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class ProbeScreen : Control
{
    private static readonly Color Ink = new("252B29");
    private static readonly Color Paper = new("EDE3CF");
    private static readonly Color Gold = new("C3A66A");
    private ContentCatalog _catalog = null!;
    private ProbeSession _session = null!;
    private readonly FileSaveStore _store = new();
    private Label _status = null!;
    private Label _hand = null!;
    private Label _feedback = null!;
    private Button _draw = null!;
    private string SavePath => ProjectSettings.GlobalizePath("user://g0-probe/session.json");

    public override void _Ready()
    {
        var smoke = OS.GetCmdlineUserArgs().Contains("--smoke");
        try
        {
            using var resource = typeof(ProbeScreen).Assembly.GetManifestResourceStream("Sanguo.G0.cards.json")!;
            using var reader = new StreamReader(resource);
            var result = ContentLoader.Load([new("cards/g0-probe.json", reader.ReadToEnd())]);
            if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Issues));
            _catalog = result.Catalog!;
            NewSession();
            if (smoke) { RunSmoke(); return; }
            BuildInterface();
            Refresh("会话已就绪。抽取试验牌，保存后可恢复同一决策点。");
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
            if (smoke) { GetTree().Quit(1); return; }
            var error = Text("初始化失败：" + ex.Message + "\n请重新构建客户端并检查内容文件。", 24);
            error.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(error);
        }
    }

    private void NewSession() => _session = ProbeSession.Create("campaign.g0", Origin.Damage, _catalog.Deck, _catalog.ContentHash, 42);

    private void BuildInterface()
    {
        Theme = new Theme
        {
            DefaultFont = new SystemFont { FontNames = ["PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC"] },
            DefaultFontSize = 20
        };
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 40);
        AddChild(margin);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        margin.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 20);
        scroll.AddChild(column);
        var heading = Text("三国行纪", 44);
        heading.AddThemeColorOverride("font_color", Gold);
        column.AddChild(heading);
        column.AddChild(Text("G0 工具链试验", 24));
        column.AddChild(Text("固定种子抽样与存档恢复。战斗规则、武将和OL桌面尚未实现。", 20));

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Paper, ContentMarginLeft = 28, ContentMarginTop = 24,
            ContentMarginRight = 28, ContentMarginBottom = 24
        });
        column.AddChild(panel);
        var paper = new VBoxContainer();
        paper.AddThemeConstantOverride("separation", 16);
        panel.AddChild(paper);
        _status = Text("", 24, Ink);
        _hand = Text("", 22, Ink);
        paper.AddChild(_status);
        paper.AddChild(_hand);

        var buttons = new HFlowContainer();
        buttons.AddThemeConstantOverride("h_separation", 12);
        buttons.AddThemeConstantOverride("v_separation", 12);
        column.AddChild(buttons);
        _draw = ActionButton("抽取试验牌", DrawProbeCard);
        buttons.AddChild(_draw);
        buttons.AddChild(ActionButton("保存会话", () => { _store.Save(SavePath, _session.State); Refresh("已保存当前决策点。"); }));
        buttons.AddChild(ActionButton("恢复会话", () =>
        {
            var result = _store.Load(SavePath, _catalog.ContentHash);
            _session = new ProbeSession(result.State);
            Refresh(result.Message);
        }));
        buttons.AddChild(ActionButton("新建试验", () => { NewSession(); Refresh("已用种子42新建试验，已有存档保留。"); }));
        _feedback = Text("", 20);
        column.AddChild(_feedback);
        column.AddChild(Text("用 Tab 切换操作，空格或回车确认。试验不使用声音或动画。", 18));
        _draw.GrabFocus();
    }

    private static Label Text(string value, int size, Color? color = null)
    {
        var label = new Label { Text = value, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? new Color("F3EDDF"));
        return label;
    }

    private Button ActionButton(string text, Action action)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(170, 52), FocusMode = FocusModeEnum.All };
        button.AddThemeColorOverride("font_color", Ink);
        button.AddThemeColorOverride("font_hover_color", Ink);
        button.AddThemeColorOverride("font_pressed_color", Ink);
        button.AddThemeColorOverride("font_focus_color", Ink);
        button.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Paper });
        button.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = new Color("F3EDDF") });
        button.AddThemeStyleboxOverride("pressed", new StyleBoxFlat { BgColor = Gold });
        button.AddThemeStyleboxOverride("disabled", new StyleBoxFlat { BgColor = new Color("555B53") });
        button.AddThemeStyleboxOverride("focus", new StyleBoxFlat
        {
            BgColor = Colors.Transparent, BorderColor = Gold,
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            ExpandMarginLeft = 4, ExpandMarginTop = 4, ExpandMarginRight = 4, ExpandMarginBottom = 4
        });
        button.Pressed += () =>
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SaveException)
            { Refresh(ex.Message); }
        };
        return button;
    }

    private void DrawProbeCard()
    {
        var state = _session.State;
        var decision = state.Probe.PendingDecision;
        if (decision is null) return;
        var result = _session.Submit(new($"draw.{state.StateVersion}", state.StateVersion, decision.ControllerId, decision.RequestId));
        Refresh(result.Message);
    }

    private void Refresh(string message)
    {
        var state = _session.State;
        _status.Text = $"种子 42    已抽 {state.Probe.Hand.Length} 张    余牌 {state.Probe.DrawPile.Length} 张";
        _hand.Text = state.Probe.Hand.IsEmpty ? "尚未抽牌。选择“抽取试验牌”开始。"
            : string.Join("    ", state.Probe.Hand.Select(c => _catalog.Cards.First(d => d.Id == c.DefinitionId).Name));
        _feedback.Text = message;
        _draw.Disabled = state.Probe.PendingDecision is null;
    }

    private void RunSmoke()
    {
        var before = _session.State;
        var result = _session.Submit(new("smoke.draw", before.StateVersion, "player", before.Probe.PendingDecision!.RequestId));
        if (!result.Accepted) throw new InvalidOperationException(result.Message);
        var path = ProjectSettings.GlobalizePath("user://g0-probe/smoke-session.json");
        _store.Save(path, _session.State);
        var restored = _store.Load(path, _catalog.ContentHash).State;
        if (SnapshotCodec.StateHash(restored) != SnapshotCodec.StateHash(_session.State))
            throw new InvalidOperationException("Godot会话恢复哈希不一致。");
        GD.Print("G0_SMOKE_OK " + SnapshotCodec.StateHash(restored));
        GetTree().Quit();
    }
}
