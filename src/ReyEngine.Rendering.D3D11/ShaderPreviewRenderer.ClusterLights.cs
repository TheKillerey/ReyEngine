using System.Numerics;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace ReyEngine.Rendering.D3D11;

// M456: RIOT'S OWN light loop, fed rather than imitated.
//
// M452 added dynamic point lights to this surface as an additive overlay pass (see
// ShaderPreviewRenderer.DynamicLights.cs). That is structurally wrong, not mis-tuned: Riot has no additive
// light pass at all. Every light is evaluated INSIDE the same forward pixel shader that computes the base
// colour, and the result leaves the shader as one colour the material's blend state consumes exactly once
// (docs/research/light-system.md §3.4, §7.1). An overlay therefore has to re-derive surface alpha, re-run
// the alpha test and exclude decals and additive materials to avoid painting light where the base pass
// painted none - three rules that only approximate what the real shader does for free.
//
// The real shader already ships. Both DefaultEnv_Flat and Mantis_Env_Baked_PBR cook a
// USE_DYNAMIC_LIGHTING=1 permutation with the complete point/spot loop compiled in, so selecting it
// (Dx11SceneBuilder) and supplying the three CPU-side structures it reads is the whole job. No shader
// authoring at all.
//
// WHAT THIS FILE OWNS - the GPU side of those structures:
//   ClusterData cbuffer                        -> WORLD_TO_CLUSTER_TRANSFORM + CLUSTER_MAX_CLAMP,
//                                                 written by name in FillConstantBuffer
//   CLUSTER_MAP_SharedTexture                  -> Texture3D<uint>, one uint per cell
//   CLUSTER_DATA_BUFFER_SharedDataBuffer       -> StructuredBuffer<uint4>, stride 16
//   LightRegionInfo_SharedDataBuffer           -> a zeroed one-element dummy (§1.6: nothing on Live
//                                                 authors a light region, so zero IS the shipped answer)
//   DYNAMIC_ENV_LIGHT_FACTOR / _IDS            -> zeroed 1x1 stand-ins, same reason
//
// EVERY REGISTER IS RESOLVED BY REFLECTION. The two families do not agree on a single slot: ClusterData is
// b4 on Mantis and b2 on DefaultEnv_Flat, CLUSTER_MAP is t10 vs t5, CLUSTER_DATA_BUFFER t14 vs t6. Nothing
// here is hardcoded.
//
// The cluster CONTENT is built by ReyEngine.Formats.Lighting.ClusterLightBuilder, which is device-free and
// unit-tested - the bit packing and the float bit-casts are where this goes wrong, and none of that needs a
// GPU to be wrong.
public sealed unsafe partial class ShaderPreviewRenderer
{
    // --- the uploaded resources -------------------------------------------------------------------
    private ComPtr<ID3D11Texture3D> _clusterMapTex;
    private ComPtr<ID3D11ShaderResourceView> _clusterMapSrv;
    private ComPtr<ID3D11Buffer> _clusterDataBuf;
    private ComPtr<ID3D11ShaderResourceView> _clusterDataSrv;
    private int _clusterDataCapacityUint4;

    /// <summary>The one-element <c>LightRegionRenderData</c> (112 B stride, §1.4), packed from the map's
    /// authored sun by <see cref="LightRegionInfoBuilder"/>.
    ///
    /// <para><b>M463 corrects M456 here.</b> This buffer used to be zero-filled, on the same reasoning that
    /// is genuinely right for the two dynamic-env TEXTURES below (§1.6: nothing on Live authors a light
    /// region). It does not transfer. With the FACTOR texture at zero the shader computes
    /// <c>wDefault = 1 - saturate(0) = 1</c> and routes the whole contribution to element 0 - so element 0
    /// is not the unused fallback, it is the ONLY entry read. And offset +0 is the sun: Mantis never reads
    /// <c>SUN_LIGHT_COLOR</c> (§1.7), so a zeroed record multiplied every Mantis pixel's sun by zero.</para>
    ///
    /// <para>Dynamic rather than Immutable now, because the Lighting panel edits it live.</para></summary>
    private ComPtr<ID3D11Buffer> _lightRegionBuf;
    private ComPtr<ID3D11ShaderResourceView> _lightRegionSrv;

    /// <summary>The record last uploaded, so an unchanged sun does not re-Map the buffer every frame.
    /// Null until the first upload, which is why <see cref="UpdateLightRegion"/> cannot skip on a null
    /// sun - the zero state has to be written once too.</summary>
    private byte[]? _lightRegionPacked;

    /// <summary>1x1 zero stand-ins for the light-region lookup textures. The IDS one must be a real
    /// uint4 texture: the ordinary white stand-in is RGBA8, and binding it to a <c>Texture2D&lt;uint4&gt;</c>
    /// slot is a type mismatch that the runtime resolves as undefined rather than as an error.</summary>
    private ComPtr<ID3D11ShaderResourceView> _zeroFloat4Srv;
    private ComPtr<ID3D11ShaderResourceView> _zeroUint4Srv;
    private bool _clusterStandInsTried;

    // --- the CPU-side grid and what it was built from ---------------------------------------------
    private ClusterLightGrid? _clusterGrid;
    private object? _clusterLightsSource;
    private int _clusterLightsCount = -1;
    private (float Intensity, float RadiusScale, float Spread, Vector2 ScaleXZ, Vector2 Offset, bool On) _clusterKnobs
        = (float.NaN, 0, 0, Vector2.Zero, Vector2.Zero, false);
    private (Vector3 Min, Vector3 Max) _clusterBounds = (new Vector3(float.NaN), new Vector3(float.NaN));

    /// <summary>The <c>ClusterData</c> cbuffer contents, in constant-register order. Consumed by name in
    /// <c>FillConstantBuffer</c>, so it reaches the shader through the ordinary reflected-cbuffer path
    /// rather than a second binding mechanism.</summary>
    private float[] _clusterXform = IdentityClusterXform();
    private float[] _clusterMaxClamp = { 0f, 0f, 0f, 0f };

    /// <summary>M456: slices drawn last frame through Riot's own in-shader light loop, and slices that
    /// fell back to the M452 additive overlay because their permutation has no such loop. Reported because
    /// "the lights moved to the real path" and "the lights silently stopped being drawn at all" look
    /// identical from a screenshot.</summary>
    public int ClusterLitSlices { get; private set; }
    public int OverlayLitSlices { get; private set; }

    /// <summary>Diagnostics for the last cluster build. Zero lights with a non-empty light list means the
    /// build gate rejected them; zero cells means the grid never saw the geometry.</summary>
    public int ClusterLightCount { get; private set; }
    public int ClusterNonEmptyCells { get; private set; }
    public int ClusterUint4Count { get; private set; }

    private string _clusterLastLog = "";

    /// <summary>True when at least one live material's PIXEL SHADER declares the cluster map.
    ///
    /// <para>Derived from the bytecode, never from the builder's intention. That is the point: the same
    /// fact decides whether the cluster data is uploaded AND whether the overlay skips the slice, so the
    /// two can never disagree and double-light a surface. A permutation that reached us with the loop
    /// compiled in gets fed whether or not we asked for it.</para></summary>
    public bool AnyClusterLitMaterial
    {
        get
        {
            foreach (var m in _materials) if (m.Visible && m.UsesClusterLighting) return true;
            return false;
        }
    }

    private static float[] IdentityClusterXform() => new[]
    {
        0f, 0f, 0f, 0f,
        0f, 0f, 0f, 0f,
        0f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f,
    };

    // ---------------------------------------------------------------- build + upload

    /// <summary>Rebuild and upload the cluster structures when anything they depend on has changed, then
    /// leave <see cref="_clusterXform"/> / <see cref="_clusterMaxClamp"/> ready for the cbuffer fill.
    /// A no-op when no live material can consume them.</summary>
    private void UpdateClusterLights(PreviewSettings s)
    {
        ClusterLitSlices = 0;
        OverlayLitSlices = 0;
        if (!AnyClusterLitMaterial) return;

        var lights = s.DynamicLights;
        bool on = s.DynamicLightsEnabled && lights is { Count: > 0 };

        // The same clamps the overlay and the GL viewport apply, so the two paths cannot disagree about
        // the same slider. There is no global-intensity uniform in Riot's record - only per-light colour
        // and a per-light word1.w that HALF the shader families do not read - so both the slider and the
        // per-light strength are folded into the colour by ClusterLightBuilder.MakeLight. See there.
        float intensity = Math.Clamp(s.DynamicLightIntensity, 0f, 8f);
        float radiusScale = Math.Clamp(s.DynamicLightRadiusScale, 0.01f, 40f);
        float spread = Math.Clamp(s.DynamicLightPositionScale, 0.05f, 20f);
        var scaleXZ = Vector2.Clamp(s.DynamicLightPositionScaleXZ, new Vector2(0.05f), new Vector2(20f));
        var offset = s.DynamicLightPositionOffset;
        if (intensity <= 0f) on = false;

        var bounds = ClusterWorldBounds();
        var knobs = (intensity, radiusScale, spread, scaleXZ, offset, on);

        // Reference equality is a valid change signal here for the same reason the view-model's own icon
        // cache uses it: RepublishLights builds a NEW list on every edit rather than mutating in place, so
        // a moved or retuned light always arrives as a different object. The count is checked too, so a
        // caller that does mutate in place still gets a rebuild whenever a light is added or removed.
        bool sameLights = ReferenceEquals(_clusterLightsSource, lights)
                          && _clusterLightsCount == (lights?.Count ?? 0);
        if (_clusterGrid is not null && sameLights && _clusterKnobs.Equals(knobs)
            && _clusterBounds.Equals(bounds))
        {
            CountClusterSlices();
            return;
        }

        _clusterLightsSource = lights;
        _clusterLightsCount = lights?.Count ?? 0;
        _clusterKnobs = knobs;
        _clusterBounds = bounds;

        var packed = new List<ClusterLight>(on ? lights!.Count : 0);
        if (on)
        {
            foreach (var l in lights!)
            {
                // THE shared fit formula the baker and the overlay both use - not a third copy.
                var p = Formats.Baking.BakeLighting.FitPosition(l.Position, spread, scaleXZ, offset);
                float strength = Math.Clamp(l.Intensity, 0f, 64f) * intensity;
                packed.Add(ClusterLightBuilder.MakeLight(p, l.Radius * radiusScale, l.Color, strength));
            }
        }

        _clusterGrid = ClusterLightBuilder.Build(packed, bounds.Min, bounds.Max);
        ClusterLightCount = _clusterGrid.LightCount;
        ClusterNonEmptyCells = _clusterGrid.NonEmptyCells;
        ClusterUint4Count = _clusterGrid.Uint4Count;

        Array.Copy(_clusterGrid.WorldToCluster, _clusterXform, 16);
        _clusterMaxClamp[0] = _clusterGrid.MaxClamp.X;
        _clusterMaxClamp[1] = _clusterGrid.MaxClamp.Y;
        _clusterMaxClamp[2] = _clusterGrid.MaxClamp.Z;

        UploadClusterMap(_clusterGrid);
        UploadClusterData(_clusterGrid);
        CountClusterSlices();

        // Every value here is an int, so there is no culture-sensitive formatting to guard - the
        // InvariantCulture rule this project follows is about FLOATS reaching a string.
        string line = $"cluster lights: {_clusterGrid.LightCount} light(s) -> {_clusterGrid.NonEmptyCells}"
            + $" of {_clusterGrid.CellCount} cells, {_clusterGrid.DistinctBlocks} distinct block(s), "
            + $"{_clusterGrid.Uint4Count} uint4"
            + (_clusterGrid.DroppedBindings > 0
                ? $", {_clusterGrid.DroppedBindings} binding(s) over the per-cell cap" : "");
        if (line != _clusterLastLog) { Log(line); _clusterLastLog = line; }
    }

    private void CountClusterSlices()
    {
        int cluster = 0;
        foreach (var m in _materials)
            if (m.Visible && m.MapGroupIndex >= 0 && m.UsesClusterLighting) cluster++;
        ClusterLitSlices = cluster;
    }

    /// <summary>The world box the grid spans: every map slice's bounds. Light spheres outside it are folded
    /// in by the builder, so this only has to describe the GEOMETRY.</summary>
    private (Vector3 Min, Vector3 Max) ClusterWorldBounds()
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        bool any = false;
        foreach (var m in _materials)
        {
            if (m.MapGroupIndex < 0 || m.Bounds is not { } b) continue;
            lo = Vector3.Min(lo, b.Min);
            hi = Vector3.Max(hi, b.Max);
            any = true;
        }
        return any ? (lo, hi) : (Vector3.Zero, Vector3.Zero);
    }

    private void UploadClusterMap(ClusterLightGrid grid)
    {
        if (_clusterMapTex.Handle is null)
        {
            var desc = new Texture3DDesc
            {
                Width = (uint)grid.DimX, Height = (uint)grid.DimY, Depth = (uint)grid.DimZ,
                MipLevels = 1,
                // R32_UINT, matching the shader's declared Texture3D<uint>. A typed mismatch here is not an
                // error at bind time - it is a silent read of zero, i.e. every cell pointing at header 0.
                Format = Format.FormatR32Uint,
                Usage = Usage.Default,
                BindFlags = (uint)BindFlag.ShaderResource,
            };
            ComPtr<ID3D11Texture3D> tex = default;
            if (_device.CreateTexture3D(in desc, null, ref tex) < 0)
            { Log("cluster: CreateTexture3D failed"); return; }
            _clusterMapTex = tex;

            var srvDesc = new ShaderResourceViewDesc
            {
                Format = Format.FormatR32Uint,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionTexture3D,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    Texture3D = new Tex3DSrv { MostDetailedMip = 0, MipLevels = 1 },
                },
            };
            ComPtr<ID3D11ShaderResourceView> srv = default;
            if (_device.CreateShaderResourceView(_clusterMapTex, in srvDesc, ref srv) < 0)
            { Log("cluster: CreateShaderResourceView(map) failed"); return; }
            _clusterMapSrv = srv;
        }

        fixed (uint* p = grid.Map)
            _ctx.UpdateSubresource(_clusterMapTex, 0, (Box*)null, p,
                (uint)(grid.DimX * 4), (uint)(grid.DimX * grid.DimY * 4));
    }

    private void UploadClusterData(ClusterLightGrid grid)
    {
        int need = Math.Max(1, grid.Uint4Count);
        if (_clusterDataBuf.Handle is null || _clusterDataCapacityUint4 < need)
        {
            _clusterDataSrv.Dispose(); _clusterDataSrv = default;
            _clusterDataBuf.Dispose(); _clusterDataBuf = default;

            // Grown in steps so an edit that adds one light does not recreate the buffer every frame.
            int capacity = 256;
            while (capacity < need) capacity *= 2;

            var desc = new BufferDesc
            {
                ByteWidth = (uint)(capacity * 16),
                Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.ShaderResource,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
                MiscFlags = (uint)ResourceMiscFlag.BufferStructured,
                StructureByteStride = 16,
            };
            ComPtr<ID3D11Buffer> buf = default;
            if (_device.CreateBuffer(in desc, null, ref buf) < 0)
            { Log("cluster: CreateBuffer(structured) failed"); return; }
            _clusterDataBuf = buf;
            _clusterDataCapacityUint4 = capacity;

            // Format MUST be Unknown for a structured buffer view; a typed format is rejected outright.
            var srvDesc = new ShaderResourceViewDesc
            {
                Format = Format.FormatUnknown,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionBuffer,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    Buffer = new BufferSrv
                    {
                        Anonymous1 = new BufferSrvUnion1 { FirstElement = 0 },
                        Anonymous2 = new BufferSrvUnion2 { NumElements = (uint)capacity },
                    },
                },
            };
            ComPtr<ID3D11ShaderResourceView> srv = default;
            if (_device.CreateShaderResourceView(_clusterDataBuf, in srvDesc, ref srv) < 0)
            { Log("cluster: CreateShaderResourceView(data) failed"); return; }
            _clusterDataSrv = srv;
        }

        MappedSubresource m = default;
        if (_ctx.Map(_clusterDataBuf, 0, Map.WriteDiscard, 0, ref m) < 0) return;
        // WriteDiscard hands back undefined memory, so the tail past the real data has to be cleared or a
        // stale header from a previous, larger build stays reachable.
        new Span<uint>(m.PData, _clusterDataCapacityUint4 * 4).Clear();
        grid.Data.AsSpan().CopyTo(new Span<uint>(m.PData, _clusterDataCapacityUint4 * 4));
        _ctx.Unmap(_clusterDataBuf, 0);
    }

    /// <summary>Create the light-region buffer and the two zero lookup TEXTURES.
    ///
    /// <para>§1.6 measured the textures directly: across 18 map WADs and 12,747 bins, ZERO author a light
    /// region, so on Live every Mantis pixel samples these and gets nothing. Zero is not a placeholder
    /// there - it is the shipped value, and it is what makes <c>wDefault</c> resolve to 1.</para>
    ///
    /// <para>The BUFFER is a different matter and M463 stopped treating it the same way - see
    /// <see cref="_lightRegionBuf"/>. It is created zeroed here only so a frame that reaches a shader
    /// before <see cref="UpdateLightRegion"/> has run reads defined memory rather than whatever the
    /// allocator had.</para></summary>
    private void EnsureClusterStandIns()
    {
        if (_clusterStandInsTried) return;
        _clusterStandInsTried = true;

        // Dynamic + CPU write, because M463 makes these 112 bytes editable from the Lighting panel. The
        // stride is the shader's own: every consumer issues ld_structured_indexable(..., stride=112).
        var lrDesc = new BufferDesc
        {
            ByteWidth = LightRegionInfoBuilder.Stride,
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ShaderResource,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
            MiscFlags = (uint)ResourceMiscFlag.BufferStructured,
            StructureByteStride = LightRegionInfoBuilder.Stride,
        };
        var zeros = stackalloc byte[LightRegionInfoBuilder.Stride];
        for (int i = 0; i < LightRegionInfoBuilder.Stride; i++) zeros[i] = 0;
        var init = new SubresourceData { PSysMem = zeros };
        ComPtr<ID3D11Buffer> lrBuf = default;
        if (_device.CreateBuffer(ref lrDesc, ref init, ref lrBuf) >= 0)
        {
            _lightRegionBuf = lrBuf;
            var srvDesc = new ShaderResourceViewDesc
            {
                Format = Format.FormatUnknown,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionBuffer,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    Buffer = new BufferSrv
                    {
                        Anonymous1 = new BufferSrvUnion1 { FirstElement = 0 },
                        Anonymous2 = new BufferSrvUnion2 { NumElements = 1 },
                    },
                },
            };
            ComPtr<ID3D11ShaderResourceView> srv = default;
            if (_device.CreateShaderResourceView(_lightRegionBuf, in srvDesc, ref srv) >= 0)
                _lightRegionSrv = srv;
            else Log("cluster: LightRegionInfo view failed");
        }
        else Log("cluster: LightRegionInfo buffer failed");

        // RGBA8 zero, which MakeTexture handles: it assumes 4 bytes per texel throughout.
        if (MakeTexture(new byte[] { 0, 0, 0, 0 }, 1, 1) is { } zf) _zeroFloat4Srv = zf;

        // R32G32B32A32_UINT, built HERE rather than through MakeTexture, because that method's length
        // check and SysMemPitch are both hardcoded to 4 bytes per texel and this format is 16.
        // DYNAMIC_ENV_LIGHT_IDS is declared Texture2D<uint4> and read with ld, so the ordinary white
        // RGBA8 stand-in it has been getting is a typed mismatch rather than a working default.
        var idDesc = new Texture2DDesc
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.FormatR32G32B32A32Uint,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Immutable,
            BindFlags = (uint)BindFlag.ShaderResource,
        };
        var idTexel = stackalloc uint[4] { 0, 0, 0, 0 };
        var idInit = new SubresourceData { PSysMem = idTexel, SysMemPitch = 16 };
        ComPtr<ID3D11Texture2D> idTex = default;
        if (_device.CreateTexture2D(in idDesc, in idInit, ref idTex) >= 0)
        {
            ComPtr<ID3D11ShaderResourceView> srv = default;
            if (_device.CreateShaderResourceView(idTex, null, ref srv) >= 0) _zeroUint4Srv = srv;
            else Log("cluster: DYNAMIC_ENV_LIGHT_IDS stand-in view failed");
            idTex.Dispose();
        }
        else Log("cluster: DYNAMIC_ENV_LIGHT_IDS stand-in texture failed");
    }

    // ---------------------------------------------------------------- the light region

    /// <summary>M463: pack the map's authored sun into <c>LightRegionInfo[0]</c> and upload it.
    ///
    /// <para>This is the whole fix for "the sun does not work on Mantis materials". Mantis's sun radiance is
    /// this record's <c>SunLightColor</c>, not <c>PerFramePixelCB.SUN_LIGHT_COLOR</c> - reflection marks
    /// that constant UNUSED in blob 27 and <c>cb1[6]</c> appears zero times in the listing (§1.7). While
    /// this buffer was zero-filled, every Mantis pixel multiplied its sun by zero and kept only its ambient
    /// IBL, which reads as a flat, directionless surface rather than as an obvious error.</para>
    ///
    /// <para>Runs before any material binds, alongside <see cref="UpdateClusterLights"/>. Deliberately NOT
    /// behind the <see cref="AnyClusterLitMaterial"/> gate that guards the cluster upload: that gate asks
    /// whether a shader declares the CLUSTER MAP, and a Mantis permutation cooked without
    /// <c>USE_DYNAMIC_LIGHTING</c> declares this buffer and the IBL cubemap while declaring no cluster map
    /// at all. Gating on the wrong question is how the sun would have stayed black on exactly the
    /// permutations that have no point lights to distract from it.</para></summary>
    private void UpdateLightRegion(PreviewSettings s)
    {
        EnsureClusterStandIns();
        UpdateSkyAmbient(s);
        if (_lightRegionBuf.Handle is null) return;

        byte[] packed = LightRegionInfoBuilder.Pack(s.MapSun);

        // Sequence equality rather than a cached MapSunProperties reference: the view-model rebuilds the
        // record on EVERY slider tick (RebuildSun does `_baseSun with { ... }`), so reference equality would
        // re-upload constantly, and record equality would still miss the case where two different records
        // pack to the same bytes. The bytes are what the GPU sees, so the bytes are what is compared.
        if (_lightRegionPacked is not null && _lightRegionPacked.AsSpan().SequenceEqual(packed)) return;

        MappedSubresource m = default;
        if (_ctx.Map(_lightRegionBuf, 0, Map.WriteDiscard, 0, ref m) < 0) return;
        packed.AsSpan().CopyTo(new Span<byte>(m.PData, LightRegionInfoBuilder.Stride));
        _ctx.Unmap(_lightRegionBuf, 0);
        _lightRegionPacked = packed;

        // InvariantCulture: these are FLOATS reaching a string, which is the case the project's German-
        // locale rule is actually about. A comma decimal separator here would make the diagnostics panel
        // disagree with every other number in it.
        var c = System.Globalization.CultureInfo.InvariantCulture;
        float r = BitConverter.ToSingle(packed, 0), g = BitConverter.ToSingle(packed, 4),
              b = BitConverter.ToSingle(packed, 8);
        string line = "light region[0]: sun colour ("
            + r.ToString("0.###", c) + ", " + g.ToString("0.###", c) + ", " + b.ToString("0.###", c)
            + "), probe " + LightRegionInfoBuilder.ProbeIndex.ToString(c)
            + " - Mantis reads its sun from HERE, not SUN_LIGHT_COLOR";
        if (line != _lightRegionLastLog) { Log(line); _lightRegionLastLog = line; }
    }

    private string _lightRegionLastLog = "";

    // ---------------------------------------------------------------- the sky / ambient half

    /// <summary>A 1x1 cube ARRAY tinted with the map's <c>skyLightColor</c>, standing in for
    /// <c>IBL_CUBEMAP_SharedTexture</c> when the map supplied no real probe.</summary>
    private ComPtr<ID3D11ShaderResourceView> _skyIblSrv;
    private Vector4? _skyIblColor;
    private bool _skyIblBuilt;

    /// <summary>The value <c>IBL_CUBEMAP_SCALES</c> is filled with. Null = no map sky, keep the neutral 1.</summary>
    public float? SkyAmbientScale { get; private set; }

    /// <summary>
    /// M463: make the SKY slider do something on the one family whose shader has an ambient input.
    ///
    /// <para><b>Why the sky needed a different answer from the sun.</b> The report was "skylight was not
    /// changing", and the reason is not a missing upload - it is that <c>DefaultEnv_Flat</c>, which nearly
    /// all shipped map geometry runs, HAS NO AMBIENT TERM. Blob 226's entire lighting equation is
    /// <c>light = NdotL * shadow * SUN_LIGHT_COLOR + baked.rgb * LIGHT_MAP_COLOR_SCALE</c> (lines 201-205,
    /// re-read for this milestone) - a sun and a lightmap, and nowhere to put a sky colour. On that family
    /// the field is genuinely save-only and the UI says so rather than shipping a dead slider.</para>
    ///
    /// <para><b>The PBR family is different, and it is measured.</b> Mantis's ambient is the pair of
    /// <c>texturecubearray</c> samples at blob 27 lines 494 and 498, each multiplied by
    /// <c>IBL_CUBEMAP_SCALES[probeIndex].x</c> at lines 495 and 499, accumulated by the same region weights
    /// as the sun. That is exactly an ambient environment colour times a scalar - so the authored sky maps
    /// onto it with no invention: HUE into the cube texels, STRENGTH into the scale. Keeping those two apart
    /// mirrors M451's sun split for the same reason, and it is also forced here: the cube stand-in is RGBA8
    /// and would clamp a scale above 1, while the scale is a float.</para>
    ///
    /// <para>Only ever a STAND-IN. It reaches the shader through <c>StandIn</c>, which runs only for a
    /// texture nothing else bound, so a map that ships a real IBL probe (MapLightingV2) keeps it.</para>
    /// </summary>
    private void UpdateSkyAmbient(PreviewSettings s)
    {
        // Sanitised BEFORE the change check, for two reasons that are both about NaN: (int)float.NaN is an
        // unspecified conversion in C#, and NaN != NaN would make the equality test below never match, so
        // an unsanitised NaN would rebuild this texture every single frame forever.
        static float Safe(float f) => float.IsFinite(f) ? Math.Clamp(f, 0f, 1f) : 0f;
        Vector4? want = s.MapSun is { } ms
            ? new Vector4(Safe(ms.SkyLightColor.X), Safe(ms.SkyLightColor.Y), Safe(ms.SkyLightColor.Z), 1f)
            : null;
        float? scale = s.MapSun?.SkyLightScale;
        SkyAmbientScale = scale is { } sc && float.IsFinite(sc) ? Math.Clamp(sc, 0f, 64f) : null;

        if (_skyIblBuilt && Nullable.Equals(_skyIblColor, want)) return;
        _skyIblBuilt = true;
        _skyIblColor = want;

        _skyIblSrv.Dispose();
        _skyIblSrv = default;
        if (want is not { } c) return;      // no map sun: the white neutral stays

        static byte Chan(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
        var texel = new byte[] { Chan(c.X), Chan(c.Y), Chan(c.Z), 255 };
        if (MakeTexture(texel, 1, 1, resourceDimension: 10) is { } srv) _skyIblSrv = srv;
        else Log("sky: IBL cube-array stand-in failed; ambient stays neutral white");
    }

    // ---------------------------------------------------------------- binding

    /// <summary>Is this reflected name an ENGINE-owned lighting resource - one this file supplies and no
    /// material ever binds?
    ///
    /// <para>The leading-character test is not decoration. This runs for every declared texture of every
    /// material every frame (on Map12 that is over ten thousand calls), and a chain of five string
    /// comparisons per call is real time spent proving that <c>DiffuseTexture__TX</c> is still not the
    /// cluster map. One char rejects almost everything.</para></summary>
    private static bool IsEngineLightingResource(string name) => name.Length > 0
        && name[0] is 'C' or 'L' or 'D'
        && (name.Equals("CLUSTER_MAP_SharedTexture", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CLUSTER_DATA_BUFFER_SharedDataBuffer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LightRegionInfo_SharedDataBuffer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DYNAMIC_ENV_LIGHT_IDS_SharedTexture", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DYNAMIC_ENV_LIGHT_FACTOR_SharedTexture", StringComparison.OrdinalIgnoreCase));

    /// <summary>The engine-owned SRV for a reflected resource name. A NULL handle for a name this file
    /// does owns (see <see cref="IsEngineLightingResource"/>) means creation failed - and binding nothing
    /// is then strictly better than the caller's white stand-in, because a Texture3D&lt;uint&gt; slot
    /// holding an RGBA8 2D view is a type mismatch the runtime resolves as undefined, whereas an unbound
    /// SRV is defined to read zero.</summary>
    private ComPtr<ID3D11ShaderResourceView> ClusterResourceFor(string name)
    {
        if (!IsEngineLightingResource(name)) return default;
        EnsureClusterStandIns();
        if (name.Equals("CLUSTER_MAP_SharedTexture", StringComparison.OrdinalIgnoreCase))
            return _clusterMapSrv;
        if (name.Equals("CLUSTER_DATA_BUFFER_SharedDataBuffer", StringComparison.OrdinalIgnoreCase))
            return _clusterDataSrv;
        if (name.Equals("LightRegionInfo_SharedDataBuffer", StringComparison.OrdinalIgnoreCase))
            return _lightRegionSrv;
        if (name.Equals("DYNAMIC_ENV_LIGHT_IDS_SharedTexture", StringComparison.OrdinalIgnoreCase))
            return _zeroUint4Srv;
        return _zeroFloat4Srv;
    }

    /// <summary>Bind the STRUCTURED buffers a shader declares. They never reach
    /// <see cref="BindResources"/>'s texture loop: <c>DxbcShader.Textures</c> filters on
    /// <c>DxbcResourceKind.Texture</c> and a StructuredBuffer reflects as <c>Structured</c>, so until now
    /// nothing bound them at all and the shader read an unbound SRV.</summary>
    private void BindStructuredBuffers(DxbcShader? refl, bool pixel)
    {
        if (refl is null) return;
        foreach (var r in refl.Resources)
        {
            if (r.Kind != DxbcResourceKind.Structured) continue;
            var srv = ClusterResourceFor(r.Name);
            if (srv.Handle is null) continue;
            if (pixel) _ctx.PSSetShaderResources(r.BindPoint, 1, ref srv);
            else _ctx.VSSetShaderResources(r.BindPoint, 1, ref srv);
        }
    }

    private string _lightPathLastLog = "";

    /// <summary>Say which light path the frame actually used, once per distinct answer.
    ///
    /// <para>Not per frame - <see cref="Log"/> backs a capped list the diagnostics panel shows verbatim, and
    /// a line per frame would evict everything else within seconds. Logged at all because "the lights moved
    /// to Riot's own shader" and "the lights stopped being drawn" produce the same screenshot, and this is
    /// the only place that can tell them apart.</para></summary>
    private void LogLightPath()
    {
        string line = $"lights: {ClusterLitSlices} slice(s) in-shader (Riot's own loop), "
                      + $"{OverlayLitSlices} on the M452 additive overlay";
        if (line == _lightPathLastLog) return;
        _lightPathLastLog = line;
        Log(line);
    }

    private void DisposeClusterLights()
    {
        _clusterMapSrv.Dispose(); _clusterMapTex.Dispose();
        _clusterDataSrv.Dispose(); _clusterDataBuf.Dispose();
        _lightRegionSrv.Dispose(); _lightRegionBuf.Dispose();
        _zeroFloat4Srv.Dispose(); _zeroUint4Srv.Dispose();
        _skyIblSrv.Dispose();
    }
}
