using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M295: placed prop meshes - Baron, the dragons, jungle camps - in the D3D11 viewport, matching what
/// the OpenGL viewport draws for <c>PropRenderSet</c>.</para>
///
/// <para><b>No double-render risk, and that is a fact about the data rather than a precaution.</b> Props are
/// parsed from the map's <c>.materials.bin</c> by MapPlaceableExtractor and load their own <c>.skn</c>
/// files; the D3D11 static mesh is built solely from the mapgeo's own position/index arrays and its
/// groups. The two share no geometry, so drawing props here cannot duplicate anything
/// <see cref="Dx11SceneBuilder"/> committed.</para>
///
/// <para>Geometry is uploaded ONCE per distinct mesh and drawn once per placement, which is why a map with
/// six camps of the same monster costs one buffer. Animation follows GL exactly: the pose is per MESH, not
/// per placement, because one vertex buffer serves every instance of it.</para>
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
        public required int GeometryId { get; init; }
        public readonly List<Matrix4x4> Instances = new();
    }
    private readonly List<PropGeom> _geoms = new();

    public D3D11MapProps(ShaderPreviewRenderer renderer, ShaderCacheReader cache)
    { _renderer = renderer; _cache = cache; }

    public int PropMeshCount => _geoms.Count;
    public int PropInstanceCount { get; private set; }
    public int SkippedProps { get; private set; }
    public string Report { get; private set; } = "";

    /// <summary>Drop everything this driver owns. The renderer's mesh geometry is pool-owned and released
    /// with the scene, so only the materials are removed here.</summary>
    public void Clear()
    {
        if (_mine.Count > 0) _renderer.RemoveMaterials(m => _mine.Contains(m));
        _mine.Clear();
        _geoms.Clear();
        PropInstanceCount = 0;
    }

    /// <summary>Build materials for a prop set. Safe to call with null - that is "props are switched off",
    /// which just clears.</summary>
    public void Load(PropRenderSet? set)
    {
        Clear();
        if (set is null || set.Instances.Count == 0) { Report = "no props"; return; }

        var tocs = VfxD3D11EmitterPipeline.ReadTocs(_cache, out var tocError);
        if (tocs is null) { Report = tocError ?? "the particle shaders could not be read"; return; }

        var sb = new StringBuilder();
        // One geometry per DISTINCT mesh. PropMesh is shared by reference across placements of the same
        // skin precisely so this dedup is possible - six identical camps upload one buffer.
        var byMesh = new Dictionary<PropMesh, PropGeom>(ReferenceEqualityComparer.Instance);

        foreach (var inst in set.Instances)
        {
            if (!byMesh.TryGetValue(inst.Mesh, out var g))
            {
                var m = inst.Mesh;
                if (m.Positions.Length == 0 || m.Indices.Length == 0)
                {
                    // Malformed content is counted and named, never thrown - a map with one bad prop must
                    // still open.
                    SkippedProps++;
                    sb.AppendLine($"   {m.Key}: no geometry ({m.Positions.Length / 3} verts, {m.Indices.Length} indices)");
                    byMesh[m] = null!;
                    continue;
                }
                int id = _renderer.CreateMeshGeometry(m.Positions, m.Uvs, m.Indices);
                if (id < 0)
                {
                    SkippedProps++;
                    sb.AppendLine($"   {m.Key}: geometry upload failed");
                    byMesh[m] = null!;
                    continue;
                }
                g = new PropGeom { Mesh = m, GeometryId = id };
                byMesh[m] = g;
                _geoms.Add(g);
            }
            if (g is null) continue;              // a mesh already rejected above
            g.Instances.Add(inst.Transform);
            PropInstanceCount++;
        }

        // One material per (mesh, submesh): a prop's submeshes carry their own diffuse, so they cannot
        // share a material even though they share a geometry.
        foreach (var g in _geoms)
        {
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
                    _renderer.SetTexture(mat, "TEXTURE__TX", $"prop:{g.Mesh.Key}:{sub.Start}",
                        img.Rgba, img.Width, img.Height);

                _renderer.AddMaterial(mat);
                _mine.Add(mat);
            }
        }

        Report = $"{_geoms.Count} prop mesh(es), {PropInstanceCount} placement(s)"
               + (SkippedProps > 0 ? $", {SkippedProps} skipped" : "")
               + (sb.Length > 0 ? "\n" + sb : "");
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
    /// Advance the idle animations. CPU skinning, as GL does and as the VFX mesh emitters already do here.
    ///
    /// <para>The pose is per MESH: one vertex buffer serves every placement, so all six of a camp breathe
    /// together. That is GL's behaviour too, for the same reason, and is not a shortcut.</para>
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
            float dur = m.IdleClip!.Duration > 1e-3f ? m.IdleClip.Duration : 1f;
            try
            {
                var frame = ReyEngine.Formats.Animation.SkinnedMeshAnimator.Skin(
                    m.SknMesh!, m.Skeleton!, m.IdleClip, seconds % dur);
                _renderer.UpdateMeshGeometryPositions(g.GeometryId, frame.Positions);
            }
            catch { /* a bad clip must not take the frame down; the prop simply stays in bind pose */ }
        }
    }
}
