using System;
using System.Collections.Generic;
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
    }
    private readonly List<PropGeom> _geoms = new();
    private readonly HashSet<string> _textureKeys = new(StringComparer.Ordinal);

    public D3D11MapProps(ShaderPreviewRenderer renderer, ShaderCacheReader cache)
    { _renderer = renderer; _cache = cache; }

    public int PropMeshCount => _geoms.Count;
    public int PropInstanceCount { get; private set; }
    public int SkippedProps { get; private set; }
    /// <summary>M676: how many of the prop meshes draw through Riot's own shaders.</summary>
    public int RiotShaderMeshes { get; private set; }
    public string Report { get; private set; } = "";

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
        PropInstanceCount = 0;
        SkippedProps = 0;
        RiotShaderMeshes = 0;
    }

    /// <summary>Build materials for a prop set. Safe to call with null - that is "props are switched off",
    /// which just clears. <paramref name="prepare"/> is the host's way from a prop mesh to a D3D11 character
    /// scene (M676); null, or a null result, keeps that mesh on the diffuse-only draw.</summary>
    public void Load(PropRenderSet? set, Func<PropMesh, PreparedCharacterScene?>? prepare = null)
    {
        Clear();
        if (set is null || set.Instances.Count == 0) { Report = "no props"; return; }

        var sb = new StringBuilder();
        // One geometry per DISTINCT mesh. PropMesh is shared by reference across placements of the same
        // skin precisely so this dedup is possible - six identical camps upload one buffer.
        var byMesh = new Dictionary<PropMesh, PropGeom?>(ReferenceEqualityComparer.Instance);

        foreach (var inst in set.Instances)
        {
            if (!byMesh.TryGetValue(inst.Mesh, out var g))
            {
                g = Upload(inst.Mesh, prepare, sb);
                byMesh[inst.Mesh] = g;   // null = rejected, remembered so the next placement does not retry
                if (g is not null) _geoms.Add(g);
            }
            if (g is null) continue;
            g.Instances.Add(inst.Transform);
            PropInstanceCount++;
        }

        // The diffuse-only draw for whatever the Riot path did not take. One material per (mesh, submesh):
        // a prop's submeshes carry their own diffuse, so they cannot share a material even though they
        // share a geometry. The carrier shaders are read on first need - a map whose every prop took the
        // Riot path never touches them.
        VfxD3D11EmitterPipeline.Tocs? tocs = null;
        foreach (var g in _geoms)
        {
            if (g.RiotGeometryId >= 0) continue;
            if (tocs is null)
            {
                tocs = VfxD3D11EmitterPipeline.ReadTocs(_cache, out var tocError);
                if (tocs is null)
                {
                    SkippedProps++;
                    sb.AppendLine($"   {g.Mesh.Key}: {tocError ?? "the particle shaders could not be read"}");
                    continue;
                }
            }
            foreach (var sub in g.Mesh.Submeshes)
            {
                if (sub.Count <= 0) continue;
                var mat = BuildPropMaterial(tocs, $"prop:{g.Mesh.Key}", sb);
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

        Report = $"{_geoms.Count} prop mesh(es), {PropInstanceCount} placement(s)"
               + (RiotShaderMeshes > 0 ? $", {RiotShaderMeshes} on Riot's shaders" : "")
               + (SkippedProps > 0 ? $", {SkippedProps} skipped" : "")
               + (sb.Length > 0 ? "\n" + sb : "");
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
    public void Tick(float seconds, bool playing)
    {
        if (!playing) return;
        foreach (var g in _geoms)
        {
            var m = g.Mesh;
            if (!m.CanAnimate) continue;
            // M636: a driven mesh (the playground actor) supplies its own clip and time; a prop idles.
            var (clip, time) = m.PoseAt(seconds);
            try
            {
                if (g.RiotGeometryId >= 0)
                {
                    var palette = BonePalette.Build(m.Skeleton!, clip, time);
                    foreach (var mat in g.Materials) mat.BonePalette = palette;
                    continue;
                }
                var frame = SkinnedMeshAnimator.Skin(m.SknMesh!, m.Skeleton!, clip, time);
                _renderer.UpdateMeshGeometryPositions(g.GeometryId, frame.Positions);
            }
            catch { /* a bad clip must not take the frame down; the prop simply stays in bind pose */ }
        }
    }
}
