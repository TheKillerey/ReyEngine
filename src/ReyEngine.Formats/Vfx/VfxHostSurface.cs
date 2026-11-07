using System.Numerics;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M755: the character an effect is attached to, as an emission surface - for the emitters whose
/// <see cref="VfxEmissionSurface"/> names no mesh (<see cref="VfxEmissionSurface.NeedsHost"/>, about 1,190
/// in the shipped game: Akali's W cast, Aatrox's dash, the ward pads).
///
/// <para><b>In WORLD space.</b> Unlike a mesh surface, which lives in the emitter's own frame, the host is
/// wherever the character stands and however it is posed; the simulator takes its samples as world
/// positions (<c>EmitterState.EmissionInWorld</c>) rather than adding them to the emitter's point. A
/// preview rig that lifts or flies the system therefore does not drag the body with it - which is also the
/// truth in the game, where the body belongs to the champion and not to the effect.</para>
///
/// <para><b>Skinned on the CPU</b> with the same two calls the character viewport's skinner makes
/// (<see cref="SkeletonPose.ComputeSkin"/>, <see cref="SkinnedMeshAnimator.Deform"/>), positions only, and
/// only when the pose, the time or the placement moved. Both viewports call <see cref="Pose"/> with the
/// clock they already animate the character by, so the particles leave the body the viewer sees.</para>
///
/// <para>Every submesh counts, visible or not; which parts of a body League emits from when a submesh is
/// hidden is not known, and the whole mesh is what the file names (nothing).</para>
/// </summary>
public sealed class VfxHostSurface
{
    private readonly PoseBuffer _pose = new();
    private float[]? _skinned;
    private readonly Vector3[] _world;
    private readonly Vector3[] _worldNormals;
    private float[]? _skinnedNormals;
    private (SkeletonAsset? Skeleton, AnimationClip? Clip, float Time, Matrix4x4 World) _last;
    private bool _posed;

    public MeshAsset Mesh { get; }
    public VfxSurfaceSampler Sampler { get; }

    /// <summary>M860: where the character stands (the world transform's origin) - an attached-mesh emitter scales the body about it.</summary>
    public Vector3 Origin { get; private set; }

    /// <summary>M860: the skinned body in world space, three floats per vertex, as of the last <see cref="Pose"/>.</summary>
    public void CopyWorldPositions(float[] destination)
    {
        for (int v = 0; v < _world.Length && v * 3 + 2 < destination.Length; v++)
        { destination[v * 3] = _world[v].X; destination[v * 3 + 1] = _world[v].Y; destination[v * 3 + 2] = _world[v].Z; }
    }

    /// <summary>M876: the skinned body's normals in world space (rotation of the placement only), three floats per vertex,
    /// as of the last <see cref="Pose"/>; the bind normals when the mesh does not skin.</summary>
    public void CopyWorldNormals(float[] destination)
    {
        for (int v = 0; v < _worldNormals.Length && v * 3 + 2 < destination.Length; v++)
        { destination[v * 3] = _worldNormals[v].X; destination[v * 3 + 1] = _worldNormals[v].Y; destination[v * 3 + 2] = _worldNormals[v].Z; }
    }

    private VfxHostSurface(MeshAsset mesh, VfxSurfaceSampler sampler)
    {
        Mesh = mesh;
        Sampler = sampler;
        _world = new Vector3[mesh.Positions.Length / 3];
        _worldNormals = new Vector3[_world.Length];
    }

    /// <summary>A host over <paramref name="mesh"/>'s triangles, or null for a mesh with no area.</summary>
    public static VfxHostSurface? For(MeshAsset mesh)
    {
        var sampler = VfxSurfaceSampler.FromMesh(mesh.Positions, mesh.Indices);
        if (sampler is null) return null;
        var host = new VfxHostSurface(mesh, sampler);
        host.Pose(null, null, 0f, Matrix4x4.Identity);
        return host;
    }

    /// <summary>Put the body where the viewer sees it: this skeleton, this clip at this time, placed by
    /// <paramref name="world"/>. No skeleton, or a mesh that cannot skin, is the bind pose.</summary>
    public void Pose(SkeletonAsset? skeleton, AnimationClip? clip, float time, Matrix4x4 world)
    {
        var key = (skeleton, clip, time, world);
        if (_posed && key.Equals(_last)) return;
        _last = key;
        _posed = true;
        Origin = Vector3.Transform(Vector3.Zero, world);

        float[] src = Mesh.Positions;
        float[] srcN = Mesh.Normals;
        if (skeleton is not null && Mesh.CanSkin)
        {
            var index = SkeletonIndex.For(skeleton);
            SkeletonPose.ComputeSkin(index, clip, time, _pose);
            _skinned ??= new float[Mesh.VertexCount * 3];
            _skinnedNormals ??= new float[Mesh.VertexCount * 3];
            SkinnedMeshAnimator.Deform(Mesh, index, _pose, _skinned, _skinnedNormals);
            src = _skinned;
            srcN = _skinnedNormals;
        }
        for (int v = 0; v < _worldNormals.Length && v * 3 + 2 < srcN.Length; v++)
            _worldNormals[v] = Vector3.TransformNormal(new Vector3(srcN[v * 3], srcN[v * 3 + 1], srcN[v * 3 + 2]), world);
        for (int v = 0; v < _world.Length; v++)
            _world[v] = Vector3.Transform(new Vector3(src[v * 3], src[v * 3 + 1], src[v * 3 + 2]), world);
        Sampler.UpdatePositions(_world);
    }
}
