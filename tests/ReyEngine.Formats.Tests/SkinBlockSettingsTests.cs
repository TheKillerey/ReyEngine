using System;
using System.IO;
using System.Linq;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M705: the skin's OWN block is what a character with no material draws from, and only its
/// diffuse was ever shown. Measured over 25 champion WADs (2,230 blocks): selfIllumination on 2,217,
/// reflectionFresnelColor on 2,054, skinScale on 1,783, brushAlphaOverride on 287, reflectionOpacityDirect
/// on 177, reflectionFresnel on 169, reflectionMap on 111, glossTexture on 91, emissiveTexture on 58,
/// castShadows on 32. Those are the settings, and they are editable now - including adding one the block
/// omits.</summary>
public sealed class SkinBlockSettingsTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly CharacterPackageSpec Spec = new(
        "MyGolem", "MyGolem.skn", "MyGolem.skl", "MyGolem_TX_CM.tex",
        new[] { new CharacterClipSpec("Idle1", "mygolem_idle1.anm", Loop: true) }, SkinScale: 1.5f, SelfIllumination: 0.7f);

    private static byte[] CreatedSkinBin() =>
        CharacterPackageBuilder.Build(Spec).Bins.Single(b => b.Path.EndsWith("skins/skin0.bin", StringComparison.Ordinal)).Bytes;

    /// <summary>Names for the fields the created skin carries, so the document reads them as names.</summary>
    private static string? Name(uint hash) => new[]
    {
        "skinMeshProperties", "SkinMeshDataProperties", "SkinCharacterDataProperties", "texture", "skeleton",
        "simpleSkin", "skinScale", "selfIllumination", "championSkinName", "objectPath", "emoteBuffbone",
        "godrayFXbone", "skinAnimationProperties", "animationGraphData", "materialOverride", "reflectionFresnel",
    }.FirstOrDefault(n => H(n) == hash);

    [Fact]
    public void TheBlocksOwnSettingsAreListedAndEditable()
    {
        var doc = MaterialDocument.Parse(CreatedSkinBin(), Name, _ => null);
        var block = Assert.Single(doc.Materials, m => m.IsDefault);

        // the two the Character Creator writes are there, as editable settings rather than nothing
        var scale = Assert.Single(block.Parameters, p => p.Name == "skinScale");
        var illumination = Assert.Single(block.Parameters, p => p.Name == "selfIllumination");
        Assert.True(scale.IsEditable);
        Assert.True(illumination.IsEditable);
        Assert.Equal("1.5", scale.CurrentText, ignoreCase: true);

        // the diffuse is still the first slot, because it is the one every skin has
        Assert.Equal("texture", block.Slots[0].SamplerName);

        // what this view shows elsewhere is not repeated here as a "setting"
        Assert.DoesNotContain(block.Parameters, p => p.Name is "skeleton" or "simpleSkin" or "materialOverride");
        Assert.DoesNotContain(block.Slots, s => s.SamplerName is "skeleton" or "simpleSkin");

        // and an edit round-trips through the document
        illumination.Apply("0.25");
        Assert.True(illumination.IsDirty);
        Assert.True(doc.IsDirty);
        var reparsed = MaterialDocument.Parse(doc.Serialize(), Name, _ => null);
        Assert.Equal("0.25", reparsed.Materials.Single(m => m.IsDefault).Parameters
            .Single(p => p.Name == "selfIllumination").CurrentText, ignoreCase: true);
    }

    [Fact]
    public void ASettingTheBlockOmitsCanBeAdded()
    {
        var doc = MaterialDocument.Parse(CreatedSkinBin(), Name, _ => null);
        var block = doc.Materials.Single(m => m.IsDefault);

        // the block is a write target, and it knows which class it is, so the schema can offer its fields
        Assert.True(block.CanAddSchemaField);
        Assert.Equal(H("SkinMeshDataProperties"), block.ClassHash);
        Assert.Contains(H("selfIllumination"), block.PresentHashes);
        Assert.DoesNotContain(H("reflectionFresnel"), block.PresentHashes);

        Assert.True(block.TryAddDefaultProperty(H("reflectionFresnel"), "F32", "0.6", out var reason), reason);
        Assert.Null(reason);
        Assert.False(block.TryAddDefaultProperty(H("reflectionFresnel"), "F32", "0.6", out reason));
        Assert.Contains("already carries", reason!);

        // it is written into the skin's block, not somewhere else, and comes back as a setting
        var reparsed = MaterialDocument.Parse(doc.Serialize(), Name, _ => null);
        var after = reparsed.Materials.Single(m => m.IsDefault);
        var added = Assert.Single(after.Parameters, p => p.Name == "reflectionFresnel");
        Assert.Equal("0.6", added.CurrentText, ignoreCase: true);
        Assert.Contains(H("reflectionFresnel"), after.PresentHashes);
    }

    [Fact]
    public void ARealChampionSkinShowsTheSettingsRiotAuthored()
    {
        string wad = Path.Combine(Champions, "Aatrox.wad.client");
        if (!File.Exists(wad)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        ulong hash = HashAlgorithms.WadPath("data/characters/aatrox/skins/skin0.bin");
        if (!archive.TryGetEntry(hash, out _)) return;

        var doc = MaterialDocument.Parse(archive.Extract(hash), h => db.TryGetBinName(h, out var n) ? n : null,
            h => db.TryGetPath(h, out var p) ? p : null);
        // a champion can have TWO default bindings: the skin's own block, and a StaticMaterialDef the
        // block links as the base-mesh material. The block is the one with no shader to change.
        var block = Assert.Single(doc.Materials, m => m.IsDefault && !m.IsStaticMaterialDef);

        // the two nearly every shipped block carries
        Assert.Contains(block.Parameters, p => p.Name == "selfIllumination");
        Assert.Contains(block.Parameters, p => p.Name == "reflectionFresnelColor");
        Assert.All(block.Parameters, p => Assert.True(p.IsEditable, p.Name));
        Assert.Contains(block.Slots, s => s.SamplerName == "texture");
        // and it is still the binding with no shader to change, which is why M703 exists
        Assert.False(block.CanChangeShader);
    }
}
