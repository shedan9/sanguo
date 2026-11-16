using System.Globalization;
using System.Text.Json;
using Sanguo.AI;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.CampaignCore;
using Sanguo.CampaignCore.FirstCity;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Simulator;

public static class FirstCityProbe
{
    public static int Run(string[] args)
    {
        if (args.Length != 3 || args[0] == "--campaign-first-city" && !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            Console.Error.WriteLine("用法：--campaign-first-city <种子> <输出目录> 或 --campaign-resume <checkpoint.json> <输出目录>");
            return 2;
        }
        try
        {
            if (args[0] == "--campaign-resume")
                Complete(new(FirstCitySnapshotCodec.Decode(File.ReadAllBytes(args[1]))), args[2], checkpoint: false);
            else
                foreach (var origin in Enum.GetValues<Origin>())
                    Complete(FirstCitySession.Create("campaign.first-city", origin, ulong.Parse(args[1], CultureInfo.InvariantCulture)),
                        Path.Combine(args[2], origin.ToString().ToLowerInvariant()), checkpoint: true);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or SaveException or ArgumentException or InvalidOperationException)
        { Console.Error.WriteLine("首城战役验证失败：" + ex.Message); return 1; }
    }
    private static void Complete(FirstCitySession session, string directory, bool checkpoint)
    {
        Directory.CreateDirectory(directory);
        var steps = 0;
        var restores = 0;
        var store = new FirstCitySaveStore();
        var slot = Path.Combine(directory, "runs", Guid.NewGuid().ToString("N"), "session.json");
        while (!session.OwnsFirstCity)
        {
            if (++steps > 12000 || session.Progress.Attempt > 30) throw new InvalidOperationException("首城战役超过步数或重试保护，未判定通关。");
            var p = session.Progress;
            if (checkpoint && p.Stage == 2 && session.ActiveBattle?.State.Battle.StateVersion == 12)
            {
                File.WriteAllBytes(Path.Combine(directory, "checkpoint.json"), FirstCitySnapshotCodec.Encode(session.Snapshot));
                checkpoint = false;
            }
            if (session.ActiveBattle is { } battle && battle.State.Battle.PendingDecision is { } decision)
            {
                var choice = BasicBattleAi.Choose(BasicBattle.Observe(battle.State.Battle, decision.ControllerId))!;
                var command = new BattleCommand($"campaign.battle.{battle.State.Battle.StateVersion}", battle.State.Battle.StateVersion,
                    decision.ControllerId, decision.RequestId, decision.ActorId, choice.Action);
                var result = battle.Submit(command);
                if (!result.Accepted || !battle.Submit(command).Duplicate) throw new InvalidOperationException("战役牌局命令失败。");
                // 战斗中每12步恢复；叙事、阶段切换和所有战果点另外逐点恢复。
                if (battle.State.Battle.StateVersion % 12 != 0) continue;
            }
            else
            {
                var (kind, value) = p.Phase switch
                {
                    CampaignPhase.Choice when p.Stage == 1 && !p.Roster.Contains(FirstCityStory.Volunteer) => ("recruit", FirstCityStory.Volunteer),
                    CampaignPhase.Choice => ("choose", p.Origin == Origin.Control ? "b" : "a"),
                    CampaignPhase.Deployment => ("start", string.Join('|', p.Roster)),
                    CampaignPhase.Battle => ("resolve", (string?)null),
                    CampaignPhase.Defeat => ("retry", (string?)null),
                    _ => throw new InvalidOperationException("未知战役状态。")
                };
                var command = new FirstCityCommand($"campaign.command.{session.Version}", session.Version, kind, value);
                if (!session.Submit(command).Accepted || !session.Submit(command).Duplicate) throw new InvalidOperationException("战役推进命令失败。");
            }
            var before = FirstCitySnapshotCodec.StateHash(session.Snapshot);
            store.Save(slot, session.Snapshot);
            session = new(store.Load(slot).State);
            if (FirstCitySnapshotCodec.StateHash(session.Snapshot) != before) throw new InvalidOperationException("战役恢复状态不一致。");
            restores++;
        }
        if (checkpoint) throw new InvalidOperationException("未生成跨进程恢复检查点。");
        var state = session.Snapshot;
        File.WriteAllBytes(Path.Combine(directory, "final.json"), FirstCitySnapshotCodec.Encode(state));
        var report = new { Origin = state.Progress.Origin.ToString(), City = FirstCityStory.CityId, Victories = state.Results.Count(r => r.FinalState.Battle.WinnerTeamId == "a"),
            Defeats = state.Results.Count(r => r.FinalState.Battle.WinnerTeamId == "b"), Restores = restores,
            Commands = state.Results.Sum(r => r.FinalState.Receipts.Length), Hash = FirstCitySnapshotCodec.StateHash(state), Status = "development-only" };
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"FIRST_CITY_OK {report.Origin} wins={report.Victories} defeats={report.Defeats} restores={restores} hash={report.Hash}");
    }
}
