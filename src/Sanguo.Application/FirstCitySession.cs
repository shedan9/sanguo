using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Sanguo.BattleCore.Basic;
using Sanguo.CampaignCore;
using Sanguo.CampaignCore.FirstCity;

namespace Sanguo.Application;

public sealed record FirstCityCommand(string CommandId, long ExpectedVersion, string Kind, string? Value = null);
public sealed record FirstCityReceipt(FirstCityCommand Command, long Version);
public sealed record CampaignBattleRecord(int Stage, int Attempt, ImmutableArray<string> Deployment, BasicBattleSessionState FinalState);
public sealed record FirstCitySessionState(string EngineVersion, string ContentHash, CampaignProgress Progress,
    BasicBattleSessionState? Battle, ImmutableArray<string> Deployment,
    ImmutableArray<CampaignBattleRecord> Results, ImmutableArray<FirstCityReceipt> Receipts);

/// <summary>首城战役单写入口；结算只读取自己持有的真实牌局，不接受调用方传入胜负或奖励。</summary>
public sealed class FirstCitySession
{
    public const string EngineVersion = "0.1.0-first-city-development";
    private FirstCitySessionState _state;
    private BasicBattleSession? _battle;
    public FirstCitySessionState Snapshot => _state with { Battle = _battle?.State };
    public CampaignProgress Progress => _state.Progress;
    public BasicBattleSession? ActiveBattle => _battle;
    public long Version => _state.Receipts.Length;
    public bool OwnsFirstCity => Progress.Phase == CampaignPhase.Complete;

    public FirstCitySession(FirstCitySessionState state)
    {
        Validate(state);
        _state = state;
        _battle = state.Battle is null ? null : new(state.Battle);
    }

    public static FirstCitySession Create(string campaignId, Origin origin, ulong seed = 42)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        return new(new(EngineVersion, FirstCityScenario.ContentHash,
            new(campaignId, origin, seed, 0, 1, CampaignPhase.Choice, [FirstCityStory.Protagonist], []), null, [], [], []));
    }

    public CommandResult Submit(FirstCityCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.CommandId)) return Reject("command.invalid", "命令ID不能为空。");
        var previous = _state.Receipts.FirstOrDefault(r => r.Command.CommandId == command.CommandId);
        if (previous is not null)
            return previous.Command == command ? new(true, true, "command.duplicate", "已经提交。", previous.Version)
                : Reject("command.id_conflict", "命令ID已用于其他输入。");
        if (command.ExpectedVersion != Version) return Reject("state.stale", "战役状态已更新，请重新选择。");
        var progress = Progress;
        var next = _state;
        switch (command.Kind)
        {
            case "choose" when progress.Phase == CampaignPhase.Choice:
                if (!FirstCityStory.Event(progress.Origin, progress.Stage).Choices.Any(c => c.Id == command.Value))
                    return Reject("choice.invalid", "当前剧情选项不存在。");
                progress = progress with { Phase = CampaignPhase.Deployment, Choices = progress.Choices.Add(new(progress.Stage, command.Value!)) };
                break;
            case "recruit" when progress.Stage == 1 && progress.Phase is CampaignPhase.Choice or CampaignPhase.Deployment:
                if (command.Value != FirstCityStory.Volunteer || progress.Roster.Contains(FirstCityStory.Volunteer))
                    return Reject("recruit.invalid", "此人物尚不可招募或已在名册中。");
                progress = progress with { Roster = progress.Roster.Add(FirstCityStory.Volunteer) };
                break;
            case "start" when progress.Phase == CampaignPhase.Deployment:
                var deployment = (command.Value ?? "").Split('|').ToImmutableArray();
                if (deployment.Length is < 1 or > 4 || deployment.Distinct().Count() != deployment.Length
                    || deployment.Any(id => !progress.Roster.Contains(id)))
                    return Reject("deployment.invalid", "请选择名册中1至4名不同人物出战。");
                _battle = FirstCityScenario.Create(progress, deployment);
                next = next with { Deployment = deployment };
                progress = progress with { Phase = CampaignPhase.Battle };
                break;
            case "resolve" when progress.Phase == CampaignPhase.Battle:
                if (command.Value is not null) return Reject("result.invalid", "战果只能从当前牌局读取。");
                if (_battle?.State.Battle is not { Phase: BattlePhase.Finished, WinnerTeamId: not null } finished)
                    return Reject("battle.unfinished", "本场牌局尚未结束，不能推进剧情或占城。");
                next = next with { Results = next.Results.Add(new(progress.Stage, progress.Attempt, next.Deployment, _battle.State)), Deployment = [] };
                if (finished.WinnerTeamId == "a")
                {
                    var stage = progress.Stage + 1;
                    progress = progress with { Stage = stage, Attempt = 1, Phase = stage == 4 ? CampaignPhase.Complete
                        : stage == 3 ? CampaignPhase.Deployment : CampaignPhase.Choice };
                }
                else progress = progress with { Phase = CampaignPhase.Defeat };
                _battle = null;
                break;
            case "retry" when progress.Phase == CampaignPhase.Defeat:
                progress = progress with { Phase = CampaignPhase.Deployment, Attempt = checked(progress.Attempt + 1) };
                break;
            default: return Reject("campaign.illegal", "当前阶段不能执行此操作。");
        }
        _state = next with { Progress = progress, Battle = null, Receipts = next.Receipts.Add(new(command, Version + 1)) };
        return new(true, false, "command.accepted", "战役进度已更新。", Version);
    }

    private CommandResult Reject(string code, string message) => new(false, false, code, message, Version);

    public static void Validate(FirstCitySessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var p = state.Progress;
        if (state.EngineVersion != EngineVersion || state.ContentHash != FirstCityScenario.ContentHash || p is null
            || string.IsNullOrWhiteSpace(p.CampaignId) || !Enum.IsDefined(p.Origin) || !Enum.IsDefined(p.Phase)
            || p.Stage is < 0 or > 4 || p.Attempt < 1 || p.Roster.IsDefaultOrEmpty || p.Choices.IsDefault
            || state.Deployment.IsDefault || state.Results.IsDefault || state.Receipts.IsDefault)
            throw new ArgumentException("首城战役版本、进度或集合无效。");
        if (p.Roster[0] != FirstCityStory.Protagonist || p.Roster.Length > 2 || p.Roster.Distinct().Count() != p.Roster.Length
            || p.Roster.Any(id => id is not (FirstCityStory.Protagonist or FirstCityStory.Volunteer)))
            throw new ArgumentException("首城名册无效。");
        var requiredChoices = Math.Min(p.Stage + (p.Phase == CampaignPhase.Choice ? 0 : 1), 3);
        if (p.Choices.Length != requiredChoices || p.Choices.Where((c, i) => c is null || c.Stage != i
            || !FirstCityStory.Event(p.Origin, i).Choices.Any(option => option.Id == c.ChoiceId)).Any()
            || (p.Phase == CampaignPhase.Choice && p.Stage > 2)
            || (p.Phase == CampaignPhase.Complete) != (p.Stage == 4))
            throw new ArgumentException("剧情选择、阶段与首城归属不一致。");
        var stage = 0;
        var attempt = 1;
        foreach (var result in state.Results)
        {
            if (result is null || result.Stage != stage || result.Attempt != attempt || stage > 3)
                throw new ArgumentException("战果顺序或重试编号无效。");
            ValidateBattle(result.FinalState, p with { Stage = stage, Attempt = attempt }, result.Deployment);
            if (result.FinalState.Battle.Phase != BattlePhase.Finished || result.FinalState.Battle.WinnerTeamId is null)
                throw new ArgumentException("战役战果必须来自已结束的牌局。");
            if (result.FinalState.Battle.WinnerTeamId == "a") { stage++; attempt = 1; }
            else attempt++;
        }
        if (stage != p.Stage || p.Attempt != attempt - (p.Phase == CampaignPhase.Defeat ? 1 : 0)
            || p.Phase == CampaignPhase.Defeat && (state.Results.IsEmpty || state.Results[^1].FinalState.Battle.WinnerTeamId != "b"))
            throw new ArgumentException("战果记录与当前战役进度不一致。");
        if (p.Phase == CampaignPhase.Battle)
        {
            if (state.Battle is null) throw new ArgumentException("进行中的战役缺少牌局。");
            ValidateBattle(state.Battle, p, state.Deployment);
        }
        else if (state.Battle is not null || !state.Deployment.IsEmpty) throw new ArgumentException("非战斗阶段不能残留活动牌局。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < state.Receipts.Length; i++)
        {
            var receipt = state.Receipts[i];
            if (receipt?.Command is not { } command || string.IsNullOrWhiteSpace(command.CommandId) || !ids.Add(command.CommandId)
                || command.ExpectedVersion != i || receipt.Version != i + 1
                || command.Kind is not ("choose" or "recruit" or "start" or "resolve" or "retry"))
                throw new ArgumentException("战役命令记录无效。");
        }
        var choices = state.Receipts.Where(r => r.Command.Kind == "choose").Select(r => r.Command.Value);
        if (!choices.SequenceEqual(p.Choices.Select(c => c.ChoiceId))
            || state.Receipts.Count(r => r.Command.Kind == "resolve") != state.Results.Length
            || state.Receipts.Count(r => r.Command.Kind == "start") != state.Results.Length + (state.Battle is null ? 0 : 1)
            || state.Receipts.Count(r => r.Command.Kind == "retry") != state.Results.Count(r => r.FinalState.Battle.WinnerTeamId == "b") - (p.Phase == CampaignPhase.Defeat ? 1 : 0)
            || state.Receipts.Count(r => r.Command.Kind == "recruit" && r.Command.Value == FirstCityStory.Volunteer) != p.Roster.Length - 1)
            throw new ArgumentException("战役命令与结果或永久名册不一致。");
        // 重新执行全部已提交命令，拒绝仅修改体力、胜方、名册或城池归属的自洽伪造状态。
        if (!state.Receipts.IsEmpty)
        {
            var replay = Create(p.CampaignId, p.Origin, p.Seed);
            var resultIndex = 0;
            foreach (var receipt in state.Receipts)
            {
                if (receipt.Command.Kind == "resolve")
                {
                    if (resultIndex >= state.Results.Length) throw new ArgumentException("缺少战果记录。");
                    ReplayBattle(replay._battle, state.Results[resultIndex++].FinalState);
                }
                if (!replay.Submit(receipt.Command).Accepted) throw new ArgumentException("战役命令无法按原顺序重放。");
            }
            if (state.Battle is not null) ReplayBattle(replay._battle, state.Battle);
            if (JsonSerializer.Serialize(replay.Snapshot) != JsonSerializer.Serialize(state))
                throw new ArgumentException("战役快照与命令重放结果不一致。");
        }
    }

    private static void ReplayBattle(BasicBattleSession? replay, BasicBattleSessionState target)
    {
        if (replay is null) throw new ArgumentException("战果没有对应的活动牌局。");
        foreach (var receipt in target.Receipts)
            if (!replay.Submit(receipt.Command).Accepted) throw new ArgumentException("战斗命令无法合法重放。");
        if (JsonSerializer.Serialize(replay.State) != JsonSerializer.Serialize(target))
            throw new ArgumentException("战斗快照与合法命令重放结果不一致。");
    }

    private static void ValidateBattle(BasicBattleSessionState battle, CampaignProgress p, ImmutableArray<string> deployment)
    {
        if (battle is null || deployment.IsDefaultOrEmpty || deployment.Distinct().Count() != deployment.Length
            || deployment.Any(id => !p.Roster.Contains(id))) throw new ArgumentException("战役出战名册无效。");
        BasicBattleSession.Validate(battle);
        var initial = FirstCityScenario.Create(p, deployment).State;
        var actual = battle.Battle;
        if (battle.ContentHash != initial.ContentHash || actual.BattleId != initial.Battle.BattleId
            || !actual.CardInventory.SequenceEqual(initial.Battle.CardInventory)
            || !actual.Combatants.Select(u => (u.EntityId, u.ControllerId, u.TeamId, u.SeatId, u.MaxHp, u.Profile))
                .SequenceEqual(initial.Battle.Combatants.Select(u => (u.EntityId, u.ControllerId, u.TeamId, u.SeatId, u.MaxHp, u.Profile))))
            throw new ArgumentException("牌局不属于当前战役、遭遇或出战队伍。");
        var cards = actual.DrawPile.Concat(actual.Processing).Concat(actual.DiscardPile).Concat(actual.Combatants.SelectMany(u => u.Hand));
        var originalCards = initial.Battle.DrawPile.Concat(initial.Battle.Combatants.SelectMany(u => u.Hand));
        if (!cards.OrderBy(c => c.InstanceId, StringComparer.Ordinal).SequenceEqual(originalCards.OrderBy(c => c.InstanceId, StringComparer.Ordinal)))
            throw new ArgumentException("战役牌组实体或牌面不匹配。");
    }
}

public static class FirstCityScenario
{
    private static readonly ImmutableArray<BattleCard> Cards = Enumerable.Range(0, 80).Select(i => new BattleCard(
        $"campaign.development.{i:D3}", (i % 16) switch
        {
            0 or 1 => BasicCardKind.Dodge, 2 => BasicCardKind.Peach, 3 => BasicCardKind.Duel,
            4 => BasicCardKind.ExNihilo, 5 or 6 => BasicCardKind.Nullification,
            7 => BasicCardKind.Dismantlement, 8 => BasicCardKind.Snatch, _ => BasicCardKind.Slash
        }, new[] { "spade", "heart", "club", "diamond" }[i % 4], i % 13 + 1)).ToImmutableArray();
    public static string ContentHash { get; } = Hash(new
    {
        FirstCityStory.Version, BasicBattle.RulesetId, Cards,
        Stories = Enum.GetValues<Origin>().SelectMany(origin => Enumerable.Range(0, 3).Select(stage => FirstCityStory.Event(origin, stage))).ToArray(),
        Units = Enum.GetValues<Origin>().SelectMany(origin => Enumerable.Range(0, 4).Select(stage => Units(origin, stage, [FirstCityStory.Protagonist, FirstCityStory.Volunteer]))).ToArray(),
        Skills = BattleSkills.For(BattleProfile.ProtagonistFenjiI), City = FirstCityStory.CityId,
        Epilogues = Enum.GetValues<Origin>().SelectMany(origin => new[] { "a", "b" }.Select(choice => FirstCityStory.Epilogue(
            new("hash", origin, 0, 4, 1, CampaignPhase.Complete, [], [new(0, choice)])))).ToArray()
    });
    public static BasicBattleSession Create(CampaignProgress progress, ImmutableArray<string> deployment)
    {
        var units = Units(progress.Origin, progress.Stage, deployment);
        var hash = Hash(new { ContentHash, progress.Origin, progress.Stage, Deployment = deployment, Units = units });
        return BasicBattleSession.Create($"{progress.CampaignId}.encounter.{progress.Stage}.attempt.{progress.Attempt}", units, Cards, hash,
            unchecked(progress.Seed + (ulong)progress.Stage * 1009 + (ulong)(progress.Attempt - 1) * 7919));
    }
    private static CombatantSetup[] Units(Origin origin, int stage, ImmutableArray<string> deployment)
    {
        var players = deployment.Select(id => new CombatantSetup(id, "player", "a", Profile:
            id == FirstCityStory.Protagonist && origin == Origin.Damage ? BattleProfile.ProtagonistFenjiI : BattleProfile.Generic));
        var enemies = Enumerable.Range(0, 1).Select(i => new CombatantSetup($"guard.{i + 1}", "opponent", "b", MaxHp: stage == 0 ? 3 : stage == 3 ? 5 : 4));
        return players.Concat(enemies).ToArray();
    }
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
