using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M788. A map material's pass carries a real <c>writeMask</c> (StaticMaterialPassDef.writeMask) that
/// nothing on the D3D11 map path ever read: a stencil-only mask mesh (TFT's
/// <c>Maps/KitPieces/TFT/Set13/Materials/Default/TheLastDrop_Stencil01_MAT</c> - depthEnable false,
/// stencilEnable true, writeMask 32) drew as an opaque black box because both its colour and depth writes
/// stayed on.
///
/// <para>Censused over every shipped map materials.bin (8,717 StaticMaterialDef bindings across
/// Map11/12/22/30/453 + Common): writeMask 7/15/16/23/31/32/40/63 all decompose cleanly under bit0-3 =
/// R,G,B,A (D3D11's own RenderTargetWriteMask numbering), bit4 = depth write, bit5 = stencil write. 8,488 of
/// the 8,717 author the schema default (31 or absent) - <see cref="MaterialProfile.AuthoredColorWriteMask"/>
/// and <see cref="MaterialProfile.AuthoredWriteMaskHasDepthBit"/> must read exactly as before for those.</para>
/// </summary>
public sealed class MapMaterialWriteMaskTests
{
    // ===================================================== pure bit decoding (no I/O)

    [Theory]
    [InlineData(31, 15, true)]    // schema default: RGBA + depth, no stencil
    [InlineData(7, 7, false)]     // Map22 shadow receivers: RGB only, no depth
    [InlineData(15, 15, false)]   // water/blur passes: RGBA, no depth
    [InlineData(16, 0, true)]     // a depth-only occluder: no colour at all
    [InlineData(23, 7, true)]     // RGB + depth, no alpha
    [InlineData(32, 0, false)]    // TheLastDrop_Stencil01_MAT: stencil-only - writes NEITHER colour NOR depth
    [InlineData(40, 8, false)]    // BattleAcademia_Transition_MAT: alpha + stencil, no depth
    [InlineData(63, 15, true)]    // everything: RGBA + depth + stencil
    public void TheColourAndDepthBitsDecodeAsCensused(int writeMask, byte expectedColorMask, bool expectedDepthBit)
    {
        var profile = MaterialProfile.Default with { AuthoredWriteMask = writeMask };
        Assert.Equal(expectedColorMask, profile.AuthoredColorWriteMask);
        Assert.Equal(expectedDepthBit, profile.AuthoredWriteMaskHasDepthBit);
    }

    [Fact]
    public void AnAbsentWriteMaskIsTheSameAsTheSchemaDefault()
    {
        // MaterialBinding.WriteMask defaults to 31 when the pass never authors the field at all - the same
        // value MaterialProfile.Default carries without any override.
        Assert.Equal((byte)15, MaterialProfile.Default.AuthoredColorWriteMask);
        Assert.True(MaterialProfile.Default.AuthoredWriteMaskHasDepthBit);
    }

    // ===================================================== a synthetic StaticMaterialDef, field for field

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const uint StaticMaterialDefHash = 0x1000u;
    private const uint TechniqueHash = 0x1001u;
    private const uint PassHash = 0x1002u;
    private const uint ShaderLinkHash = 0x2000u;

    private static string? Resolve(uint h) => h switch
    {
        StaticMaterialDefHash => "StaticMaterialDef",
        ShaderLinkHash => "Shaders/StaticMesh/DefaultEnv_Transition",
        _ => null,
    };

    /// <summary>Reproduces TheLastDrop_Stencil01_MAT's own pass, field for field (dump: anniversary bin,
    /// TheLastDrop_Stencil01_MAT): shader DefaultEnv_Transition, depthEnable false, stencilEnable true,
    /// NO blendEnable field at all, writeMask 32. Only the fields <see cref="MaterialProfile"/> actually
    /// reads are reproduced; stencilCompareFunc/StencilReferenceId/stencilMask/stencilFail* are real but
    /// unconsumed by any renderer today (there is no map stencil consumer on D3D11 yet).</summary>
    private static byte[] StencilOnlyBin(bool? depthEnable, int? writeMask, bool? blendEnable = null)
    {
        var passProps = new List<BinTreeProperty>
        {
            new BinTreeObjectLink(H("shader"), ShaderLinkHash),
        };
        if (depthEnable is { } de) passProps.Add(new BinTreeBool(H("depthEnable"), de));
        passProps.Add(new BinTreeBool(H("stencilEnable"), true));
        if (blendEnable is { } be) passProps.Add(new BinTreeBool(H("blendEnable"), be));
        if (writeMask is { } wm) passProps.Add(new BinTreeU32(H("writeMask"), (uint)wm));

        var objProps = new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Maps/KitPieces/TFT/Set13/Materials/Default/TheLastDrop_Stencil01_MAT"),
            new BinTreeContainer(H("techniques"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, TechniqueHash, new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Struct, new BinTreeProperty[]
                    {
                        new BinTreeStruct(0, PassHash, passProps.ToArray()),
                    }),
                }),
            }),
        };
        var tree = new BinTree(new[] { new BinTreeObject(0xC0DEu, StaticMaterialDefHash, objProps) },
            Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static MaterialProfile ProfileOf(bool? depthEnable, int? writeMask, bool? blendEnable = null)
    {
        var doc = MaterialDocument.Parse(StencilOnlyBin(depthEnable, writeMask, blendEnable), Resolve);
        var b = Assert.Single(doc.Materials);
        Assert.Equal(MaterialSourceKind.MapMaterials, doc.Kind);
        return b.Profile;
    }

    [Fact]
    public void TheStencilOnlyMaterialWritesNeitherColourNorDepth()
    {
        var p = ProfileOf(depthEnable: false, writeMask: 32);
        Assert.Equal((byte)0, p.AuthoredColorWriteMask);
        Assert.False(p.AuthoredWriteMaskHasDepthBit);
        // Real data carries no blendEnable field at all for this material - confirm the parse agrees so the
        // profile's own DepthWrite/UsesAuthoredColorBlend classification take the same route Dx11SceneBuilder
        // exercises for it (the plain, non-authored-blend branch - see BlendStateMasked).
        Assert.False(p.BlendEnabled);
    }

    [Fact]
    public void AMaterialWithNoWriteMaskFieldAtAllStaysAtTheSchemaDefault()
    {
        // writeMask 31/absent must stay BYTE-IDENTICAL to before M788 existed.
        var p = ProfileOf(depthEnable: null, writeMask: null);
        Assert.Equal((byte)15, p.AuthoredColorWriteMask);
        Assert.True(p.AuthoredWriteMaskHasDepthBit);
    }

    [Fact]
    public void ADepthOnlyWriteMaskWritesNoColourAtAll()
    {
        var p = ProfileOf(depthEnable: true, writeMask: 16, blendEnable: true);
        Assert.Equal((byte)0, p.AuthoredColorWriteMask);
        Assert.True(p.AuthoredWriteMaskHasDepthBit);
    }

    // ===================================================== real data: the anniversary bin itself

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string StencilMaterial = "Maps/KitPieces/TFT/Set13/Materials/Default/TheLastDrop_Stencil01_MAT";
    private const string TransitionMaterial = "Maps/KitPieces/TFT/Set13/Materials/Default/TheLastDrop_Transition01_MAT";

    [Fact]
    public void TheRealAnniversaryStencilMaterialResolvesToWritesNothingVisible()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map22.wad.client");
        if (!File.Exists(wadPath)) return;   // no game install on this machine - nothing to assert against
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(wadPath, resolver);
        string? BinName(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => db.TryGetPath(h, out var p) ? p : null;

        ulong h = HashAlgorithms.WadPath("data/maps/mapgeometry/map22/anniversary.materials.bin");
        if (!wad.TryGetEntry(h, out _)) return;   // the asset moved - nothing to assert against
        var bin = wad.Extract(h);
        var doc = MaterialDocument.Parse(bin, BinName, WadPathOf);

        foreach (var name in new[] { StencilMaterial, TransitionMaterial })
        {
            var mat = doc.Materials.SingleOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(mat);

            // pin the exact evidence values, so a future patch changing this material is visible here first
            Assert.Equal(32, mat!.WriteMask);
            Assert.False(mat.DepthEnable);
            Assert.False(mat.BlendEnable);
            Assert.Equal("Shaders/StaticMesh/DefaultEnv_Transition", mat.RenderShader);

            Assert.Equal((byte)0, mat.Profile.AuthoredColorWriteMask);
            Assert.False(mat.Profile.AuthoredWriteMaskHasDepthBit);
        }
    }

    // ===================================================== real data + a real device: what Dx11SceneBuilder hands the renderer

    [Fact]
    public void TheRealAnniversaryStageTwoStencilMeshCommitsWithNoColourOrDepthWrite()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map22.wad.client");
        if (!File.Exists(wadPath)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(wadPath, resolver);
        string? BinName(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        byte[]? Read(ulong hh) => wad.TryGetEntry(hh, out _) ? wad.Extract(hh) : null;

        const string geoPath = "data/maps/mapgeometry/map22/anniversary.mapgeo";
        const string binPath = "data/maps/mapgeometry/map22/anniversary.materials.bin";
        ulong geoHash = HashAlgorithms.WadPath(geoPath), binHash = HashAlgorithms.WadPath(binPath);
        if (!wad.TryGetEntry(geoHash, out _) || !wad.TryGetEntry(binHash, out _)) return;

        var matBin = wad.Extract(binHash);
        var map = MapGeoDecoder.Decode(wad.Extract(geoHash), ExtendedChannelRule.From(matBin, BinName));
        var doc = MaterialDocument.Parse(matBin, BinName, WadPathOf);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;

        var scene = Dx11SceneBuilder.Prepare(cache, new ShaderPermutationIndex(Final), map, doc.Materials, Read,
            geoPath, null, false);
        Assert.Contains(scene.Slices, s => s.Name.Equals(StencilMaterial, StringComparison.OrdinalIgnoreCase));

        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;   // no D3D11 device on this machine
        Dx11SceneBuilder.Commit(renderer, scene, "");

        var mat = renderer.Materials.SingleOrDefault(m => m.Name.Equals(StencilMaterial, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(mat);
        Assert.Equal((byte)0, mat!.ColorWriteMask);
        Assert.False(mat.WritesDepth);
    }
}
