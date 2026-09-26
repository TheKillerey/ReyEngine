using System;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M777: a champion skin's <c>skinMeshProperties.material</c>/<c>materialOverride[].material</c> can name a
/// StaticMaterialDef that lives only in a LINKED bin (<c>tree.Dependencies</c>), not in the skin bin itself.
/// Reported as "Nexus skin30's glass is not showing correctly": <c>data/characters/nexus/skins/skin31.bin</c>
/// names <c>Characters/Nexus/Skins/Skin30/Materials/Glass_inst</c> for its <c>glass</c>/<c>glass_out</c>
/// submeshes, but that object exists only in
/// <c>Nexus_Multi_Skins_Skin30_Skins_Skin31.bin</c> (one of skin31's dependencies) - so every reader that
/// only looked at the skin bin fell back to the skin's own default binding (the body atlas, opaque cutout)
/// and the dome drew stamped with the character's texture instead of clear fresnel glass.
///
/// <para>These tests run against the real, shipped Map11.wad.client - Nexus's mesh and materials are map
/// content, not a champion WAD - and are no-ops (not failures) when the install is not present, the same
/// convention <see cref="CharacterMaterialTests"/> uses.</para>
/// </summary>
public sealed class LinkedMaterialResolutionTests
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private const string SkinPath = "data/characters/nexus/skins/skin31.bin";
    private const string GlassMaterial = "Characters/Nexus/Skins/Skin30/Materials/Glass_inst";
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static bool TryOpen(out WadArchive archive, out HashDatabase db)
    {
        archive = null!; db = null!;
        if (!File.Exists(Map11)) return false;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return false; }
        try { archive = WadArchive.Open(Map11, new WadPathResolver(db)); } catch { return false; }
        return true;
    }

    private static byte[]? ReadBin(WadArchive a, string path)
    {
        try
        {
            ulong h = HashAlgorithms.WadPath(path);
            return a.TryGetEntry(h, out _) ? a.Extract(h) : null;
        }
        catch { return null; }
    }

    [Fact]
    public void MaterialDocumentResolvesTheLinkedGlassMaterial()
    {
        if (!TryOpen(out var a, out var db)) return;
        using (a)
        {
            byte[]? skinBin = ReadBin(a, SkinPath);
            if (skinBin is null) return;   // patch moved the asset - nothing to assert against
            string? Bin(uint h) => db.TryGetBinName(h, out var n) ? n : null;
            string? Wad(ulong h) => db.TryGetPath(h, out var p) ? p : null;

            // WITHOUT readBin: exactly the old behaviour - the link is real but unresolved, so no binding
            // in this document is named after Glass_inst.
            var withoutLink = MaterialDocument.Parse(skinBin, Bin, Wad);
            Assert.DoesNotContain(withoutLink.Materials, m => m.Name.Equals(GlassMaterial, StringComparison.OrdinalIgnoreCase));

            // WITH readBin: the skin bin does not define Glass_inst itself, so it must be resolved through
            // tree.Dependencies - and marked as coming from there.
            var doc = MaterialDocument.Parse(skinBin, Bin, Wad, p => ReadBin(a, p));
            var glass = doc.Materials.SingleOrDefault(m => m.Name.Equals(GlassMaterial, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(glass);
            Assert.True(glass!.IsLinked);
            Assert.NotNull(glass.LinkedFromBin);
            Assert.Contains("skin31", glass.LinkedFromBin, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Shaders/SkinnedMesh/Glass", glass.RenderShader);
            Assert.Contains("glass", glass.Submeshes, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("glass_out", glass.Submeshes, StringComparer.OrdinalIgnoreCase);
            Assert.True(glass.IsStaticMaterialDef);   // contributes a real profile (M32), not a pseudo-binding

            // M777: the pass's own depthEnable, carried through - Glass_inst is one of the 19 linked
            // materials that author depthEnable=false. writeMask is absent, so WritesDepth keeps its
            // schema default (true).
            Assert.False(glass.DepthEnable);
            Assert.True(glass.WritesDepth);

            // A linked binding is external and read-only by construction: nothing here can be an editable
            // live container, because none of it belongs to THIS document's tree.
            Assert.False(glass.CanEditSwitches);
            Assert.False(glass.CanEditParameters);
            Assert.False(glass.CanEditSamplers);
            Assert.False(glass.CanChangeShader);
            Assert.False(glass.CanEditRenderState);
            Assert.False(glass.CanAddSchemaField);
        }
    }

    [Fact]
    public void ChampionMaterialResolverAgreesAndDoesNotInheritTheDefaultAtlas()
    {
        if (!TryOpen(out var a, out var db)) return;
        using (a)
        {
            byte[]? skinBin = ReadBin(a, SkinPath);
            if (skinBin is null) return;
            string? Bin(uint h) => db.TryGetBinName(h, out var n) ? n : null;
            string? Wad(ulong h) => db.TryGetPath(h, out var p) ? p : null;

            // Without readBin: the old (broken) behaviour - glass falls back to the skin's default diffuse.
            var without = ChampionMaterialResolver.Resolve(skinBin, Bin, Wad);
            Assert.NotNull(without.For("glass"));
            Assert.Equal(without.DefaultDiffuse, without.For("glass"));

            // With readBin: glass has its OWN material (Glass_inst, sampler-less) and must not inherit the
            // skin's default atlas - "no material of its own" and "material with no diffuse" are different.
            var with = ChampionMaterialResolver.Resolve(skinBin, Bin, Wad, p => ReadBin(a, p));
            Assert.Null(with.For("glass"));
            Assert.Null(with.For("glass_out"));
            var glassProfile = with.Profile("glass");
            Assert.False(glassProfile.AuthoredDepthTest);
            Assert.Equal(MaterialRenderMode.Transparent, glassProfile.RenderMode);
            // M777: the GL fallback tint - Glass_inst has no TintColor either, so Glass_Color1 stands in.
            Assert.NotNull(glassProfile.Tint);
        }
    }

    [Fact]
    public void EditingTheSkinRoundTripsWithoutLossAndNeverGainsGlassInst()
    {
        if (!TryOpen(out var a, out var db)) return;
        using (a)
        {
            byte[]? skinBin = ReadBin(a, SkinPath);
            if (skinBin is null) return;
            string? Bin(uint h) => db.TryGetBinName(h, out var n) ? n : null;
            string? Wad(ulong h) => db.TryGetPath(h, out var p) ? p : null;

            var doc = MaterialDocument.Parse(skinBin, Bin, Wad, p => ReadBin(a, p));
            byte[] serialized = doc.Serialize();

            // NOT raw byte equality: LeagueToolkit's BinTree.Write sorts objects by path hash while Riot's
            // files are not sorted (see BinRoundTripTests - measured over 1,599 bins, only 28.4% come back
            // byte-identical for that reason alone, with 0 property reorders and 0 lost objects). The
            // property this milestone owns is that resolving a LINKED material changes nothing about what
            // gets serialized: same object set, same properties, and specifically no Glass_inst.
            var original = new BinTree(new MemoryStream(skinBin));
            var reparsed = new BinTree(new MemoryStream(serialized));
            Assert.Equal(original.Objects.Count, reparsed.Objects.Count);
            foreach (var (hash, obj) in original.Objects)
            {
                Assert.True(reparsed.Objects.TryGetValue(hash, out var other), $"object 0x{hash:x8} was lost");
                Assert.True(BinPropEquality.ObjectsEqual(obj, other!), $"object 0x{hash:x8} changed semantically");
            }
            Assert.Equal(original.Dependencies, reparsed.Dependencies);

            Assert.False(reparsed.Objects.ContainsKey(H(GlassMaterial)),
                "Glass_inst lives in a linked bin and must never be written into the skin bin's own tree.");

            // A second save must not keep churning the file (M200's "fixed point" property).
            var doc2 = MaterialDocument.Parse(serialized, Bin, Wad, p => ReadBin(a, p));
            Assert.Equal(serialized, doc2.Serialize());
        }
    }

    [Fact]
    public void SubmeshRenderOrderIsParsedAndAppliedInFileOrderFallback()
    {
        if (!TryOpen(out var a, out var db)) return;
        using (a)
        {
            byte[]? skinBin = ReadBin(a, SkinPath);
            if (skinBin is null) return;
            string? Bin(uint h) => db.TryGetBinName(h, out var n) ? n : null;
            string? Wad(ulong h) => db.TryGetPath(h, out var p) ? p : null;

            var doc = MaterialDocument.Parse(skinBin, Bin, Wad, p => ReadBin(a, p));
            // skin31 authors "body top bowl orb frame glass glass_out" - measured off the real bin.
            Assert.Equal(new[] { "body", "top", "bowl", "orb", "frame", "glass", "glass_out" },
                doc.SkinMesh!.SubmeshRenderOrder);

            // The reorder rule itself (Dx11CharacterScene.ApplySubmeshRenderOrder), on data where the
            // authored order genuinely disagrees with file order - unlike skin31, where they happen to
            // match, so exercising it there would not prove the reorder ran at all.
            var fileOrder = new[] { "body", "glass", "top", "glass_out", "bowl" };
            var authored = new[] { "glass", "glass_out", "body" };   // omits top/bowl - they keep file order, after these
            var reordered = Dx11CharacterScene.ApplySubmeshRenderOrder(fileOrder, s => s, authored);
            Assert.Equal(new[] { "glass", "glass_out", "body", "top", "bowl" }, reordered);
        }
    }
}
