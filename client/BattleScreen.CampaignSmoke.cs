using Godot;
using Sanguo.AI;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

public partial class BattleScreen
{
    internal void PauseCampaignSmoke() => _aiTimer.Stop();
    internal void CampaignSmokeStep(bool passive)
    {
        _aiTimer.Stop();
        if (_interaction.WaitingForAi)
        {
            var result = _interaction.AdvanceAi(v => BasicBattleAi.Choose(v)?.Action);
            if (result?.Accepted != true) throw new InvalidOperationException("战役AI提交失败。");
            ClearSelection(); Refresh("战役回归推进敌方。");
            return;
        }
        var view = _interaction.View;
        var action = passive ? view.LegalActions.FirstOrDefault(a => a.Kind is BattleActionKind.EndPlay or BattleActionKind.Pass or BattleActionKind.Discard)
            ?? BasicBattleAi.Choose(view)!.Action : BasicBattleAi.Choose(view)!.Action;
        DriveAction(action);
        if (_interaction.View.StateVersion <= view.StateVersion) throw new InvalidOperationException("战役玩家确认未推进牌局。");
    }
}
