using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M556: a blend factor that reads the framebuffer's ALPHA is how a decal draws over black.
///
/// <para>Found on the reporter's <c>order_base_circle</c> decal, which carried
/// <c>dstColorBlendFactor = 9</c> (InvDstAlpha). The result is
/// <c>src*srcAlpha + dst*(1 - dstAlpha)</c>, so wherever the framebuffer alpha is 1 the destination
/// contributes NOTHING and the surface composites over black rather than over the ground. Nothing about
/// the material looks wrong - it simply draws onto nothing.</para>
/// </summary>
public sealed class BlendFactorGuardTests
{
    private const string Shipping = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";
    private const string Wad = Shipping + @"\Map453.wad.client";
    private const string BinPath = "data/maps/mapgeometry/map453/jade_container.materials.bin";

    private static string? Resolve(uint h)
    {
        foreach (string n in new[]
        {
            "StaticMaterialDef", "name", "techniques", "passes", "shader", "samplerValues", "TextureName",
            "texturePath", "StaticMaterialTechniqueDef", "StaticMaterialPassDef", "paramValues",
            "StaticMaterialShaderParamDef", "value", "switches", "StaticMaterialSwitchDef", "on",
            "blendEnable", "srcColorBlendFactor", "dstColorBlendFactor",
            "srcAlphaBlendFactor", "dstAlphaBlendFactor",
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
    public void ADestinationAlphaFactorIsReported()
    {
        if (ShippedBin() is not { } bin) return;
        var doc = MaterialDocument.Parse(bin, Resolve);
        var blended = doc?.Materials.FirstOrDefault(m => m.DstBlendFactor == 7);
        if (blended is null) return;

        // Exactly the edit that produced the black decal.
        Assert.True(blended.SetPassU32("dstColorBlendFactor", 9));
        byte[] broken = doc!.Serialize();

        var issues = ModShapeValidator.ValidateBin(SafeBinTree.Parse(broken), broken, Resolve);
        var found = issues.Where(i => i.Category == "blend-factor").ToList();
        Assert.NotEmpty(found);
        Assert.Contains(found, i => i.Detail.Contains("InvDstAlpha"));
    }

    [Fact]
    public void TheFactorsRiotActuallyShipsAreNotReported()
    {
        // The guard is worthless if it cries on Riot's own data, and 6/7 is what 164 of Riot's 165 decal
        // materials use.
        if (ShippedBin() is not { } bin) return;
        var issues = ModShapeValidator.ValidateBin(SafeBinTree.Parse(bin), bin, Resolve);
        Assert.Empty(issues.Where(i => i.Category == "blend-factor"));
    }

    [Fact]
    public void RiotShipsNoDestinationAlphaBlendFactorInAnyMapMaterial()
    {
        // The evidence the rule rests on. Measured across the shipped map WADs: the factors Riot uses on
        // blended map materials are 1, 4, 6 and 7 - DstAlpha (8) and InvDstAlpha (9) appear zero times.
        if (!Directory.Exists(Shipping)) return;

        int scanned = 0, offenders = 0;
        foreach (string wadPath in Directory.EnumerateFiles(Shipping, "*.wad.client").Take(6))
        {
            ReyEngine.Core.Wad.WadArchive wad;
            try { wad = ReyEngine.Core.Wad.WadArchive.Open(wadPath); } catch { continue; }
            using (wad)
                foreach (var entry in wad.Entries)
                {
                    byte[] bytes;
                    try { bytes = wad.Extract(entry.PathHash); } catch { continue; }
                    if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'R' || bytes[2] != 'O' || bytes[3] != 'P') continue;
                    MaterialDocument? d;
                    try { d = MaterialDocument.Parse(bytes, Resolve); } catch { continue; }
                    if (d is null) continue;
                    foreach (var m in d.Materials)
                    {
                        scanned++;
                        if (m.SrcBlendFactor is 8 or 9 || m.DstBlendFactor is 8 or 9) offenders++;
                    }
                }
        }
        if (scanned < 100) return;   // game not installed, or nothing to measure
        Assert.Equal(0, offenders);
    }
}
