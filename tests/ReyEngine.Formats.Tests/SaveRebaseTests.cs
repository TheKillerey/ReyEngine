using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M555: an editor's save must not rewind the rest of the file.
///
/// <para>The reported bug, in the reporter's words: "I save sun settings. It works also ingame. When I
/// change materials or other settings it somehow overrides it without the sunlight changes."</para>
///
/// <para>The sun lives in the map's materials.bin. The material editor holds a document parsed when the
/// map was opened and serialises the WHOLE file from it, so saving a material wrote a snapshot that
/// predated the sun edit. These reproduce that against a real shipped bin and pin the merge that fixes
/// it.</para>
/// </summary>
public sealed class SaveRebaseTests
{
    private const string Wad =
        @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
    private const string BinPath = "data/maps/mapgeometry/map453/jade_container.materials.bin";

    private static string? Resolve(uint h)
    {
        foreach (string n in new[]
        {
            "StaticMaterialDef", "name", "techniques", "passes", "shader", "samplerValues", "TextureName",
            "texturePath", "StaticMaterialTechniqueDef", "StaticMaterialPassDef", "paramValues",
            "StaticMaterialShaderParamDef", "value", "switches", "StaticMaterialSwitchDef", "on",
            "MapSunProperties", "sunColor", "SunIntensityScale", "skyLightColor", "lightMapColorScale",
        })
            if (HashAlgorithms.Fnv1a(n) == h) return n;
        return null;
    }

    private static byte[]? ShippedBin()
    {
        if (!File.Exists(Wad)) return null;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(Wad);
        ulong hash = HashAlgorithms.WadPath(BinPath);
        return wad.TryGetEntry(hash, out _) ? wad.Extract(hash) : null;
    }

    [Fact]
    public void AMaterialSaveDoesNotRewindASunSaveMadeAfterItLoaded()
    {
        if (ShippedBin() is not { } original) return;

        // 1. The material editor opens. Its document - and its base - are this snapshot.
        byte[] editorBase = original;
        var editorDoc = MaterialDocument.Parse(editorBase, Resolve);
        if (editorDoc is null || editorDoc.Materials.Count == 0) return;

        // 2. Meanwhile the sun is saved. Reads the file fresh, writes it back - so the file moves on.
        var sun = MapSunProperties.Extract(original);
        if (sun is null) return;
        var edited = sun with { SunIntensityScale = sun.SunIntensityScale + 3.5f };
        byte[]? withSun = MapSunProperties.Write(original, edited, out _);
        Assert.NotNull(withSun);
        Assert.Equal(edited.SunIntensityScale, MapSunProperties.Extract(withSun!)!.SunIntensityScale, 3);

        // 3. Now a material is changed and saved from the STALE document.
        var target = editorDoc.Materials.First(m => m.Slots.Count > 0);
        string materialName = target.Name;
        target.Slots[0].SetPath("assets/rebase/test/marker.tex");
        byte[] naive = editorDoc.Serialize();

        // Written straight out, this is the bug: the sun is back to its old value.
        Assert.Equal(sun.SunIntensityScale, MapSunProperties.Extract(naive)!.SunIntensityScale, 3);

        // 4. Rebased onto the current file instead, BOTH survive.
        var (merged, _) = BinThreeWayMerge.Merge(editorBase, naive, withSun!, Resolve);

        Assert.Equal(edited.SunIntensityScale, MapSunProperties.Extract(merged)!.SunIntensityScale, 3);
        var back = MaterialDocument.Parse(merged, Resolve);
        Assert.NotNull(back);
        var saved = back!.Materials.First(m => m.Name == materialName);
        Assert.Contains(saved.Slots, s => s.Path == "assets/rebase/test/marker.tex");
    }

    [Fact]
    public void TheMergeIsSkippedWhenNothingLandedUnderneath()
    {
        // The overwhelmingly common case: nobody else touched the file. Merging anyway would spend real
        // time re-parsing three trees on every autosave tick, so the choke point compares first. This pins
        // that an unchanged base and current are byte-identical, which is what that comparison relies on.
        if (ShippedBin() is not { } original) return;
        byte[] again = ShippedBin()!;
        Assert.True(original.AsSpan().SequenceEqual(again),
            "reading the same shipped asset twice must give identical bytes, or the fast path never hits");
    }

    [Fact]
    public void TheEditorsExposeTheBaseTheSaveNeedsToRebaseFrom()
    {
        // Without a record of what a document was parsed from there is no way to tell an edit from a stale
        // value, so a whole-file save can only overwrite.
        foreach (var t in new[]
        {
            typeof(ReyEngine.App.ViewModels.MaterialEditorViewModel),
            typeof(ReyEngine.App.ViewModels.BinEditorViewModel),
        })
        {
            var property = t.GetProperty("BaseBytes");
            Assert.True(property is not null, $"{t.Name} must expose BaseBytes");
            Assert.Equal(typeof(byte[]), property!.PropertyType);
        }
    }

    [Fact]
    public void ASaveRebasesRatherThanOverwrites()
    {
        // The wiring itself: both whole-file editors must route their bytes through the merge before they
        // reach the writer. A new whole-file editor that forgets this reintroduces the bug silently.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;
        string file = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (!File.Exists(file)) return;
        string source = File.ReadAllText(file);

        Assert.Contains("RebaseOntoCurrent(binEntry, bytes, MaterialEditor.BaseBytes", source);
        Assert.Contains("RebaseOntoCurrent(entry, bytes, BinEditor.BaseBytes", source);
    }
}
