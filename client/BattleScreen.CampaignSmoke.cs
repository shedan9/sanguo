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
        if (action.CardId is { } cardId)
            _handCards.Single(pair => pair.Value.InstanceId == cardId).Key.EmitSignal(BaseButton.SignalName.Pressed);
        if (action.TargetId is { } targetId && _seatButtons.TryGetValue(targetId, out var target) && !target.Disabled)
            target.EmitSignal(BaseButton.SignalName.Pressed);
        _optionActions.Single(pair => pair.Value == action).Key.EmitSignal(BaseButton.SignalName.Pressed);
        if (_interaction.Selection != action) throw new InvalidOperationException("战役玩家按钮选择失败。");
        _confirm.EmitSignal(BaseButton.SignalName.Pressed);
        if (_interaction.View.StateVersion != view.StateVersion + 1) throw new InvalidOperationException("战役玩家确认未推进牌局。");
    }
}
