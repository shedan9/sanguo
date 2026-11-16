using Godot;
using Sanguo.Application;
using Sanguo.CampaignCore;
using Sanguo.CampaignCore.FirstCity;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Client;

public partial class CampaignScreen
{
    private async Task RunCampaignSmoke(string[] args)
    {
        try
        {
            if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) throw new ArgumentException("用法：--campaign-smoke <专用绝对目录>");
            _smokeDirectory = Path.Combine(args[1], "run-" + Guid.NewGuid().ToString("N"));
            var games = 0;
            var commands = 0;
            var defeats = 0;
            foreach (var size in new[] { new Vector2I(960, 600), new Vector2I(1280, 720) })
            foreach (var origin in Enum.GetValues<Origin>())
            {
                GetWindow().Size = size;
                _session = null; _origin.Selected = (int)origin; Render();
                _origin.GrabFocus(); await KeyTap(Key.Tab); await KeyTap(Key.Enter);
                if (_session is null) throw new InvalidOperationException("Tab与Enter未能新建战役。");
                if (games == 0)
                {
                    var hash = FirstCitySnapshotCodec.StateHash(_session.Snapshot);
                    Press("新行程"); await Frame();
                    if (!_newDialog.Visible) throw new InvalidOperationException("新行程未确认离开进度。");
                    await KeyTap(Key.Escape);
                    Press("自由战斗"); await Frame();
                    if (!_leaveDialog.Visible) throw new InvalidOperationException("切换战斗未确认离开进度。");
                    await KeyTap(Key.Escape);
                    if (_newDialog.Visible || _leaveDialog.Visible || hash != FirstCitySnapshotCodec.StateHash(_session.Snapshot))
                        throw new InvalidOperationException("关闭离开确认改变了战役。");
                }
                var forceDefeat = games == 0;
                var checkedResume = false;
                var guard = 0;
                while (!_session!.OwnsFirstCity)
                {
                    if (++guard > 12000 || _session.Progress.Attempt > 30) throw new InvalidOperationException("战役客户端步数保护触发。");
                    var p = _session.Progress;
                    if (_battleScreen is not null)
                    {
                        _battleScreen.PauseCampaignSmoke();
                        if (_session.ActiveBattle!.State.Battle.PendingDecision is not null)
                        {
                            _battleScreen.CampaignSmokeStep(forceDefeat); commands++;
                            if (!checkedResume && _session.ActiveBattle.State.Battle.StateVersion == 12)
                            {
                                var hash = FirstCitySnapshotCodec.StateHash(_session.Snapshot);
                                Press("保存行程", _battleScreen);
                                Press("恢复行程", _battleScreen); await Frame();
                                if (_battleScreen is not null || hash != FirstCitySnapshotCodec.StateHash(_session.Snapshot))
                                    throw new InvalidOperationException("战役恢复没有回到相同的暂停点：" + _feedback.Text);
                                Press("继续牌局"); _battleScreen!.PauseCampaignSmoke(); checkedResume = true;
                            }
                            if (commands % 20 == 0) await Frame();
                            continue;
                        }
                        Press("返回行程", _battleScreen); await Frame();
                        if (_session.Progress.Phase == CampaignPhase.Defeat) { defeats++; forceDefeat = false; }
                        continue;
                    }
                    switch (p.Phase)
                    {
                        case CampaignPhase.Choice:
                            if (p.Stage == 1 && games % 2 == 0 && !p.Roster.Contains(FirstCityStory.Volunteer))
                                Press("邀请乡勇同行");
                            else
                            {
                                var choice = FirstCityStory.Event(p.Origin, p.Stage).Choices[games % 2];
                                var button = FindButton(this, choice.Text);
                                button.GrabFocus(); await KeyTap(Key.Enter);
                            }
                            break;
                        case CampaignPhase.Deployment:
                            // 主角可不上场；另一组走未招募的独行路径。
                            if (p.Roster.Length == 2 && games % 2 == 0) _deployment[FirstCityStory.Protagonist].ButtonPressed = false;
                            Press("进入牌局"); _battleScreen!.PauseCampaignSmoke(); break;
                        case CampaignPhase.Defeat: Press("重新编队挑战"); break;
                        default: throw new InvalidOperationException("客户端未显示当前阶段操作。");
                    }
                    await Frame();
                }
                if (_session.Snapshot.Results.Count(r => r.FinalState.Battle.WinnerTeamId == "a") != 4 || !checkedResume)
                    throw new InvalidOperationException("首城缺少四场胜利或中途恢复检查。");
                Press("保存行程");
                var finalHash = FirstCitySnapshotCodec.StateHash(_session.Snapshot);
                Press("恢复行程"); await Frame();
                if (!_session.OwnsFirstCity || FirstCitySnapshotCodec.StateHash(_session.Snapshot) != finalHash)
                    throw new InvalidOperationException("首城归属恢复不一致。");
                var rect = GetViewportRect();
                foreach (var control in Controls(_margin).Where(c => c.IsVisibleInTree() && c is BaseButton))
                    if (!rect.Encloses(control.GetGlobalRect())) throw new InvalidOperationException("固定战役控件越出窗口：" + control.Name);
                games++;
                GD.Print($"CAMPAIGN_CLIENT_GAME_OK origin={origin} size={size} games={games} commands={commands}");
            }
            GD.Print($"CAMPAIGN_CLIENT_SMOKE_OK games={games} commands={commands} defeats={defeats} windows=2");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
    private async Task CaptureCampaignPreview(string[] args)
    {
        try
        {
            if (args.Length is < 2 or > 3) throw new ArgumentException("用法：--campaign-preview <PNG绝对路径> [战役存档路径]");
            if (args.Length == 3) { _session = new(FirstCitySnapshotCodec.Decode(File.ReadAllBytes(args[2]))); _origin.Selected = (int)_session.Progress.Origin; Render(); }
            else BeginRun();
            if (args[1].Contains("960", StringComparison.Ordinal)) GetWindow().Size = new(960, 600);
            await Frame(); await Frame();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var error = GetViewport().GetTexture().GetImage().SavePng(args[1]);
            if (error != Error.Ok) throw new IOException("战役截图保存失败：" + error);
            GD.Print("CAMPAIGN_PREVIEW_OK"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
    private static IEnumerable<Control> Controls(Node parent)
    {
        foreach (var node in parent.GetChildren())
        {
            if (node is Control control) yield return control;
            foreach (var child in Controls(node)) yield return child;
        }
    }
    private static Button FindButton(Node parent, string text) => Controls(parent).OfType<Button>().Single(b => b.Text == text && b.IsVisibleInTree());
    private void Press(string text, Node? parent = null) => FindButton(parent ?? this, text).EmitSignal(BaseButton.SignalName.Pressed);
    private async Task Frame() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private async Task KeyTap(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true }); await Frame();
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false }); await Frame();
    }
}
