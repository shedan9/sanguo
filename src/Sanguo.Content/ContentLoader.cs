using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sanguo.BattleCore.Probe;

namespace Sanguo.Content;

public sealed record ContentSource(string Path, string Json);
public sealed record ContentIssue(string File, string Field, string Code, string Message)
{
    public override string ToString() => $"{File}:{Field} [{Code}] {Message}";
}
public sealed record CardDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Effect { get; init; }
    public required string Implementation { get; init; }
}
public sealed record CardContentFile
{
    public required int FormatVersion { get; init; }
    public required string PackageId { get; init; }
    public required string Scope { get; init; }
    public required ImmutableArray<CardDefinition> Cards { get; init; }
    public required ImmutableArray<ProbeCard> Deck { get; init; }
}
public sealed record ContentCatalog(string ContentHash, ImmutableArray<CardDefinition> Cards, ImmutableArray<ProbeCard> Deck);
public sealed record ContentLoadResult(ContentCatalog? Catalog, ImmutableArray<ContentIssue> Issues)
{
    public bool Success => Catalog is not null && Issues.IsEmpty;
}

public static class ContentLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static ContentLoadResult LoadDirectory(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "cards");
        if (!Directory.Exists(path))
            return new(null, [new(path, "$", "content.missing", "缺少cards目录。")]);
        return Load(Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories)
            .Select(file => new ContentSource(Path.GetRelativePath(contentRoot, file).Replace('\\', '/'), File.ReadAllText(file))));
    }

    public static ContentLoadResult Load(IEnumerable<ContentSource> sources)
    {
        var ordered = sources.OrderBy(s => s.Path, StringComparer.Ordinal).ToArray();
        var issues = ImmutableArray.CreateBuilder<ContentIssue>();
        var cards = new List<CardDefinition>();
        var copies = new List<(ContentSource Source, int Index, ProbeCard Card)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in ordered)
        {
            if (!sourcePaths.Add(source.Path))
                issues.Add(new(source.Path, "$", "source.duplicate", "内容文件路径重复。"));
            CardContentFile? data;
            try { data = JsonSerializer.Deserialize<CardContentFile>(source.Json, Options); }
            catch (JsonException ex)
            {
                issues.Add(new(source.Path, ex.Path ?? "$", "json.invalid", ex.Message));
                continue;
            }
            if (data is null || data.FormatVersion != 1 || data.Scope != "g0-probe"
                || data.Cards.IsDefault || data.Deck.IsDefault)
            {
                issues.Add(new(source.Path, "$", "schema.invalid", "需要版本1、g0-probe范围及cards/deck数组。"));
                continue;
            }
            CheckId(data.PackageId, source.Path, "$.packageId");
            for (var i = 0; i < data.Cards.Length; i++)
            {
                var card = data.Cards[i];
                var field = $"$.cards[{i}]";
                if (card is null) { issues.Add(new(source.Path, field, "schema.invalid", "牌定义不能为null。")); continue; }
                CheckId(card.Id, source.Path, field + ".id");
                if (string.IsNullOrWhiteSpace(card.Name) || string.IsNullOrWhiteSpace(card.Effect)
                    || card.Implementation != "declared-only")
                    issues.Add(new(source.Path, field, "schema.invalid", "需要牌名、效果索引及declared-only标记；G0不加载牌效。"));
                cards.Add(card);
            }
            for (var i = 0; i < data.Deck.Length; i++)
            {
                var copy = data.Deck[i];
                var field = $"$.deck[{i}]";
                if (copy is null) { issues.Add(new(source.Path, field, "schema.invalid", "实体牌不能为null。")); continue; }
                CheckId(copy.InstanceId, source.Path, field + ".instanceId");
                if (copy.Rank is < 1 or > 13 || copy.Suit is not ("spade" or "heart" or "club" or "diamond"))
                    issues.Add(new(source.Path, field, "card.invalid", "花色必须合法，点数必须为1至13。"));
                copies.Add((source, i, copy));
            }
        }
        var definitions = cards.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (source, index, card) in copies)
            if (string.IsNullOrWhiteSpace(card.DefinitionId) || !definitions.Contains(card.DefinitionId))
                issues.Add(new(source.Path, $"$.deck[{index}].definitionId", "reference.missing", $"不存在的牌定义：{card.DefinitionId}。"));
        if (cards.Count == 0 || copies.Count == 0)
            issues.Add(new("cards/", "$", "content.empty", "至少需要一张牌定义和一个实体实例。"));
        if (issues.Count != 0) return new(null, issues.ToImmutable());

        // 长度前缀避免路径与内容拼接碰撞；来源清单未冻结，不将此摘要称为OL规则包摘要。
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var source in ordered)
        {
            Append(source.Path);
            Append(source.Json);
        }
        return new(new(Convert.ToHexStringLower(digest.GetHashAndReset()),
            cards.OrderBy(c => c.Id, StringComparer.Ordinal).ToImmutableArray(),
            copies.Select(c => c.Card).OrderBy(c => c.InstanceId, StringComparer.Ordinal).ToImmutableArray()), []);

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            digest.AppendData(length);
            digest.AppendData(bytes);
        }
        void CheckId(string? id, string file, string field)
        {
            if (string.IsNullOrWhiteSpace(id)) issues.Add(new(file, field, "id.empty", "ID不能为空。"));
            else if (!ids.Add(id)) issues.Add(new(file, field, "id.duplicate", $"重复ID：{id}。"));
        }
    }
}
