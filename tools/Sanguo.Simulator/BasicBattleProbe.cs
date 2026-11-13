using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Sanguo.Application;
using Sanguo.BattleCore.Basic;
using Sanguo.Infrastructure.Saves;

namespace Sanguo.Simulator;

/// <summary>公开的合成场景及固定操作脚本，不作为正式牌组或战术AI。</summary>
internal static class BasicBattleProbe
{
    private static readonly CombatantSetup[] Units =
    [
        new("generic.a1", "player", "a"), new("generic.b1", "opponent", "b"),
        new("generic.a2", "player", "a"), new("generic.b2", "opponent", "b")
    ];
    private static readonly BattleCard[] Cards = Enumerable.Range(0, 64).Select(i => new BattleCard(
        $"basic.test.{i:D3}", (i % 8) switch
        {
            0 or 1 => BasicCardKind.Dodge,
            2 => BasicCardKind.Peach,
            _ => BasicCardKind.Slash
        }, new[] { "spade", "heart", "club", "diamond" }[i % 4], i % 13 + 1)).ToArray();
    private static readonly BattleCard[] TrickCards = Enumerable.Range(0, 64).Select(i => new BattleCard(
        $"tricks.test.{i:D3}", (i % 16) switch
        {
            0 or 1 => BasicCardKind.Dodge, 2 => BasicCardKind.Peach,
            3 or 4 => BasicCardKind.Duel, 5 => BasicCardKind.ExNihilo,
            6 or 7 => BasicCardKind.Nullification, _ => BasicCardKind.Slash
        }, new[] { "spade", "heart", "club", "diamond" }[i % 4], i % 13 + 1)).ToArray();
    private static readonly BattleCard[] HandTrickCards = TrickCards.Select((c, i) => c with
    {
        InstanceId = $"handtricks.test.{i:D3}",
        Kind = (i % 16) switch { 8 => BasicCardKind.Dismantlement, 9 => BasicCardKind.Snatch, _ => c.Kind }
    }).ToArray();

    public static int Run(string[] args)
    {
        var resume = args[0] is "--battle-resume" or "--battle-tricks-resume" or "--battle-hand-tricks-resume";
        var handTricks = args[0] is "--battle-hand-tricks" or "--battle-hand-tricks-resume";
        var tricks = handTricks || args[0] is "--battle-tricks" or "--battle-tricks-resume";
        if ((!resume && (args.Length > 3 || (args.Length > 1 && !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))))
            || (resume && args.Length is < 2 or > 3))
        {
            Console.Error.WriteLine("用法：--battle-basic、--battle-tricks 或 --battle-hand-tricks [非负种子] [证据目录]；恢复使用对应的 --battle-resume、--battle-tricks-resume 或 --battle-hand-tricks-resume <快照> [证据目录]");
            return 2;
        }
        try
        {
            var seed = !resume && args.Length > 1 ? ulong.Parse(args[1], CultureInfo.InvariantCulture) : 42UL;
            var cards = handTricks ? HandTrickCards : tricks ? TrickCards : Cards;
            var contentHash = Convert.ToHexStringLower(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { BasicBattle.RulesetId, Units, Cards = cards })));
            var directory = Path.GetFullPath(args.Length > 2 ? args[2] : Path.Combine("build", handTricks ? "hand-trick-battle" : tricks ? "trick-battle" : "basic-battle"));
            Directory.CreateDirectory(directory);
            var session = resume
                ? new BasicBattleSession(BasicBattleSnapshotCodec.Decode(File.ReadAllBytes(args[1]), contentHash))
                : BasicBattleSession.Create(handTricks ? "battle.hand-trick-probe" : tricks ? "battle.trick-probe" : "battle.basic-probe", Units, cards, contentHash, seed);
            var steps = 0;
            var checkpoints = 0;
            var maxCounterDepth = 0;
            var counterSnapshotWritten = false;
            var duelSnapshotWritten = false;
            var handSnapshotWritten = false;
            var stepsByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            while (session.State.Battle.PendingDecision is not null)
            {
                if (++steps > 4000) throw new InvalidOperationException("步数保护触发，场景未完成；不得视为胜利。");
                var before = session.State.Battle;
                var kind = before.Frames.IsEmpty ? before.Phase.ToString() : before.Frames[^1].Kind.ToString();
                stepsByKind[kind] = stepsByKind.GetValueOrDefault(kind) + 1;
                var path = Path.Combine(directory, "checkpoint.json");
                var bytes = BasicBattleSnapshotCodec.Encode(session.State);
                File.WriteAllBytes(path, bytes);
                if (!before.Frames.IsEmpty)
                {
                    maxCounterDepth = Math.Max(maxCounterDepth, before.Frames[^1].ChainDepth);
                    if (!counterSnapshotWritten && before.Frames[^1].Kind == EffectKind.Nullification && before.Frames[^1].ChainDepth >= 2)
                    {
                        File.WriteAllBytes(Path.Combine(directory, "counter-chain.json"), bytes);
                        counterSnapshotWritten = true;
                    }
                    if (!duelSnapshotWritten && before.Frames[^1].Kind == EffectKind.Duel)
                    {
                        File.WriteAllBytes(Path.Combine(directory, "duel-response.json"), bytes);
                        duelSnapshotWritten = true;
                    }
                    if (!handSnapshotWritten && before.Frames[^1].Kind == EffectKind.SelectHandCard)
                    {
                        File.WriteAllBytes(Path.Combine(directory, "hand-choice.json"), bytes);
                        handSnapshotWritten = true;
                    }
                }
                var restored = new BasicBattleSession(BasicBattleSnapshotCodec.Decode(File.ReadAllBytes(path), contentHash));
                var action = Choose(BasicBattle.Observe(before, before.PendingDecision!.ControllerId));
                var decision = before.PendingDecision;
                var command = new BattleCommand($"basic.command.{before.StateVersion}", before.StateVersion,
                    decision.ControllerId, decision.RequestId, decision.ActorId, action);
                if (!session.Submit(command).Accepted || !restored.Submit(command).Accepted)
                    throw new InvalidOperationException("场景脚本提交了不合法动作。");
                var stateHash = BasicBattleSnapshotCodec.StateHash(session.State);
                if (stateHash != BasicBattleSnapshotCodec.StateHash(restored.State))
                    throw new InvalidOperationException("基础战斗恢复后状态哈希不一致。");
                if (!restored.Submit(command).Duplicate || stateHash != BasicBattleSnapshotCodec.StateHash(restored.State))
                    throw new InvalidOperationException("基础战斗恢复后的重复命令未保持幂等。");
                checkpoints++;
            }
            var state = session.State.Battle;
            var finalHash = BasicBattleSnapshotCodec.StateHash(session.State);
            if (handTricks && !resume && (!handSnapshotWritten
                || !state.Events.Any(e => e.Code == "card.snatched") || !state.Events.Any(e => e.Code == "card.dismantled")))
                throw new InvalidOperationException("八牌场景未覆盖拆顺与暗牌选择，不能生成通过报告。");
            File.WriteAllBytes(Path.Combine(directory, "final.json"), BasicBattleSnapshotCodec.Encode(session.State));
            File.WriteAllText(Path.Combine(directory, "events.json"), JsonSerializer.Serialize(state.Events, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
            {
                rulesetId = state.RulesetId, contentHash, stateHash = finalHash,
                stateVersion = state.StateVersion, turnNumber = state.TurnNumber, winnerTeamId = state.WinnerTeamId,
                cardCount = state.CardInventory.Length,
                restoredCheckpoints = checkpoints, stepsByKind, maxCounterDepth, formalOlAccepted = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{(handTricks ? "HAND_TRICK_BATTLE_OK" : tricks ? "TRICK_BATTLE_OK" : "BASIC_BATTLE_OK")} {finalHash}");
            Console.WriteLine($"通用角色2对2：{state.TurnNumber}个角色回合，{state.StateVersion}条命令，胜方{state.WinnerTeamId}；本次{checkpoints}个决策点恢复与重复命令检查通过。");
            Console.WriteLine($"证据：{directory}");
            Console.WriteLine($"{(handTricks ? "八种牌含手牌拆顺" : tricks ? "六种牌含决斗、无中生有、无懈" : "三种基本牌")}的开发场景；装备、判定、OL武将与战斗界面尚未接入，M1未验收。");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SaveException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"基础战斗试验失败：{ex.Message}");
            return 1;
        }
    }

    private static BattleAction Choose(BattleView view)
    {
        var actions = view.LegalActions;
        var actor = view.Combatants.Single(u => u.EntityId == view.PendingDecision!.ActorId);
        // 决策仅使用当前控制者视图；未知牌和牌堆顺序不参与选择。
        return actions.FirstOrDefault(a => a.Kind == BattleActionKind.Dodge)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.SelectHandCard)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.RespondSlash)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Nullify && view.Response is { } response
                && view.Combatants.Single(u => u.EntityId == response.SourceId).TeamId != actor.TeamId)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Peach && view.Combatants.Single(u => u.EntityId == a.TargetId).TeamId == actor.TeamId)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.ExNihilo)
            ?? actions.FirstOrDefault(a => a.Kind is BattleActionKind.Snatch or BattleActionKind.Dismantlement
                && view.Combatants.Single(u => u.EntityId == a.TargetId).TeamId != actor.TeamId)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Duel && view.Combatants.Single(u => u.EntityId == a.TargetId).TeamId != actor.TeamId)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Slash && view.Combatants.Single(u => u.EntityId == a.TargetId).TeamId != actor.TeamId)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Pass)
            ?? actions.FirstOrDefault(a => a.Kind == BattleActionKind.Discard)
            ?? new(BattleActionKind.EndPlay);
    }
}
