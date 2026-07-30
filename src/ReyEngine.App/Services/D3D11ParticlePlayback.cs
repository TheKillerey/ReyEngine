using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M232: animated VFX playback through Riot's own particle shaders.</para>
///
/// <para>The existing <see cref="VfxParticleSimulator"/> does the physics — it is GL-free, so it can drive a
/// D3D11 preview just as well as the OpenGL one. This class is the bridge: it resolves each emitter's shader
/// permutation from the emitter's authored flags (see <see cref="VfxShaderFlags"/>), then every frame turns
/// the simulator's packed instance data into camera-facing quads in one shared dynamic vertex buffer, with
/// one draw slice per emitter.</para>
///
/// <para>Deliberately in the App layer: <c>ReyEngine.Rendering.D3D11</c> must not take a dependency on the
/// GL assembly, and this needs both.</para>
/// </summary>
public sealed class D3D11ParticlePlayback
{
    /// <summary>19 floats per particle, defined by VfxParticleRenderer.Stride and filled by
    /// VfxParticleSimulator.BuildInstances: pos(0-2) sizeX(3) sizeY(4) rgba(5-8) rot(9) frame(10)
    /// age(11) vel(12-14) euler(15-17) erosionDrive(18).</summary>
    private const int Stride = 19;
    private const int PosX = 0, SizeX = 3, SizeY = 4, ColR = 5, Rot = 9, Frame = 10, Erosion = 18;

    /// <summary>A hard ceiling on quads per frame, so a pathological system cannot grow the buffer without
    /// bound. Reported rather than silently applied - see <see cref="Report"/>.</summary>
    private const int MaxQuads = 20000;

    private readonly ShaderPreviewRenderer _renderer;
    private readonly ShaderCacheReader _cache;
    private readonly Func<string, byte[]?> _readAsset;

    private VfxParticleSimulator? _sim;
    private readonly List<Slice> _slices = new();
    private PreviewVertex[] _verts = Array.Empty<PreviewVertex>();
    private uint[] _indices = Array.Empty<uint>();
    private int _clamped;

    private sealed class Slice
    {
        public required PreviewMaterial Material { get; init; }
        public required int EmitterIndex { get; init; }
        public required string Name { get; init; }
        public int Quads;
        /// <summary>M283: >= 0 for a mesh-primitive emitter - its handle into the renderer's mesh
        /// geometry. Such a slice contributes no quads and is skipped by the packing loop entirely.</summary>
        public int GeometryId { get; init; } = -1;
        public ReyEngine.Formats.Meshes.VfxMeshAnimation? Animation { get; init; }
        public bool IsMesh => GeometryId >= 0;
    }

    public string Report { get; private set; } = "";
    public int LiveParticles { get; private set; }
    public int DrawSlices => _slices.Count;

    public D3D11ParticlePlayback(ShaderPreviewRenderer renderer, ShaderCacheReader cache,
        Func<string, byte[]?> readAsset)
    {
        _renderer = renderer;
        _cache = cache;
        _readAsset = readAsset;
    }

    // ---------------------------------------------------------------- build

    /// <summary>Resolve one pipeline per emitter and start the simulation. Returns false with a reason in
    /// <see cref="Report"/> when nothing could be built.</summary>
    /// <param name="clearMaterials">M264: false when the renderer is already holding a map scene, so the
    /// particles are added ON TOP of it instead of replacing it. The preview window still clears, because
    /// there it is the only thing on screen.</param>
    public bool Load(VfxSystemDefinition system, out string? error, bool clearMaterials = true)
    {
        error = null;
        var sb = new StringBuilder();
        if (clearMaterials) _renderer.ClearMaterials();
        _slices.Clear();

        var tocs = VfxD3D11EmitterPipeline.ReadTocs(_cache, out error);
        if (tocs is null) return false;

        sb.AppendLine($"SYSTEM  {system.Name}");
        _sim = new VfxParticleSimulator(seed: 12345);
        _sim.SetSystem(system, Vector3.Zero);

        sb.AppendLine($"{system.Emitters.Count} emitter(s) authored, {_sim.Emitters.Count} visual");
        if (_sim.Emitters.Count < system.Emitters.Count)
            sb.AppendLine($"   {system.Emitters.Count - _sim.Emitters.Count} non-visual emitter(s) skipped "
                          + "(no texture and no mesh) - the simulator does not run them");
        sb.AppendLine();

        // M236: iterate the SIMULATOR's emitter list, not the system's.
        //
        // SetSystem drops every non-visual emitter (`if (!includeNonVisual && !e.IsVisual) continue`), so
        // the two lists have different lengths and different indices. Slices built from system.Emitters
        // then indexed _sim.Emitters, which threw out of range on the very first Tick - the crash on
        // loading a particle. Taking the definition off EmitterState keeps index and definition in step
        // by construction, so they cannot drift apart again.
        for (int i = 0; i < _sim.Emitters.Count; i++)
        {
            var e = _sim.Emitters[i].Def;

            sb.AppendLine($"[{i}] {e.Name}");

            // M266: the whole defines -> permutation -> pipeline -> textures -> Params sequence now lives in
            // VfxD3D11EmitterPipeline, shared with the map viewport. It used to be inline here, and each of
            // the two bugs this path has had (M236, M237) was a thing one copy of it knew and the other
            // did not.
            // M283: a mesh-primitive emitter draws its .skn, not a billboard. This path used to have no
            // mesh check at all - it built a quad pipeline for one and drew the mesh's texture as flat
            // cards, so the two D3D11 hosts disagreed: the map viewport skipped these, this one faked them.
            int geometryId = -1;
            ReyEngine.Formats.Meshes.VfxMeshAnimation? meshAnim = null;
            if (e.IsMeshPrimitive)
            {
                var decoded = DecodeMesh(e, sb);
                if (decoded is null) { sb.AppendLine("     mesh primitive: no usable mesh - not drawn"); continue; }
                geometryId = _renderer.CreateMeshGeometry(decoded.Positions, decoded.Uvs,
                    decoded.Indices is { Length: > 0 } ? decoded.Indices : null);
                if (geometryId < 0) { sb.AppendLine("     mesh primitive: geometry upload failed"); continue; }
                meshAnim = decoded.Animation;
            }

            var mat = VfxD3D11EmitterPipeline.Build(_renderer, _cache, tocs, e,
                sampler => DecodeByPath(sampler, e, sb), sb);
            if (mat is null) continue;

            if (geometryId >= 0)
            {
                mat.MeshGeometryId = geometryId;
                mat.UsesDynamicMesh = false;
                sb.AppendLine($"     mesh primitive: geometry {geometryId}"
                    + (meshAnim is not null ? ", animated" : ", bind pose"));
            }

            // M235: Build CREATES the pipeline but does not register it for drawing - the caller must add
            // it, which is what LoadShaders does for the single-shader path. Without this the renderer has
            // no materials at all, IsReady is false, and RenderFrame bails out with "no shader loaded": the
            // entire particle path built correct pipelines and drew nothing.
            _renderer.AddMaterial(mat);
            _slices.Add(new Slice
            {
                Material = mat, EmitterIndex = i, Name = e.Name,
                GeometryId = geometryId, Animation = meshAnim,
            });
        }

        if (_slices.Count == 0)
        {
            error = "no emitter produced a usable pipeline";
            Report = sb.ToString();
            return false;
        }

        _renderer.SetDynamicMesh(MaxQuads * 4, MaxQuads * 6);

        sb.AppendLine();
        sb.AppendLine($"pipelines: {_renderer.PipelineCacheHits} cache hit(s), "
                      + $"{_renderer.PipelineCacheMisses} built, {_renderer.CachedPipelineCount} resident");

        Report = sb.ToString();
        return true;
    }

    /// <summary>The preview window's half of the sprite seam: it holds an asset reader, so it resolves a
    /// stage by reading and decoding the emitter's authored path. The map viewport instead hands back the
    /// TextureImage its view-model already resolved, which is what makes the two agree about WHICH file a
    /// path landed on.</summary>
    /// <summary>M283: read and decode a mesh emitter's .skn/.scb, plus its skeleton and animation when it
    /// has them. The map viewport gets this from the view-model's cache; this window holds an asset reader
    /// instead, so it does its own read - the same split the sprite stages already have.</summary>
    private ReyEngine.Formats.Meshes.StaticMeshData? DecodeMesh(VfxEmitterDefinition e, StringBuilder sb)
    {
        if (string.IsNullOrEmpty(e.MeshPath)) return null;
        byte[]? bytes;
        try { bytes = _readAsset(e.MeshPath.ToLowerInvariant()); }
        catch (Exception ex) { sb.AppendLine($"     mesh read failed: {ex.Message}"); return null; }
        if (bytes is null) return null;

        if (!e.MeshPath.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
        {
            try { return ReyEngine.Formats.Meshes.StaticObjectDecoder.Decode(bytes, e.MeshPath); }
            catch (Exception ex) { sb.AppendLine($"     mesh decode failed: {ex.Message}"); return null; }
        }

        try
        {
            var m = ReyEngine.Formats.Meshes.SkinnedMeshDecoder.Decode(bytes);
            ReyEngine.Formats.Meshes.VfxMeshAnimation? anim = null;
            if (m.CanSkin && e.MeshSkeletonPath is { } sklP && e.MeshAnimationPath is { } anmP)
            {
                try
                {
                    var sklB = _readAsset(sklP.ToLowerInvariant());
                    var anmB = _readAsset(anmP.ToLowerInvariant());
                    if (sklB is not null && anmB is not null)
                        anim = new ReyEngine.Formats.Meshes.VfxMeshAnimation(m,
                            ReyEngine.Formats.Skeletons.SkeletonDecoder.Decode(sklB),
                            ReyEngine.Formats.Animation.AnimationDecoder.Decode(anmB, System.IO.Path.GetFileName(anmP)));
                }
                catch { /* bind pose fallback, exactly as the view-model's resolver does */ }
            }
            return new ReyEngine.Formats.Meshes.StaticMeshData(m.Positions, m.Uvs, m.Indices,
                System.IO.Path.GetFileName(e.MeshPath)) { Animation = anim };
        }
        catch (Exception ex) { sb.AppendLine($"     skn decode failed: {ex.Message}"); return null; }
    }

    private VfxD3D11EmitterPipeline.Sprite? DecodeByPath(string sampler, VfxEmitterDefinition e, StringBuilder sb)
    {
        string? path = sampler switch
        {
            "TEXTURE" => e.TexturePath,
            "TEXTUREMULT" => e.TextureMultPath,
            "sAlphaErosionTexture" => e.AlphaErosion?.MapPath,
            "sPalettesTexture" => e.Palette?.TexturePath,
            // M282: the heat-haze normal map, read by path like every other stage in this window.
            "DISTORTION" => e.Distortion?.NormalMapTexturePath,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path)) return null;

        string key = path.ToLowerInvariant();
        // Deferred: the pipeline probes the texture pool by this key first, so a repeat play of the same
        // system costs no WAD read at all.
        return new VfxD3D11EmitterPipeline.Sprite(key, () =>
        {
            var data = _readAsset(key);
            if (data is null || data.Length == 0) { sb.AppendLine($"     {sampler}: NOT FOUND {path}"); return null; }
            return ReyEngine.Core.Decoding.TextureDecoder.Decode(data);
        });
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>Advance the simulation and rebuild the vertex buffer for this frame.</summary>
    public void Tick(float dt, Vector3 toCamera, Vector3 worldUp)
    {
        if (_sim is null || _slices.Count == 0) return;
        _sim.Update(dt);

        // Billboard basis. quad_vs does not billboard - it only projects - so the orientation is built
        // here, exactly as the engine's particle system builds it on the CPU.
        // M232: the billboard basis and the packing rules live in ParticleQuadBuilder, in the D3D11
        // assembly, so they can be unit-tested without the simulator or a device.
        var (right, up, normal) = ParticleQuadBuilder.Basis(toCamera, worldUp);

        // Bounds-guarded on purpose. The index/definition pairing is now correct by construction, but a
        // simulator that reshapes its emitter list for any other reason should degrade to drawing nothing
        // rather than taking the whole app down - a preview is not worth a crash.
        int totalQuads = 0;
        foreach (var sl in _slices)
            if (!sl.IsMesh && (uint)sl.EmitterIndex < (uint)_sim.Emitters.Count)
                totalQuads += _sim.Emitters[sl.EmitterIndex].InstanceCount;
        _clamped = Math.Max(0, totalQuads - MaxQuads);
        totalQuads = Math.Min(totalQuads, MaxQuads);
        LiveParticles = totalQuads;

        if (_verts.Length < totalQuads * 4)
        {
            _verts = new PreviewVertex[Math.Max(totalQuads * 4, 256)];
            _indices = new uint[Math.Max(totalQuads * 6, 384)];
        }

        int v = 0, idx = 0;
        foreach (var sl in _slices)
        {
            if ((uint)sl.EmitterIndex >= (uint)_sim.Emitters.Count) { sl.Material.Visible = false; continue; }
            var es = _sim.Emitters[sl.EmitterIndex];

            // M283: a mesh emitter draws its own geometry, so it takes none of the quad packing below.
            if (sl.IsMesh)
            {
                var mm = sl.Material;
                mm.Visible = es.InstanceCount > 0;
                mm.MeshInstances = es.Instances;
                mm.MeshInstanceCount = es.InstanceCount;
                mm.MeshRight = es.PlacementRight;
                mm.MeshUp = es.PlacementUp;
                mm.MeshForward = es.PlacementForward;
                mm.MeshUvOffset = es.Def.UvScrollRate * es.EmitterAge;
                mm.MeshUvOffsetMult = es.Def.TextureMultUvScrollRate * es.EmitterAge;
                var d0 = es.Def.TexDiv;
                mm.MeshTexDiv = new Vector2(d0.X > 0 ? d0.X : 1f, d0.Y > 0 ? d0.Y : 1f);
                var d1 = es.Def.TextureMultTexDiv;
                mm.MeshTexDivMult = new Vector2(d1.X > 0 ? d1.X : 1f, d1.Y > 0 ? d1.Y : 1f);
                if (sl.Animation is { } anim && anim.Clip.Duration > 1e-3f)
                {
                    float t = es.EmitterAge % anim.Clip.Duration;
                    var frame = ReyEngine.Formats.Animation.SkinnedMeshAnimator.Skin(
                        anim.Mesh, anim.Skeleton, anim.Clip, t);
                    _renderer.UpdateMeshGeometryPositions(sl.GeometryId, frame.Positions);
                }
                continue;
            }

            int start = idx;
            // M238: each emitter carries its own orientation mode, and arbitraryQuad needs the emitter's
            // PLACEMENT frame, which the simulator computed when the system was placed.
            var orient = new ParticleQuadBuilder.QuadOrientation(
                es.Def.IsArbitraryQuad, es.Def.IsDirectionOriented,
                es.PlacementRight, es.PlacementUp, es.PlacementForward);

            int drawn = ParticleQuadBuilder.Append(es.Instances, es.InstanceCount,
                _verts, ref v, _indices, ref idx, right, up, normal, orient);

            sl.Quads = drawn;
            sl.Material.StartIndex = start;
            sl.Material.IndexCount = idx - start;
            sl.Material.Visible = drawn > 0;
        }

        _renderer.UpdateDynamicMesh(_verts, v, _indices, idx);
    }

    /// <summary>Live per-emitter counts for the debug tab, including anything the quad ceiling dropped.</summary>
    public string FrameReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"live particles: {LiveParticles}   ·   draw slices: {_slices.Count}");
        if (_clamped > 0)
            sb.AppendLine($"CLAMPED: {_clamped} particle(s) over the {MaxQuads} quad ceiling were not drawn this frame");
        foreach (var sl in _slices)
            sb.AppendLine($"   [{sl.EmitterIndex}] {sl.Name,-34} {sl.Quads,6} quads   {(sl.Material.Additive ? "additive" : "alpha")}");
        return sb.ToString();
    }

    /// <summary>Emitter count the simulator is actually running, for the report.</summary>
    public int SimulatedEmitters => _sim?.Emitters.Count ?? 0;



    public void Restart() => _sim?.Reset();
}
