using System;
using System.Collections.Generic;
using System.Linq;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Meta;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M706: reported on a custom character (urf_ghost) - adding a material failed because the skin
/// object had no name in the hash database, and adding a setting said "added" while the editor went on
/// showing nothing to edit. Both are fixed here: the object path falls back to the file the bin is in, and
/// an added field gets its row at once.</summary>
public sealed class SchemaFieldAddTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly CharacterPackageSpec Spec = new(
        "Urf_Ghost", "urf_ghost.skn", "urf_ghost.skl", "urf_ghost_tx_cm.tex",
        new[] { new CharacterClipSpec("Idle1", "urf_ghost_idle1.anm", Loop: true) });

    private static byte[] SkinBin() =>
        CharacterPackageBuilder.Build(Spec).Bins.Single(b => b.Path.EndsWith("skins/skin0.bin", StringComparison.Ordinal)).Bytes;

    private static string? KnownNames(uint hash) => new[]
    {
        "skinMeshProperties", "SkinMeshDataProperties", "SkinCharacterDataProperties", "texture", "skeleton",
        "simpleSkin", "skinScale", "selfIllumination", "championSkinName", "objectPath", "emoteBuffbone",
        "godrayFXbone", "skinAnimationProperties", "animationGraphData", "reflectionFresnel", "glossTexture",
    }.FirstOrDefault(n => H(n) == hash);

    [Fact]
    public void TheObjectPathComesFromTheFileWhenTheDictionaryIsSilent()
    {
        Assert.Equal("Characters/Urf/Skins/Skin0",
            CharacterMaterialBinder.ObjectPathFromBinPath("data/characters/urf/skins/skin0.bin"));
        // only the first letter of each segment is raised - the rest is the file's own spelling, and a bin
        // name is hashed case-insensitively so it does not matter
        Assert.Equal("Characters/Urf_ghost/Skins/Skin0",
            CharacterMaterialBinder.ObjectPathFromBinPath(@"DATA\Characters\urf_ghost\Skins\Skin0.BIN"));
        Assert.Null(CharacterMaterialBinder.ObjectPathFromBinPath("skin0.bin"));
        Assert.Null(CharacterMaterialBinder.ObjectPathFromBinPath(""));
        Assert.Null(CharacterMaterialBinder.ObjectPathFromBinPath(null));

        byte[] skin = SkinBin();
        // no dictionary at all: the file still names it, and the derivation is only taken because it
        // hashes to the object the bin actually holds
        Assert.Equal("Characters/Urf_ghost/Skins/Skin0",
            CharacterMaterialBinder.SkinObjectPath(skin, resolveBinName: null, "data/characters/urf_ghost/skins/skin0.bin"));
        // a file path that is not this bin's is refused rather than used
        Assert.Null(CharacterMaterialBinder.SkinObjectPath(skin, null, "data/characters/someone_else/skins/skin0.bin"));
        Assert.Null(CharacterMaterialBinder.SkinObjectPath(skin, null, null));
        // and the dictionary still wins when it knows, because it spells the path the way Riot does
        Assert.Equal("Characters/URF_Ghost/Skins/Skin0", CharacterMaterialBinder.SkinObjectPath(skin,
            h => h == H("Characters/Urf_Ghost/Skins/Skin0") ? "Characters/URF_Ghost/Skins/Skin0" : null,
            "data/characters/urf_ghost/skins/skin0.bin"));
    }

    [Fact]
    public void AnAddedFieldIsImmediatelyEditable()
    {
        var doc = MaterialDocument.Parse(SkinBin(), KnownNames, _ => null);
        var block = doc.Materials.Single(m => m.IsDefault);
        int before = block.Parameters.Count;

        Assert.True(block.TryAddDefaultProperty(H("reflectionFresnel"), "F32", "0.6", out var reason), reason);
        var added = Assert.Single(block.Parameters, p => p.Name == "reflectionFresnel");
        Assert.Equal(before + 1, block.Parameters.Count);
        Assert.True(added.IsEditable);
        Assert.Equal("0.6", added.CurrentText, ignoreCase: true);

        // it edits the property that was written, not a copy of it
        added.Apply("0.25");
        var reparsed = MaterialDocument.Parse(doc.Serialize(), KnownNames, _ => null);
        Assert.Equal("0.25", reparsed.Materials.Single(m => m.IsDefault).Parameters
            .Single(p => p.Name == "reflectionFresnel").CurrentText, ignoreCase: true);

        // a texture-valued field becomes a sampler slot instead of a parameter. The schema records its
        // default as the empty chunk link "0x0", which is exactly what the field's absence already means.
        int slots = block.Slots.Count;
        Assert.True(block.TryAddDefaultProperty(H("glossTexture"), "File", "\"0x0\"", out reason), reason);
        Assert.Equal(slots + 1, block.Slots.Count);
        var slot = Assert.Single(block.Slots, s => s.SamplerName == "glossTexture");
        slot.SetPath("assets/characters/urf_ghost/skins/base/urf_ghost_gloss.tex");
        Assert.True(slot.IsDirty);
    }

    [Fact]
    public void TheEditorShowsTheAddedFieldWithoutReloading()
    {
        var doc = MaterialDocument.Parse(SkinBin(), KnownNames, _ => null);
        var editor = new MaterialEditorViewModel
        {
            // the schema the panel offers from: one field this block omits
            DeclaredProperties = h => h == H("SkinMeshDataProperties")
                ? new List<MetaProperty>
                {
                    new(H("selfIllumination"), "selfIllumination", "F32", "", "", "", "1"),
                    new(H("reflectionFresnel"), "reflectionFresnel", "F32", "", "", "", "0.6"),
                }
                : new List<MetaProperty>(),
            ClassName = h => KnownNames(h),
        };
        editor.Load(doc, new WadAssetEntry { Path = "data/characters/urf_ghost/skins/skin0.bin", IsResolved = true }, null);

        var block = editor.Materials.Single(m => m.Model.IsDefault);
        editor.SelectedMaterial = block;
        int before = block.Parameters.Count;

        var row = Assert.Single(block.Schema.UnsetRows, r => r.Name == "reflectionFresnel");
        Assert.True(row.CanAdd);
        row.AddCommand.Execute(null);

        Assert.True(row.Added);
        Assert.Null(row.Error);
        // the row the user came to set is on screen now, not after a reload
        Assert.Equal(before + 1, block.Parameters.Count);
        var parameter = Assert.Single(block.Parameters, p => p.Name == "reflectionFresnel");
        Assert.Equal("0.6", parameter.EditedText, ignoreCase: true);
        Assert.True(block.HasParameters);
        Assert.True(editor.IsDirty);
    }
}
