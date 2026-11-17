using Godot;

namespace Sanguo.Client;

public partial class BattleScreen
{
    private AcceptDialog _resultDialog = null!;
    private Label _resultTitle = null!, _resultSummary = null!;
    private Button _resultContinue = null!;
    private bool _resultShown;

    private void BuildResultDialog()
    {
        _resultDialog = new AcceptDialog { Title = "战斗结算", Exclusive = true, WrapControls = false, OkButtonText = "查看牌桌", DialogCloseOnEscape = true };
        AddChild(_resultDialog);
        var content = new Control(); Place(content, 0, 0, 1, 1); _resultDialog.AddChild(content);
        _resultTitle = DesktopLabel("", 36, Gold); _resultTitle.HorizontalAlignment = HorizontalAlignment.Center;
        Place(_resultTitle, .05f, .06f, .95f, .20f); content.AddChild(_resultTitle);
        _resultSummary = Text("", 18, Paper); _resultSummary.AutowrapMode = TextServer.AutowrapMode.Off;
        _resultSummary.HorizontalAlignment = HorizontalAlignment.Center;
        Place(_resultSummary, .05f, .23f, .95f, .67f); content.AddChild(_resultSummary);
        _resultContinue = Button(CampaignSession is null ? "再来一局" : "返回行程", () =>
        {
            _resultDialog.Hide();
            if (CampaignSession is null) _setupDialog.PopupCentered(new Vector2I(420, 200));
            else { _aiTimer.Stop(); LeaveCampaignBattle?.Invoke(); }
        });
        DarkButton(_resultContinue, true); _resultContinue.CustomMinimumSize = new Vector2(180, 40);
        Place(_resultContinue, .31f, .75f, .69f, .84f); content.AddChild(_resultContinue);
        _resultDialog.Confirmed += RestoreDialogFocus; _resultDialog.Canceled += RestoreDialogFocus;
    }

    private void UpdateBattleResult()
    {
        if (_resultShown || _fastSmoke || _interaction.View.WinnerTeamId is null || MotionBusy || InspectionOpen) return;
        ShowBattleResult();
    }

    private void ShowBattleResult()
    {
        var view = _interaction.View;
        if (view.WinnerTeamId is not { } winner) return;
        _resultShown = true;
        _resultTitle.Text = winner == "a" ? "胜  利" : "败  北";
        var log = _interaction.ReadLog();
        var allies = view.Combatants.Where(u => u.TeamId == "a").Select(u => u.EntityId).ToHashSet(StringComparer.Ordinal);
        var damage = log.Where(e => e.Code == "damage.dealt" && allies.Contains(e.ActorId)).Sum(e => e.Amount);
        var recovery = log.Where(e => e.Code == "hp.recovered" && allies.Contains(e.ActorId)).Sum(e => e.Amount);
        _resultSummary.Text = $"历经 {view.TurnNumber} 回合\n\n我方造成伤害 {damage}　·　回复体力 {recovery}\n"
            + $"我方存活 {view.Combatants.Count(u => u.TeamId == "a" && u.Alive)} / {allies.Count}"
            + (CampaignSession is null ? "\n\n可以查看战斗记录，或开始新牌局。" : "\n\n返回行程记录战果，继续接下来的故事。");
        ResetHandMotion(); ClearCardInspection();
        ShowBattleDialog(_resultDialog);
    }
}
