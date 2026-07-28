using System;
using System.Collections.Generic;
using System.Linq;
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
    public bool Load(VfxSystemDefinition system, out string? error)
    {
        error = null;
        var sb = new StringBuilder();
        _renderer.ClearMaterials();
        _slices.Clear();

        const string vsName = "assets/shaders/hlsl/particlesystem/quad_vs";
        const string psName = "assets/shaders/hlsl/particlesystem/quad_ps";
        var vsToc = _cache.ReadToc(ShaderCacheReader.TocPathFor(vsName, DxbcStage.Vertex));
        var psToc = _cache.ReadToc(ShaderCacheReader.TocPathFor(psName, DxbcStage.Pixel));
        if (vsToc is null || psToc is null) { error = "particlesystem/quad_vs+quad_ps not in the shader cache"; return false; }

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

            // The whole point of the milestone: the define set comes from the emitter's own flags.
            var defines = VfxShaderFlags.For(e, out var why);

            var vsPerm = ShaderCacheReader.ResolvePermutation(vsToc, defines, null, null, null, out var vw);
            var psPerm = ShaderCacheReader.ResolvePermutation(psToc, defines, null, null, null, out var pw);

            sb.AppendLine($"[{i}] {e.Name}");
            sb.AppendLine($"     defines: {(defines.Count == 0 ? "(none - base permutation)" : string.Join(", ", defines.Keys))}");
            foreach (var w in why) sb.AppendLine($"       {w}");
            sb.AppendLine($"     vs {(vsPerm is null ? "UNRESOLVED" : "blob " + vsPerm.BlobIndex)}   ps {(psPerm is null ? "UNRESOLVED" : "blob " + psPerm.BlobIndex)}");
            if (vsPerm is null) sb.AppendLine($"       vs: {vw}");
            if (psPerm is null) sb.AppendLine($"       ps: {pw}");
            if (vsPerm is null || psPerm is null) continue;

            var vs = _cache.LoadShader(ShaderCacheReader.TocPathFor(vsName, DxbcStage.Vertex), vsPerm.BlobIndex, out _);
            var ps = _cache.LoadShader(ShaderCacheReader.TocPathFor(psName, DxbcStage.Pixel), psPerm.BlobIndex, out _);
            if (vs is null || ps is null) { sb.AppendLine("       bytecode would not load"); continue; }

            var mat = _renderer.BuildMaterial(e.Name, vs, ps, 0, 0, out var rep);
            if (mat is null) { sb.AppendLine($"       pipeline failed: {rep.Error}"); continue; }

            mat.Additive = VfxShaderFlags.IsAdditive(e.BlendMode);
            sb.AppendLine($"     blend: {(mat.Additive ? "additive" : "alpha")} (blendMode {e.BlendMode})");

            // The sprite. TEXTURE__TX is the name quad_ps declares for it.
            BindTexture(mat, ps, "TEXTURE", e.TexturePath, sb);
            if (!string.IsNullOrEmpty(e.TextureMultPath)) BindTexture(mat, ps, "TEXTUREMULT", e.TextureMultPath, sb);
            if (e.AlphaErosion is not null) BindTexture(mat, ps, "sAlphaErosionTexture", e.AlphaErosion.MapPath, sb);
            if (e.Palette is not null) BindTexture(mat, ps, "sPalettesTexture", e.Palette.TexturePath, sb);

            // The flipbook atlas descriptor, per emitter: (columns, 1/columns, 1/rows).
            // Derived in M231 from quad_vs's cell arithmetic.
            mat.Params["TEXTURE_INFO"] = ParticleQuadBuilder.TextureInfo(e.TexDiv);

            // M237: pass the values that SELECTED the permutation, which the first cut did not.
            //
            // VfxShaderFlags turns ALPHA_TEST on precisely because alphaRef > 0, and then the renderer's
            // engine default bound AlphaTestReferenceValue = 0 - a cutoff of zero discards nothing, so the
            // permutation was selected and then neutered. The GL renderer discards at the authored value
            // (`if (uAlphaRef > 0.0 && t.a * vColor.a < uAlphaRef) discard`), so the two previews disagreed
            // on every one of the 425,866 emitters that author it.
            if (e.AlphaRef > 0) mat.Params["AlphaTestReferenceValue"] = new[] { e.AlphaRef / 255f, 0f, 0f, 0f };

            // Same shape of omission: quad_vs slides each vertex along its own camera ray by this, and the
            // GL path applies the emitter's authored value, while this bound a flat 0.
            if (e.DepthPushPull != 0f)
                mat.Params["PARTICLE_DEPTH_PUSH_PULL"] = new[] { e.DepthPushPull, 0f, 0f, 0f };

            // M235: BuildMaterial CREATES the pipeline but does not register it for drawing - the caller
            // must add it, which is what LoadShaders does for the single-shader path. Without this the
            // renderer has no materials at all, IsReady is false, and RenderFrame bails out with
            // "no shader loaded": the entire particle path built correct pipelines and drew nothing.
            _renderer.AddMaterial(mat);
            _slices.Add(new Slice { Material = mat, EmitterIndex = i, Name = e.Name });
        }

        if (_slices.Count == 0)
        {
            error = "no emitter produced a usable pipeline";
            Report = sb.ToString();
            return false;
        }

        _renderer.SetDynamicMesh(MaxQuads * 4, MaxQuads * 6);
        Report = sb.ToString();
        return true;
    }

    private void BindTexture(PreviewMaterial mat, DxbcShader ps, string sampler, string? path, StringBuilder sb)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var slot = ps.Textures.FirstOrDefault(t =>
            t.Name.Equals(sampler + "__TX", StringComparison.OrdinalIgnoreCase)
            || t.Name.Equals(sampler, StringComparison.OrdinalIgnoreCase));
        if (slot is null) { sb.AppendLine($"     {sampler}: no such slot in this permutation"); return; }

        string key = path.ToLowerInvariant();
        if (_renderer.TryBindCached(mat, slot.Name, key)) return;
        try
        {
            var data = _readAsset(key);
            if (data is null || data.Length == 0) { sb.AppendLine($"     {sampler}: NOT FOUND {path}"); return; }
            var img = ReyEngine.Core.Decoding.TextureDecoder.Decode(data);
            _renderer.SetTexture(mat, slot.Name, key, img.Rgba, img.Width, img.Height);
            sb.AppendLine($"     {sampler} -> {slot.Name} ({img.Width}x{img.Height})");
        }
        catch (Exception ex) { sb.AppendLine($"     {sampler}: FAILED {ex.Message}"); }
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
            if ((uint)sl.EmitterIndex < (uint)_sim.Emitters.Count)
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
            int start = idx;
            int drawn = ParticleQuadBuilder.Append(es.Instances, es.InstanceCount,
                _verts, ref v, _indices, ref idx, right, up, normal);

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
