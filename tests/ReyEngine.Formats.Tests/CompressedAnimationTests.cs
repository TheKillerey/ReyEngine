using System.Numerics;
using LeagueToolkit.Core.Primitives;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Skeletons;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M889: compressed .anm clips that omit tracks (K/DA Firestarter, True Damage Know My Name) evaluate at any time, in any order.
/// LTK's hot-frame evaluator indexed its frames with the jump caches' 0xFFFF "no track" entry on every re-seed, threw, and the
/// pose walk showed the bind pose from then on. Real-data tests read the installed champion WADs and skip when the install is missing.
/// </summary>
public sealed class CompressedAnimationTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- synthetic

    /// <summary>A minimal r3d2canm: joint 0 has two rotation keys (identity, then 90 degrees about Y), joint 1 has no frame at all.</summary>
    private static byte[] TwoJointClip(float duration, out uint j0, out uint j1)
    {
        j0 = 0x11111111; j1 = 0x22222222;
        var frames = new List<(ushort Time, ushort Key, byte[] Value)>();
        foreach (var (time, q) in new[] { ((ushort)0, Quaternion.Identity), ((ushort)65535, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)) })
        {
            var q6 = new byte[6];
            QuantizedQuaternion.Compress(q, q6);
            frames.Add((time, 0, q6));     // joint 0, type 0 (rotation)
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("r3d2canm"u8.ToArray()); w.Write(1); w.Write(0); w.Write(0x6d6e6163);
        w.Write(4);                      // flags: keyframe parametrization
        w.Write(2); w.Write(frames.Count); w.Write(0);
        w.Write(duration); w.Write(30f);
        for (int i = 0; i < 6; i++) w.Write(0f);                       // error metrics
        foreach (var v in new[] { -1f, -1f, -1f, 1f, 1f, 1f, 0f, 0f, 0f, 2f, 2f, 2f }) w.Write(v);   // translation / scale min, max
        int framesOff = 116, hashesOff = framesOff + frames.Count * 10;
        w.Write(framesOff); w.Write(hashesOff); w.Write(hashesOff);
        foreach (var f in frames) { w.Write(f.Time); w.Write(f.Key); w.Write(f.Value); }
        w.Write(j0); w.Write(j1);
        return ms.ToArray();
    }

    [Fact]
    public void A_clip_with_absent_tracks_is_evaluated_here_and_the_missing_components_come_from_the_bind()
    {
        var data = TwoJointClip(2f, out uint j0, out uint j1);
        var tracks = CompressedAnimationTracks.TryParse(data)!;
        Assert.NotNull(tracks);
        Assert.Equal(5, tracks.AbsentTracks);            // joint 0: translation, scale; joint 1: all three

        var bindT = new Vector3(3, 4, 5); var bindS = new Vector3(1.5f);
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        var clip = AnimationDecoder.Decode(data, "synthetic");
        clip.Evaluate(1f, pose, h => h == j0 ? (Quaternion.Identity, bindT, bindS) : null);

        Assert.False(pose.ContainsKey(j1));              // nothing authored: the skeleton walk keeps the bind local
        var (r, t, s) = pose[j0];
        Assert.Equal(bindT, t);
        Assert.Equal(bindS, s);
        float angle = 2 * MathF.Acos(Math.Min(1f, MathF.Abs(r.W))) * 180 / MathF.PI;
        Assert.InRange(angle, 44f, 46f);                 // halfway between the two keys
        Assert.True(MathF.Abs(r.Y) > 0.3f);              // about Y

        // stateless: an earlier time after a later one answers as a fresh evaluation does
        clip.Evaluate(2f, pose, null);
        clip.Evaluate(0f, pose, null);
        Assert.True(MathF.Abs(pose[j0].Rotation.W) > 0.9999f);
    }

    // ---------------------------------------------------------------- real data

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private static readonly Lazy<(HashDatabase Db, WadPathResolver Resolver)> Hashes = new(() =>
    {
        var db = new HashSyncService().LoadLocal(_ => { });
        return (db, new WadPathResolver(db));
    });

    private bool Skip()
    {
        if (Directory.Exists(Path.Combine(Final, "Champions"))) return false;
        output.WriteLine("SKIPPED: League of Legends is not installed at " + Final);
        return true;
    }

    private static byte[] Read(string champion, string path)
    {
        using var wad = WadArchive.Open(Path.Combine(Final, "Champions", champion + ".wad.client"), Hashes.Value.Resolver);
        return wad.Extract(HashAlgorithms.WadPath(path));
    }

    private static SkeletonAsset Skeleton(string champion, string skl) => SkeletonDecoder.Decode(Read(champion, skl));

    [Theory]
    [InlineData("Akali", "assets/characters/akali/skins/skin102/animations/idle1.anm", "assets/characters/akali/skins/skin102/akali_skin102.skl")]
    [InlineData("Akali", "assets/characters/akali/skins/skin103/animations/idle.anm", "assets/characters/akali/skins/skin103/akali_skin103.skl")]
    [InlineData("Evelynn", "assets/characters/evelynn/skins/skin65/animations/run.anm", "assets/characters/evelynn/skins/skin65/evelynn_skin65.skl")]
    public void A_new_compressed_clip_moves_the_skeleton_at_any_time_and_after_a_wrap(string champion, string anm, string skl)
    {
        if (Skip()) return;
        var data = Read(champion, anm);
        var tracks = CompressedAnimationTracks.TryParse(data);
        Assert.NotNull(tracks);
        Assert.True(tracks!.AbsentTracks > 0, "the clip omits tracks (the form LTK cannot seek)");

        var skeleton = Skeleton(champion, skl);
        var clip = AnimationDecoder.Decode(data, "real");
        float d = clip.Duration;
        // forward to the end, then wrap - the order a looping viewport asks in
        var times = new[] { 0.6f * d, 0.9f * d, 0.1f * d, 0.6f * d };
        var palettes = times.Select(t => BonePalette.Build(skeleton, clip, t)).ToArray();
        foreach (var p in palettes)
            Assert.All(p, m => Assert.True(float.IsFinite(m.M11) && float.IsFinite(m.M41) && float.IsFinite(m.M43)));

        static double Moved(Matrix4x4[] a, Matrix4x4[] b) => a.Zip(b).Max(x => (x.First.Translation - x.Second.Translation).Length());
        Assert.True(Moved(BonePalette.Bind(palettes[0].Length), palettes[0]) > 1, "the pose is not the bind pose");
        Assert.True(Moved(palettes[0], palettes[2]) > 0.5, "the pose changes over the clip");
        Assert.Equal(0, Moved(palettes[0], palettes[3]), 4);   // the same time after a wrap gives the same pose
    }

    [Fact]
    public void An_older_compressed_clip_keeps_ltks_decoder_and_the_evaluator_here_agrees_with_it()
    {
        if (Skip()) return;
        var data = Read("Kaisa", "assets/characters/kaisa/skins/base/animations/kaisa_idle1.anm");
        var tracks = CompressedAnimationTracks.TryParse(data)!;
        Assert.Equal(0, tracks.AbsentTracks);            // AnimationDecoder therefore keeps LTK for it

        var ltk = AnimationDecoder.Decode(data, "ltk");
        double worstAngle = 0, worstTranslation = 0;
        for (int i = 0; i <= 60; i++)                    // forward only: the order LTK's evaluator is reliable in
        {
            float t = tracks.Duration * i / 60f;
            var a = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
            var b = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
            ltk.Evaluate(t, a);
            tracks.Evaluate(t, b);
            Assert.Equal(a.Count, b.Count);
            foreach (var (k, v) in b)
            {
                var l = a[k];
                worstAngle = Math.Max(worstAngle, 2 * Math.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(l.Rotation, v.Rotation)))) * 180 / Math.PI);
                worstTranslation = Math.Max(worstTranslation, (l.Translation - v.Translation).Length());
            }
        }
        output.WriteLine($"max rotation difference {worstAngle:0.000} deg, translation {worstTranslation:0.0000}");
        Assert.True(worstAngle < 0.5, $"rotation differs by {worstAngle} degrees");
        Assert.True(worstTranslation < 0.05, $"translation differs by {worstTranslation}");
    }
}
