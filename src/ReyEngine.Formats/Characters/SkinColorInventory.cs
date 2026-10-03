using System.Globalization;
using System.Numerics;
using System.Text;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Characters;

/// <summary>What a body texture is to the skin. Only these draw colour; normal maps, masks and data maps are not here.</summary>
public enum BodyTextureRole
{
    /// <summary>The skin's <c>texture</c>, a material's Diffuse / Albedo / Colour / Main sampler.</summary>
    Diffuse,
    /// <summary>Emissive / glow / illumination: the skin block's <c>emissiveTexture</c> or an Emiss* / Glow* sampler.</summary>
    Emissive,
    /// <summary>A Gradient sampler: a colour ramp the shader maps brightness through.</summary>
    Gradient,
    /// <summary>A MatCap sampler: a sphere-mapped lighting picture, RGB, added over the lit surface.</summary>
    MatCap,
    /// <summary>The skin block's <c>reflectionMap</c>: the picture reflected in the surface.</summary>
    Reflection,
}

/// <summary>M812: one place a body texture is used - which material, through which sampler, on which submeshes.</summary>
public sealed record BodyTextureUse(string Material, string Sampler, IReadOnlyList<string> Submeshes, bool IsDefaultMaterial);

/// <summary>M812: one colour texture the body draws with, once, however many samplers name it.</summary>
/// <param name="Path">The path as the bin spells it, or the <c>0x...</c> form of a chunk link nothing can name.</param>
/// <param name="Hash"><c>BinTexturePath.HashOfReference(Path)</c> - the chunk. Not <c>HashAlgorithms.WadPath</c>.</param>
/// <param name="Role">The first role found, in the order the skin's materials list them.</param>
/// <param name="IsLinked">EVERY use comes from a material in a linked bin (read-only: the skin bin cannot be edited to
/// repoint it). False when any use is a material of the skin's own.</param>
/// <param name="SharedWith">The OTHER skins and chromas of the same character folder that reference the same file in any
/// role (labels, <see cref="SkinColorScanner.SkinLabel"/>). Pets and forms that ship in the same champion WAD are
/// characters of their own and are not compared. Empty when it is unshared, or when sharing was not computed.</param>
public sealed record BodyTexture(string Path, ulong Hash, BodyTextureRole Role, IReadOnlyList<BodyTextureUse> Uses,
    bool IsLinked, string? LinkedFromBin, IReadOnlyList<string> SharedWith)
{
    public bool IsShared => SharedWith.Count > 0;

    /// <summary>"Sword_inst (Diffuse_Texture)", "(skin default texture)" or "(inline override: Hair)" - the first use, then a count.</summary>
    public string Source
    {
        get
        {
            if (Uses.Count == 0) return "";
            var first = Uses[0];
            string text = first.Material.StartsWith('(') ? first.Material : $"{ShortName(first.Material)} ({first.Sampler})";
            return Uses.Count == 1 ? text : $"{text} +{Uses.Count - 1} more";
        }
    }

    internal static string ShortName(string material)
    {
        int cut = material.LastIndexOf('/');
        return cut >= 0 && cut + 1 < material.Length ? material[(cut + 1)..] : material;
    }
}

/// <summary>
/// M812: a body material parameter that looks like a colour.
///
/// <para><b>A heuristic, and it says so:</b> a parameter is listed when its NAME contains color, colour or tint
/// (case-insensitive) and its value is a Vector4, a Color or a Vector3. Riot names the parameter and nothing tags it as
/// a colour, so scalars that are merely named after one are listed too - measured over every champion skin bin,
/// <c>Fresnel_Color_Intensity</c>, <c>TailDistortIntensity</c> ("DistorTINTensity"), <c>ColorFresnelSize</c> and
/// <c>VColor_G_Mask_Discard_Size</c> all match. The value is shown so a person can tell.</para>
/// </summary>
/// <param name="ValueOmitted">The material names the parameter and writes no value: an authored zero (M673), not an
/// absent parameter.</param>
public sealed record BodyColorParameter(string Material, string Name, Vector4 Value, string TypeName, bool ValueOmitted,
    bool IsLinked, string? LinkedFromBin);

/// <summary>M812: a sampler the inventory left out, and why - so "no mask is listed" is checkable rather than asserted.</summary>
public sealed record ExcludedSampler(string Material, string Sampler, string Path, string Reason);

/// <summary>M812: an effect texture, once per file, across every system that names it.</summary>
public sealed record EffectTextureEntry(string Path, ulong Hash, IReadOnlyList<string> Roles, int SystemCount,
    bool IsCubemap, IReadOnlyList<string> SharedWith)
{
    public bool IsShared => SharedWith.Count > 0;
}

/// <summary>M812: one effect system the skin uses.</summary>
/// <param name="PathHash">The system object's path hash - the identity the sharing test compares.</param>
/// <param name="EffectKeys">The ResourceResolver keys that lead here (FNV-1a of an effect name such as
/// <c>Aatrox_AA_Trail_02</c>); empty for a system reached only as somebody's child.</param>
/// <param name="ReachedBy">Why it is on the list, in words: an effect key, an idle effect on a bone, a direct link from
/// the skin, or "child of X".</param>
/// <param name="Bin">The bin the system object lives in.</param>
/// <param name="SharedWith">OTHER skins of the champion that use the same system object.</param>
/// <param name="IsInferred">The system is not reached by anything the skin itself names - its resolver, its direct links, and
/// the children of those - but only through a <c>GearSkinUpgrade</c> in its dependency files (or a child of one). That the
/// upgrade is THIS skin's is read from how Riot groups the shared bins, not from a link, so it is shown as inferred.</param>
public sealed record EffectSystemEntry(uint PathHash, string Name, string ParticlePath, IReadOnlyList<uint> EffectKeys,
    IReadOnlyList<string> ReachedBy, string Bin, IReadOnlyList<VfxColorEmitter> Emitters, IReadOnlyList<string> SharedWith,
    bool IsInferred = false)
{
    public bool IsShared => SharedWith.Count > 0;
    public int ColorValueCount => Emitters.Sum(e => e.Colors.Count);
    public int TextureCount => Emitters.Sum(e => e.Textures.Count);
}

/// <summary>
/// M812: how the skin's effect systems were found - the facts a test (or a doubtful person) needs to see that
/// the list is "what THIS skin uses" and not "what its dependency closure happens to contain".
///
/// <para>A champion's skin bin names its dependency bins, and the shared <c>*_Multi_Skins_*</c> ones among them can hold
/// systems for several skins. For almost every shipped skin that costs nothing - the bin grouping puts a system in
/// exactly the closures of the skins that reference it - and for 44 of 14,937 the closure holds a few that the skin does
/// not use (see <see cref="SkinColorScanner"/>), which is what this record lets a test see.</para>
/// </summary>
/// <param name="ClosureBins">The skin bin and every bin it links, transitively, in walk order.</param>
/// <param name="ClosureSystems">Every system object in those bins. A superset of the listed systems.</param>
/// <param name="ResolverSource">Where the skin's ResourceResolver was found, in words.</param>
/// <param name="ResolverEntries">Distinct effect keys the resolver maps.</param>
/// <param name="UnresolvedEntries">Resolver entries (and child links) whose system is in none of the closure's bins. Often
/// dangling in Riot's own data: Aatrox skin 0's four are in no bin of his WAD, of Global, Common or DATA either.</param>
/// <param name="UnavailableBins">Dependency bins the skin names that were NOT part of the walk, each with why
/// (<c>not found</c>, <c>could not be read: ...</c>, <c>could not be parsed</c>). Systems and materials in them cannot be
/// listed, so a non-empty list is also a warning on the inventory.</param>
public sealed record EffectReach(IReadOnlyList<string> ClosureBins, IReadOnlySet<uint> ClosureSystems,
    string ResolverSource, int ResolverEntries, int UnresolvedEntries, IReadOnlyList<string> UnavailableBins)
{
    public static readonly EffectReach None = new(Array.Empty<string>(), new HashSet<uint>(), "no skin bin", 0, 0, Array.Empty<string>());
}

/// <summary>
/// M812: everything colour-bearing one champion skin or chroma draws, with what it shares with the champion's other
/// skins. READ-ONLY by construction: nothing here holds a bin or a handle, and producing it wrote nothing.
///
/// <para><b>Body</b>: the colour textures (diffuse, emissive, gradient, matcap, reflection) of the skin's materials -
/// local and linked - with the colour parameters; <see cref="ExcludedSamplers"/> lists what was left out and why.
/// <b>Effects</b>: the systems the skin uses (see <see cref="EffectReach"/>), each with its emitters' colour values and
/// colour textures. Masks and data maps appear nowhere.</para>
///
/// <para><b>Never a partial list that looks whole.</b> Whatever the scan could not read - a dependency bin that is not
/// there, a bin that would not parse, a sibling skin it could not compare with - is a line in <see cref="Warnings"/>
/// (and the sibling count is always stated, zero included), because an item missing from a list and an item that is
/// genuinely unshared look the same on screen.</para>
/// </summary>
/// <param name="ComparedSkins">The other skins of the character that were read and compared. Empty is a fact when
/// <paramref name="SharingComputed"/> is true: nothing is shared with nobody.</param>
/// <param name="SharingComputed">False when the comparison was switched off or the path is not a character skin bin;
/// <see cref="ComparedSkins"/> is then empty because nothing was compared, which is not the same as having no siblings.</param>
/// <param name="Notes">What the reader should know about how the list was made (a skin with no resolver, keys that lead
/// nowhere...).</param>
/// <param name="Warnings">What could not be read or compared, so the lists may be incomplete. Empty when everything the
/// scan set out to read was read.</param>
/// <param name="UnresolvedMaterials">The materials the skin names (its base mesh's and each submesh override's) that no file the
/// material reader follows defines - the skin bin and its direct dependencies - so the textures they would bring are missing
/// from <see cref="BodyTextures"/>. Each is <c>submesh "Cape": Characters/Nunu/Skins/Skin1/Materials/Cape_inst (0x0d9bbdc7)</c>
/// (the name when the dictionary has it). Also a line in <see cref="Warnings"/>.</param>
public sealed record SkinColorInventory(
    string SkinBinPath,
    string CharacterFolder,
    int SkinNumber,
    string SkinLabel,
    IReadOnlyList<BodyTexture> BodyTextures,
    IReadOnlyList<BodyColorParameter> BodyParameters,
    IReadOnlyList<ExcludedSampler> ExcludedSamplers,
    IReadOnlyList<EffectSystemEntry> Effects,
    IReadOnlyList<EffectTextureEntry> EffectTextures,
    EffectReach Reach,
    IReadOnlyList<string> ComparedSkins,
    bool SharingComputed,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnresolvedMaterials)
{
    public static SkinColorInventory Empty(string skinBinPath, string warning) => new(skinBinPath, "", -1, skinBinPath,
        Array.Empty<BodyTexture>(), Array.Empty<BodyColorParameter>(), Array.Empty<ExcludedSampler>(),
        Array.Empty<EffectSystemEntry>(), Array.Empty<EffectTextureEntry>(), EffectReach.None,
        Array.Empty<string>(), false, Array.Empty<string>(), new[] { warning }, Array.Empty<string>());

    /// <summary>
    /// M812: does this asset path lie OUTSIDE the character's own folder, <c>assets/characters/&lt;folder&gt;/</c>? A shared
    /// texture (<c>assets/shared/...</c>), another champion's, a global particle. The sharing comparison covers the skins of
    /// THIS character only, so for such a file "unshared" says nothing about the other characters, maps and global assets
    /// that may sample it - the card marks it. An unnamed <c>0x...</c> reference has no known place and is not classified.
    /// </summary>
    public static bool IsOutsideCharacter(string path, string characterFolder)
    {
        if (string.IsNullOrWhiteSpace(path) || characterFolder.Length == 0) return false;
        if (path.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        string normalised = path.Replace('\\', '/');
        return !normalised.StartsWith($"assets/characters/{characterFolder}/", StringComparison.OrdinalIgnoreCase);
    }

    public int EffectColorValueCount => Effects.Sum(e => e.ColorValueCount);

    /// <summary>Shared items: body textures, effect textures and effect systems with at least one other skin on them.</summary>
    public int SharedItemCount => BodyTextures.Count(t => t.IsShared) + EffectTextures.Count(t => t.IsShared)
                                  + Effects.Count(e => e.IsShared);

    /// <summary>The one-line summary the Character window shows.</summary>
    public string Summary =>
        $"{BodyTextures.Count} body textures · {BodyParameters.Count} colour parameters · {Effects.Count} effect systems · "
        + $"{EffectColorValueCount} colour values · {EffectTextures.Count} effect textures · {SharedItemCount} shared";

    /// <summary>Colour as the inventory prints it: <c>(1, 0.3, 0.027, 1)</c>, invariant culture, at most three decimals.</summary>
    public static string Format(Vector4 v) =>
        string.Create(CultureInfo.InvariantCulture, $"({Num(v.X)}, {Num(v.Y)}, {Num(v.Z)}, {Num(v.W)})");

    private static string Num(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A short human text for one colour value: <c>constant (1, 0.3, 0.027, 1)</c> or
    /// <c>curve, 4 keys (0, 0, 0, 1) -> (1, 0.3, 0.027, 0)</c>.</summary>
    public static string Describe(VfxColorValue value)
    {
        string probability = value.HasProbabilityTables ? ", randomised" : "";
        if (!value.IsCurve)
            return (value.Constant is { } c ? $"constant {Format(c)}" : "default (no value written)") + probability;
        string range = value.First is { } a && value.Last is { } z ? $" {Format(a)} -> {Format(z)}" : "";
        return $"curve, {value.KeyCount} key{(value.KeyCount == 1 ? "" : "s")}{range}" + probability;
    }

    /// <summary>A deterministic text rendering of the whole inventory - for the console, the probe and for tests that
    /// compare two scans.</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        sb.Append("skin ").Append(SkinLabel).Append(" [").Append(SkinBinPath).AppendLine("]");
        sb.Append("summary: ").AppendLine(Summary);
        sb.Append("compared against: ").Append(SharingComputed ? ComparedSkins.Count.ToString(CultureInfo.InvariantCulture) + " skin(s) " : "(not computed) ")
          .AppendLine(string.Join(", ", ComparedSkins));
        foreach (var warning in Warnings) sb.Append("WARNING: ").AppendLine(warning);
        foreach (var note in Notes) sb.Append("note: ").AppendLine(note);
        sb.AppendLine("-- body textures");
        foreach (var t in BodyTextures)
            sb.Append("  ").Append(t.Role).Append(' ').Append(t.Path).Append("  ").Append($"0x{t.Hash:x16}")
              .Append(t.IsLinked ? "  LINKED " + t.LinkedFromBin : "").Append("  uses: ").Append(t.Source)
              .Append(t.IsShared ? "  SHARED: " + string.Join("; ", t.SharedWith) : "").AppendLine();
        sb.AppendLine("-- body parameters");
        foreach (var p in BodyParameters)
            sb.Append("  ").Append(BodyTexture.ShortName(p.Material)).Append('.').Append(p.Name).Append(' ').Append(Format(p.Value))
              .Append(' ').Append(p.TypeName).Append(p.ValueOmitted ? " (value omitted)" : "").AppendLine();
        sb.AppendLine("-- excluded samplers");
        foreach (var e in ExcludedSamplers)
            sb.Append("  ").Append(BodyTexture.ShortName(e.Material)).Append('.').Append(e.Sampler).Append(" : ").AppendLine(e.Reason);
        sb.AppendLine("-- effects");
        foreach (var s in Effects)
        {
            sb.Append("  ").Append(s.Name).Append($" 0x{s.PathHash:x8}").Append("  via ").Append(string.Join("; ", s.ReachedBy))
              .Append(s.IsInferred ? "  (inferred)" : "")
              .Append(s.IsShared ? "  SHARED: " + string.Join("; ", s.SharedWith) : "").AppendLine();
            foreach (var e in s.Emitters)
            {
                if (e.Colors.Count == 0 && e.Textures.Count == 0) continue;
                sb.Append("    [").Append(e.Index).Append("] ").Append(e.Name).AppendLine(e.Disabled ? " (disabled)" : "");
                foreach (var c in e.Colors) sb.Append("      ").Append(c.Field).Append(' ').AppendLine(Describe(c));
                foreach (var t in e.Textures) sb.Append("      ").Append(t.Field).Append(' ').Append(t.Path).AppendLine();
            }
        }
        sb.AppendLine("-- effect textures");
        foreach (var t in EffectTextures)
            sb.Append("  ").Append(t.Path).Append(" x").Append(t.SystemCount).Append(' ').Append(string.Join("/", t.Roles))
              .Append(t.IsShared ? "  SHARED: " + string.Join("; ", t.SharedWith) : "").AppendLine();
        return sb.ToString();
    }
}
