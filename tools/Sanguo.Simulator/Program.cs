using System.Globalization;
using Sanguo.Application;
using Sanguo.CampaignCore;
using Sanguo.Content;
using Sanguo.Infrastructure.Saves;
using Sanguo.Simulator;

if (args.Length > 0 && args[0] is "--battle-basic" or "--battle-resume" or "--battle-tricks" or "--battle-tricks-resume")
    return BasicBattleProbe.Run(args);

if (args.Length is < 1 or > 3 || (args.Length >= 2 && !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
{
    Console.Error.WriteLine("用法：dotnet run --project tools/Sanguo.Simulator -- <content目录> [非负种子] [保存路径]");
    return 2;
}
try
{
    var loaded = ContentLoader.LoadDirectory(args[0]);
    if (!loaded.Success)
    {
        foreach (var issue in loaded.Issues) Console.Error.WriteLine(issue);
        return 1;
    }
    var catalog = loaded.Catalog!;
    var seed = args.Length >= 2 ? ulong.Parse(args[1], CultureInfo.InvariantCulture) : 42UL;
    var savePath = args.Length >= 3 ? args[2] : Path.Combine("build", "g0-probe", "session.json");
    var uninterrupted = ProbeSession.Create("campaign.g0", Origin.Damage, catalog.Deck, catalog.ContentHash, seed);
    Submit(uninterrupted);
    var store = new FileSaveStore();
    store.Save(savePath, uninterrupted.State);
    var restored = new ProbeSession(store.Load(savePath, catalog.ContentHash).State);
    while (uninterrupted.State.Probe.PendingDecision is not null)
    {
        Submit(uninterrupted);
        Submit(restored);
        if (SnapshotCodec.StateHash(uninterrupted.State) != SnapshotCodec.StateHash(restored.State))
            throw new InvalidOperationException("恢复后的状态与连续运行不一致。");
    }
    Console.WriteLine($"G0抽样试验通过：种子{seed}，共{restored.State.Probe.Hand.Length}张牌，恢复后逐步哈希一致。");
    Console.WriteLine($"存档：{Path.GetFullPath(savePath)}");
    Console.WriteLine($"终态SHA-256：{SnapshotCodec.StateHash(restored.State)}");
    Console.WriteLine("此入口仅验证G0抽牌；基础战斗使用 --battle-basic，完整P0、武将、AI与OL桌面仍待后续实现。");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SaveException or InvalidOperationException)
{
    Console.Error.WriteLine($"试验失败：{ex.Message}");
    return 1;
}

static void Submit(ProbeSession session)
{
    var state = session.State;
    var decision = state.Probe.PendingDecision!;
    var result = session.Submit(new($"draw.{state.StateVersion}", state.StateVersion, decision.ControllerId, decision.RequestId));
    if (!result.Accepted) throw new InvalidOperationException(result.Message);
}
