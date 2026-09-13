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
    // M694: systems entering the gate are warmed a few per frame; a system is active once it is warm
    private readonly ParticleWarmupQueue _warmup = new();
    private readonly HashSet<VfxParticleSimulator> _warmed = new(ReferenceEqualityComparer.Instance);
    private readonly List<VfxParticleSimulator> _readyScratch = new();
    public int WarmupPending => _warmup.Pending;
    public double LastWarmupMs => _warmup.LastPumpMs;

    private List<Slice> _slices = new();
    private readonly Dictionary<VfxEmitterDefinition, Slice> _byEmitter =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>Emitters whose pipeline would not build. Remembered so the 2,400th placement of the same
    /// system does not retry a permutation that failed identically for the first.</summary>
    private readonly HashSet<VfxEmitterDefinition> _noPipeline = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PreviewMaterial> _mine = new(ReferenceEqualityComparer.Instance);
    /// <summary>M710: materials built during a rebuild, waiting to be handed to the renderer in DRAW order.
    ///
    /// <para>The renderer draws a material that does not write depth in the order it was given one, and
    /// every particle material is one of those. So the order a rebuild calls AddMaterial in IS this host's
    /// draw order, and until M710 that order was whatever the build happened to walk: placement-major, then
    /// authored emitter. <c>pass</c> never reached the picture at all, on the 79.7% of emitters that author
    /// it. Collecting here and registering once, sorted, is what makes the order real - and it is the only
    /// shape that reaches all four channels, because quads, Riot meshes, legacy meshes and ribbons register
    /// from four different places and draw from one list.</para></summary>
    private readonly List<(PreviewMaterial Mat, VfxEmitterDefinition Def)> _pending = new();

    /// <summary>Build-time registration: remember the material for the sorted flush, and for removal.</summary>
    private void Register(PreviewMaterial mat, VfxEmitterDefinition def)
    {
        _pending.Add((mat, def));
        _mine.Add(mat);
    }
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
        /// <summary>M640: set when the slice draws through Riot's mesh_vs/mesh_ps; GeometryId is -1 then.</summary>
        public int RiotGeometryId { get; init; } = -1;
        public Vector2? BeamZRange { get; init; }
        public List<Matrix4x4> BeamTransforms { get; } = new();
    }
    private readonly List<MeshSlice> _meshSlices = new();
    /// <summary>M640: the mesh shader pair, read once per rebuild. Null when the cache lacks it, in which
    /// case mesh emitters draw through the M283 approximation and the report says so.</summary>
    private VfxD3D11EmitterPipeline.Tocs? _meshTocs;
    /// <summary>M640: how many mesh emitters were built on Riot's mesh shaders in the last rebuild.</summary>
    public int RiotMeshEmitters { get; private set; }
    /// <summary>M640: off = every mesh emitter takes the M283 approximation. Exists so the two can be
    /// rendered from one process and compared as pictures; the window never turns it off.</summary>
    public bool UseRiotMeshShaders { get; set; } = true;

    /// <summary>M364: a beam or trail emitter. One per PLACEMENT for the same reason <see cref="MeshSlice"/>
    /// is: the ribbon is world-space geometry extruded from THIS placement's particle history and basis
    /// vectors, so two placements of one system cannot share a vertex buffer the way two billboard
    /// placements can share a quad slice.</summary>
    private sealed class RibbonSlice
    {
        public required PreviewMaterial Material { get; init; }
        public required VfxParticleSimulator Owner { get; init; }
        public required VfxParticleSimulator.EmitterState State { get; init; }
        public required int RibbonId { get; init; }
        public required bool IsBeam { get; init; }
    }
    private readonly List<RibbonSlice> _ribbonSlices = new();

    /// <summary>Scratch for ribbon assembly, reused across slices and frames. Passed by ref because the
    /// builders grow it to fit - a long trail can need far more than the initial guess.</summary>
    private float[] _ribbonVerts = Array.Empty<float>();

    public D3D11MapParticles(ShaderPreviewRenderer renderer, ShaderCacheReader cache,
        int maxQuads = DefaultMaxQuads)
    {
        _renderer = renderer;
        _cache = cache;
        _maxQuads = Math.Max(1, maxQuads);
    }

    // ---------------------------------------------------------------- state the UI reads

    // ---- M630: re-anchoring -----------------------------------------------------------------------
    //
    // Three things the GL viewport has always done per frame and this driver did none of. They are one
    // milestone rather than three because they are one idea: a placement's transform is not fixed at
    // build time. Measured on Aatrox - 23 of 23 of his clip particle events name a bone, so without the
    // first of these EVERY clip-driven effect he has plays at the world origin instead of on him.

    private IReadOnlyDictionary<string, Matrix4x4>? _boneGlobals;
    private Matrix4x4 _boneModelWorld = Matrix4x4.Identity;
    private Vector3? _beamTarget;
    /// <summary>How long each travelling placement has been in flight. Keyed by the item, like _sims, so a
    /// rebuild drops the elapsed time with the simulator it belonged to.</summary>
    private readonly Dictionary<VfxPlaybackItem, float> _travelElapsed =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The animated bone transforms this frame, and the model transform to apply on top of them.
    /// Bone globals are pre-scale and pre-placement, exactly as the GL path documents.
    ///
    /// <para>Null for a map, which has no skeleton - and then nothing is re-anchored, which is the
    /// behaviour this driver had before.</para></summary>
    public void SetBoneGlobals(IReadOnlyDictionary<string, Matrix4x4>? bones, Matrix4x4 modelWorld)
    {
        _boneGlobals = bones;
        _boneModelWorld = modelWorld;
    }

    /// <summary>Where beams terminate. Pushed every frame rather than at build time so dragging the target
    /// moves live beams instead of needing a replay - the same reason GL pushes it (M183).</summary>
    public void SetBeamTarget(Vector3? worldTarget) => _beamTarget = worldTarget;

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
    /// <summary>M707: emitters that name no base texture at all. Not a failure - the engine binds a
    /// transparent texel on that slot and the emitter draws nothing - but counted and reported all the
    /// same, because "the effect is missing" and "the effect was never drawn" look identical on screen
    /// and only the build report can tell them apart.</summary>
    public int UnnamedSprites { get; private set; }
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
    /// <summary>M630: move every placement that is not standing still - bone-attached systems onto their
    /// bone, travelling systems along their flight. Both were GL-only.</summary>
    private void Reanchor(float dt)
    {
        foreach (var (item, sim) in _sims)
        {
            // A clip particle event rides its bone. Bone globals are pre-scale and pre-placement, so the
            // model transform goes on top - the SAME matrix the mesh is drawn with, or the effect stands
            // at the origin while the character walks away (the M613/M614 lesson, on this path now).
            if (item.AttachBone is { Length: > 0 } bone
                && _boneGlobals is { } bones && bones.TryGetValue(bone, out var bm))
            {
                sim.SetWorldTransform(_boneModelWorld.IsIdentity ? bm : bm * _boneModelWorld);
                continue;
            }

            // A missile flies from where it was spawned to where it was aimed. Nothing here reads the
            // clock except this: the elapsed time is accumulated per item so a paused viewport freezes
            // the flight along with everything else.
            if (item.TravelTo is not { } destination || item.TravelSeconds <= 0f) continue;

            float elapsed = (_travelElapsed.TryGetValue(item, out var t) ? t : 0f) + dt;
            _travelElapsed[item] = elapsed;
            float progress = Math.Clamp((elapsed - item.StartDelay) / item.TravelSeconds, 0f, 1f);
            // M635: the AIM travels with it. Rebuilding the matrix from the lerped position alone threw the
            // item's rotation away on the first tick of flight, so a missile flew sideways-on.
            sim.SetWorldTransform(VfxCastFrame.RotationOf(item.Transform)
                * Matrix4x4.CreateTranslation(Vector3.Lerp(item.WorldPos, destination, progress)));
        }
    }

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
        // M710: cleared beside _mine so a rebuild that returns early - no playback, no shader toc - cannot
        // leak its half-built materials into the next one's draw order.
        _pending.Clear();
        _slices.Clear();
        foreach (int id in _meshSlices.Where(s => s.GeometryId >= 0).Select(s => s.GeometryId).Distinct())
            _renderer.ReleaseMeshGeometry(id);
        foreach (int id in _meshSlices.Where(s => s.RiotGeometryId >= 0).Select(s => s.RiotGeometryId).Distinct())
            _renderer.ReleaseRiotMeshGeometry(id);
        _meshSlices.Clear();
        foreach (int id in _ribbonSlices.Select(s => s.RibbonId).Distinct())
            _renderer.ReleaseRibbon(id);
        _ribbonSlices.Clear();
        _liveBySlice.Clear();
        _travelElapsed.Clear();   // M630: elapsed flight belongs to the sims being discarded
        _byEmitter.Clear();
        _noPipeline.Clear();
        _sims.Clear();
        _warmed.Clear(); _warmup.Clear();   // M694: a rebuilt set warms from scratch
        _active.Clear();
        _activeSet.Clear();
        Placements = ActivePlacements = LiveParticles = QuadsRequested = 0;
        SlicesTruncated = SkippedMeshEmitters = SkippedBeamTrailEmitters = UnresolvedSprites = UnnamedSprites = 0;

        var pb = _playback;
        if (pb is null || pb.Items.Count == 0) { BuildReport = "no playback"; return; }

        var sb = new StringBuilder();
        var tocs = VfxD3D11EmitterPipeline.ReadTocs(_cache, out var tocError);
        if (tocs is null)
        {
            BuildReport = tocError ?? "the particle shaders could not be read";
            return;
        }
        // M640: the mesh pair is optional - its absence degrades mesh emitters to the M283 path, visibly.
        string? meshTocError = null;
        _meshTocs = UseRiotMeshShaders ? VfxD3D11EmitterPipeline.ReadMeshTocs(_cache, out meshTocError) : null;
        RiotMeshEmitters = 0;
        if (_meshTocs is null) sb.AppendLine($"mesh emitters on the M283 approximation: {meshTocError ?? "UseRiotMeshShaders is off"}");

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
                // M364: beams and trails are ribbon strips, not billboards. They used to be skipped here
                // with exactly that reason recorded; they now build a ribbon slice of their own.
                if (def.Beam is not null || def.Trail is not null)
                {
                    if (def.Beam is not null && def.IsMeshPrimitive && item.BeamTarget is not null
                        && BuildMeshSlice(item, sim, es, def, tocs, sb)) continue;
                    if (!BuildRibbonSlice(item, sim, es, def, tocs, sb)) SkippedBeamTrailEmitters++;
                    continue;
                }

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

                Register(mat, def);
                var slice = new Slice { Material = mat, Def = def };
                slice.Sources.Add((sim, es));
                _byEmitter[def] = slice;
                _slices.Add(slice);
            }
        }

        // M710: the draw order of this host, at the one moment it exists. Everything above only BUILT
        // materials; a material that does not write depth is drawn in the order the renderer was handed it,
        // so this loop is the picture's order and nothing downstream can change it.
        //
        // M709 established the key: the ground layer first, then the authored pass, with a stable sort
        // leaving authored order as the tiebreak. Four channels register from four different places and
        // draw from one list, so they are ordered together here rather than four times over.
        //
        // The scope is a documented divergence from the OpenGL viewport, which sorts inside one simulator.
        // This host shares one slice per emitter DEFINITION across every placement of it - that sharing is
        // the whole reason the map can carry thousands of placements - so it cannot sort per placement
        // without multiplying the draw count by the placement count. It sorts across the map instead.
        //
        // M720: and so it takes only the first two keys. The blend rank and the render-flags byte order
        // emitters inside one system; across a map they would pull every NONE emitter of a pass ahead of
        // every other system's ADD, because the engine key that keeps systems apart - their position - is one
        // neither renderer has. Registration order, which is system by system, breaks the tie instead.
        foreach (var pending in _pending.OrderBy(static e => VfxDrawOrder.KeyAcrossSystems(e.Def)))
            _renderer.AddMaterial(pending.Mat);
        _pending.Clear();

        // The slice list takes the SAME key, so the quad budget's ranges and its thinning follow the order
        // the frame actually draws in. Pack thins the last slices first, and "last" is meant to be the
        // draws on top; under a different key from the one above it would have meant something else.
        //
        // OrderBy is stable; List.Sort is not, and an unstable sort here would reshuffle same-key emitters
        // from frame to frame, which for additive draws IS the image.
        _slices = _slices.OrderBy(static s => VfxDrawOrder.KeyAcrossSystems(s.Def)).ToList();
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
        // M364: these now DRAW as ribbons, so the count only survives for the ones that still could not be
        // built - almost always a missing texture. The old reason ("ribbon geometry, not billboards") was
        // the whole feature and is no longer true of the general case.
        if (_ribbonSlices.Count > 0)
            head.AppendLine($"   {N(_ribbonSlices.Count)} beam/trail emitter instance(s) drawn as ribbons");
        if (SkippedBeamTrailEmitters > 0)
            head.AppendLine($"   {N(SkippedBeamTrailEmitters)} beam/trail emitter instance(s) not drawn - see below");
        if (UnresolvedSprites > 0)
            head.AppendLine($"   {N(UnresolvedSprites)} emitter sprite(s) unresolved - drawn with the shared "
                            + "soft dot, the same substitute the OpenGL viewport makes");
        if (UnnamedSprites > 0)
            head.AppendLine($"   {N(UnnamedSprites)} emitter(s) name no texture at all - given the engine's "
                            + "transparent texel and drawing nothing, which is what the game draws for them");
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

        // The diffuse stage always gets something, because D3D11's stand-in for an unbound slot is an opaque
        // 1x1 WHITE and that turns a missing sprite into a solid card. WHICH something depends on why it is
        // missing (M707), and until then both cases took the soft dot:
        //   - the emitter names no texture at all -> the engine's 1x1 transparent black. Nothing failed and
        //     nothing draws, which is exactly what the game does with it.
        //   - the emitter names one the editor could not resolve -> the soft dot, a placeholder that says as
        //     much, and the only one of the two worth counting as unresolved.
        if (sampler == "TEXTURE")
        {
            if (def.NamesNoTexture) { UnnamedSprites++; return VfxD3D11EmitterPipeline.Sprite.Unnamed; }
            UnresolvedSprites++;
            return VfxD3D11EmitterPipeline.Sprite.Fallback;
        }

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
        // M364: ribbon slices count too, for the same reason M283 added mesh slices here - a system whose
        // only drawable emitter is a beam has no quad slices at all, and testing _slices alone would read
        // as "nothing to do" and freeze it.
        if (_playback is not { } pb
            || (_slices.Count == 0 && _meshSlices.Count == 0 && _ribbonSlices.Count == 0)) return;

        UpdateActive(pb, mirrorInclusiveViewProj, cameraPosition, cameraDistance);

        // M630: re-anchor BEFORE the step, so a bone-attached system is simulated from where its bone is
        // this frame rather than from where it was last frame.
        Reanchor(dt);

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var (item, sim) = _active[i];
            if (item.EndTime is { } end && _travelElapsed.GetValueOrDefault(item) >= end)
            {
                _active.RemoveAt(i);
                _activeSet.Remove(sim);
            }
        }

        foreach (var (item, sim) in _active)
        {
            // M630: beams terminate at the target. The map host still passes null - it has no dummy to
            // bind - and the simulator then resolves endpoints from the emitter's own authored target
            // offset, which is Riot's pattern for untargeted beams. So this is inert for maps and is the
            // whole of "the cast points at the dummy" for a character.
            sim.SetBeamTarget(item.BeamTarget ?? _beamTarget);
            sim.Update(dt);
        }
        TickMeshSlices();
        TickRibbonSlices(cameraPosition);

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
        // M707: asked of the stand-in SET, not of one key by name. Both stand-ins mean "no real sprite",
        // and a mesh or a ribbon is far too large a surface to hand either one.
        if (ResolveSprite("TEXTURE", item, def) is not { } sprite || VfxPlaybackSim.IsStandIn(sprite.Key))
        {
            sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: mesh primitive with no texture - not drawn");
            return false;
        }

        // M640: Riot's own mesh_vs/mesh_ps for the static case. The permutation, the stage textures and the
        // blend decision come from the same Build the quad path uses; only the shader pair and the geometry
        // differ. Animated meshes stay on the M283 path, which owns their re-skinning.
        if (_meshTocs is { } meshTocs && mesh.Animation is null)
        {
            int riotId = _renderer.CreateRiotMeshGeometry(mesh.Positions, null, mesh.Uvs,
                mesh.Indices is { Length: > 0 } ? mesh.Indices : null);
            if (riotId >= 0)
            {
                var riotMat = VfxD3D11EmitterPipeline.Build(_renderer, _cache, meshTocs, def,
                    sampler => ResolveSprite(sampler, item, def), sb);
                if (riotMat is not null)
                {
                    riotMat.RiotMeshGeometryId = riotId;
                    riotMat.UsesDynamicMesh = false;
                    riotMat.Visible = false;
                    // The authored flag, as GL applies it (absent = cull). Gated on the window's Cull
                    // toggle by the renderer like every other per-material cull.
                    riotMat.CullBackFaces = !def.DisableBackfaceCull;
                    Register(riotMat, def);
                    _meshSlices.Add(new MeshSlice
                    {
                        Material = riotMat, Def = def, Owner = sim, State = es,
                        GeometryId = -1, RiotGeometryId = riotId,
                        BeamZRange = def.Beam is null ? null : new Vector2(
                            Enumerable.Range(0, mesh.Positions.Length / 3).Min(v => mesh.Positions[v * 3 + 2]),
                            Enumerable.Range(0, mesh.Positions.Length / 3).Max(v => mesh.Positions[v * 3 + 2])),
                    });
                    RiotMeshEmitters++;
                    return true;
                }
                _renderer.ReleaseRiotMeshGeometry(riotId);
                sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: mesh_vs/mesh_ps did not resolve - M283 mesh pipeline instead");
            }
        }

        // The legacy mesh pipeline cannot carry a per-particle constrained transform.
        // Let the caller use its ribbon fallback when Riot's mesh shader is unavailable.
        if (def.Beam is not null) return false;
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
        mat.CullBackFaces = !def.DisableBackfaceCull;   // M640: the authored flag, as on the Riot path
        mat.Visible = false;              // until a Tick finds it active and gives it particles
        Register(mat, def);
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
            if (ms.BeamZRange is { } range)
            {
                mat.Visible = es.HasBeamEndpoints;
                ms.BeamTransforms.Clear();
                for (int i = 0; i < es.InstanceCount; i++)
                    ms.BeamTransforms.Add(VfxBeamMesh.Transform(es.BeamSource, es.BeamTarget, range,
                        new Vector2(es.Instances[i * 19 + 3], es.Instances[i * 19 + 4])));
                mat.MeshParticleTransforms = ms.BeamTransforms;
            }

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

            // M640: Riot's mesh_vs takes the same tiling and scroll as ONE affine on the mesh UV -
            // vParticleUVTransform is a float4x3 and the decoded VS does o2.x = dot((u,v,1), reg1.xyz),
            // o2.y = dot((u,v,1), reg2.xyz). The rim light is vFresnel: rgb = colour, w = power.
            if (mat.RiotMeshGeometryId is not null)
            {
                var sc = mat.MeshUvOffset;
                var dv = mat.MeshTexDiv;
                mat.Params["vParticleUVTransform"] = new[] { dv.X, 0f, sc.X, 0f, 0f, dv.Y, sc.Y, 0f, 0f, 0f, 1f, 0f };
                // MULT_PASS blobs read a second affine (vParticleUVTransformMult, $Globals+64) into the
                // multiplier UV output the same way. Left unbound it is zero, and the whole mesh then
                // samples texel (0,0) of its multiplier - which is how Aatrox's W cone went dark.
                var scm = mat.MeshUvOffsetMult;
                var dvm = mat.MeshTexDivMult;
                mat.Params["vParticleUVTransformMult"] = new[] { dvm.X, 0f, scm.X, 0f, 0f, dvm.Y, scm.Y, 0f, 0f, 0f, 1f, 0f };
                var refl = ms.Def.Reflection;
                mat.Params["vFresnel"] = refl is { HasFresnel: true }
                    ? new[] { refl.FresnelColor.X, refl.FresnelColor.Y, refl.FresnelColor.Z, refl.Fresnel }
                    : new[] { 0f, 0f, 0f, 1f };
            }

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

    /// <summary>M364: register a beam or trail emitter as a ribbon slice.
    ///
    /// <para>The material is still built through the ordinary emitter pipeline - the texture resolution,
    /// blend decision and alpha-test value all come from there - and only the DRAW is diverted to the ribbon
    /// pipeline. That keeps one owner for "what does this emitter look like" and confines the ribbon
    /// specialisation to geometry.</para>
    ///
    /// <para>Like the mesh path, an untextured ribbon draws NOTHING rather than taking the white stand-in: a
    /// beam is a long thin quad, and a solid white one across the map is worse than an absent effect.</para></summary>
    private bool BuildRibbonSlice(VfxPlaybackItem item, VfxParticleSimulator sim,
        VfxParticleSimulator.EmitterState es, VfxEmitterDefinition def,
        VfxD3D11EmitterPipeline.Tocs tocs, StringBuilder sb)
    {
        // M707: asked of the stand-in SET, not of one key by name. Both stand-ins mean "no real sprite",
        // and a mesh or a ribbon is far too large a surface to hand either one.
        if (ResolveSprite("TEXTURE", item, def) is not { } sprite || VfxPlaybackSim.IsStandIn(sprite.Key))
        {
            sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: {(def.Beam is not null ? "beam" : "trail")} with no texture - not drawn");
            return false;
        }

        var mat = VfxD3D11EmitterPipeline.Build(_renderer, _cache, tocs, def,
            sampler => ResolveSprite(sampler, item, def), sb);
        if (mat is null)
        {
            sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: no pipeline (ribbon)");
            return false;
        }

        int ribbonId = _renderer.CreateRibbon();
        if (ribbonId < 0)
        {
            sb.AppendLine($"   ^ {item.System.Name} / {def.Name}: the ribbon pipeline is unavailable");
            return false;
        }

        mat.RibbonId = ribbonId;
        mat.UsesDynamicMesh = false;      // its geometry is its own, not the shared quad buffer
        mat.Visible = false;              // until a Tick finds it active and gives it a strip
        Register(mat, def);
        _ribbonSlices.Add(new RibbonSlice
        {
            Material = mat, Owner = sim, State = es, RibbonId = ribbonId, IsBeam = def.Beam is not null,
        });
        return true;
    }

    /// <summary>M364: re-extrude every live ribbon. Unlike the billboard slices this cannot be skipped when
    /// the camera has not moved - a trail's shape follows its particles, and a camera-facing trail twists
    /// with the view as well.</summary>
    private void TickRibbonSlices(Vector3 cameraPosition)
    {
        RibbonEmittersDrawn = 0;
        foreach (var rs in _ribbonSlices)
        {
            var mat = rs.Material;
            if (!_activeSet.Contains(rs.Owner)) { mat.Visible = false; continue; }

            // Assembled by the GL renderer's own builders. Not a shared style choice - the per-particle
            // history a trail walks is internal to that assembly, so this is the only way to get the same
            // geometry rather than a second implementation of it.
            int k = rs.IsBeam
                ? ReyEngine.Rendering.Vfx.VfxParticleRenderer.BuildBeamRibbon(ref _ribbonVerts, rs.State, cameraPosition)
                : ReyEngine.Rendering.Vfx.VfxParticleRenderer.BuildTrailRibbon(ref _ribbonVerts, rs.State, cameraPosition);

            // k == 0 is the ordinary early state, not a failure: a trail whose particles have not moved far
            // enough has no history to extrude yet, and a beam whose endpoints did not resolve has nowhere
            // to go. Either way the slice is simply invisible this frame.
            if (k == 0) { mat.Visible = false; continue; }

            mat.Visible = _renderer.UpdateRibbon(rs.RibbonId, _ribbonVerts, k);
            if (mat.Visible) RibbonEmittersDrawn++;
        }
    }

    /// <summary>How many beam/trail emitters actually drew last frame.</summary>
    public int RibbonEmittersDrawn { get; private set; }

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

        // M694: a system that just entered the gate is queued and warmed under the frame budget (M536's
        // pre-warm, M595's fill duration - unchanged, only no longer all in one frame), and joins the
        // active set once warm. One that leaves before its turn is forgotten.
        foreach (var (_, sim) in _sims)
        {
            bool wanted = _wanted.Contains(sim);
            if (!wanted) { _warmed.Remove(sim); _warmup.Remove(sim); }
            else if (!_warmed.Contains(sim) && !_warmup.IsPending(sim)) _warmup.Enqueue(sim);
        }
        _readyScratch.Clear();
        _warmup.Pump(_readyScratch);
        foreach (var sim in _readyScratch) _warmed.Add(sim);

        bool changed = false;
        int wantedWarm = 0;
        foreach (var sim in _wanted)
            if (_warmed.Contains(sim)) { wantedWarm++; if (!_activeSet.Contains(sim)) changed = true; }
        if (wantedWarm != _active.Count) changed = true;
        if (!changed) { ActivePlacements = _active.Count; return; }

        _active.Clear();
        _activeSet.Clear();
        foreach (var (item, sim) in _sims)
            if (_wanted.Contains(sim) && _warmed.Contains(sim)) { _active.Add((item, sim)); _activeSet.Add(sim); }
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

                // M634: the authored UV scrolls, baked into the vertices because quad_vs has no constant
                // for them. The multiplier's matters most: 184 of the 401 multiplier emitters across eight
                // champions' skin0 VFX author one, and a mask that is meant to drift stood still here while
                // it moved in the GL viewport and in the game.
                // M719: the whole per-layer transform now, through the factory the preview host uses too.
                var uvLayers = ParticleQuadBuilder.UvLayers.For(es.Def, es.EmitterAge);

                int n = ParticleQuadBuilder.Append(es.Instances, Math.Min(es.InstanceCount, cap),
                    verts, ref v, indices, ref idx, right, up, normal, orient, uvLayers);
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
        // M364: "billboards only on this path" was the honest summary when meshes and ribbons were both
        // skipped wholesale. Both draw now, so what is left is per-emitter failure - almost always a
        // missing texture - and calling that a path limitation would send someone looking in the wrong
        // place. The build report carries the specific reason per emitter.
        if (_ribbonSlices.Count > 0)
            sb.Append(Environment.NewLine
                      + $"ribbons: {N(RibbonEmittersDrawn)}/{N(_ribbonSlices.Count)} beam/trail emitter(s) drawing");
        int notDrawn = SkippedMeshEmitters + SkippedBeamTrailEmitters;
        if (notDrawn > 0)
            sb.Append(Environment.NewLine
                      + $"not drawn: {N(SkippedMeshEmitters)} mesh-primitive and {N(SkippedBeamTrailEmitters)} "
                      + "beam/trail emitter instance(s) - see the build report for why");

        if (ParticlesDropped > 0)
            sb.Append(Environment.NewLine + OverBudgetLine(ParticlesDropped, _maxQuads, SlicesTruncated));
        return sb.ToString();
    }

    /// <summary>Thousands separators, always the invariant ones. This string ends up in the UI on a German
    /// locale, where "n0" would otherwise render 30,000 as 30.000 next to invariant numbers from elsewhere in
    /// the same report.</summary>
    private static string N(int value) => value.ToString("n0", CultureInfo.InvariantCulture);
}
