using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LeagueToolkit.Core.Animation;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M695: the header of every skn / skl / anm form the client ships is recognised, and an outdated
/// file upgraded to today's form decodes to the same mesh, the same rig and the same poses. The outdated
/// samples are the real ones the installed Summoner's Rift still carries (skn 0.1 / 1.1 / 2.1, anm v3 /
/// v4); the legacy rig is the one form no shipped wad has, so it is built from a current rig here and,
/// when the old Halloween files are on this machine, read from disk as well.</summary>
public sealed class CharacterFormatTests
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private const string LegacyRigOnDisk = @"D:\Mods\OldHalloweenRift\OldFiles\Map11\2a5551f4d0c42e5a.skl";

    [Fact]
    public void EveryShippedHeaderIsRecognised()
    {
        Assert.Equal(("SKN 4.1", true, true), Triple(Skn(4, 1)));
        Assert.Equal(("SKN 2.1", true, false), Triple(Skn(2, 1)));
        Assert.Equal(("SKN 1.1", true, false), Triple(Skn(1, 1)));
        Assert.Equal(("SKN 0.1", true, false), Triple(Skn(0, 1)));
        Assert.Equal(("SKN 3.0", false, false), Triple(Skn(3, 0)));
        Assert.Equal(("SKL v0", true, true), Triple(Skl(0)));
        Assert.Equal(("SKL legacy v1", true, false), Triple(LegacySkl(1)));
        Assert.Equal(("SKL legacy v2", true, false), Triple(LegacySkl(2)));
        Assert.Equal(("SKL legacy v7", false, false), Triple(LegacySkl(7)));
        Assert.Equal(("ANM v5", true, true), Triple(Anm("r3d2anmd", 5)));
        Assert.Equal(("ANM v4", true, false), Triple(Anm("r3d2anmd", 4)));
        Assert.Equal(("ANM v3", true, false), Triple(Anm("r3d2anmd", 3)));
        Assert.Equal(("ANM compressed v1", true, true), Triple(Anm("r3d2canm", 1)));
        Assert.Equal(("ANM compressed v9", false, false), Triple(Anm("r3d2canm", 9)));
        Assert.Equal(CharacterFileKind.Unknown, CharacterFileFormats.Inspect(Encoding.ASCII.GetBytes("not a character file at all")).Kind);
        Assert.Equal(CharacterFileKind.Unknown, CharacterFileFormats.Inspect(Array.Empty<byte>()).Kind);
        Assert.Equal(CharacterFileKind.Skeleton, CharacterFileFormats.KindOf(@"c:\x\Annie.SKL"));
        Assert.Equal(CharacterFileKind.Unknown, CharacterFileFormats.KindOf("annie.dds"));

        // a current or unreadable file is handed back untouched, with the reason on the format
        var current = CharacterFormatUpgrader.Upgrade(Skl(0));
        Assert.False(current.Changed);
        Assert.Same(current.Before, current.After);
        var broken = CharacterFormatUpgrader.Upgrade(Skn(3, 0));
        Assert.False(broken.Changed);
        Assert.Equal("unknown simple-skin version", broken.Before.Note);
        Assert.False(broken.Before.CanUpgrade);

        static (string, bool, bool) Triple(byte[] b) { var f = CharacterFileFormats.Inspect(b); return (f.Label, f.IsReadable, f.IsCurrent); }
        static byte[] Skn(ushort major, ushort minor) => BitConverter.GetBytes(0x00112233u).Concat(BitConverter.GetBytes(major)).Concat(BitConverter.GetBytes(minor)).Concat(new byte[8]).ToArray();
        static byte[] Skl(uint version) => new byte[4].Concat(BitConverter.GetBytes(0x22FD4FC3u)).Concat(BitConverter.GetBytes(version)).Concat(new byte[4]).ToArray();
        static byte[] LegacySkl(uint version) => Encoding.ASCII.GetBytes("r3d2sklt").Concat(BitConverter.GetBytes(version)).Concat(new byte[8]).ToArray();
        static byte[] Anm(string magic, uint version) => Encoding.ASCII.GetBytes(magic).Concat(BitConverter.GetBytes(version)).Concat(new byte[8]).ToArray();
    }

    [Fact]
    public void FrameCountsFollowTheSourceFamily()
    {
        Assert.Equal(11, AnimationWriter.FrameCount(11f / 30f, 30f, sourceIsCompressed: false));   // v5: duration = frames * frameDuration
        Assert.Equal(51, AnimationWriter.FrameCount(51f / 30f, 30f, sourceIsCompressed: false));
        Assert.Equal(31, AnimationWriter.FrameCount(1f, 30f, sourceIsCompressed: true));          // canm: the last frame lands on the duration
        Assert.Equal(1, AnimationWriter.FrameCount(0f, 30f, sourceIsCompressed: false));
    }

    [Fact]
    public void AnUpgradedAnimationPlaysTheSamePoses()
    {
        var samples = Samples(".anm", "ANM v3", "ANM v4", "ANM v5", "ANM compressed v1");
        if (samples is null) return;
        foreach (var (label, path, bytes) in samples)
        {
            var upgrade = CharacterFormatUpgrader.Upgrade(bytes);
            Assert.Equal(label, upgrade.Before.Label);
            if (label.StartsWith("ANM compressed", StringComparison.Ordinal) || label == "ANM v5")
            {
                Assert.False(upgrade.Changed);   // already current: not resampled
                continue;
            }
            Assert.True(upgrade.Changed, path);
            Assert.Equal("ANM v5", upgrade.After.Label);
            Assert.True(upgrade.After.IsCurrent);
            AssertSamePoses(bytes, upgrade.Bytes, path);
        }

        // a v5 written by us is the same file family Riot writes: the data starts at byte 76 and every
        // section is addressed relative to byte 12, so LeagueToolkit sizes them by the next offset
        var v3 = samples.First(s => s.Label == "ANM v3");
        var written = CharacterFormatUpgrader.UpgradeAnimation(v3.Bytes);
        Assert.Equal("r3d2anmd", Encoding.ASCII.GetString(written, 0, 8));
        Assert.Equal(5u, BitConverter.ToUInt32(written, 8));
        Assert.Equal((uint)(written.Length - 12), BitConverter.ToUInt32(written, 12));
        Assert.Equal(76, BitConverter.ToInt32(written, 52) + 12);                    // vector palette first
        Assert.True(BitConverter.ToInt32(written, 56) > BitConverter.ToInt32(written, 52));   // then quaternions
        Assert.True(BitConverter.ToInt32(written, 40) > BitConverter.ToInt32(written, 56));   // then joint hashes
        Assert.True(BitConverter.ToInt32(written, 60) > BitConverter.ToInt32(written, 40));   // then frames
        // and resampling the result gives the same bytes back - the palettes are already minimal
        Assert.Equal(written, CharacterFormatUpgrader.UpgradeAnimation(written));
    }

    [Fact]
    public void AnUpgradedSimpleSkinHasTheSameGeometry()
    {
        var samples = Samples(".skn", "SKN 0.1", "SKN 1.1", "SKN 2.1", "SKN 4.1");
        if (samples is null) return;
        foreach (var (label, path, bytes) in samples)
        {
            var upgrade = CharacterFormatUpgrader.Upgrade(bytes);
            Assert.Equal(label, upgrade.Before.Label);
            if (label == "SKN 4.1") { Assert.False(upgrade.Changed); continue; }
            Assert.True(upgrade.Changed, path);
            Assert.Equal("SKN 4.1", upgrade.After.Label);
            var before = SkinnedMeshDecoder.Decode(bytes);
            var after = SkinnedMeshDecoder.Decode(upgrade.Bytes);
            Assert.Equal(before.VertexCount, after.VertexCount);
            Assert.Equal(before.Positions, after.Positions);
            Assert.Equal(before.Normals, after.Normals);
            Assert.Equal(before.Uvs, after.Uvs);
            Assert.Equal(before.Indices, after.Indices);
            Assert.Equal(before.BlendIndices, after.BlendIndices);
            Assert.Equal(before.BlendWeights, after.BlendWeights);
            Assert.Equal(before.SubMeshes.Select(s => (s.Material, s.StartIndex, s.IndexCount, s.VertexCount)),
                         after.SubMeshes.Select(s => (s.Material, s.StartIndex, s.IndexCount, s.VertexCount)));
            // 4.1 carries what the old header lacked: a vertex type and the bounds
            Assert.NotEqual(Vector3.Zero, after.BoundsMax - after.BoundsMin);
        }
    }

    [Fact]
    public void AnUpgradedLegacyRigKeepsEveryJoint()
    {
        var samples = Samples(".skl", "SKL v0");
        if (samples is null) return;
        var modern = samples[0].Bytes;
        Assert.False(CharacterFormatUpgrader.Upgrade(modern).Changed);
        var reference = SkeletonDecoder.Decode(modern);

        // the same rig in the pre-2015 form: joints by name with world transforms, v2 influences
        byte[] legacy = WriteLegacyRig(reference, version: 2);
        Assert.Equal("SKL legacy v2", CharacterFileFormats.Inspect(legacy).Label);
        var upgrade = CharacterFormatUpgrader.Upgrade(legacy);
        Assert.True(upgrade.Changed);
        Assert.Equal("SKL v0", upgrade.After.Label);
        // the legacy form stores WORLD bind transforms only, so the upgraded local is derived from them;
        // a modern file authors its local separately and may differ by a little from that derivation
        AssertSameRig(reference, SkeletonDecoder.Decode(upgrade.Bytes), samples[0].Path, localsDerivedFromWorld: true);
        Assert.Equal(reference.Influences, SkeletonDecoder.Decode(upgrade.Bytes).Influences);

        // v1 has no influence list: every joint influences, in order
        var v1 = CharacterFormatUpgrader.Upgrade(WriteLegacyRig(reference, version: 1));
        Assert.Equal("SKL v0", v1.After.Label);
        Assert.Equal(Enumerable.Range(0, reference.Joints.Count).Select(i => (short)i), SkeletonDecoder.Decode(v1.Bytes).Influences);

        if (File.Exists(LegacyRigOnDisk))
        {
            var real = File.ReadAllBytes(LegacyRigOnDisk);
            Assert.Equal("SKL legacy v1", CharacterFileFormats.Inspect(real).Label);
            var realUpgrade = CharacterFormatUpgrader.Upgrade(real);
            Assert.Equal("SKL v0", realUpgrade.After.Label);
            AssertSameRig(SkeletonDecoder.Decode(real), SkeletonDecoder.Decode(realUpgrade.Bytes), LegacyRigOnDisk);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static void AssertSamePoses(byte[] before, byte[] after, string path)
    {
        var a = AnimationDecoder.Decode(before, "before");
        var b = AnimationDecoder.Decode(after, "after");
        Assert.True(Math.Abs(a.Fps - b.Fps) < 1e-3f, $"{path}: fps {a.Fps} -> {b.Fps}");
        Assert.True(Math.Abs(a.Duration - b.Duration) < 1e-3f, $"{path}: duration {a.Duration} -> {b.Duration}");
        var pa = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        var pb = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        float step = 1f / a.Fps;
        foreach (float t in new[] { 0f, step, 2.5f * step, a.Duration * 0.37f, a.Duration * 0.5f, a.Duration - step, a.Duration })
        {
            pa.Clear(); pb.Clear();
            a.Evaluate(t, pa); b.Evaluate(t, pb);
            Assert.Equal(pa.Keys.OrderBy(k => k), pb.Keys.OrderBy(k => k));
            foreach (var (joint, x) in pa)
            {
                var y = pb[joint];
                Assert.True((x.Translation - y.Translation).Length() < 1e-3f, $"{path} t={t} joint {joint:x8}: translation {x.Translation} -> {y.Translation}");
                Assert.True((x.Scale - y.Scale).Length() < 1e-3f, $"{path} t={t} joint {joint:x8}: scale {x.Scale} -> {y.Scale}");
                float dot = Math.Abs(Quaternion.Dot(Quaternion.Normalize(x.Rotation), Quaternion.Normalize(y.Rotation)));
                Assert.True(dot > 1f - 2e-4f, $"{path} t={t} joint {joint:x8}: rotation {x.Rotation} -> {y.Rotation} (dot {dot})");
            }
        }
    }

    private static void AssertSameRig(SkeletonAsset a, SkeletonAsset b, string path, bool localsDerivedFromWorld = false)
    {
        Assert.Equal(a.Joints.Count, b.Joints.Count);
        for (int i = 0; i < a.Joints.Count; i++)
        {
            var x = a.Joints[i]; var y = b.Joints[i];
            Assert.Equal(x.Name, y.Name);
            Assert.Equal(x.Id, y.Id);
            Assert.Equal(x.ParentId, y.ParentId);
            Assert.Equal(x.AnimHash, y.AnimHash);
            AssertClose(x.InverseBindTransform, y.InverseBindTransform, $"{path} joint {x.Name} inverse bind");
            var expectedLocal = x.LocalTransform;
            if (localsDerivedFromWorld)
            {
                Assert.True(Matrix4x4.Invert(x.InverseBindTransform, out var world));
                expectedLocal = world;
                if (x.ParentId >= 0) expectedLocal = world * a.Joints[x.ParentId].InverseBindTransform;
            }
            AssertClose(expectedLocal, y.LocalTransform, $"{path} joint {x.Name} local");
        }
    }

    private static void AssertClose(Matrix4x4 a, Matrix4x4 b, string what)
    {
        float max = 0f;
        var d = a - b;
        foreach (float v in new[] { d.M11, d.M12, d.M13, d.M14, d.M21, d.M22, d.M23, d.M24, d.M31, d.M32, d.M33, d.M34, d.M41, d.M42, d.M43, d.M44 })
            max = Math.Max(max, Math.Abs(v));
        Assert.True(max < 2e-3f, $"{what}: max difference {max}");
    }

    /// <summary>A legacy <c>r3d2sklt</c> file for the given rig: 32-byte padded names, parent, radius and
    /// the 4x3 WORLD transform stored column by column, as LeagueToolkit's legacy reader expects.</summary>
    private static byte[] WriteLegacyRig(SkeletonAsset rig, uint version)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("r3d2sklt"));
        bw.Write(version);
        bw.Write(0x12345678u);                 // skeleton id
        bw.Write((uint)rig.Joints.Count);
        foreach (var j in rig.Joints)
        {
            var name = new byte[32];
            Encoding.ASCII.GetBytes(j.Name, 0, Math.Min(31, j.Name.Length), name, 0);
            bw.Write(name);
            bw.Write(j.ParentId);
            bw.Write(2.1f);                    // radius
            Assert.True(Matrix4x4.Invert(j.InverseBindTransform, out var world));
            for (int col = 0; col < 3; col++)
                for (int row = 0; row < 4; row++)
                    bw.Write(world[row, col]);
        }
        if (version == 2)
        {
            bw.Write((uint)rig.Influences.Count);
            foreach (short inf in rig.Influences) bw.Write((uint)inf);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>The first file of each wanted form inside Map11.wad.client, or null when the game is not
    /// installed. Fails when the installed map no longer carries a wanted form, so the test cannot pass
    /// by finding nothing.</summary>
    private static IReadOnlyList<(string Label, string Path, byte[] Bytes)>? Samples(string extension, params string[] labels)
    {
        if (!File.Exists(Map11)) return null;
        HashDatabase database;
        try { database = new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
        var wanted = labels.ToHashSet(StringComparer.Ordinal);
        var found = new List<(string, string, byte[])>();
        using var wad = WadArchive.Open(Map11, new WadPathResolver(database));
        foreach (var e in wad.Entries)
        {
            if (!e.IsResolved || !e.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes;
            try { bytes = wad.Extract(e); } catch { continue; }
            var format = CharacterFileFormats.Inspect(bytes);
            if (!wanted.Remove(format.Label)) continue;
            found.Add((format.Label, e.Path, bytes));
            if (wanted.Count == 0) break;
        }
        Assert.True(wanted.Count == 0, $"Map11.wad.client no longer ships {string.Join(", ", wanted)} - pick another sample source");
        return found;
    }
}
