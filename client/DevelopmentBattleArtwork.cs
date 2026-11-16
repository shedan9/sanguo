using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Client;

/// <summary>已采集原图的内部开发映射，不装载或冻结正式OL规则/人物内容包。</summary>
internal sealed class DevelopmentBattleArtwork
{
    private const string Assets = "pc/res/assets/";
    private readonly Dictionary<string, (string Hash, int Bytes)> _sources;
    private readonly Dictionary<string, Texture2D> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<BasicCardKind, Texture2D> _cards = new();
    private readonly Dictionary<(bool Confirm, string State), Texture2D> _buttons = new();
    public Texture2D SeatFrame { get; }
    public Texture2D Guanyu { get; }
    public Texture2D Caocao { get; }
    public Texture2D BloodEmpty { get; }
    public Texture2D BloodGreen { get; }
    public Texture2D BloodYellow { get; }
    public Texture2D BloodRed { get; }

    public DevelopmentBattleArtwork()
    {
        using var catalog = JsonDocument.Parse(ReadResource("catalog.json"));
        _sources = catalog.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
            e => e.GetProperty("path").GetString()!,
            e => (e.GetProperty("sha256").GetString()!, e.GetProperty("bytes").GetInt32()), StringComparer.Ordinal);
        Guanyu = LoadImage("portraits/guanyu-200.png");
        Caocao = LoadImage("portraits/caocao-1500.png");
        using (var seat = JsonDocument.Parse(ReadVerified(Assets + "game/seat.atlas")))
        {
            SeatFrame = Frame(seat, Assets + "game/seat.png", "self_general_frame.png");
            BloodEmpty = Frame(seat, Assets + "game/seat.png", "blood_empty_s.png");
            BloodGreen = Frame(seat, Assets + "game/seat.png", "blood_green_s.png");
            BloodYellow = Frame(seat, Assets + "game/seat.png", "blood_yellow_s.png");
            BloodRed = Frame(seat, Assets + "game/seat.png", "blood_red_s.png");
        }
        using (var cards = JsonDocument.Parse(ReadVerified(Assets + "cards/normal/cards.atlas")))
        {
            (BasicCardKind Kind, string Name)[] faces =
            [
                (BasicCardKind.Slash, "Sha"), (BasicCardKind.Dodge, "Shan"), (BasicCardKind.Peach, "Tao"),
                (BasicCardKind.Duel, "JueDou"), (BasicCardKind.ExNihilo, "WuZhongShengYou"),
                (BasicCardKind.Nullification, "WuXieKeJi"), (BasicCardKind.Dismantlement, "GuoHeChaiQiao"),
                (BasicCardKind.Snatch, "ShunShouQianYang")
            ];
            foreach (var (kind, name) in faces)
                _cards.Add(kind, Frame(cards, Assets + "cards/normal/cards.png", name + "_png.png"));
        }
        using var self = JsonDocument.Parse(ReadVerified(Assets + "game/selfseat.atlas"));
        foreach (var confirm in new[] { true, false })
        foreach (var state in new[] { "normal", "over", "down", "disabled" })
            _buttons.Add((confirm, state), Frame(self, Assets + "game/selfseat.png",
                $"selfSeat{(confirm ? "Ok" : "Cancel")}Btn_{state}.png"));
    }

    public Texture2D CardFace(BasicCardKind kind) => _cards[kind];
    public Texture2D? Portrait(BattleProfile profile) => profile switch
    {
        BattleProfile.GuanyuStandard => Guanyu, BattleProfile.CaocaoStandard => Caocao, _ => null
    };

    public void StyleDecisionButton(Button button, bool confirm)
    {
        foreach (var (style, state) in new[] { ("normal", "normal"), ("hover", "over"), ("pressed", "down"), ("disabled", "disabled") })
            button.AddThemeStyleboxOverride(style, new StyleBoxTexture
            {
                Texture = _buttons[(confirm, state)],
                ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6
            });
    }

    private AtlasTexture Frame(JsonDocument atlas, string imagePath, string name)
    {
        var entry = atlas.RootElement.GetProperty("frames").GetProperty(name);
        var frame = entry.GetProperty("frame");
        var region = new Rect2(frame.GetProperty("x").GetInt32(), frame.GetProperty("y").GetInt32(),
            frame.GetProperty("w").GetInt32(), frame.GetProperty("h").GetInt32());
        var image = LoadImage(imagePath);
        var source = entry.GetProperty("sourceSize");
        var sprite = entry.GetProperty("spriteSourceSize");
        // Only the untrimmed, single-page frames used here are supported. Fail visibly if the source changes.
        if (frame.GetProperty("idx").GetInt32() != 0 || entry.GetProperty("scale").GetInt32() != 2
            || region.Size.X <= 0 || region.Size.Y <= 0 || !new Rect2(Vector2.Zero, image.GetSize()).Encloses(region)
            || source.GetProperty("w").GetInt32() != region.Size.X || source.GetProperty("h").GetInt32() != region.Size.Y
            || sprite.GetProperty("x").GetInt32() != 0 || sprite.GetProperty("y").GetInt32() != 0
            || sprite.GetProperty("w").GetInt32() != region.Size.X || sprite.GetProperty("h").GetInt32() != region.Size.Y)
            throw new InvalidDataException("不支持或越界的OL开发图集帧：" + name);
        return new AtlasTexture { Atlas = image, Region = region, FilterClip = true };
    }

    private Texture2D LoadImage(string path)
    {
        if (_images.TryGetValue(path, out var cached)) return cached;
        using var image = new Image();
        if (image.LoadPngFromBuffer(ReadVerified(path)) != Error.Ok)
            throw new InvalidDataException("无法解码OL开发图片：" + path);
        var texture = ImageTexture.CreateFromImage(image);
        _images.Add(path, texture);
        return texture;
    }

    private byte[] ReadVerified(string path)
    {
        var bytes = ReadResource(Path.GetFileName(path));
        var source = _sources[path];
        if (bytes.Length != source.Bytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != source.Hash)
            throw new InvalidDataException("OL开发素材与采集摘要不一致：" + path);
        return bytes;
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(DevelopmentBattleArtwork).Assembly.GetManifestResourceStream("Sanguo.Ol." + name)
            ?? throw new InvalidDataException("缺少内嵌OL开发素材：" + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
