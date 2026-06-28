using System.Numerics;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;

namespace ReyEngine.Formats.Skeletons;

/// <summary>Decodes a .skl (RigResource) into joints (bind/inverse-bind transforms + anim hashes).</summary>
public static class SkeletonDecoder
{
    public static SkeletonAsset Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        var rig = new RigResource(ms);

        var bones = new List<BoneInfo>(rig.Joints.Count);
        var joints = new List<SkinJoint>(rig.Joints.Count);
        foreach (var j in rig.Joints)
        {
            Vector3 worldPos = Matrix4x4.Invert(j.InverseBindTransform, out var global)
                ? global.Translation
                : Vector3.Zero;
            bones.Add(new BoneInfo(j.Name, j.Id, j.ParentId, worldPos));
            joints.Add(new SkinJoint(j.Name, j.Id, j.ParentId, Elf.HashLower(j.Name), j.LocalTransform, j.InverseBindTransform));
        }

        return new SkeletonAsset { Bones = bones, Joints = joints, Influences = rig.Influences.ToList() };
    }
}
