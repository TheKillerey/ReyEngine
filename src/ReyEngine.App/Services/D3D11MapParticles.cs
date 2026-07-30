using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M266: every placed VFX system on the open map, animated into the D3D11 viewport's ONE dynamic
/// buffer.</para>
///
/// <para><b>Why one object rather than N <see cref="D3D11ParticlePlayback"/> instances.</b> The renderer has a
/// single <c>_dynVb/_dynIb</c> pair, and <c>UpdateDynamicMesh</c> always writes from offset 0. N drivers
/// would each overwrite the previous one's vertices and the last Tick of the frame would win, which reads as
/// "only one effect plays" rather than as a buffer conflict.</para>
///
/// <para><b>Emitter-major packing.</b> Materials are keyed by <see cref="VfxEmitterDefinition"/> REFERENCE, so
/// every placement of the same system shares one material and one contiguous index range. Draw calls then
/// scale with distinct (system, emitter) pairs instead of placements x emitters - a map with 2,400 placements
/// of 90 systems draws in the low hundreds. This is safe because everything placement-specific is already
/// baked CPU-side before the quads exist: the placement tint lands in the packed instance colour and the
/// placement frame lands in each <c>EmitterState</c>'s own orientation vectors.</para>
///
/// <para><b>What this path does NOT draw</b>, deliberately and counted rather than faked: mesh-primitive,
/// beam and trail emitters. <see cref="ParticleQuadBuilder"/> only emits billboards, and the GL viewport
/// routes those three classes to separate programs. Drawing them as untextured quads would put huge opaque
/// white cards over the map, which is the reason the GL path already blanks a mesh emitter it cannot
/// texture.</para>
/// </summary>
public sealed class D3D11MapParticles
{
    /// <summary>
    /// <para>The per-frame quad ceiling. 30,000 quads = 120,000 vertices x 168 B = 20.2 MB of vertex data,
    /// plus 180,000 indices x 4 B. 1.5x the per-effect ceiling <see cref="D3D11ParticlePlayback"/> uses, on
    /// the reasoning that a whole map legitimately holds more live particles than one previewed system.</para>
    ///
    /// <para>GL imposes no ceiling at all, so this is by definition a place where the two viewports can differ
    /// under load. That is why going over it is REPORTED (see <see cref="FrameReport"/>) and why the thinning
    /// is proportional rather than "the tail vanishes" - an emitter that silently stops looks exactly like an
    /// emitter that finished.</para>
    /// </summary>
    public const int DefaultMaxQuads = 30_000;

    private readonly ShaderPreviewRenderer _renderer;
    private readonly ShaderCacheReader _cache;
    private readonly int _maxQuads;

    private VfxPlayback? _playback;
    private bool _dirty;

    /// <summary>One simulator per placement, keyed by the item INSTANCE - mirrors the GL viewport's own
    /// cache. Reference equality is not an optimisation here: <see cref="VfxPlaybackItem"/> is a record, so
    /// value equality would collapse two placements of the same system at the same transform into one.</summary>
    private readonly Dictionary<VfxPlaybackItem, VfxParticleSimulator> _sims =
        new(ReferenceEqualityComparer.Instance);

    private readonly List<(VfxPlaybackItem Item, VfxParticleSimulator Sim)> _active = new();
    private readonly HashSet<VfxParticleSimulator> _activeSet = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<VfxParticleSimulator> _wanted = new(ReferenceEqualityComparer.Instance);

    private List<Slice> _slices = new();
    private readonly Dictionary<VfxEmitterDefinition, Slice> _byEmitter =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>Emitters whose pipeline would not build. Remembered so the 2,400th placement of the same
    /// system does not retry a permutation that failed identically for the first.</summary>
    private readonly HashSet<VfxEmitterDefinition> _noPipeline = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PreviewMaterial> _mine = new(ReferenceEqualityComparer.Instance);
    /// <summary>The per-slice live lists, in slice order, built once per rebuild. <see cref="Pack"/> wants a
    /// list of lists and this frame loop must not allocate one per frame.</summary>
    private readonly List<IReadOnlyList<VfxParticleSimulator.EmitterState>> _liveBySlice = new();

    private PreviewVertex[] _verts = Array.Empty<PreviewVertex>();
    private uint[] _indices = Array.Empty<uint>();
    private PackedRange[] _ranges = Array.Empty<PackedRange>();

    /// <summary>One draw. <see cref="Sources"/> is every live emitter state that shares this definition -
    /// one per placement - paired with its owning simulator, because a placement culled by the camera must
    /// contribute nothing while its neighbours still draw.</summary>
    private sealed class Slice
    {
        public required PreviewMaterial Material { get; init; }
        public required VfxEmitterDefinition Def { get; init; }
        public readonly List<(VfxParticleSimulator Owner, VfxParticleSimulator.EmitterState State)> Sources = new();
        /// <summary>Refilled each frame from <see cref="Sources"/>, reused so a 60-slice frame does not
        /// allocate 60 lists.</summary>
        public readonly List<VfxParticleSimulator.EmitterState> Live = new();
    }

    /// <summary>M283: a mesh-primitive emitter. Unlike <see cref="Slice"/> this is one per PLACEMENT, not
    /// one per emitter definition, because everything a mesh draw needs is placement-specific: the basis
    /// vectors come from that placement's transform, and an animated mesh is skinned to that placement's
    /// own emitter age. Two base doors on opposite sides of the map are the same .skn at different
    /// orientations and different points in their animation, so they cannot share geometry.</summary>
    private sealed class MeshSlice
    {
        public required PreviewMaterial Material { get; init; }
        public required VfxEmitterDefinition Def { get; init; }
        public required VfxParticleSimulator Owner { get; init; }
        public required VfxParticleSimulator.EmitterState State { get; init; }
        public required int GeometryId { get; init; }
        public ReyEngine.Formats.Meshes.VfxMeshAnimation? Animation { get; init; }
    }
    private readonly List<MeshSlice> _meshSlices = new();

    public D3D11MapParticles(ShaderPreviewRenderer renderer, ShaderCacheReader cache,
        int maxQuads = DefaultMaxQuads)
    {
        _renderer = renderer;
        _cache = cache;
        _maxQuads = Math.Max(1, maxQuads);
    }

    // ---------------------------------------------------------------- state the UI reads

    public bool HasPlayback => _playback is not null;
    public int Placements { get; private set; }
    public int ActivePlacements { get; private set; }
    public int DrawSlices => _slices.Count;
    public int LiveParticles { get; private set; }
    public int QuadsRequested { get; private set; }
    public int ParticlesDropped => Math.Max(0, QuadsRequested - LiveParticles);
    public int SlicesTruncated { get; private set; }
    public int SkippedMeshEmitters { get; private set; }
    public int SkippedBeamTrailEmitters { get; private set; }
    public int UnresolvedSprites { get; private set; }
    public string BuildReport { get; private set; } = "";

    /// <summary>Exactly the materials this driver added to the renderer, and nothing else.
    ///
    /// <para>Exposed for the pixel harness, which measures the particles' contribution by rendering the SAME
    /// frame twice with only <c>Visible</c> flipped on this set. Hiding them any other way - re-ticking,
    /// moving the camera, clearing materials - changes something else as well, and a diff that includes a
    /// second variable cannot say what it measured. That mistake cost M264 a wrong diagnosis.</para></summary>
    public IReadOnlyCollection<PreviewMaterial> Materials => _mine;

    /// <summary>Stop every simulator: emitters stop spawning and the live particles play out.
    ///
    /// <para>Not wired to any UI, and deliberately so - the GL map viewport has no stop either, because its
    /// ParticleStopped property is unbound in MainWindow.axaml. This exists for the harness's NEGATIVE
    /// CONTROL: after the particles have died the same measurement must read ~0%, and without that a
    /// "greater than 0.5%" result cannot tell working particles from a background that moved.</para></summary>
    public void StopAll()
    {
        foreach (var sim in _sims.Values) sim.Stop();
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Point at a new playback. Null tears everything down. The rebuild itself is deferred to the
    /// next <see cref="Tick"/> because this is called from the UI thread while the render thread owns the
    /// device.</summary>
    public void SetPlayback(VfxPlayback? playback)
    {
        if (ReferenceEquals(_playback, playback)) return;
        _playback = playback;
        _dirty = true;
    }

    /// <summary>Call after <c>Dx11SceneBuilder.Commit</c>. Its <c>ClearMaterials</c> disposed OUR materials
    /// and emptied the texture pool along with the map's, so the retained playback has to be rebuilt from
    /// scratch. Nothing here touches the dead material objects - the next rebuild only ever asks the renderer
    /// which of its own materials are ours, and it no longer holds any.</summary>
    public void Invalidate() => _dirty = true;

    // ---------------------------------------------------------------- build

    private void Rebuild()
    {
        _dirty = false;

        // Only ours. ClearMaterials would take the ~1,600-material map scene with it, and rebuilding that
        // costs seconds - a particle selection click must not do that.
        _renderer.RemoveMaterials(m => _mine.Contains(m));
        _mine.Clear();
        _slices.Clear();
        _meshSlices.Clear();
        _liveBySlice.Clear();
        _byEmitter.Clear();
        _noPipeline.Clear();
        _sims.Clear();
        _active.Clear();
        _activeSet.Clear();
        Placements = ActivePlacements = LiveParticles = QuadsRequested = 0;
        SlicesTruncated = SkippedMeshEmitters = SkippedBeamTrailEmitters = UnresolvedSprites = 0;

        var pb = _playback;
        if (pb is null || pb.Items.Count == 0) { BuildReport = "no playback"; return; }

        var sb = new StringBuilder();
        var tocs = VfxD3D11EmitterPipeline.ReadTocs(_cache, out var tocError);
        if (tocs is null)
        {
            BuildReport = tocError ?? "the particle shaders could not be read";
            return;
        }

        Placements = pb.Items.Count;
        int emptySystems = 0, failedPipelines = 0;
        var systems = new Dictionary<uint, (string Name, int Placements, int Emitters)>();

        foreach (var item in pb.Items)
        {
            // M266: seed, transform, tint and start delay all come from the one shared contract - see
            // VfxPlaybackSim for why a second copy of the seed expression could never be shown to agree.
            var sim = VfxPlaybackSim.Create(item);
            if (sim is null) { emptySystems++; continue; }
            VfxPlaybackSim.ApplySimulationAssets(sim, item);
            _sims[item] = sim;

            var tally = systems.GetValueOrDefault(item.System.PathHash);
            systems[item.System.PathHash] = (item.System.Name, tally.Placements + 1, sim.Emitters.Count);

            foreach (var es in sim.Emitters)
            {
                var def = es.Def;
                if (def.Beam is not null || def.Trail is not null) { SkippedBeamTrailEmitters++; continue; }

                // M283: mesh-primitive emitters draw their own .skn through the renderer's mesh pipeline.
                // The decoded mesh has been on the playback item all along - it is what GL draws - and this
                // path simply never read it, so the emitter was counted as skipped and nothing appeared.
                if (def.IsMeshPrimitive)
                {
                    if (!BuildMeshSlice(item, sim, es, def, tocs, sb)) SkippedMeshEmitters++;
                    continue;
                }

                if (_byEmitter.TryGetValue(def, out var existing))
                {
                    existing.Sources.Add((sim, es));
                    continue;
                }
                if (_noPipeline.Contains(def)) continue;

                var mat = VfxD3D11EmitterPipeline.Build(_renderer, _cache, tocs, def,
                    sampler => ResolveSprite(sampler, item, def), sb);
                if (mat is null)
                {
                    // The per-emitter detail is already in sb; name the emitter so the reason has a subject.
                    sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: no pipeline");
                    failedPipelines++;
                    _noPipeline.Add(def);
                    continue;
                }

                _renderer.AddMaterial(mat);
                _mine.Add(mat);
                var slice = new Slice { Material = mat, Def = def };
                slice.Sources.Add((sim, es));
                _byEmitter[def] = slice;
                _slices.Add(slice);
            }
        }

        // Authored pass order, globally rather than per system. OrderBy is stable; List.Sort is not, and an
        // unstable sort here would reshuffle same-pass emitters from frame to frame, which for additive
        // draws IS the image.
        //
        // Global rather than per system IS a documented divergence from the GL viewport, which sorts within
        // each simulator. For one previewed system the two are identical; across a whole map, additive glows
        // from different placements interleave differently. Reverting to placement-major would multiply the
        // draw count by the placement count, which is the cost this whole design exists to avoid.
        _slices = _slices.OrderBy(static s => s.Def.Pass).ToList();
        foreach (var sl in _slices) _liveBySlice.Add(sl.Live);
        if (_ranges.Length < _slices.Count) _ranges = new PackedRange[_slices.Count];

        var head = new StringBuilder();
        head.AppendLine($"{N(Placements)} placement(s) over {N(systems.Count)} system(s), "
                        + $"{N(_slices.Count)} draw slice(s)");
        if (emptySystems > 0) head.AppendLine($"   {N(emptySystems)} placement(s) of emitterless systems skipped");
        // Counted per PLACEMENT, i.e. occurrences rather than distinct definitions - that is the number that
        // says how much of what the map authored this path does not draw.
        if (SkippedMeshEmitters > 0)
            head.AppendLine($"   {N(SkippedMeshEmitters)} mesh-primitive emitter instance(s) skipped - this path "
                            + "draws billboards only, and a solid white card would be worse than nothing");
        if (SkippedBeamTrailEmitters > 0)
            head.AppendLine($"   {N(SkippedBeamTrailEmitters)} beam/trail emitter instance(s) skipped - ribbon "
                            + "geometry, not billboards");
        if (UnresolvedSprites > 0)
            head.AppendLine($"   {N(UnresolvedSprites)} emitter sprite(s) unresolved - drawn with the shared "
                            + "soft dot, the same substitute the OpenGL viewport makes");
        if (failedPipelines > 0) head.AppendLine($"   {N(failedPipelines)} emitter(s) produced no pipeline:");
        head.AppendLine();
        foreach (var (_, s) in systems.OrderByDescending(kv => kv.Value.Placements).Take(12))
            head.AppendLine($"   {s.Placements,5}x  {s.Emitters,3} emitter(s)  {s.Name}");

        // Per-emitter detail only for the failures - the successful path would be thousands of lines.
        BuildReport = failedPipelines > 0 ? head + Environment.NewLine + sb : head.ToString();
    }

    /// <summary>
    /// <para>The map viewport's half of the sprite seam: hand back the <see cref="TextureImage"/> the
    /// view-model already resolved rather than reading the WAD again.</para>
    ///
    /// <para>That is not only cheaper - it is what makes this viewport swallow exactly the decode failures
    /// the GL viewport swallows, because both consume the same resolved per-emitter lists. A second lookup
    /// path would eventually disagree about which file a path landed on.</para>
    /// </summary>
    private VfxD3D11EmitterPipeline.Sprite? ResolveSprite(string sampler, VfxPlaybackItem item,
        VfxEmitterDefinition def)
    {
        int idx = VfxPlaybackSim.AuthoredIndex(item.System, def);
        if (idx < 0) return null;

        (IReadOnlyList<TextureImage?>? List, string? Path) stage = sampler switch
        {
            "TEXTURE" => (item.EmitterTextures, def.TexturePath),
            "TEXTUREMULT" => (item.EmitterMultTextures, def.TextureMultPath),
            "sAlphaErosionTexture" => (item.EmitterErosionTextures, def.AlphaErosion?.MapPath),
            "sPalettesTexture" => (item.EmitterPaletteTextures, def.Palette?.TexturePath),
            // M282: the heat-haze normal map. The view-model has resolved this list all along - it is what
            // GL refracts through - and the D3D11 side simply never asked for it, so a heat-haze emitter
            // arrived with its actual visual missing and only its blank colour-hold sprite left to draw.
            "DISTORTION" => (item.EmitterDistortionTextures, def.Distortion?.NormalMapTexturePath),
            _ => (null, null),
        };

        var img = stage.List is { } list && idx < list.Count ? list[idx] : null;
        if (img is not null)
            return VfxD3D11EmitterPipeline.Sprite.Decoded(img,
                stage.Path?.ToLowerInvariant() ?? $"vfx:{item.System.PathHash:x8}:{idx}:{sampler}");

        // The diffuse stage always gets something. GL substitutes its soft dot whenever the resolved image is
        // null - authored path or not - and D3D11's own stand-in is an opaque 1x1 white, which turns an
        // unresolved sprite into a solid card instead of a dim placeholder.
        if (sampler == "TEXTURE") { UnresolvedSprites++; return VfxD3D11EmitterPipeline.Sprite.Fallback; }

        // Every other stage is optional: nothing bound, exactly as GL leaves the handle at 0.
        return null;
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>
    /// <para>Advance every active placement and refill the dynamic buffer. Must run BEFORE
    /// <c>ShaderPreviewRenderer.RenderFrame</c>: that is what reads the index count this writes, so ticking
    /// afterwards would draw last frame's quads and read as particles trailing the camera.</para>
    ///
    /// <para><paramref name="mirrorInclusiveView"/> and <paramref name="mirrorInclusiveViewProj"/> must be the
    /// SAME matrices the coming frame draws with, mirror included. The -X mirror is applied inside
    /// <c>RenderFrame</c>, so a caller that builds a basis from the raw camera view billboards the quads
    /// against a camera that does not exist and culls the wrong half of the map.</para>
    /// </summary>
    public void Tick(float dt, in Matrix4x4 mirrorInclusiveView, in Matrix4x4 mirrorInclusiveViewProj,
        Vector3 cameraPosition, float cameraDistance)
    {
        if (_dirty) Rebuild();
        // M283: mesh emitters count too. Returning on _slices alone would freeze a system whose only
        // drawable emitters are meshes - it has no quad slices at all, so the old test read as "nothing
        // to do" and its meshes never advanced or drew.
        if (_playback is not { } pb || (_slices.Count == 0 && _meshSlices.Count == 0)) return;

        UpdateActive(pb, mirrorInclusiveViewProj, cameraPosition, cameraDistance);

        foreach (var (_, sim) in _active) sim.Update(dt);
        // SetBeamTarget is deliberately not called: TargetDummyPosition is unbound in MainWindow.axaml, so
        // the GL map path passes null too, and beam emitters are not drawn by this path at all.

        TickMeshSlices();

        var (right, up, normal) = VfxBillboardBasis.FromView(mirrorInclusiveView);
        if (_slices.Count == 0) return;   // meshes are updated above; there is nothing to pack

        // Refill each slice's live source list: a placement the camera gate dropped contributes nothing,
        // but its emitter's material stays registered so re-entering costs no pipeline work.
        int requested = 0;
        foreach (var sl in _slices)
        {
            sl.Live.Clear();
            foreach (var (owner, state) in sl.Sources)
            {
                if (!_activeSet.Contains(owner) || state.InstanceCount == 0) continue;
                sl.Live.Add(state);
                requested += state.InstanceCount;
            }
        }

        EnsureCapacity(Math.Min(requested, _maxQuads));

        int quads = Pack(_liveBySlice, _maxQuads, _verts, _indices,
            right, up, normal, _ranges, out int vertexCount, out int indexCount,
            out int packRequested, out int truncated);

        QuadsRequested = packRequested;
        LiveParticles = quads;
        SlicesTruncated = truncated;

        for (int i = 0; i < _slices.Count; i++)
        {
            var mat = _slices[i].Material;
            var r = _ranges[i];
            mat.StartIndex = r.Start;
            mat.IndexCount = r.Count;
            mat.Visible = r.Count > 0;
        }

        // Grow the device buffers to fit what was just packed, then upload once for the whole frame.
        // UpdateDynamicMesh CLAMPS silently to capacity, so the ensure has to come first or an over-budget
        // frame would lose its tail without saying so.
        _renderer.SetDynamicMesh(vertexCount, indexCount);
        _renderer.UpdateDynamicMesh(_verts, vertexCount, _indices, indexCount);
    }

    /// <summary>M283: build one mesh-primitive emitter's material and upload its geometry. Returns false
    /// when the emitter cannot be drawn, which keeps it counted as skipped rather than silently absent.
    ///
    /// <para>Indices are used whenever the mesh HAS them. The GL path instead passes indices only when an
    /// animation resolved (ViewportControl.cs:1443-1450), so an indexed .skn whose .skl or .anm is missing
    /// is drawn there as unindexed triangle soup - visible garbage. That is not replicated.</para></summary>
    private bool BuildMeshSlice(VfxPlaybackItem item, VfxParticleSimulator sim,
        VfxParticleSimulator.EmitterState es, VfxEmitterDefinition def,
        VfxD3D11EmitterPipeline.Tocs tocs, StringBuilder sb)
    {
        int idx = VfxPlaybackSim.AuthoredIndex(item.System, def);
        var mesh = item.EmitterMeshes is { } list && idx >= 0 && idx < list.Count ? list[idx] : null;
        if (mesh is null || mesh.Positions.Length == 0)
        {
            sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: mesh primitive with no decoded mesh");
            return false;
        }

        // An untextured mesh emitter draws NOTHING rather than taking the renderer's opaque-white stand-in.
        // A white 1x1 stretched over a door-sized mesh is a huge solid card, which is worse than an absent
        // effect; the GL host refuses the same case for the same stated reason (ViewportControl.cs:1451-1456).
        if (ResolveSprite("TEXTURE", item, def) is not { } sprite || sprite.Key == VfxPlaybackSim.SoftDotKey)
        {
            sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: mesh primitive with no texture - not drawn");
            return false;
        }

        int geometryId = _renderer.CreateMeshGeometry(mesh.Positions, mesh.Uvs,
            mesh.Indices is { Length: > 0 } ? mesh.Indices : null);
        if (geometryId < 0)
        {
            sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: mesh geometry upload failed");
            return false;
        }

        var mat = VfxD3D11EmitterPipeline.Build(_renderer, _cache, tocs, def,
            sampler => ResolveSprite(sampler, item, def), sb);
        if (mat is null)
        {
            sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: no pipeline (mesh)");
            return false;
        }

        mat.MeshGeometryId = geometryId;
        mat.UsesDynamicMesh = false;      // its geometry is its own, not the shared quad buffer
        mat.Visible = false;              // until a Tick finds it active and gives it particles
        _renderer.AddMaterial(mat);
        _mine.Add(mat);
        _meshSlices.Add(new MeshSlice
        {
            Material = mat, Def = def, Owner = sim, State = es,
            GeometryId = geometryId, Animation = mesh.Animation,
        });
        return true;
    }

    /// <summary>Per-frame update for the mesh emitters: particle instances, the placement basis, UV scroll,
    /// and a re-skin for anything animated.</summary>
    private void TickMeshSlices()
    {
        MeshEmittersDrawn = 0;
        foreach (var ms in _meshSlices)
        {
            var mat = ms.Material;
            var es = ms.State;
            bool live = _activeSet.Contains(ms.Owner) && es.InstanceCount > 0;
            mat.Visible = live;
            if (!live) continue;

            mat.MeshInstances = es.Instances;
            mat.MeshInstanceCount = es.InstanceCount;
            mat.MeshRight = es.PlacementRight;
            mat.MeshUp = es.PlacementUp;
            mat.MeshForward = es.PlacementForward;

            // M47c: mesh particles animate by scrolling their texture along the mesh UVs; M117: texDiv is
            // fractional tiling. Both taken from the GL path verbatim (VfxParticleRenderer.cs:949-960) -
            // guessing either would show up as a smeared atlas rather than as an obvious fault.
            // es.EmitterAge is the public accessor for the same field GL scrolls by (EmitterAge => Age).
            mat.MeshUvOffset = ms.Def.UvScrollRate * es.EmitterAge;
            mat.MeshUvOffsetMult = ms.Def.TextureMultUvScrollRate * es.EmitterAge;
            var div = ms.Def.TexDiv;
            mat.MeshTexDiv = new Vector2(div.X > 0 ? div.X : 1f, div.Y > 0 ? div.Y : 1f);
            var divMult = ms.Def.TextureMultTexDiv;
            mat.MeshTexDivMult = new Vector2(divMult.X > 0 ? divMult.X : 1f, divMult.Y > 0 ? divMult.Y : 1f);

            // The skinned pose is per EMITTER, not per particle: one vertex buffer serves every particle of
            // this emitter, so they necessarily share a pose. GL has the same property for the same reason.
            if (ms.Animation is { } anim && anim.Clip.Duration > 1e-3f)
            {
                float t = es.EmitterAge % anim.Clip.Duration;
                var frame = ReyEngine.Formats.Animation.SkinnedMeshAnimator.Skin(
                    anim.Mesh, anim.Skeleton, anim.Clip, t);
                _renderer.UpdateMeshGeometryPositions(ms.GeometryId, frame.Positions);
            }
            MeshEmittersDrawn++;
        }
    }

    /// <summary>How many mesh emitters actually drew last frame.</summary>
    public int MeshEmittersDrawn { get; private set; }

    /// <summary>The camera gate, and the reason it is not just a cost saving: a placement ENTERING the set is
    /// Reset, so it restarts at t=0 exactly as the GL viewport restarts it. Without that, a system that
    /// leaves and re-enters the frustum resumes mid-life here and starts from nothing there.</summary>
    private void UpdateActive(VfxPlayback pb, in Matrix4x4 viewProj, Vector3 cameraPosition, float cameraDistance)
    {
        if (!pb.CullByCamera)
        {
            // The single-selection case: everything runs, whatever the camera is doing.
            if (_active.Count == _sims.Count) { ActivePlacements = _active.Count; return; }
            _active.Clear();
            _activeSet.Clear();
            foreach (var (item, sim) in _sims) { _active.Add((item, sim)); _activeSet.Add(sim); }
            ActivePlacements = _active.Count;
            return;
        }

        // World space is unmirrored and the flip lives in the view matrix, so the position tested against it
        // must be mirrored too - the GL viewport caches exactly this vector.
        var mirroredCam = new Vector3(-cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        float maxDistSq = VfxPlaybackSim.MaxDistanceSquared(cameraDistance);

        _wanted.Clear();
        foreach (var item in pb.Items)
        {
            if (!VfxPlaybackSim.IsActive(item, mirroredCam, maxDistSq, viewProj)) continue;
            if (_sims.TryGetValue(item, out var sim)) _wanted.Add(sim);
        }

        bool changed = _wanted.Count != _active.Count;
        if (!changed)
            foreach (var (_, sim) in _active)
                if (!_wanted.Contains(sim)) { changed = true; break; }
        if (!changed) { ActivePlacements = _active.Count; return; }

        foreach (var sim in _wanted)
            if (!_activeSet.Contains(sim)) sim.Reset();

        _active.Clear();
        _activeSet.Clear();
        foreach (var (item, sim) in _sims)
            if (_wanted.Contains(sim)) { _active.Add((item, sim)); _activeSet.Add(sim); }
        ActivePlacements = _active.Count;
    }

    private void EnsureCapacity(int quads)
    {
        int wantV = Math.Max(quads, 1) * 4;
        int wantI = Math.Max(quads, 1) * 6;
        if (_verts.Length >= wantV && _indices.Length >= wantI) return;
        // Geometric growth up to the ceiling: a map whose particle count breathes settles on one allocation
        // instead of reallocating 20 MB every time an emitter bursts.
        int grownV = Math.Min(Math.Max(wantV, _verts.Length * 2), _maxQuads * 4);
        int grownI = Math.Min(Math.Max(wantI, _indices.Length * 2), _maxQuads * 6);
        _verts = new PreviewVertex[Math.Max(grownV, wantV)];
        _indices = new uint[Math.Max(grownI, wantI)];
    }

    // ---------------------------------------------------------------- the packing pass

    /// <summary>Where one slice's quads landed in the shared index buffer.</summary>
    public readonly record struct PackedRange(int Start, int Count, int Quads);

    /// <summary>
    /// <para>Turn every live emitter state into quads, emitter-major, under one budget.</para>
    ///
    /// <para><b>One running cursor pair across ALL slices and ALL placements.</b> The indices
    /// <see cref="ParticleQuadBuilder.Append"/> writes are absolute and the draw uses BaseVertexLocation 0, so
    /// a per-slice cursor starting at zero would point every slice at the first placement's vertices.</para>
    ///
    /// <para><b>The budget is proportional.</b> When the frame asks for more than
    /// <paramref name="maxQuads"/> every emitter is thinned by the same factor, and every emitter that asked
    /// for anything still gets at least one quad - PROVIDED the live emitters do not outnumber the ceiling
    /// outright, in which case that promise is arithmetically impossible and is dropped deliberately rather
    /// than quietly. The obvious alternative - fill until the buffer runs out - makes whole emitters
    /// disappear, which is indistinguishable from an emitter that finished, and it does it to the LAST
    /// emitters in pass order, i.e. the additive glows on top.</para>
    ///
    /// <para>The per-source floor alone does not deliver that: the floors can sum past the ceiling and the
    /// remaining-budget clamp then hands 0 to whatever is late in pass order - the exact failure the rule
    /// exists to prevent, and it read as "45 of 176 slices drew" printed next to a PASS. Reserving one slot
    /// for every source still to come is what makes the floor real.</para>
    ///
    /// <para>Static and device-free on purpose: the budget rule is the part that fails silently, so it has to
    /// be testable without a GPU.</para>
    /// </summary>
    public static int Pack(
        IReadOnlyList<IReadOnlyList<VfxParticleSimulator.EmitterState>> slices, int maxQuads,
        PreviewVertex[] verts, uint[] indices, Vector3 right, Vector3 up, Vector3 normal,
        PackedRange[] ranges, out int vertexCount, out int indexCount,
        out int requested, out int slicesTruncated)
    {
        requested = 0;
        foreach (var sources in slices)
            foreach (var es in sources)
                requested += es.InstanceCount;

        float scale = requested > maxQuads ? maxQuads / (float)requested : 1f;

        int liveSources = 0;
        foreach (var sources in slices)
            foreach (var es in sources)
                if (es.InstanceCount > 0) liveSources++;
        int liveRemaining = liveSources;

        int v = 0, idx = 0, written = 0;
        slicesTruncated = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            int start = idx;
            int sliceWanted = 0, sliceWritten = 0;
            foreach (var es in slices[i])
            {
                sliceWanted += es.InstanceCount;
                if (es.InstanceCount == 0) continue;

                // Max(1) says every live emitter shows at least one quad. On its own that is a lie:
                // the per-source floors can sum past the ceiling, and the clamp below then hands 0 to
                // whatever is late in pass order - so the budget erased whole emitters, which is exactly
                // the failure "proportional" is supposed to prevent. Reviewers found it printed as
                // "45 of 176 slices drew" next to a PASS.
                //
                // Holding back one slot for each source still to come makes the floor real. When the
                // sources outnumber the ceiling entirely the promise is arithmetically impossible, so it
                // is dropped deliberately and counted rather than quietly broken.
                int cap = scale < 1f
                    ? Math.Max(1, (int)(es.InstanceCount * scale))
                    : es.InstanceCount;
                liveRemaining--;
                int reserve = liveSources <= maxQuads ? liveRemaining : 0;
                cap = Math.Min(cap, maxQuads - written - reserve);
                if (cap <= 0) continue;

                var orient = new ParticleQuadBuilder.QuadOrientation(
                    es.Def.IsArbitraryQuad, es.Def.IsDirectionOriented,
                    es.PlacementRight, es.PlacementUp, es.PlacementForward);

                int n = ParticleQuadBuilder.Append(es.Instances, Math.Min(es.InstanceCount, cap),
                    verts, ref v, indices, ref idx, right, up, normal, orient);
                written += n;
                sliceWritten += n;
            }

            if (sliceWritten < sliceWanted) slicesTruncated++;
            if (i < ranges.Length) ranges[i] = new PackedRange(start, idx - start, sliceWritten);
        }

        vertexCount = v;
        indexCount = idx;
        return written;
    }

    // ---------------------------------------------------------------- reporting

    /// <summary>The sentence an over-budget frame prints. It exists as its own function because the failure
    /// this whole design guards against is a budget that is hit SILENTLY - if the count is ever dropped from
    /// the report, particles just quietly thin out under load and nothing says why.</summary>
    public static string OverBudgetLine(int dropped, int maxQuads, int slicesTruncated) =>
        $"OVER BUDGET: {N(dropped)} particle(s) beyond the {N(maxQuads)}-quad ceiling were thinned "
        + $"across {N(slicesTruncated)} slice(s)";

    /// <summary>One line for the viewport's detail tooltip, plus the budget warning when there is one. Cheap
    /// enough to build every frame.</summary>
    public string FrameReport()
    {
        if (_playback is null) return "";
        var sb = new StringBuilder();
        sb.Append($"particles  {N(ActivePlacements)}/{N(Placements)} placements active  ·  "
                  + $"{N(_slices.Count)} slices  ·  {N(LiveParticles)} quads");

        // The scope limit belongs where a user can see it, not only in a build report nothing displays.
        // "The map has particles that this viewport never draws" is a stated behaviour if it is on screen
        // and a bug report if it is not.
        int notDrawn = SkippedMeshEmitters + SkippedBeamTrailEmitters;
        if (notDrawn > 0)
            sb.Append(Environment.NewLine
                      + $"not drawn: {N(SkippedMeshEmitters)} mesh-primitive and {N(SkippedBeamTrailEmitters)} "
                      + "beam/trail emitter instance(s) - billboards only on this path");

        if (ParticlesDropped > 0)
            sb.Append(Environment.NewLine + OverBudgetLine(ParticlesDropped, _maxQuads, SlicesTruncated));
        return sb.ToString();
    }

    /// <summary>Thousands separators, always the invariant ones. This string ends up in the UI on a German
    /// locale, where "n0" would otherwise render 30,000 as 30.000 next to invariant numbers from elsewhere in
    /// the same report.</summary>
    private static string N(int value) => value.ToString("n0", CultureInfo.InvariantCulture);
}
