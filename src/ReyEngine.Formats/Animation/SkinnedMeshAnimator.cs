using System.Numerics;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Animation;

public sealed class SkinnedFrame
{
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required float[] BoneSegments { get; init; } // animated bone overlay (line pairs)
    public Dictionary<string, Matrix4x4>? BoneGlobals { get; init; }
}

/// <summary>
/// CPU skinning: the pose, then every vertex through its four influences.
///
/// <para>M694: split in two. <see cref="SkinnedMeshAnimator.Skin"/> is unchanged in what it returns, but
/// the pose now comes from <see cref="SkeletonPose"/> (cached skeleton index, caller-owned scratch) and
/// the vertex pass is <see cref="Deform"/>, a pure function of the pose and the mesh that a driver can
/// run for several meshes on several cores at once. On the jade map the vertex passes of the 21
/// animated skins are 16 ms of one core per frame - the whole of a 60 fps budget - and they share
/// nothing but read-only mesh data.</para>
/// </summary>
public static class SkinnedMeshAnimator
{
    public static SkinnedFrame Skin(MeshAsset mesh, SkeletonAsset skeleton, AnimationClip clip, float time)
    {
        var index = SkeletonIndex.For(skeleton);
        var scratch = new PoseBuffer();
        SkeletonPose.ComputeSkin(index, clip, time, scratch);

        int vc = mesh.VertexCount;
        var pos = new float[vc * 3];
        var nrm = new float[vc * 3];
        Deform(mesh, index, scratch, pos, nrm);

        var seg = BonePalette.Segments(index, scratch);

        // M86: expose the animated global per joint NAME so clip particle events can ride their bone.
        var joints = skeleton.Joints;
        var boneGlobals = new Dictionary<string, Matrix4x4>(joints.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var j in joints) boneGlobals[j.Name] = SkeletonPose.GlobalOf(scratch, j.Id);

        return new SkinnedFrame { Positions = pos, Normals = nrm, BoneSegments = seg, BoneGlobals = boneGlobals };
    }

    /// <summary>
    /// The vertex pass: <paramref name="mesh"/>'s positions and normals through the skin matrices in
    /// <paramref name="pose"/> (from <see cref="SkeletonPose.ComputeSkin"/>) into <paramref name="positions"/>
    /// and <paramref name="normals"/>, each VertexCount * 3 long. Reads only; safe to run for different
    /// meshes on different threads, and for one mesh once its pose has been computed.
    /// </summary>
    public static void Deform(MeshAsset mesh, SkeletonIndex index, PoseBuffer pose, float[] positions, float[] normals)
    {
        int vc = mesh.VertexCount;
        if (positions.Length < vc * 3 || normals.Length < vc * 3)
            throw new ArgumentException("output arrays must hold VertexCount * 3 floats");
        var skin = pose.Skin;
        int maxId = index.MaxId;
        var bi = mesh.BlendIndices!;
        var bw = mesh.BlendWeights!;
        var influences = index.Skeleton.Influences;
        var src = mesh.Positions;
        var srcN = mesh.Normals;

        for (int v = 0; v < vc; v++)
        {
            var bp = new Vector3(src[v * 3], src[v * 3 + 1], src[v * 3 + 2]);
            var bn = new Vector3(srcN[v * 3], srcN[v * 3 + 1], srcN[v * 3 + 2]);
            Vector3 sp = Vector3.Zero, sn = Vector3.Zero;
            float wsum = 0;

            for (int k = 0; k < 4; k++)
            {
                float w = bw[v * 4 + k];
                if (w <= 0f) continue;
                int idx = bi[v * 4 + k];
                int jointId = influences.Count > 0 && idx >= 0 && idx < influences.Count ? influences[idx] : idx;
                if (jointId < 0 || jointId > maxId) continue;
                var m = skin[jointId];
                sp += w * Vector3.Transform(bp, m);
                sn += w * Vector3.TransformNormal(bn, m);
                wsum += w;
            }

            if (wsum <= 0f) { sp = bp; sn = bn; }
            positions[v * 3] = sp.X; positions[v * 3 + 1] = sp.Y; positions[v * 3 + 2] = sp.Z;
            sn = sn.LengthSquared() > 1e-8f ? Vector3.Normalize(sn) : bn;
            normals[v * 3] = sn.X; normals[v * 3 + 1] = sn.Y; normals[v * 3 + 2] = sn.Z;
        }
    }
}
