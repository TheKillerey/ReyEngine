using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M295: placed prop meshes - Baron, the dragons, jungle camps - in the D3D11 viewport, matching what
/// the OpenGL viewport draws for <c>PropRenderSet</c>.</para>
///
/// <para>M676: through Riot's own shaders. A prop is a character skin, and the character window has drawn
/// those with the skin's real materials since M214 - shader, textures, parameters, the drivers' rest
/// values; this driver drew the same skins with one diffuse and the renderer's mesh shader. It now
/// prepares the same <see cref="Dx11CharacterScene"/> the window prepares (the host supplies how, because
/// only it owns the asset readers), uploads the skinned mesh once, registers the scene's materials over it
/// with the placements as <see cref="PreviewMaterial.CharacterInstances"/>, and poses them per frame with a
/// bone palette instead of CPU-skinned vertices. The sun and the dynamic lights the map viewport already
/// pushes apply the way they apply to a champion; the ambient cube stays the renderer's neutral stand-in,
/// as it does in the character window - the map's lightgrid is not read for it yet. A mesh the host cannot
/// prepare (an added mesh, no shader cache, no materials resolved) keeps the diffuse-only draw.</para>
///
/// <para><b>No double-render risk, and that is a fact about the data rather than a precaution.</b> Props are
/// parsed from the map's <c>.materials.bin</c> by MapPlaceableExtractor and load their own <c>.skn</c>
/// files; the D3D11 static mesh is built solely from the mapgeo's own position/index arrays and its
/// groups. The two share no geometry, so drawing props here cannot duplicate anything
/// <see cref="Dx11SceneBuilder"/> committed.</para>
///
/// <para>Geometry is uploaded ONCE per distinct mesh and drawn once per placement, which is why a map with
/// six camps of the same monster costs one buffer. Animation follows GL exactly: the pose is per MESH, not
/// per placement, because one vertex buffer - or one palette - serves every instance of it.</para>
/// </summary>
public sealed class D3D11MapProps
{
    private readonly ShaderPreviewRenderer _renderer;
    private readonly ShaderCacheReader _cache;

    /// <summary>Materials this driver owns, so a rebuild removes exactly its own and leaves the map's and
    /// the particles' alone.</summary>
    private readonly HashSet<PreviewMaterial> _mine = new(ReferenceEqualityComparer.Instance);

    /// <summary>One uploaded geometry per distinct prop mesh, with the payload kept for re-skinning.</summary>
    private sealed class PropGeom
    {
        public required PropMesh Mesh { get; init; }
        /// <summary>The mesh-pipeline geometry of the diffuse-only draw, or -1 on the Riot path.</summary>
        public int GeometryId { get; init; } = -1;
        /// <summary>M676: the skinned geometry Riot's shaders draw, or -1 on the diffuse-only draw.</summary>
        public int RiotGeometryId { get; init; } = -1;
        /// <summary>M676: the Riot path's materials, which take the pose each frame.</summary>
        public readonly List<PreviewMaterial> Materials = new();
        public readonly List<Matrix4x4> Instances = new();
        /// <summary>M680: per placement, the lightgrid's cube where it stands (null = the stand-in).</summary>
        public readonly List<float[]?> Ambient = new();
        public float[]? LightGridScale;
        // M694: the pose scratch and the outputs, reused frame after frame - the materials hold the
        // palette array by reference and see each frame's values in place
        public readonly PoseBuffer Pose = new();
        public Matrix4x4[]? Palette;
        public float[]? SkinPositions;
        public float LastPoseTime = float.NegativeInfinity;
    }
    private readonly List<PropGeom> _geoms = new();
    private readonly HashSet<string> _textureKeys = new(StringComparer.Ordinal);

    /// <summary>M733: one distinct prop mesh still to be uploaded, and the placements waiting on it.</summary>
    private sealed class PendingUpload
    {
        public required PropMesh Mesh { get; init; }
        public readonly List<Matrix4x4> Transforms = new();
    }
    private readonly List<PendingUpload> _pending = new();
    private int _pendingAt;
    private Func<PropMesh, PreparedCharacterScene?>? _prepare;
    private Func<Vector3, PropLighting?>? _lightingAt;
    private readonly StringBuilder _loadLog = new();
    private VfxD3D11EmitterPipeline.Tocs? _fallbackTocs;
    private bool _fallbackTocsRead;

    public D3D11MapProps(ShaderPreviewRenderer renderer, ShaderCacheReader cache)
    { _renderer = renderer; _cache = cache; }

    public int PropMeshCount => _geoms.Count;
    public int PropInstanceCount { get; private set; }
    public int SkippedProps { get; private set; }
    /// <summary>M676: how many of the prop meshes draw through Riot's own shaders.</summary>
    public int RiotShaderMeshes { get; private set; }
    public string Report { get; private set; } = "";
    /// <summary>M733: distinct prop meshes still waiting to be uploaded. Reported beside the particle
    /// warm-up count, because both mean the same thing to the user: the viewport is still filling in.</summary>
    public int UploadsPending => _pending.Count - _pendingAt;

    /// <summary>M733: how long one frame may spend uploading prop meshes - the same 3 ms budget
    /// <see cref="ParticleWarmupQueue"/> takes, and like it at least one mesh lands per frame, so a single
    /// expensive mesh can overshoot but never stall the queue.</summary>
    public double UploadBudgetMs { get; set; } = 3.0;

    /// <summary>M694: what the last <see cref="Tick"/> cost, and what it did.</summary>
    public double LastTickMs { get; private set; }
    public int PosedThisFrame { get; private set; }
    public int NearMeshes { get; private set; }

    /// <summary>Drop every material, texture, and geometry this driver owns.</summary>
    public void Clear()
    {
        if (_mine.Count > 0) _renderer.RemoveMaterials(m => _mine.Contains(m));
        _mine.Clear();
        _renderer.RemoveCachedTextures(_textureKeys);
        _textureKeys.Clear();
        foreach (var g in _geoms)
        {
            if (g.GeometryId >= 0) _renderer.ReleaseMeshGeometry(g.GeometryId);
            if (g.RiotGeometryId >= 0) _renderer.ReleaseRiotMeshGeometry(g.RiotGeometryId);
        }
        _geoms.Clear();
        _pending.Clear();
        _pendingAt = 0;
        _loadLog.Clear();
        _prepare = null;
        _lightingAt = null;
        PropInstanceCount = 0;
        SkippedProps = 0;
        RiotShaderMeshes = 0;
    }

    /// <summary>Build materials for a prop set. Safe to call with null - that is "props are switched off",
    /// which just clears. <paramref name="prepare"/> is the host's way from a prop mesh to a D3D11 character
    /// scene (M676); null, or a null result, keeps that mesh on the diffuse-only draw.</summary>
    public void Load(PropRenderSet? set, Func<PropMesh, PreparedCharacterScene?>? prepare = null,
        Func<Vector3, PropLighting?>? lightingAt = null)
    {
        Clear();
        if (set is null || set.Instances.Count == 0) { Report = "no props"; return; }

        _prepare = prepare;
        _lightingAt = lightingAt;

        // M733: grouping is ALL this does now. It used to upload every distinct mesh here - the scene
        // prepare, every texture decoded, the pipeline built and the geometry sent to the card - and it is
        // called from the PropMeshes setter, which the host assigns inside its render frame. So switching
        // props on, or any scene rebuild, did 28 meshes' worth of that work between two presented frames.
        // The uploads are pumped from Tick under a budget instead; a placement draws once its mesh lands.
        //
        // One group per DISTINCT mesh. PropMesh is shared by reference across placements of the same skin
        // precisely so this dedup is possible - six identical camps upload one buffer.
        var byMesh = new Dictionary<PropMesh, PendingUpload>(ReferenceEqualityComparer.Instance);
        foreach (var inst in set.Instances)
        {
            if (!byMesh.TryGetValue(inst.Mesh, out var p))
            {
                p = new PendingUpload { Mesh = inst.Mesh };
                byMesh[inst.Mesh] = p;
                _pending.Add(p);
            }
            p.Transforms.Add(inst.Transform);
        }
        UpdateReport();
    }

    /// <summary>M733: upload as many waiting prop meshes as <see cref="UploadBudgetMs"/> allows, and at
    /// least one. Returns how many landed. Called from <see cref="Tick"/> every frame, including the
    /// frames where prop animation is switched off - a still prop still has to appear.</summary>
    public int PumpUploads()
    {
        if (_pendingAt >= _pending.Count) return 0;

        var clock = Stopwatch.StartNew();
        int done = 0;
        while (_pendingAt < _pending.Count && (done == 0 || clock.Elapsed.TotalMilliseconds < UploadBudgetMs))
        {
            UploadOne(_pending[_pendingAt++]);
            done++;
        }

        if (_pendingAt >= _pending.Count) { _pending.Clear(); _pendingAt = 0; }
        UpdateReport();
        return done;
    }

    /// <summary>One waiting mesh: the geometry and materials, then the placements that were held for it.</summary>
    private void UploadOne(PendingUpload pending)
    {
        var g = Upload(pending.Mesh, _prepare, _loadLog);
        if (g is null) return;
        _geoms.Add(g);

        foreach (var transform in pending.Transforms)
        {
            g.Instances.Add(transform);
            // M680: the lightgrid where this placement stands. The list rides beside Instances, and the
            // Riot path's materials read both per placement; the diffuse-only draw has no ambient cube.
            PropLighting? lighting = null;
            if (g.RiotGeometryId >= 0 && _lightingAt is not null)
                try { lighting = _lightingAt(transform.Translation); } catch { lighting = null; }
            g.Ambient.Add(lighting?.LightGridColors);
            g.LightGridScale ??= lighting?.LightGridScale;
            PropInstanceCount++;
        }

        // M680: the cube per placement and the grid's scale, onto every material of the Riot path
        if (g.RiotGeometryId >= 0)
        {
            foreach (var mat in g.Materials)
            {
                mat.CharacterInstanceAmbient = g.Ambient;
                if (g.LightGridScale is { } scale) mat.Params["LIGHTGRID_SCALE"] = scale;
            }
            return;
        }

        BuildFallbackMaterials(g);
    }

    /// <summary>The diffuse-only draw for whatever the Riot path did not take. One material per (mesh,
    /// submesh): a prop's submeshes carry their own diffuse, so they cannot share a material even though
    /// they share a geometry. The carrier shaders are read on first need - a map whose every prop took the
    /// Riot path never touches them.</summary>
    private void BuildFallbackMaterials(PropGeom g)
    {
        if (!_fallbackTocsRead)
        {
            _fallbackTocsRead = true;
            _fallbackTocs = VfxD3D11EmitterPipeline.ReadTocs(_cache, out var tocError);
            if (_fallbackTocs is null) _loadLog.AppendLine($"   {g.Mesh.Key}: {tocError ?? "the particle shaders could not be read"}");
        }
        if (_fallbackTocs is not { } tocs) { SkippedProps++; return; }

        foreach (var sub in g.Mesh.Submeshes)
        {
            if (sub.Count <= 0) continue;
            var mat = BuildPropMaterial(tocs, $"prop:{g.Mesh.Key}", _loadLog);
            if (mat is null) { SkippedProps++; continue; }

            mat.MeshGeometryId = g.GeometryId;
            mat.MeshIndexStart = sub.Start;
            mat.MeshIndexCount = sub.Count;
            mat.MeshModels = g.Instances;
            mat.UsesDynamicMesh = false;
            mat.SortableByPipeline = false;
            // Props are opaque scene objects, unlike particles: they WRITE depth, or a prop behind
            // another would draw over it.
            mat.WritesDepth = true;
            mat.MeshCull = true;
            // M297: cut out rather than blend. GL uses 0.35 explicitly so fur and wing alpha reads;
            // with depth writes on, blending instead makes those fringes stamp depth and halo.
            mat.MeshAlphaCutoff = 0.35f;

            if (sub.Texture is { } img)
            {
                string textureKey = $"prop:{g.Mesh.Key}:{g.GeometryId}:{sub.Start}";
                _renderer.SetTexture(mat, "TEXTURE__TX", textureKey, img.Rgba, img.Width, img.Height);
                _textureKeys.Add(textureKey);
            }

            _renderer.AddMaterial(mat);
            _mine.Add(mat);
        }
    }

    /// <summary>What this driver holds, and what it is still waiting to upload.</summary>
    private void UpdateReport()
    {
        int lit = _geoms.Sum(g => g.Ambient.Count(a => a is not null));
        int waiting = UploadsPending;
        Report = $"{_geoms.Count} prop mesh(es), {PropInstanceCount} placement(s)"
               + (RiotShaderMeshes > 0 ? $", {RiotShaderMeshes} on Riot's shaders" : "")
               + (lit > 0 ? $", {lit} lit by the lightgrid" : "")
               + (SkippedProps > 0 ? $", {SkippedProps} skipped" : "")
               + (waiting > 0 ? $", {waiting} still uploading" : "")
               + (_loadLog.Length > 0 ? "\n" + _loadLog : "");
    }

    /// <summary>Upload one distinct mesh: Riot's shaders when the host can prepare the scene and the
    /// renderer takes the skinned geometry, the diffuse-only mesh-pipeline geometry otherwise. Null when
    /// neither is possible - counted and named, never thrown, so a map with one bad prop still opens.</summary>
    private PropGeom? Upload(PropMesh m, Func<PropMesh, PreparedCharacterScene?>? prepare, StringBuilder sb)
    {
        if (m.Positions.Length == 0 || m.Indices.Length == 0)
        {
            SkippedProps++;
            sb.AppendLine($"   {m.Key}: no geometry ({m.Positions.Length / 3} verts, {m.Indices.Length} indices)");
            return null;
        }

        if (prepare is not null && m.SknBytes is not null)
        {
            PreparedCharacterScene? scene = null;
            try { scene = prepare(m); }
            catch (Exception ex) { sb.AppendLine($"   {m.Key}: prepare threw: {ex.Message}"); }

            if (scene is { Slices.Count: > 0 })
            {
                int rid = _renderer.CreateRiotMeshGeometry(scene.Mesh);
                if (rid >= 0)
                {
                    var g = new PropGeom { Mesh = m, RiotGeometryId = rid };
                    var mats = Dx11CharacterScene.CommitSlices(_renderer, scene, mat =>
                    {
                        mat.RiotMeshGeometryId = rid;
                        mat.CharacterInstances = g.Instances;   // filled as the placements are walked
                    });
                    foreach (var why in scene.Failures) sb.AppendLine($"   {m.Key}: {why}");
                    if (mats.Count > 0)
                    {
                        foreach (var mat in mats) { _mine.Add(mat); g.Materials.Add(mat); }
                        foreach (var key in scene.Textures.Keys) _textureKeys.Add(key);
                        RiotShaderMeshes++;
                        return g;
                    }
                    _renderer.ReleaseRiotMeshGeometry(rid);
                    sb.AppendLine($"   {m.Key}: no material built - drawn diffuse-only");
                }
                else sb.AppendLine($"   {m.Key}: skinned geometry upload failed - drawn diffuse-only");
            }
            else if (scene is not null)
                sb.AppendLine($"   {m.Key}: no material resolved - drawn diffuse-only"
                              + (scene.Failures.Count > 0 ? ": " + scene.Failures[0] : ""));
        }

        int id = _renderer.CreateMeshGeometry(m.Positions, m.Uvs, m.Indices);
        if (id < 0)
        {
            SkippedProps++;
            sb.AppendLine($"   {m.Key}: geometry upload failed");
            return null;
        }
        return new PropGeom { Mesh = m, GeometryId = id };
    }

    /// <summary>The mesh pipeline draws props, but BuildMaterial still needs a valid shader pair to make a
    /// PreviewMaterial. quad_vs/quad_ps are used as that carrier and then ignored, exactly as the VFX mesh
    /// emitters do - the actual vertex and pixel work is the renderer's own mesh shader.</summary>
    private PreviewMaterial? BuildPropMaterial(VfxD3D11EmitterPipeline.Tocs tocs, string name, StringBuilder sb)
    {
        var vsPerm = ShaderCacheReader.ResolvePermutation(tocs.Vs, null, null, null, null, out _);
        var psPerm = ShaderCacheReader.ResolvePermutation(tocs.Ps, null, null, null, null, out _);
        if (vsPerm is null || psPerm is null) { sb.AppendLine($"   {name}: no base permutation"); return null; }

        var vs = _cache.LoadShader(ShaderCacheReader.TocPathFor(VfxD3D11EmitterPipeline.VsName, DxbcStage.Vertex),
                                   vsPerm.BlobIndex, out _);
        var ps = _cache.LoadShader(ShaderCacheReader.TocPathFor(VfxD3D11EmitterPipeline.PsName, DxbcStage.Pixel),
                                   psPerm.BlobIndex, out _);
        if (vs is null || ps is null) { sb.AppendLine($"   {name}: bytecode would not load"); return null; }

        var mat = _renderer.BuildMaterial(name, vs, ps, 0, 0, out var rep, null, null,
            StateDescription.Particle(BlendKind.Alpha));
        if (mat is null) sb.AppendLine($"   {name}: pipeline failed: {rep.Error}");
        return mat;
    }

    /// <summary>
    /// Advance the idle animations.
    ///
    /// <para>The pose is per MESH: one geometry serves every placement, so all six of a camp breathe
    /// together. That is GL's behaviour too, for the same reason, and is not a shortcut.</para>
    ///
    /// <para>M676: on Riot's path the pose is a bone palette, built as the character window builds its own
    /// and handed to each of the mesh's materials - the shader skins on the GPU. The diffuse-only draw keeps
    /// CPU skinning into its vertex buffer, as GL does and as the VFX mesh emitters already do here.</para>
    ///
    /// <para><paramref name="seconds"/> comes from the viewport's own animation clock rather than a private
    /// stopwatch, so pausing the DX11 viewport pauses props with everything else. GL uses a dedicated
    /// stopwatch there - a deliberate divergence, noted rather than hidden.</para>
    /// </summary>
    /// <summary>
    /// Pose every animated prop mesh that is worth posing this frame.
    ///
    /// <para>M694: a mesh is posed only when a placement of it is within the particle gate's distance of
    /// the camera and, for an idle, no more often than 30 Hz (<see cref="PropAnimationGate"/>); the pose
    /// scratch and the palette are reused, so a frame allocates nothing here. On the jade map the 21
    /// animated skins cost 0.8 ms and 355 KB of garbage per frame before; the palette is the same array
    /// the materials already hold, filled in place.</para>
    /// </summary>
    public void Tick(float seconds, bool playing, Vector3 cameraPosition, float gateDistanceSq)
    {
        var clock = Stopwatch.StartNew();
        PosedThisFrame = 0;
        NearMeshes = 0;
        // M733: ahead of the pose gate and ahead of the `playing` early-out - a prop that is not animating
        // still has to arrive, and switching animations off must not leave the rest of the set unuploaded.
        PumpUploads();
        if (!playing) { LastTickMs = clock.Elapsed.TotalMilliseconds; return; }
        // world space is unmirrored and the flip lives in the view matrix, so the camera is mirrored to
        // compare - the same vector the particle gate tests against
        var mirroredCam = new Vector3(-cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        foreach (var g in _geoms)
        {
            var m = g.Mesh;
            if (!m.CanAnimate) continue;
            bool near = PropAnimationGate.AnyNear(g.Instances, mirroredCam, gateDistanceSq);
            if (near) NearMeshes++;
            if (!PropAnimationGate.ShouldPose(g.LastPoseTime, seconds, m.PoseSource is not null, near)) continue;
            // M636: a driven mesh (the playground actor) supplies its own clip and time; a prop idles.
            var (clip, time) = m.PoseAt(seconds);
            try
            {
                if (g.RiotGeometryId >= 0)
                {
                    g.Palette = BonePalette.Build(m.Skeleton!, clip, time, g.Pose, g.Palette);
                    foreach (var mat in g.Materials) mat.BonePalette = g.Palette;
                }
                else
                {
                    var index = SkeletonIndex.For(m.Skeleton!);
                    SkeletonPose.ComputeSkin(index, clip, time, g.Pose);
                    int n = m.SknMesh!.VertexCount * 3;
                    if (g.SkinPositions is null || g.SkinPositions.Length < n) g.SkinPositions = new float[n];
                    // M732: positions only. UpdateMeshGeometryPositions writes a position + uv vertex, so
                    // the normals this used to compute were transformed, normalized, stored and dropped.
                    SkinnedMeshAnimator.Deform(m.SknMesh!, index, g.Pose, g.SkinPositions, null);
                    _renderer.UpdateMeshGeometryPositions(g.GeometryId, g.SkinPositions);
                }
                g.LastPoseTime = seconds;
                PosedThisFrame++;
            }
            catch { /* a bad clip must not take the frame down; the prop simply stays in bind pose */ }
        }
        LastTickMs = clock.Elapsed.TotalMilliseconds;
    }
}
