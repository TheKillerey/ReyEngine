using System.Numerics;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Animation;

/// <summary>The result of skinning a mesh for one animation time.</summary>
public sealed class SkinnedFrame
{
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required float[] BoneSegments { get; init; } // animated bone overlay (line pairs)
    /// <summary>M86: animated GLOBAL transform per joint name — lets VFX attach to (and follow) bones.</summary>
    public Dictionary<string, Matrix4x4>? BoneGlobals { get; init; }
}

/// <summary>
/// CPU linear-blend skinning. Samples an animation at a time, builds per-joint skin matrices
/// (inverseBind * animatedGlobal), and deforms the bind-pose mesh. Pure math — no GL.
/// </summary>
public static class SkinnedMeshAnimator
{
    public static SkinnedFrame Skin(MeshAsset mesh, SkeletonAsset skeleton, AnimationClip clip, float time)
    {
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        try { clip.Evaluate(time, pose); } catch { /* fall back to bind pose */ }

        var joints = skeleton.Joints;
        int maxId = 0;
        foreach (var j in joints) maxId = Math.Max(maxId, j.Id);

        var byId = new Dictionary<int, SkinJoint>(joints.Count);
        foreach (var j in joints) byId[j.Id] = j;

        var global = new Matrix4x4[maxId + 1];
        var done = new bool[maxId + 1];

        Matrix4x4 Global(int id)
        {
            if ((uint)id > maxId || !byId.TryGetValue(id, out var j)) return Matrix4x4.Identity;
            if (done[id]) return global[id];
            done[id] = true; // guard against cycles

            Matrix4x4 local = pose.TryGetValue(j.AnimHash, out var trs)
                ? Compose(trs.Translation, trs.Rotation, trs.Scale)
                : j.LocalTransform;

            global[id] = j.ParentId >= 0 && byId.ContainsKey(j.ParentId) ? local * Global(j.ParentId) : local;
            return global[id];
        }

        var skin = new Matrix4x4[maxId + 1];
        foreach (var j in joints) skin[j.Id] = j.InverseBindTransform * Global(j.Id);

        int vc = mesh.VertexCount;
        var pos = new float[vc * 3];
        var nrm = new float[vc * 3];
        var bi = mesh.BlendIndices!;
        var bw = mesh.BlendWeights!;
        var influences = skeleton.Influences;

        for (int v = 0; v < vc; v++)
        {
            var bp = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
            var bn = new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]);
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
            pos[v * 3] = sp.X; pos[v * 3 + 1] = sp.Y; pos[v * 3 + 2] = sp.Z;
            sn = sn.LengthSquared() > 1e-8f ? Vector3.Normalize(sn) : bn;
            nrm[v * 3] = sn.X; nrm[v * 3 + 1] = sn.Y; nrm[v * 3 + 2] = sn.Z;
        }

        var seg = new List<float>();
        foreach (var j in joints)
        {
            if (j.ParentId < 0 || !byId.ContainsKey(j.ParentId)) continue;
            var a = Global(j.Id).Translation;
            var b = Global(j.ParentId).Translation;
            seg.Add(a.X); seg.Add(a.Y); seg.Add(a.Z);
            seg.Add(b.X); seg.Add(b.Y); seg.Add(b.Z);
        }

        // M86: expose the animated global per joint NAME so clip particle events can ride their bone.
        var boneGlobals = new Dictionary<string, Matrix4x4>(joints.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var j in joints) boneGlobals[j.Name] = Global(j.Id);

        return new SkinnedFrame { Positions = pos, Normals = nrm, BoneSegments = seg.ToArray(), BoneGlobals = boneGlobals };
    }

    private static Matrix4x4 Compose(Vector3 t, Quaternion r, Vector3 s) =>
        Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
}
