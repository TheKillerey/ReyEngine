using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M697: a character folder as the user has it - files of several eras, a hud icon beside the
/// diffuse, a stray clip for another rig - is read, checked, upgraded and packaged. The folders are real:
/// two of Map11's own characters, extracted to disk, whose files the installed game still ships in the
/// old forms (skn 2.1 / 1.1, anm v4 / v3).</summary>
public sealed class CharacterFolderImportTests : IDisposable
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private readonly string _root = Directory.CreateTempSubdirectory("reyengine_m697_").FullName;

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Extract every entry under a wad prefix into the temp root, keeping the relative paths.
    /// Null when the game is not installed.</summary>
    private string? Extract(string prefix)
    {
        if (!File.Exists(Map11)) return null;
        HashDatabase database;
        try { database = new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
        using var wad = WadArchive.Open(Map11, new WadPathResolver(database));
        int written = 0;
        foreach (var e in wad.Entries.Where(x => x.IsResolved && x.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            string file = Path.Combine(_root, e.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, wad.Extract(e));
            written++;
        }
        Assert.True(written > 0, $"Map11.wad.client no longer holds {prefix} - pick another sample character");
        return Path.Combine(_root, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void TheScanPicksTheMeshTheSkeletonTheClipsAndTheDiffuse()
    {
        if (Extract("assets/characters/yellowtrinketupgrade/") is not { } folder) return;
        var scan = CharacterFolderImporter.Scan(folder);
        Assert.Empty(scan.Problems);
        Assert.Empty(scan.Warnings);
        Assert.True(scan.CanImport);
        Assert.Equal("Yellowtrinketupgrade", scan.SuggestedName);
        Assert.Equal("yellowtrinket.skn", scan.Mesh!.FileName);
        Assert.Equal("SKN 2.1", scan.Mesh.Format.Label);
        Assert.True(scan.Mesh.WillUpgrade);
        Assert.Equal("upgraded to SKN 4.1 (no vertex type or bounds in the header; still loads)", scan.Mesh.Note);
        Assert.Equal("yellowtrinket.skl", scan.Skeleton!.FileName);
        Assert.False(scan.Skeleton.WillUpgrade);
        Assert.Equal("already current", scan.Skeleton.Note);
        Assert.Equal(4, scan.Clips.Count);
        Assert.Equal(3, scan.Clips.Count(c => c.Format.Label == "ANM v4" && c.WillUpgrade));
        Assert.Single(scan.Clips, c => c.Format.Label == "ANM v5" && !c.WillUpgrade);
        // two textures: the diffuse beside the mesh is the pick, the hud icon is listed and left
        Assert.Equal(2, scan.Textures.Count);
        var diffuse = Assert.Single(scan.Textures, t => t.Note == "the diffuse texture");
        Assert.Equal("trinket_yellow_upgrade_tx_cm.tex", diffuse.FileName);
        Assert.Single(scan.Textures, t => t.FileName == "yellowtrinketupgrade_square.tex" && t.Note!.StartsWith("another texture", StringComparison.Ordinal));
        Assert.All(scan.Files, f => Assert.True(f.Size > 0));
    }

    [Fact]
    public void ClipNamesComeFromTheFilesAndIdlesLoop()
    {
        Assert.Equal("Idle1", CharacterFolderImporter.ClipNameFor("golem_idle1.anm", "Golem"));
        Assert.Equal("Attack1", CharacterFolderImporter.ClipNameFor("MyGolem_attack1.anm", "MyGolem"));
        Assert.Equal("Death", CharacterFolderImporter.ClipNameFor("death.anm", "Anything"));
        Assert.Equal("Greateryellow_trinket_idle", CharacterFolderImporter.ClipNameFor("greateryellow_trinket_idle.anm", "Yellowtrinketupgrade"));
        Assert.Equal("Clip", CharacterFolderImporter.ClipNameFor("###.anm", "X"));
        Assert.True(CharacterFolderImporter.LoopsByName("Idle1"));
        Assert.True(CharacterFolderImporter.LoopsByName("Run"));
        Assert.True(CharacterFolderImporter.LoopsByName("Greateryellow_trinket_idle"));
        Assert.False(CharacterFolderImporter.LoopsByName("Attack1"));
        Assert.False(CharacterFolderImporter.LoopsByName("Death"));
        Assert.False(CharacterFolderImporter.LoopsByName("Spell4"));

        Assert.Equal("Annie", CharacterFolderImporter.SuggestName(@"C:\old\annie\skins\base", new[] { "annie_2012.skn" }));   // Riot's layout: the character folder
        Assert.Equal("Annie_2012", CharacterFolderImporter.SuggestName(@"C:\base", new[] { "annie_2012.skn" }));               // nothing above: the mesh
        Assert.Equal("Oldchamp", CharacterFolderImporter.SuggestName(@"C:\old champ\skins", Array.Empty<string>()));
        Assert.Equal("NewCharacter", CharacterFolderImporter.SuggestName(@"C:\skins\base", Array.Empty<string>()));
        Assert.Equal("Abc", CharacterFolderImporter.Sanitize("9abc"));
        Assert.Equal("Sru_Gromp_Prop", CharacterFolderImporter.Sanitize("Sru_Gromp_Prop"));
        Assert.Equal("", CharacterFolderImporter.Sanitize("123"));
    }

    [Fact]
    public void ExecuteUpgradesWhatIsOldAndKeepsOnlyClipsThatMoveThisRig()
    {
        if (Extract("assets/characters/yellowtrinketupgrade/") is not { } folder) return;
        var scan = CharacterFolderImporter.Scan(folder);
        var spec = CharacterFolderImporter.Propose(scan, "TrinketProp", skinScale: 1f);
        Assert.Equal("TrinketProp", spec.Name);
        Assert.Equal("yellowtrinket.skn", spec.SkinFileName);
        Assert.Equal("trinket_yellow_upgrade_tx_cm.tex", spec.TextureFileName);
        Assert.Equal(4, spec.Clips.Count);
        Assert.Contains(spec.Clips, c => c.Name == "Greateryellow_trinket_idle" && c.Loop);
        Assert.Contains(spec.Clips, c => c.Name == "Greateryellow_trinket_death" && !c.Loop);

        var result = CharacterFolderImporter.Execute(scan, spec);
        // the old forms were upgraded, the current ones left alone, and the log says which
        Assert.Contains(result.Upgrades, u => u.FileName == "yellowtrinket.skn" && u.Before == "SKN 2.1" && u.After == "SKN 4.1");
        Assert.Equal(3, result.Upgrades.Count(u => u.Before == "ANM v4" && u.After == "ANM v5"));
        Assert.DoesNotContain(result.Upgrades, u => u.FileName == "yellowtrinket.skl");
        Assert.DoesNotContain(result.Upgrades, u => u.FileName.EndsWith(".tex", StringComparison.Ordinal));
        // every staged character file is current now, and lands at the package's paths
        var staged = result.Files.ToDictionary(f => f.Path, f => f.Bytes);
        foreach (var path in new[] { result.Package.SkinPath, result.Package.SkeletonPath }.Concat(result.Package.ClipPaths.Values))
            Assert.True(CharacterFileFormats.Inspect(staged[path]).IsCurrent, path);
        Assert.Equal("assets/characters/trinketprop/skins/base/trinket_yellow_upgrade_tx_cm.tex", result.Package.TexturePath);
        Assert.Contains(result.Package.TexturePath, staged.Keys);
        Assert.Equal(3, result.Package.Bins.Count);
        // a clip that animates none of this skeleton's joints is dropped and said so, never staged silent
        var rig = SkeletonDecoder.Decode(staged[result.Package.SkeletonPath]);
        var joints = rig.Joints.Select(j => j.AnimHash).ToHashSet();
        foreach (var (name, path) in result.Package.ClipPaths)
        {
            var pose = new Dictionary<uint, (System.Numerics.Quaternion, System.Numerics.Vector3, System.Numerics.Vector3)>();
            AnimationDecoder.Decode(staged[path], name).Evaluate(0f, pose);
            Assert.True(pose.Keys.Any(joints.Contains), $"{name} moves nothing of the rig");
        }
        int dropped = spec.Clips.Count - result.Package.ClipPaths.Count;
        Assert.Equal(dropped, result.Warnings.Count(w => w.Contains("another rig", StringComparison.Ordinal)));
        Assert.Equal("Greateryellow_trinket_idle", result.Package.IdleClip);
        Assert.Equal(3 + 3 + result.Package.ClipPaths.Count, result.Files.Count);
    }

    [Fact]
    public void TheOldestFormsComeThroughToo()
    {
        if (Extract("assets/characters/sru_camprespawnmarker/") is not { } folder) return;
        var scan = CharacterFolderImporter.Scan(folder);
        Assert.True(scan.CanImport);
        Assert.Equal("SKN 1.1", scan.Mesh!.Format.Label);
        Assert.Equal("ANM v3", Assert.Single(scan.Clips).Format.Label);
        Assert.Contains("No texture found", Assert.Single(scan.Warnings));
        var result = CharacterFolderImporter.Execute(scan, CharacterFolderImporter.Propose(scan, "Marker"));
        Assert.Contains(result.Upgrades, u => u.FileName == "cube.skn" && u.After == "SKN 4.1");
        Assert.Contains(result.Upgrades, u => u.FileName == "idle1.anm" && u.Before == "ANM v3" && u.After == "ANM v5");
        Assert.DoesNotContain(result.Files, f => f.Path.EndsWith(".tex", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("No texture", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderWithoutAMeshOrASkeletonCannotImport()
    {
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var scan = CharacterFolderImporter.Scan(empty);
        Assert.False(scan.CanImport);
        Assert.Equal(2, scan.Problems.Count);
        Assert.Throws<InvalidOperationException>(() => CharacterFolderImporter.Propose(scan, "X"));

        string junk = Path.Combine(_root, "junk");
        Directory.CreateDirectory(junk);
        File.WriteAllBytes(Path.Combine(junk, "thing.skn"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        File.WriteAllBytes(Path.Combine(junk, "thing.skl"), new byte[32]);
        var broken = CharacterFolderImporter.Scan(junk);
        Assert.False(broken.CanImport);
        Assert.Contains(broken.Problems, p => p.Contains("cannot be read", StringComparison.Ordinal));
        Assert.All(broken.Files, f => Assert.Equal(CharacterImportRole.Ignored, f.Role));

        Assert.False(CharacterFolderImporter.Scan(Path.Combine(_root, "nowhere")).CanImport);
    }

    [Fact]
    public void AnyTextureBecomesATex()
    {
        var tex = new byte[] { (byte)'T', (byte)'E', (byte)'X', 0, 1, 2 };
        Assert.Same(tex, CharacterFolderImporter.ToTex(tex, ".tex", null));
        Assert.Throws<NotSupportedException>(() => CharacterFolderImporter.ToTex(new byte[8], ".png", null));
        var rgba = new byte[8 * 8 * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 7);
        var encoded = CharacterFolderImporter.ToTex(new byte[8], ".png", _ => new TextureImage(8, 8, rgba));
        Assert.NotNull(TexWriter.DetectFormat(encoded));
        Assert.Equal(8, TextureDecoder.Decode(encoded).Width);
    }
}
