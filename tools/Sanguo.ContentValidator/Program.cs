using Sanguo.Content;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--require-frozen"))
{
    Console.Error.WriteLine("用法：dotnet run --project tools/Sanguo.ContentValidator -- <content目录> [--require-frozen]");
    return 2;
}
try
{
    var result = ContentLoader.LoadDirectory(args[0]);
    var sources = SourceManifestValidator.ValidateDirectory(args[0]);
    foreach (var issue in result.Issues) Console.Error.WriteLine(issue);
    foreach (var issue in sources.Issues) Console.Error.WriteLine(issue);
    foreach (var item in sources.Pending) Console.WriteLine(item);
    if (!result.Success || !sources.Success) return 1;
    var catalog = result.Catalog!;
    Console.WriteLine($"G0内容校验通过：{catalog.Cards.Length}种牌定义、{catalog.Deck.Length}张试验牌。");
    Console.WriteLine($"内容SHA-256：{catalog.ContentHash}");
    Console.WriteLine("范围：g0-probe。牌效尚未实现；此检查不代表OL来源、武将或UI冻结验收通过。");
    Console.WriteLine($"来源登记结构与本地证据校验通过；待完成项：{sources.Pending.Length}。");
    if (args.Length == 2 && !sources.ReadyToFreeze)
    {
        Console.Error.WriteLine("[source.not_frozen] 严格检查失败：来源清单仍有未冻结或未映射项。");
        return 1;
    }
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"[content.io] {ex.Message}");
    return 1;
}
