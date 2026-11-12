using Sanguo.Content;

if (args.Length != 1)
{
    Console.Error.WriteLine("用法：dotnet run --project tools/Sanguo.ContentValidator -- <content目录>");
    return 2;
}
try
{
    var result = ContentLoader.LoadDirectory(args[0]);
    foreach (var issue in result.Issues) Console.Error.WriteLine(issue);
    if (!result.Success) return 1;
    var catalog = result.Catalog!;
    Console.WriteLine($"G0内容校验通过：{catalog.Cards.Length}种牌定义、{catalog.Deck.Length}张试验牌。");
    Console.WriteLine($"内容SHA-256：{catalog.ContentHash}");
    Console.WriteLine("范围：g0-probe。牌效尚未实现；此检查不代表OL来源、武将或UI冻结验收通过。");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"[content.io] {ex.Message}");
    return 1;
}
