using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sanguo.Content;

public sealed record SourceManifestResult(ImmutableArray<ContentIssue> Issues, ImmutableArray<ContentIssue> Pending)
{
    public bool Success => Issues.IsEmpty;
    public bool ReadyToFreeze => Success && Pending.IsEmpty;
}

/// <summary>检查来源登记与本地证据的一致性，不替代人工核验或规则验收。</summary>
public static class SourceManifestValidator
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    private static readonly string[] RequiredStates =
        ["idle", "play", "select", "slash-jink", "duel", "nullification", "skill", "choose-zone", "discard", "dying"];
    private static readonly string[] RequiredGenerals = ["guanyu", "zhaoyun", "zhangliao", "huatuo", "caocao", "lvbu"];

    public static SourceManifestResult ValidateDirectory(string contentRoot)
    {
        var issues = ImmutableArray.CreateBuilder<ContentIssue>();
        var pending = ImmutableArray.CreateBuilder<ContentIssue>();
        var headers = new List<(string File, ManifestHeader Header)>();
        Check<RulesManifest>("rules-reference.json", CheckRules);
        Check<GeneralsManifest>("sgsol-generals.json", CheckGenerals);
        Check<UiManifest>("sgsol-battle-ui-reference.json", CheckUi);
        foreach (var (file, header) in headers)
        {
            Compare(header.SourceSnapshotId, h => h.SourceSnapshotId, "$.sourceSnapshotId");
            Compare(header.ClientVersion, h => h.ClientVersion, "$.clientVersion");
            void Compare(string? value, Func<ManifestHeader, string?> get, string field)
            {
                if (!string.IsNullOrWhiteSpace(value) && headers.Any(h =>
                    !string.IsNullOrWhiteSpace(get(h.Header)) && get(h.Header) != value))
                    issues.Add(new(file, field, "source.snapshot_mismatch", "三份来源清单必须使用同一快照与客户端版本。"));
            }
        }
        return new(issues.ToImmutable(), pending.ToImmutable());

        void Check<T>(string name, Action<T, ManifestContext> validate) where T : ManifestHeader
        {
            var file = "manifests/" + name;
            try
            {
                var data = JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(contentRoot, file)), Options);
                if (data is null) { issues.Add(new(file, "$", "schema.invalid", "来源清单不能为null。")); return; }
                headers.Add((file, data));
                var context = new ManifestContext(contentRoot, file, data, issues, pending);
                context.Require(data.FormatVersion == 1, "$.formatVersion", "需要来源格式版本1。");
                context.Require(data.SourceGame == "sgs_online", "$.sourceGame", "来源产品必须为sgs_online。");
                context.Status(data.Status, "$", data is UiManifest ? "pending_capture" : "pending_verification");
                if (data.Status == "frozen")
                {
                    context.Text(data.SourceSnapshotId, "$.sourceSnapshotId");
                    context.Text(data.ClientVersion, "$.clientVersion");
                }
                validate(data, context);
            }
            catch (JsonException ex) { issues.Add(new(file, ex.Path ?? "$", "json.invalid", ex.Message)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { issues.Add(new(file, "$", "source.io", ex.Message)); }
        }
    }

    private static void CheckRules(RulesManifest data, ManifestContext c)
    {
        c.Url(data.SourceUrl, "$.sourceUrl");
        var frozen = data.Status == "frozen";
        var evidence = c.Evidence(data.Evidence, "$.evidence", frozen);
        if (frozen)
            foreach (var kind in new[] { "rules", "ruling" })
                c.Require(evidence.Values.Any(e => e.Kind == kind), "$.evidence", $"缺少{kind}证据。");
        c.StringList(data.DeckList, "$.deckList", frozen);
        c.StringList(data.Rulings, "$.rulings", frozen);
        c.StringList(data.BlockingItems, "$.blockingItems", false);
        c.Text(data.RuntimePackage, "$.runtimePackage");
        c.Text(data.Note, "$.note");
        if (frozen)
        {
            c.Date(data.FrozenAt, "$.frozenAt");
            c.Require(data.BlockingItems.IsEmpty, "$.blockingItems", "冻结前必须解决所有阻断项。");
        }
        else if (!data.BlockingItems.IsDefault)
            for (var i = 0; i < data.BlockingItems.Length; i++)
                c.Pending($"$.blockingItems[{i}]", data.BlockingItems[i]);
    }

    private static void CheckGenerals(GeneralsManifest data, ManifestContext c)
    {
        if (!c.Array(data.Generals, "$.generals", true)) return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < data.Generals.Length; i++)
        {
            var general = data.Generals[i];
            var field = $"$.generals[{i}]";
            if (general is null) { c.Require(false, field, "武将条目不能为null。"); continue; }
            c.Id(general.GeneralId, field + ".generalId", ids);
            c.Text(general.DisplayName, field + ".displayName");
            c.Status(general.Status, field, "pending_verification");
            var frozen = general.Status == "frozen";
            c.Require(data.Status != "frozen" || frozen, field + ".status", "整体冻结时，每名武将都必须冻结。");
            c.Require(frozen || !general.IncludedInRuntime, field + ".includedInRuntime", "未核验武将不能进入运行内容包。");
            var evidence = c.Evidence(general.Evidence, field + ".evidence", frozen);
            if (frozen)
                foreach (var kind in new[] { "general", "portrait" })
                    c.Require(evidence.Values.Any(e => e.Kind == kind), field + ".evidence", $"缺少{kind}证据。");
            c.StringList(general.RuleCaseIds, field + ".ruleCaseIds", frozen);
            if (c.Array(general.CompleteSkills, field + ".completeSkills", frozen))
            {
                var skills = new HashSet<string>(StringComparer.Ordinal);
                for (var j = 0; j < general.CompleteSkills.Length; j++)
                {
                    var skill = general.CompleteSkills[j];
                    var skillField = $"{field}.completeSkills[{j}]";
                    if (skill is null) { c.Require(false, skillField, "技能条目不能为null。"); continue; }
                    c.Id(skill.SkillId, skillField + ".skillId", skills);
                    c.Text(skill.Text, skillField + ".text");
                }
            }
            if (!frozen) continue;
            c.Text(general.VariantId, field + ".variantId");
            c.Text(general.PortraitId, field + ".portraitId");
            c.Require(general.BaseAttributes is not null, field + ".baseAttributes", "缺少原版基础属性。");
            if (general.BaseAttributes is { } attributes)
            {
                c.Require(attributes.MaxHp > 0, field + ".baseAttributes.maxHp", "体力上限必须为正整数。");
                c.Text(attributes.Gender, field + ".baseAttributes.gender");
                c.Text(attributes.Faction, field + ".baseAttributes.faction");
            }
        }
        foreach (var id in RequiredGenerals)
            c.Require(ids.Contains(id), "$.generals", $"缺少MVP人物登记：{id}。");
    }

    private static void CheckUi(UiManifest data, ManifestContext c)
    {
        c.Text(data.ReferenceId, "$.referenceId");
        c.Require(data.Resolution is { Width: > 0, Height: > 0 }, "$.resolution", "分辨率必须为正整数。");
        var frozen = data.Status == "frozen";
        var evidence = c.Evidence(data.Evidence, "$.evidence", frozen);
        c.References(data.Screenshots, "$.screenshots", evidence, "screenshot", frozen);
        c.References(data.Recordings, "$.recordings", evidence, "recording", frozen);
        if (frozen)
        {
            c.Text(data.Theme, "$.theme");
            c.Require(data.Scale is > 0 && double.IsFinite(data.Scale.Value), "$.scale", "需要有效的正数缩放比例。");
            c.Date(data.CapturedAt, "$.capturedAt");
        }
        if (c.Array(data.InteractionStates, "$.interactionStates", true))
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < data.InteractionStates.Length; i++)
            {
                var state = data.InteractionStates[i];
                var field = $"$.interactionStates[{i}]";
                if (state is null) { c.Require(false, field, "交互状态不能为null。"); continue; }
                c.Id(state.Id, field + ".id", ids);
                c.Text(state.Name, field + ".name");
                c.Status(state.Status, field, "pending_capture");
                c.Require(!frozen || state.Status == "frozen", field + ".status", "整体冻结时，所有交互状态都必须冻结。");
                c.References(state.Evidence, field + ".evidence", evidence, null, state.Status == "frozen");
                if (state.Status == "frozen")
                    foreach (var kind in new[] { "screenshot", "recording" })
                        c.Require(!state.Evidence.IsDefault && state.Evidence.Any(id =>
                            id is not null && evidence.TryGetValue(id, out var item) && item.Kind == kind),
                            field + ".evidence", $"已冻结交互状态需要{kind}证据。");
            }
            foreach (var id in RequiredStates)
                c.Require(ids.Contains(id), "$.interactionStates", $"缺少必须采集的交互状态：{id}。");
        }
        if (c.Array(data.Adaptations, "$.adaptations", true))
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < data.Adaptations.Length; i++)
            {
                var item = data.Adaptations[i];
                var field = $"$.adaptations[{i}]";
                if (item is null) { c.Require(false, field, "适配条目不能为null。"); continue; }
                c.Id(item.Id, field + ".id", ids);
                c.Text(item.Description, field + ".description");
                c.Require(item.Status is "specified" or "awaiting_reference_mapping", field + ".status", "未知适配状态。");
                if (item.Status == "awaiting_reference_mapping")
                {
                    c.Pending(field + ".status", "项目适配尚待参照映射。");
                    c.Require(!frozen, field + ".status", "冻结前必须完成项目适配映射。");
                }
            }
        }
    }

    private sealed class ManifestContext(string root, string file, ManifestHeader header,
        ImmutableArray<ContentIssue>.Builder issues, ImmutableArray<ContentIssue>.Builder pending)
    {
        public void Require(bool valid, string field, string message, string code = "source.invalid")
        { if (!valid) issues.Add(new(file, field, code, message)); }
        public void Text(string? text, string field) => Require(!string.IsNullOrWhiteSpace(text), field, "需要非空文本。");
        public void Pending(string field, string message) => pending.Add(new(file, field, "source.pending", message));
        public void Url(string? url, string field) => Require(Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https", field, "需要HTTP或HTTPS来源地址。");
        public void Date(string? date, string field) => Require(DateTimeOffset.TryParse(date,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _), field, "需要有效的采集或冻结日期。");
        public void Status(string status, string field, string draft)
        {
            Require(status == draft || status == "frozen", field + ".status", "未知来源状态。");
            if (status == draft) Pending(field + ".status", "来源尚未核验冻结。");
        }
        public void Id(string id, string field, HashSet<string> ids)
        {
            Text(id, field);
            Require(ids.Add(id), field, $"重复ID：{id}。", "id.duplicate");
        }
        public bool Array<T>(ImmutableArray<T> items, string field, bool nonempty)
        {
            Require(!items.IsDefault && (!nonempty || !items.IsEmpty), field, nonempty ? "需要非空数组。" : "需要数组。");
            return !items.IsDefault;
        }
        public void StringList(ImmutableArray<string> items, string field, bool nonempty)
        {
            if (!Array(items, field, nonempty)) return;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(items[i])) Text(items[i], $"{field}[{i}]");
                else Id(items[i], $"{field}[{i}]", ids);
            }
        }
        public void References(ImmutableArray<string> refs, string field, Dictionary<string, SourceEvidence> evidence,
            string? kind, bool nonempty)
        {
            StringList(refs, field, nonempty);
            if (refs.IsDefault) return;
            for (var i = 0; i < refs.Length; i++)
                Require(refs[i] is not null && evidence.TryGetValue(refs[i], out var item) && (kind is null || item.Kind == kind),
                    $"{field}[{i}]", "证据引用不存在或类型不匹配。", "reference.missing");
        }
        public Dictionary<string, SourceEvidence> Evidence(ImmutableArray<SourceEvidence> items, string field, bool nonempty)
        {
            var result = new Dictionary<string, SourceEvidence>(StringComparer.Ordinal);
            if (!Array(items, field, nonempty)) return result;
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                var at = $"{field}[{i}]";
                if (item is null) { Require(false, at, "证据条目不能为null。"); continue; }
                Text(item.Id, at + ".id");
                Require(result.TryAdd(item.Id, item), at + ".id", "证据ID重复。", "id.duplicate");
                Require(item.Kind is "rules" or "ruling" or "general" or "portrait" or "screenshot" or "recording",
                    at + ".kind", "未知证据类型。");
                Url(item.SourceUrl, at + ".sourceUrl");
                Text(item.SourceSnapshotId, at + ".sourceSnapshotId");
                Text(item.ClientVersion, at + ".clientVersion");
                Require(item.SourceSnapshotId == header.SourceSnapshotId && item.ClientVersion == header.ClientVersion,
                    at, "证据与清单的快照或客户端版本不一致。", "source.snapshot_mismatch");
                var validHash = item.Sha256.Length == 64 && item.Sha256.All(Uri.IsHexDigit);
                Require(validHash, at + ".sha256", "需要64位SHA-256十六进制摘要。");
                CheckFile(item, at, validHash);
            }
            return result;
        }
        private void CheckFile(SourceEvidence item, string field, bool validHash)
        {
            // 证据限定在content目录内；拒绝上级跳转、跨平台绝对路径和符号链接。
            var segments = item.Path.Replace('\\', '/').Split('/');
            if (segments.Any(s => s is "" or "." or ".." || s.Contains(':')))
            { Require(false, field + ".path", "证据必须使用content内的相对文件路径。", "evidence.path"); return; }
            try
            {
                var path = Path.GetFullPath(root);
                foreach (var segment in segments)
                {
                    path = Path.Combine(path, segment);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    { Require(false, field + ".path", "证据路径不能包含符号链接。", "evidence.path"); return; }
                }
                using var input = File.OpenRead(path);
                var actual = Convert.ToHexStringLower(SHA256.HashData(input));
                Require(!validHash || string.Equals(actual, item.Sha256, StringComparison.OrdinalIgnoreCase),
                    field + ".sha256", "本地证据文件摘要不匹配。", "evidence.hash_mismatch");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Require(false, field + ".path", ex.Message, "evidence.io"); }
        }
    }
}

internal abstract record ManifestHeader
{
    public required int FormatVersion { get; init; }
    public required string SourceGame { get; init; }
    public required string Status { get; init; }
    public required string? SourceSnapshotId { get; init; }
    public required string? ClientVersion { get; init; }
}
internal sealed record SourceEvidence(string Id, string Kind, string Path, string Sha256, string SourceUrl,
    string SourceSnapshotId, string ClientVersion);
internal sealed record RulesManifest : ManifestHeader
{
    public required string? FrozenAt { get; init; }
    public required string SourceUrl { get; init; }
    public required ImmutableArray<string> DeckList { get; init; }
    public required ImmutableArray<string> Rulings { get; init; }
    public required ImmutableArray<SourceEvidence> Evidence { get; init; }
    public required ImmutableArray<string> BlockingItems { get; init; }
    public required string RuntimePackage { get; init; }
    public required string Note { get; init; }
}
internal sealed record GeneralsManifest : ManifestHeader
{
    public required ImmutableArray<GeneralSource> Generals { get; init; }
}
internal sealed record GeneralSource(string GeneralId, string DisplayName, string Status, string? VariantId,
    string? PortraitId, string? SkinId, SourceAttributes? BaseAttributes, ImmutableArray<SourceSkill> CompleteSkills,
    ImmutableArray<SourceEvidence> Evidence, ImmutableArray<string> RuleCaseIds, bool IncludedInRuntime);
internal sealed record SourceAttributes(int MaxHp, string Gender, string Faction);
internal sealed record SourceSkill(string SkillId, string Text);
internal sealed record UiManifest : ManifestHeader
{
    public required string ReferenceId { get; init; }
    public required string? Theme { get; init; }
    public required SourceResolution Resolution { get; init; }
    public required double? Scale { get; init; }
    public required string? CapturedAt { get; init; }
    public required ImmutableArray<string> Screenshots { get; init; }
    public required ImmutableArray<string> Recordings { get; init; }
    public required ImmutableArray<InteractionSource> InteractionStates { get; init; }
    public required ImmutableArray<SourceAdaptation> Adaptations { get; init; }
    public required ImmutableArray<SourceEvidence> Evidence { get; init; }
}
internal sealed record SourceResolution(int Width, int Height);
internal sealed record InteractionSource(string Id, string Name, string Status, ImmutableArray<string> Evidence);
internal sealed record SourceAdaptation(string Id, string Description, string Status);
