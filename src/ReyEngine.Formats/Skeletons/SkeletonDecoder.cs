using System.Numerics;
using LeagueToolkit.Core.Animation;

namespace ReyEngine.Formats.Skeletons;

/// <summary>Decodes a .skl (RigResource) into joints with global bind positions.</summary>
public static class SkeletonDecoder
{
    public static SkeletonAsset Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        var rig = new RigResource(ms);

        var bones = new List<BoneInfo>(rig.Joints.Count);
        for (int i = 0; i < rig.Joints.Count; i++)
        {
            var j = rig.Joints[i];
            // Global bind transform = inverse of the inverse-bind matrix.
            Vector3 worldPos = Matrix4x4.Invert(j.InverseBindTransform, out var global)
                ? global.Translation
                : Vector3.Zero;
            bones.Add(new BoneInfo(j.Name, j.Id, j.ParentId, worldPos));
        }

        return new SkeletonAsset { Bones = bones };
    }
}
