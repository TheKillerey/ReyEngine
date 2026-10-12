using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M840: Mantis_Env_Baked_PBR in the D3D11 viewport. Independent causes, each pinned here: the stand-in a
/// shader finds in GAMEPLAY_TEXTURE / CLOUD_CARDS (white turned the Void overlay on everywhere and multiplied
/// the sun by zero), the Texcoord6 tangent that never reached TEXCOORD6, and the LightRegionInfo element
/// stride that 16.20 moved from 112 to 128.
///
/// <para>The installed-game and project halves are no-ops without the install, the RTX Rift project or a
/// D3D11 device, as elsewhere in this suite.</para>
/// </summary>
public sealed class Mantis840Tests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string RtxRift = @"D:\ReyEngine\RTX Rift";

    // ------------------------------------------------------------------ the stand-in

    [Theory]
    [InlineData("GAMEPLAY_TEXTURE_SharedTexture", true)]
    [InlineData("CLOUD_CARDS_SharedTexture", true)]
    [InlineData("cloud_cards_sharedtexture", true)]
    [InlineData("IBL_CUBEMAP_SharedTexture", false)]
    [InlineData("SHADOW_MAP_DEPTH_PCF_SharedTexture", false)]
    [InlineData("DiffuseTexture__TX", false)]
    [InlineData("GAMEPLAY_TEXTURE_SharedSampler", false)]
    public void Only_the_two_one_minus_x_map_textures_read_as_zero(string name, bool zero)
        => Assert.Equal(zero, StandInPolicy.ReadsAsZero(name));

    // ------------------------------------------------------------------ TEXCOORD6

    [Fact]
    public unsafe void Texcoord6_maps_onto_the_vertex_tangent_float4_for_static_shaders()
    {
        var v = default(PreviewVertex);
        long tangentAt = (byte*)&v.Tangent - (byte*)&v;

        var (offset, comps, known) = ShaderPreviewRenderer.MapSemantic("TEXCOORD", 6, 4, texcoord6IsTangent: true);

        Assert.True(known);
        Assert.Equal(4, comps);
        Assert.Equal((uint)tangentAt, offset);
    }

    [Fact]
    public void Skinned_shaders_keep_texcoord6_on_the_zero_pad()
    {
        var (offset, _, known) = ShaderPreviewRenderer.MapSemantic("TEXCOORD", 6, 4);

        Assert.False(known);
        Assert.Equal(152u, offset);
    }

    [Fact]
    public void The_neighbouring_semantics_did_not_move()
    {
        Assert.Equal(136u, ShaderPreviewRenderer.MapSemantic("TEXCOORD", 5, 3, true).Offset);
        Assert.Equal(80u, ShaderPreviewRenderer.MapSemantic("TEXCOORD", 7, 2, true).Offset);
        Assert.Equal(24u, ShaderPreviewRenderer.MapSemantic("TANGENT", 0, 4, true).Offset);
    }

    [Fact]
    public void Authored_tangents_pass_through_and_zeros_stay_zero()
    {
        float[] pos = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        float[] tan = { 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 0, 0 };   // v0 authored (w = -1), v1 and v2 absent
        var mesh = PreviewGeometry.FromLeagueArrays("t", 3, pos, null, null, null, null, new uint[] { 0, 1, 2 },
            recentre: false, tangents: tan);

        Assert.Equal(new Vector4(1, 0, 0, -1), mesh.Vertices[0].Tangent);
        Assert.Equal(Vector4.Zero, mesh.Vertices[1].Tangent);
        Assert.Equal(Vector4.Zero, mesh.Vertices[2].Tangent);
    }

    [Fact]
    public void Callers_that_pass_no_tangents_keep_the_plus_x_placeholder()
    {
        float[] pos = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var mesh = PreviewGeometry.FromLeagueArrays("t", 3, pos, null, null, null, null, new uint[] { 0, 1, 2 });

        Assert.All(mesh.Vertices, v => Assert.Equal(new Vector4(1, 0, 0, 1), v.Tangent));
    }

    /// <summary>One triangle through the real writer and the real decoder: the Texcoord6 M433 writes is the
    /// tangent the viewport now reads.</summary>
    [Fact]
    public void The_decoder_surfaces_a_written_texcoord6()
    {
        var map = OneTriangle();
        Assert.Null(MapGeoDecoder.Decode(map.Write()).Tangents);        // no Texcoord6 yet: no tangents

        Assert.True(MeshTangentBuilder.AddTangents(map, new[] { 0 }, out _));
        var decoded = MapGeoDecoder.Decode(map.Write());

        Assert.NotNull(decoded.Tangents);
        Assert.Equal(3 * 4, decoded.Tangents!.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(1f, decoded.Tangents[i * 4 + 0], 4);      // UV runs along +X, so the tangent is +X
            Assert.Equal(0f, decoded.Tangents[i * 4 + 1], 4);
            Assert.Equal(0f, decoded.Tangents[i * 4 + 2], 4);
            Assert.Equal(1f, decoded.Tangents[i * 4 + 3], 4);      // handedness
        }
    }

    private static MapGeoBinary OneTriangle()
    {
        Vector3[] pos = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) };
        Vector2[] uv = { new(0, 0), new(1, 0), new(0, 1) };
        var data = new byte[3 * 32];
        for (int i = 0; i < 3; i++)
        {
            int at = i * 32;
            BitConverter.TryWriteBytes(data.AsSpan(at + 0), pos[i].X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 4), pos[i].Y);
            BitConverter.TryWriteBytes(data.AsSpan(at + 8), pos[i].Z);
            BitConverter.TryWriteBytes(data.AsSpan(at + 20), 1f);
            BitConverter.TryWriteBytes(data.AsSpan(at + 24), uv[i].X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 28), uv[i].Y);
        }
        var indices = new byte[6];
        for (int i = 0; i < 3; i++) BitConverter.TryWriteBytes(indices.AsSpan(i * 2), (ushort)i);

        var map = new MapGeoBinary { Version = 18 };
        map.Declarations.Add(new MapGeoBinary.VertexDeclaration
        {
            Elements = { (MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32),
                         (MapGeoBinary.ElemNormal, MapGeoBinary.FmtXYZ_Float32),
                         (MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32) },
            Padding = new byte[8 * 12],
        });
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { Data = data, HasVisibility = true, Visibility = 0xFF });
        map.IndexBuffers.Add(new MapGeoBinary.IndexBuffer { Data = indices, HasVisibility = true, Visibility = 0xFF });
        map.Meshes.Add(new MapGeoBinary.Mesh
        {
            VertexCount = 3, VertexDeclarationBase = 0, VertexBufferIds = { 0 },
            IndexCount = 3, IndexBufferId = 0, Transform = Matrix4x4.Identity,
            HasVisibility = true, Visibility = 0xFF,
            HasRegionHash = true, HasVcHash = true, HasDisableBackface = true,
            HasLayerTransition = true, RenderFlagsIsUshort = true,
        });
        map.Tail = new byte[8];
        return map;
    }

    // ------------------------------------------------------------------ the stride

    [Theory]
    [InlineData(112)]
    [InlineData(128)]
    public void Pack_fills_the_requested_stride_with_the_same_leading_record(int stride)
    {
        var sun = new MapSunProperties
        {
            SunColor = new Vector4(0.87f, 0.75f, 0.60f, 1f), SunIntensityScale = 0.5f, SunDirection = new Vector3(0, 1, 0),
        };

        byte[] b = LightRegionInfoBuilder.Pack(sun, stride);

        Assert.Equal(stride, b.Length);
        Assert.Equal(LightRegionInfoBuilder.Pack(sun), b.AsSpan(0, 112).ToArray());     // the old 112-byte record
        Assert.Equal(0.87f * 0.5f, BitConverter.ToSingle(b, 0), 6);
        Assert.All(b.AsSpan(112).ToArray(), x => Assert.Equal(0, x));                     // RimLightColor + padding: unauthored
    }

    [Theory]
    [InlineData(111)]
    [InlineData(113)]
    [InlineData(0)]
    [InlineData(1 << 20)]
    public void An_implausible_stride_is_refused(int stride)
    {
        Assert.False(LightRegionInfoBuilder.IsValidStride(stride));
        Assert.Throws<ArgumentOutOfRangeException>(() => LightRegionInfoBuilder.Pack(null, stride));
    }

    [Fact]
    public void Both_known_strides_are_valid_and_the_default_is_the_current_game()
    {
        Assert.True(LightRegionInfoBuilder.IsValidStride(112));
        Assert.True(LightRegionInfoBuilder.IsValidStride(128));
        Assert.Equal(128, LightRegionInfoBuilder.CurrentStride);
    }

    [Fact]
    public void A_structured_buffer_reports_its_struct_stride_and_a_texture_does_not()
    {
        var sb = new DxbcResource("LightRegionInfo_SharedDataBuffer", DxbcResourceKind.Structured, 11, 1, 0, 0, 0, 128);
        var tx = new DxbcResource("CLOUD_CARDS_SharedTexture", DxbcResourceKind.Texture, 9, 1, 4, 5, 0, 0xFFFFFFFF);

        Assert.Equal(128u, sb.StructStride);
        Assert.Equal(0u, tx.StructStride);
    }

    /// <summary>The installed 16.20 cache: every pixel permutation that declares LightRegionInfo reports the
    /// stride the buffer must have - read from the shader's own RDEF, not assumed.</summary>
    [Fact]
    public void The_installed_mantis_and_lit_uber_pixel_shaders_declare_the_stride_the_reflection_reports()
    {
        if (!Directory.Exists(Final) || Hashes.Value is not { } db) return;
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(db), out _);
        if (cache is null) return;

        foreach (string shader in new[]
                 {
                     "assets/shaders/generated/Shaders/StaticMesh/Mantis_Env_Baked_PBR",
                     "assets/shaders/generated/Shaders/SkinnedMesh/Diffuse_Only",
                 })
        {
            var toc = cache.ReadToc(ShaderCacheReader.TocPathFor(shader, DxbcStage.Pixel));
            if (toc is null) continue;
            int seen = 0;
            foreach (var blob in toc.Permutations.Select(p => p.BlobIndex).Distinct())
            {
                var sh = cache.LoadShader(toc.Path, blob, out _);
                var lr = sh?.Resources.FirstOrDefault(r => r.Name == "LightRegionInfo_SharedDataBuffer");
                if (lr is null) continue;
                seen++;
                Assert.Equal(DxbcResourceKind.Structured, lr.Kind);
                Assert.True(LightRegionInfoBuilder.IsValidStride(lr.StructStride), $"{shader} blob {blob}: {lr.StructStride}");
                Assert.True(lr.StructStride == 112 || lr.StructStride == 128, $"{shader} blob {blob}: {lr.StructStride}");
            }
            Assert.True(seen > 0, $"{shader}: no permutation declared LightRegionInfo");
        }
    }

    private static readonly Lazy<HashDatabase?> Hashes = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
    });

    // ------------------------------------------------------------------ real data, real builder, real device

    /// <summary>The user's RTX Rift project (every ground piece is Mantis_Env_Baked_PBR), read-only, through
    /// Dx11SceneBuilder and ShaderPreviewRenderer.RenderFrame. Measured before the fix: the ground was a
    /// grey-violet swirl (mean saturation 0.096 on this piece) with the sun killed; after it is the textured map (0.22).</summary>
    [Fact]
    public void The_rtx_rift_ground_is_not_covered_in_void_and_the_sun_changes_pixels()
    {
        // Opt-in (REYENGINE_M840_RTX=1): it needs the user's RTX Rift project, and preparing even one 4K PBR
        // piece of it peaks at ~2 GB, which in the parallel full suite starved the map tests running beside it
        // (OutOfMemory in three of them, all inside this test's window).
        if (System.Environment.GetEnvironmentVariable("REYENGINE_M840_RTX") is not { Length: > 0 }) return;
        string folder = Path.Combine(RtxRift, "Map11");
        string wads = Path.Combine(Final, "Maps", "Shipping");
        if (!Directory.Exists(folder) || !Directory.Exists(wads) || Hashes.Value is not { } db) return;

        var resolver = new WadPathResolver(db);
        string? BinName(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => db.TryGetPath(h, out var p) ? p : null;

        var project = new Dictionary<ulong, string>();
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            project[HashAlgorithms.WadPath(Path.GetRelativePath(folder, f).Replace('\\', '/').ToLowerInvariant())] = f;
        var archives = new List<WadArchive>();
        try
        {
            foreach (var w in new[]
                     {
                         Path.Combine(wads, "Map11.wad.client"), Path.Combine(wads, "Common.wad.client"),
                         Path.Combine(Final, "Shaders", "Shaders.wad.client"),
                     })
                if (File.Exists(w)) archives.Add(WadArchive.Open(w, resolver));

            byte[]? Read(ulong h)
            {
                if (project.TryGetValue(h, out var f)) return File.ReadAllBytes(f);
                foreach (var a in archives) if (a.TryGetEntry(h, out _)) return a.Extract(h);
                return null;
            }

            const string geoPath = "data/maps/mapgeometry/map11/base_srx.mapgeo";
            var geo = Read(HashAlgorithms.WadPath(geoPath));
            var bin = Read(HashAlgorithms.WadPath("data/maps/mapgeometry/map11/base_srx.materials.bin"));
            if (geo is null || bin is null) return;

            var map = MapGeoDecoder.Decode(geo, ExtendedChannelRule.From(bin, BinName));

            // the mapgeo carries Texcoord6 on the Mantis pieces; the others contribute zeros
            Assert.NotNull(map.Tangents);
            Assert.Equal(map.Positions.Length / 3 * 4, map.Tangents!.Length);
            int authored = 0, zero = 0;
            for (int i = 0; i < map.Tangents.Length; i += 4)
            {
                float len = MathF.Sqrt(map.Tangents[i] * map.Tangents[i] + map.Tangents[i + 1] * map.Tangents[i + 1]
                                       + map.Tangents[i + 2] * map.Tangents[i + 2]);
                if (len == 0f) { zero++; continue; }
                authored++;
                Assert.InRange(len, 0.99f, 1.01f);
            }
            Assert.True(authored > 0 && zero > 0, $"authored {authored}, absent {zero}");

            var doc = MaterialDocument.Parse(bin, BinName, WadPathOf);
            using var cache = ShaderCacheReader.Open(Final, resolver, out _);
            if (cache is null) return;
            // ONE Mantis material is handed over: every other slice then fails fast as "no material binding", so no
            // shader loads and no texture decodes for them. The whole RTX scene decodes 4K PBR textures for 161
            // materials and peaks near 16 GB in the process, which starved the map-heavy tests that ran after it
            // (OutOfMemory). One material keeps the real builder, shaders and device at a fraction of that.
            var mantis = doc.Materials.Where(m => m.Name.EndsWith("Order_Dragon_Chemtech_A_MAT", StringComparison.OrdinalIgnoreCase)
                                                  && m.RenderShader?.Contains("Mantis_Env_Baked_PBR", StringComparison.OrdinalIgnoreCase) == true).ToList();
            if (mantis.Count == 0) return;                      // the project no longer has that piece
            var prepared = Dx11SceneBuilder.Prepare(cache, new ShaderPermutationIndex(Final), map, mantis, Read,
                geoPath, null, false);

            using var renderer = new ShaderPreviewRenderer();
            if (!renderer.Initialize(out _)) return;             // no D3D11 device here
            Dx11SceneBuilder.Commit(renderer, prepared, "test");

            var authoredSun = MapLighting.EffectiveSun(bin, () => null);
            var sun = MapSunRender.RenderForm(authoredSun);
            var drawn0 = renderer.Materials.Where(m => m.Bounds is not null).ToList();
            Assert.NotEmpty(drawn0);
            var lo = drawn0.Select(m => m.Bounds!.Value.Min).Aggregate(Vector3.Min);
            var hi = drawn0.Select(m => m.Bounds!.Value.Max).Aggregate(Vector3.Max);
            byte[]? Render(float sunScale)
            {
                var s = sun with { SunIntensityScale = sun.SunIntensityScale * sunScale };
                if (!MapThumbnailCamera.TryFrameBox(lo, hi,
                        1280f / 800f, 40f * MathF.PI / 180f, out var frame)) return null;
                return renderer.RenderFrame(1280, 800, new PreviewSettings
                {
                    SuppliedView = frame.View, SuppliedProjection = frame.Projection, SuppliedCameraPosition = frame.Eye,
                    AlphaBlend = true, DepthTest = true, CullBackFaces = true, SortByPipeline = true,
                    MirrorX = true, TransposeMatrices = true, Bloom = false, Shadows = true, DebugMode = 1,
                    ClearColor = new Vector4(0.1f, 0.1f, 0.12f, 1f), TimeSeconds = 1f,
                    MapSunColor = s.SunColor, MapSunDirection = s.SunDirection,
                    MapLightMapScale = MapSunRender.LightmapScale(authoredSun), MapSun = s,
                    EnvFog = s.FogEnabled ? MapSunProperties.EnvFogConstants(s, true) : null,
                }, out _);
            }

            // RenderFrame hands back ONE reused buffer, so the first frame is copied before the second is drawn
            byte[]? one = Render(1f)?.ToArray();
            byte[]? five = Render(5f);
            Assert.NotNull(one);
            Assert.NotNull(five);

            // BGRA. Mean (max-min)/max over the frame: the void swirl is grey-violet and scored 0.096.
            // Only pixels the map drew count: the clear colour is itself slightly saturated.
            double sat = 0;
            int drawn = 0, px = one!.Length / 4;
            for (int i = 0; i < one.Length; i += 4)
            {
                int mx = Math.Max(one[i], Math.Max(one[i + 1], one[i + 2])), mn = Math.Min(one[i], Math.Min(one[i + 1], one[i + 2]));
                if (mx < 40) continue;
                drawn++;
                sat += (mx - mn) / (double)mx;
            }
            Assert.True(drawn > 1000 && sat / drawn > 0.16, $"mean saturation {sat / drawn:0.###}: the Void overlay is on everywhere");

            // the sun must reach the pixels: x5 vs x1 over the same frame
            long diff = 0;
            for (int i = 0; i < one.Length; i += 4)
                diff += Math.Abs(one[i] - five![i]) + Math.Abs(one[i + 1] - five[i + 1]) + Math.Abs(one[i + 2] - five[i + 2]);
            Assert.True(diff / (double)drawn > 10, $"sun x5 vs x1 mean RGB diff over drawn pixels {diff / (double)drawn:0.###}: the sun is not reaching Mantis");
        }
        finally
        {
            foreach (var a in archives) a.Dispose();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();     // hand the multi-GB scene back before the next test
        }
    }
}
