using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Hud;

/// <summary>A texture-atlas crop: which atlas, and the pixel rectangle within it.</summary>
public sealed record HudTexture(string AtlasPath, int AtlasWidth, int AtlasHeight, Vector4 UvRect)
{
    /// <summary>UV rect is (left, top, right, bottom) in atlas pixels.</summary>
    public float SrcX => UvRect.X;
    public float SrcY => UvRect.Y;
    public float SrcW => UvRect.Z - UvRect.X;
    public float SrcH => UvRect.W - UvRect.Y;
}

/// <summary>One HUD element (icon, text, region, scene, effect…) with its layout rectangle.</summary>
public sealed class HudElement
{
    public required uint PathHash { get; init; }
    public required string FullName { get; init; }
    public required string ClassName { get; init; }
    /// <summary>Leaf of the '/'-separated FullName — what the tree/inspector shows.</summary>
    public string ShortName => FullName.Length == 0 ? $"0x{PathHash:x8}"
        : FullName[(FullName.LastIndexOf('/') + 1)..];

    public bool IsScene { get; init; }
    public bool Enabled { get; init; } = true;
    public uint Layer { get; init; }
    /// <summary>Parent scene/group (Scene link for elements, ParentScene for scenes). 0 = root.</summary>
    public uint ParentHash { get; init; }

    // layout (absolute px in the doc's reference resolution)
    public bool HasRect { get; init; }
    public Vector2 Position { get; init; }
    public Vector2 Size { get; init; }
    public Vector2 Anchor { get; init; }

    public HudTexture? Texture { get; init; }
    /// <summary>Tint (rgba 0..1) when the element carries a Color; null otherwise.</summary>
    public Vector4? Color { get; init; }

    public List<HudElement> Children { get; } = new();
}

/// <summary>
/// M140: an in-memory view of a League HUD layout bin (ClientStates/Gameplay/UX/.../UIBase — a PROP
/// bin of UiElement*Data / UISceneData objects). Elements carry an absolute rectangle in a reference
/// resolution plus a texture-atlas UV crop, so the whole HUD can be drawn and inspected. Read-only for
/// now; editing (M141) mutates the same tree and re-serializes.
/// </summary>
public sealed class HudDocument
{
    public IReadOnlyList<HudElement> AllElements { get; }
    /// <summary>Top-level nodes (parent not present in this bin) — the tree roots.</summary>
    public IReadOnlyList<HudElement> Roots { get; }
    public int ReferenceWidth { get; }
    public int ReferenceHeight { get; }
    public IReadOnlyList<string> AtlasPaths { get; }

    private HudDocument(List<HudElement> all, List<HudElement> roots, int w, int h, List<string> atlases)
    { AllElements = all; Roots = roots; ReferenceWidth = w; ReferenceHeight = h; AtlasPaths = atlases; }

    public static HudDocument? Parse(byte[] data, Func<uint, string?> resolve)
    {
        BinTree tree;
        try { tree = SafeBinTree.Parse(data); } catch { return null; }

        uint fName = F("name"), fScene = F("Scene"), fParentScene = F("ParentScene"),
             fLayer = F("Layer"), fEnabled = F("Enabled"), fPosition = F("Position"),
             fUIRect = F("UIRect"), fSize = F("Size"), fAnchors = F("Anchors"), fAnchor = F("Anchor"),
             fSrcW = F("SourceResolutionWidth"), fSrcH = F("SourceResolutionHeight"),
             fTexData = F("TextureData"), fTexName = F("mTextureName"),
             fTexW = F("mTextureSourceResolutionWidth"), fTexH = F("mTextureSourceResolutionHeight"),
             fTexUv = F("mTextureUV"), fColor = F("Color");

        var elements = new List<HudElement>();
        var byHash = new Dictionary<uint, HudElement>();
        var resVotes = new Dictionary<int, int>();
        var atlases = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hash, o) in tree.Objects)
        {
            string cls = resolve(o.ClassHash) ?? $"0x{o.ClassHash:x8}";
            bool isScene = cls.Equals("UISceneData", StringComparison.OrdinalIgnoreCase);
            bool isElement = isScene || cls.StartsWith("UiElement", StringComparison.OrdinalIgnoreCase)
                             || cls.StartsWith("UISceneData", StringComparison.OrdinalIgnoreCase);
            if (!isElement) continue;

            string name = Str(o.Properties, fName) ?? resolve(hash) ?? $"0x{hash:x8}";
            uint parent = Link(o.Properties, isScene ? fParentScene : fScene);
            uint layer = U32(o.Properties, fLayer);
            bool enabled = Bool(o.Properties, fEnabled, true);

            // rect
            bool hasRect = false;
            Vector2 pos = default, size = default, anchor = default;
            if (Struct(o.Properties, fPosition) is { } posRect)
            {
                if (Struct(posRect.Properties, fUIRect) is { } rect)
                {
                    pos = Vec2(rect.Properties, F("Position"));
                    size = Vec2(rect.Properties, fSize);
                    hasRect = size != default || pos != default;
                    int rw = U16(rect.Properties, fSrcW), rh = U16(rect.Properties, fSrcH);
                    if (rw > 0 && rh > 0) resVotes[(rw << 16) | rh] = resVotes.GetValueOrDefault((rw << 16) | rh) + 1;
                }
                if (Struct(posRect.Properties, fAnchors) is { } anch)
                    anchor = Vec2(anch.Properties, fAnchor);
            }

            // texture atlas
            HudTexture? tex = null;
            if (Struct(o.Properties, fTexData) is { } td && Str(td.Properties, fTexName) is { Length: > 0 } atlas)
            {
                var uv = Vec4(td.Properties, fTexUv);
                tex = new HudTexture(atlas, (int)U32(td.Properties, fTexW), (int)U32(td.Properties, fTexH), uv);
                atlases.Add(atlas);
            }

            Vector4? color = o.Properties.TryGetValue(fColor, out var cp)
                ? cp switch { BinTreeColor bc => new Vector4(bc.Value.R, bc.Value.G, bc.Value.B, bc.Value.A),
                              BinTreeVector4 v4 => v4.Value, _ => null } : null;

            var el = new HudElement
            {
                PathHash = hash, FullName = name, ClassName = cls, IsScene = isScene,
                Enabled = enabled, Layer = layer, ParentHash = parent,
                HasRect = hasRect, Position = pos, Size = size, Anchor = anchor,
                Texture = tex, Color = color,
            };
            elements.Add(el);
            byHash[hash] = el;
        }

        // build the tree from parent backlinks
        var roots = new List<HudElement>();
        foreach (var e in elements)
        {
            if (e.ParentHash != 0 && byHash.TryGetValue(e.ParentHash, out var p) && !ReferenceEquals(p, e))
                p.Children.Add(e);
            else roots.Add(e);
        }
        foreach (var e in elements)
            e.Children.Sort((a, b) => a.Layer.CompareTo(b.Layer));

        int refKey = resVotes.Count > 0 ? resVotes.OrderByDescending(kv => kv.Value).First().Key : (1600 << 16) | 1200;
        return new HudDocument(elements, roots, refKey >> 16, refKey & 0xFFFF, atlases.ToList());

        static uint F(string s) => HashAlgorithms.Fnv1a(s);
    }

    // ---- typed property readers ----
    private static string? Str(IReadOnlyDictionary<uint, BinTreeProperty> p, uint h) =>
        p.TryGetValue(h, out var v) && v is BinTreeString s ? s.Value : null;
    private static uint U32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint h) =>
        p.TryGetValue(h, out var v) ? v switch { BinTreeU32 u => u.Value, BinTreeI32 i => (uint)i.Value, _ => 0 } : 0;
    private static int U16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint h) =>
        p.TryGetValue(h, out var v) && v is BinTreeU16 u ? u.Value : 0;
    private static bool Bool(IReadOnlyDictionary<uint, BinTreeProperty> p, uint h, bool dflt) =>
        p.TryGetValue(h, out var v) ? v switch { BinTreeBool b => b.Value, BinTreeBitBool bb => bb.Value, _ => dflt } : dflt;
    private static uint Link(IReadOnlyDictionary<uint, BinTreeProperty> p, uint h) =>
        p.TryGetValue(h, out var v) && v is BinTreeObjectLink l ? l.Value : 0;
    private static BinTreeStruct? Struct(IReadOnlyDictionary<uint, BinTreeProperty> p, uint h) =>
        p.TryGetValue(h, out var v) ? v as BinTreeStruct : null;
    private static Vector2 Vec2(IReadOnlyDictionary<uint, BinTreeProperty> p, uint h) =>
        p.TryGetValue(h, out var v) && v is BinTreeVector2 vv ? vv.Value : default;
    private static Vector4 Vec4(IReadOnlyDictionary<uint, BinTreeProperty> p, uint h) =>
        p.TryGetValue(h, out var v) && v is BinTreeVector4 vv ? vv.Value : default;
}
