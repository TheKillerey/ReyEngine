using System.Text;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M812: the Chroma Studio's colour inventory against Riot's own champion files.
///
/// <para>These are the promises that cannot be shown on bins somebody here wrote: a mask is never listed, the systems
/// listed are the ones the skin USES, a chroma's own body textures are its own, and what it shares with the skin it
/// recolours says so. Where the test can, it checks the model against an independent walk of the raw tree or the raw
/// bytes rather than against the model's own code.</para>
///
/// <para>No-ops (not failures) when the game install is absent, the same convention as <see cref="ChromaPreviewTests"/>
/// and <see cref="LinkedMaterialResolutionTests"/>. Assertions are structural and name specific items; no total is
/// pinned, because the data drifts every patch.</para>
/// </summary>
public sealed class SkinColorInventoryRealDataTests
{
    private readonly ITestOutputHelper _output;
    public SkinColorInventoryRealDataTests(ITestOutputHelper output) => _output = output;

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private static string Champions => Path.Combine(Final, "Champions");
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static readonly Lazy<ClientNameCatalog> Names = new(() =>
    {
        string? root = Directory.Exists(Champions) ? ClientNameCatalog.InstallRootFromChampions(Champions) : null;
        return ClientNameCatalog.Load(root is null ? null : ClientNameCatalog.FindDataWad(root));
    });

    private sealed class ChampionFiles : IDisposable
    {
        private readonly WadArchive _wad;
        public ChampionFiles(WadArchive wad) => _wad = wad;
        public byte[]? Read(string path)
        {
            try
            {
                ulong hash = BinTexturePath.HashOfReference(path);
                return _wad.TryGetEntry(hash, out _) ? _wad.Extract(hash) : null;
            }
            catch { return null; }
        }
        /// <summary>Every skin bin in the WAD, companions and forms included.</summary>
        public IEnumerable<string> AllSkinBins() => _wad.Entries
            .Where(e => e.IsResolved && SkinColorScanner.TryParseSkinPath(e.Path, out _, out _))
            .Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> SkinBins(string character) => _wad.Entries
            .Where(e => e.IsResolved && SkinColorScanner.TryParseSkinPath(e.Path, out string folder, out _)
                        && folder.Equals(character, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        /// <summary>The skin numbers the WAD lists for a character - what the Character browser knows, and the scan is checked against.</summary>
        public int[] SkinNumbers(string character) => SkinBins(character)
            .Select(p => SkinColorScanner.TryParseSkinPath(p, out _, out int n) ? n : -1).Where(n => n >= 0).ToArray();
        public void Dispose() => _wad.Dispose();
    }

    /// <summary>Opens a champion's WAD, or null when the install, the dictionary or the WAD is not here.</summary>
    private static ChampionFiles? Open(string champion)
    {
        if (!Directory.Exists(Champions) || Database.Value is not { } database) return null;
        string wad = Path.Combine(Champions, champion + ".wad.client");
        return File.Exists(wad) ? new ChampionFiles(WadArchive.Open(wad, new WadPathResolver(database))) : null;
    }

    private static SkinColorRequest Request(ChampionFiles files, string character, int skin, bool sharing = true,
        Func<string, byte[]?>? read = null, bool expectTheWadsSkins = false) => new(
        $"data/characters/{character}/skins/skin{skin}.bin", read ?? files.Read,
        h => Database.Value!.TryGetBinName(h, out var n) ? n : null,
        h => Database.Value!.TryGetPath(h, out var p) ? p : null,
        SkinName: n => Names.Value.Skin(character, n)?.DisplayName,
        IncludeSharing: sharing,
        ExpectedSkinNumbers: expectTheWadsSkins ? files.SkinNumbers(character) : null);

    private static SkinColorInventory Scan(ChampionFiles files, string character, int skin, bool sharing = true) =>
        new SkinColorScanner().Scan(Request(files, character, skin, sharing));

    // ===================================================================== a base skin

    [Fact]
    public void AatroxBaseSkinListsItsBodyAndItsEffects_AndTheSystemBehindAnEffectKeyIsOneOfThem()
    {
        using var files = Open("Aatrox");
        if (files is null) return;

        var inventory = Scan(files, "aatrox", 0);
        _output.WriteLine(inventory.Summary);

        Assert.NotEmpty(inventory.BodyTextures);
        Assert.NotEmpty(inventory.Effects);
        Assert.True(inventory.SharingComputed);
        Assert.NotEmpty(inventory.ComparedSkins);
        Assert.Empty(inventory.Warnings);                              // every file Aatrox's skin 0 needs is in his WAD
        Assert.EndsWith("aatrox_base_tx_cm.tex", inventory.BodyTextures.First(t => t.Role == BodyTextureRole.Diffuse && t.Source.StartsWith("(skin default")).Path);

        // the system behind the effect key Aatrox_AA_Trail_02 - its key is the FNV-1a of that name, no dictionary needed
        uint key = H("Aatrox_AA_Trail_02");
        var trail = Assert.Single(inventory.Effects, e => e.EffectKeys.Contains(key));
        Assert.Equal("Aatrox_Base_AA_Trail_02", trail.Name);
        Assert.Contains("effect key", trail.ReachedBy);
        Assert.NotEmpty(trail.Emitters);

        // everything listed is in the dependency closure, and the skin bin comes first in it
        Assert.All(inventory.Effects, e => Assert.Contains(e.PathHash, inventory.Reach.ClosureSystems));
        Assert.Equal("data/characters/aatrox/skins/skin0.bin", inventory.Reach.ClosureBins[0], ignoreCase: true);
        Assert.True(inventory.Reach.ClosureBins.Count > 1);

        VerifyAgainstRawBins(inventory, files);
        Assert.Equal(inventory.Dump(), Scan(files, "aatrox", 0).Dump());          // deterministic over the same bytes
    }

    [Fact]
    public void ASkinsClosureCanHoldSystemsItDoesNotUse_AndThoseAreNotListed()
    {
        // M812 census: 44 of the 14,937 shipped skin bins carry a system their skin does not reach. Named, so the test says WHICH:
        // Blitzcrank skin 0 carries a leftover Bard system, Mordekaiser skin 0 a zzDELETE_ME_ one. Finding them is the test's own
        // walk of the raw closure - the bins are parsed here and the objects picked by their particleName - so it leans on neither
        // the scanner's idea of the closure nor of what is listed.
        var candidates = new (string Champion, string Prefix)[] { ("Blitzcrank", "Bard_"), ("Mordekaiser", "zzDELETE_ME_") };
        int shown = 0;
        foreach (var (champion, prefix) in candidates)
        {
            using var files = Open(champion);
            if (files is null) return;                                  // no install
            string character = champion.ToLowerInvariant();
            var inventory = Scan(files, character, 0, sharing: false);

            var strays = RawSystemsNamed(ClosureBytes(files, $"data/characters/{character}/skins/skin0.bin"), prefix);
            if (strays.Count == 0) { _output.WriteLine($"{champion} skin 0: no {prefix}* system in its closure any more."); continue; }
            shown++;
            _output.WriteLine($"{champion} skin 0: {strays.Count} {prefix}* system(s) sit in its closure ({inventory.Reach.ClosureSystems.Count} systems, {inventory.Effects.Count} listed).");
            Assert.All(strays, hash => Assert.Contains(hash, inventory.Reach.ClosureSystems));                // in the closure...
            Assert.DoesNotContain(inventory.Effects, e => strays.Contains(e.PathHash));                         // ...and not on the list
            Assert.DoesNotContain(inventory.Effects, e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(inventory.Effects);                                                                   // not empty for want of trying
        }
        Assert.True(shown > 0, "neither stray system is in a closure any more - pick another from the census (the probe's 'unreached' mode lists them)");
    }

    // ===================================================================== a paid skin with effects of its own

    [Fact]
    public void JusticarAatroxSkin1HasItsOwnEffects_UnsharedWithAnySkin_AndSharesTheBaseOnesWithSkin0()
    {
        using var files = Open("Aatrox");
        if (files is null) return;

        var inventory = Scan(files, "aatrox", 1);
        _output.WriteLine(inventory.Summary);
        Assert.Contains("skin1", inventory.SkinLabel);
        if (Names.Value.SkinCount > 0) Assert.Contains("Justicar", inventory.SkinLabel);

        var own = inventory.Effects.Where(e => e.Name.StartsWith("Aatrox_Skin01_", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(own.Count >= 20, $"only {own.Count} Aatrox_Skin01_ systems are listed");
        Assert.All(own, e => Assert.Empty(e.SharedWith));                       // nothing else uses them
        Assert.Contains(own, e => e.Name.Equals("Aatrox_Skin01_AA_Trail_02", StringComparison.OrdinalIgnoreCase));

        // an independent look at one: no other Aatrox skin's files mention its object
        var trail = own.First(e => e.Name.Equals("Aatrox_Skin01_AA_Trail_02", StringComparison.OrdinalIgnoreCase));
        int otherSkins = 0;
        foreach (string other in files.SkinBins("aatrox"))
        {
            if (!SkinColorScanner.TryParseSkinPath(other, out _, out int number) || number == 1) continue;
            var closure = ClosureBytes(files, other);
            Assert.NotEmpty(closure);                                                    // a skin whose bins would not read proves nothing
            Assert.DoesNotContain(closure, bytes => MentionsHash(bytes, trail.PathHash));
            otherSkins++;
        }
        Assert.True(otherSkins >= 10, $"only {otherSkins} other Aatrox skins were checked");

        // the base systems it still plays are shared with the base skin
        var baseShared = inventory.Effects.Where(e => e.Name.StartsWith("Aatrox_Base_", StringComparison.OrdinalIgnoreCase) && e.IsShared).ToList();
        Assert.NotEmpty(baseShared);
        Assert.All(baseShared, e => Assert.Contains(e.SharedWith, s => s.Contains("skin0)") || s == "skin0"));

        // its body is its own: textures under skin01/, unshared
        var diffuse = inventory.BodyTextures.First(t => t.Role == BodyTextureRole.Diffuse
                                                        && t.Path.EndsWith("aatrox_skin01_tx_cm.tex", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("/skin01/", diffuse.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(diffuse.SharedWith);

        VerifyAgainstRawBins(inventory, files);
    }

    // ===================================================================== a chroma over the skin it recolours

    [Fact]
    public void LilliaChroma49DrawsSkin46sMeshWithBodyTexturesOfItsOwn_AndNamesSkin46ForWhatItSharesWithIt()
    {
        using var files = Open("Lillia");
        if (files is null) return;

        var inventory = Scan(files, "lillia", 49);
        _output.WriteLine(inventory.Dump().Split('\n').Take(40).Aggregate(new StringBuilder(), (sb, l) => sb.AppendLine(l.TrimEnd())).ToString());

        // the skin that was asked for, not the one whose mesh it draws
        Assert.Equal(49, inventory.SkinNumber);
        Assert.EndsWith("skin49.bin", inventory.SkinBinPath);
        Assert.Contains("skin49", inventory.SkinLabel);
        Assert.Contains(inventory.ComparedSkins, c => c.Contains("skin46"));

        // its body: the default diffuse is skin49's own, and nothing else uses the file
        var ownDiffuse = inventory.BodyTextures.First(t => t.Role == BodyTextureRole.Diffuse && t.Source.StartsWith("(skin default"));
        Assert.Contains("/skin49/", ownDiffuse.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ownDiffuse.SharedWith);
        int otherLillias = 0;
        foreach (string other in files.SkinBins("lillia"))
        {
            if (!SkinColorScanner.TryParseSkinPath(other, out _, out int number) || number == 49) continue;
            var closure = ClosureBytes(files, other);
            Assert.NotEmpty(closure);
            Assert.DoesNotContain(closure, bytes => MentionsTexture(bytes, ownDiffuse.Path, ownDiffuse.Hash));
            otherLillias++;
        }
        Assert.True(otherLillias >= 10, $"only {otherLillias} other Lillia skins were checked");

        // the cloth overlays it reuses from the skin it recolours are marked shared, and name skin46
        var reused = inventory.BodyTextures.Where(t => t.Path.Contains("/skin46/", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(reused);
        Assert.All(reused, t => Assert.Contains(t.SharedWith, s => s.Contains("(skin46)") || s == "skin46"));
        var skin46Closure = ClosureBytes(files, "data/characters/lillia/skins/skin46.bin");
        Assert.NotEmpty(skin46Closure);
        Assert.All(reused, t => Assert.Contains(skin46Closure, bytes => MentionsTexture(bytes, t.Path, t.Hash)));   // and skin 46's files really do name them

        // a texture its outline material takes from a LINKED bin is marked read-only, and says which bin
        var white = Assert.Single(inventory.BodyTextures, t => t.Path.EndsWith("lillia_skin46_white_tx_cm.tex", StringComparison.OrdinalIgnoreCase));
        Assert.True(white.IsLinked);
        Assert.Contains("Multi_Skins", white.LinkedFromBin);
        Assert.False(ownDiffuse.IsLinked);

        // effects: its own are unshared, the Skin46 ones it plays are shared with skin46
        var own = inventory.Effects.Where(e => e.Name.StartsWith("Lillia_Skin49_", StringComparison.OrdinalIgnoreCase)).ToList();
        var recoloured = inventory.Effects.Where(e => e.Name.StartsWith("Lillia_Skin46_", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(own);
        Assert.NotEmpty(recoloured);
        Assert.All(own, e => Assert.Empty(e.SharedWith));
        Assert.All(recoloured, e => Assert.Contains(e.SharedWith, s => s.Contains("(skin46)") || s == "skin46"));

        // the gear upgrade's resolver: Lillia's upgraded-E effects are only reachable through it - and say so
        Assert.Contains(inventory.Effects, e => e.ReachedBy.Contains("gear upgrade"));
        var inferred = inventory.Effects.Where(e => e.IsInferred).ToList();
        Assert.Contains(inferred, e => e.Name.Equals("Lillia_Skin49_E_Mis_Update", StringComparison.OrdinalIgnoreCase));
        Assert.All(inferred, e => Assert.DoesNotContain("effect key", e.ReachedBy));            // the skin's resolver does not name an inferred system
        Assert.Empty(inventory.Warnings);

        VerifyAgainstRawBins(inventory, files);
    }

    // ===================================================================== masks, against the raw tree

    [Theory]
    [InlineData("Aatrox", "aatrox", 0)]
    [InlineData("Aatrox", "aatrox", 1)]
    [InlineData("Lillia", "lillia", 49)]
    [InlineData("Lux", "lux", 7)]
    [InlineData("Yasuo", "yasuo", 0)]
    [InlineData("Ahri", "ahri", 0)]
    public void NoMaskFieldAndNoMaskTextureIsEverListed(string champion, string character, int skin)
    {
        using var files = Open(champion);
        if (files is null) return;
        var inventory = Scan(files, character, skin, sharing: false);
        Assert.NotEmpty(inventory.Effects);

        var (listed, maskOnly) = VerifyAgainstRawBins(inventory, files);
        _output.WriteLine($"{champion} skin {skin}: {listed} colour textures listed; {maskOnly} mask or data textures named in the same emitters and left out.");
        Assert.True(listed > 0);
    }

    /// <summary>
    /// The inventory's reader and the playback parser are two readers of one bin. Where they speak of the same field - a
    /// constant, the key count of a curve, a texture path - they must agree, system by system, emitter by emitter. A
    /// disagreement is a bug in one of them, and the playback parser has drawn Riot's effects for years.
    /// </summary>
    [Theory]
    [InlineData("Aatrox", "aatrox", 0)]
    [InlineData("Yasuo", "yasuo", 0)]
    [InlineData("Lillia", "lillia", 49)]
    [InlineData("Lux", "lux", 7)]
    public void TheInventoryAgreesWithThePlaybackParserOnEveryColourBothOfThemRead(string champion, string character, int skin)
    {
        using var files = Open(champion);
        if (files is null) return;
        var inventory = Scan(files, character, skin, sharing: false);

        var playback = new Dictionary<uint, ReyEngine.Formats.Vfx.VfxSystemDefinition>();
        foreach (string bin in inventory.Reach.ClosureBins)
            foreach (var (hash, def) in ReyEngine.Formats.Vfx.VfxSystemResolver.ExtractAll(files.Read(bin)!)) playback.TryAdd(hash, def);

        static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        int compared = 0;
        foreach (var system in inventory.Effects)
        {
            var def = playback[system.PathHash];
            Assert.Equal(def.Emitters.Count, system.Emitters.Count);
            for (int i = 0; i < def.Emitters.Count; i++)
            {
                var ours = system.Emitters[i];
                var theirs = def.Emitters[i];
                string where = $"{system.Name} emitter {i} ({ours.Name})";
                Assert.Equal(theirs.Name, ours.Name);
                Assert.Equal(theirs.Disabled, ours.Disabled);

                var colors = ours.Colors.ToDictionary(c => c.Field);
                if (colors.TryGetValue("birthColor", out var birth) && birth.Constant is { } birthConstant)
                { Assert.Equal(theirs.BirthColor.Constant, birthConstant); compared++; }
                if (colors.TryGetValue("birthColor", out birth) && birth.IsCurve)
                { Assert.Equal(theirs.BirthColor.Times?.Length ?? 0, birth.KeyCount); compared++; }

                // the playback parser reads these two only from a struct, which is what the data authors
                Assert.True(colors.ContainsKey("color") == (theirs.ColorOverLife is not null), $"{where}: colour over life present in one reader only");
                if (colors.TryGetValue("color", out var over))
                {
                    Assert.Equal(theirs.ColorOverLife!.Value.Times?.Length ?? 0, over.KeyCount);
                    if (over.Constant is { } overConstant) Assert.Equal(theirs.ColorOverLife.Value.Constant, overConstant);
                    compared++;
                }
                Assert.True(colors.ContainsKey("Linger.SeparateLingerColor") == (theirs.Linger?.SeparateColor is not null), $"{where}: linger colour present in one reader only");
                if (colors.TryGetValue("Linger.SeparateLingerColor", out var linger))
                {
                    Assert.Equal(theirs.Linger!.SeparateColor!.Value.Times?.Length ?? 0, linger.KeyCount);
                    compared++;
                }

                var textures = ours.Textures.ToDictionary(t => t.Field);
                Assert.True(textures.ContainsKey("texture") == !string.IsNullOrEmpty(theirs.TexturePath), $"{where}: sprite present in one reader only");
                if (textures.TryGetValue("texture", out var sprite)) { Assert.True(SamePath(theirs.TexturePath, sprite.Path)); compared++; }
                Assert.True(textures.ContainsKey("textureMult.textureMult") == !string.IsNullOrEmpty(theirs.TextureMultPath), $"{where}: multiplier present in one reader only");
                if (textures.TryGetValue("textureMult.textureMult", out var mult)) { Assert.True(SamePath(theirs.TextureMultPath, mult.Path)); compared++; }
                Assert.True(textures.ContainsKey("particleColorTexture") == !string.IsNullOrEmpty(theirs.ParticleColorTexturePath), $"{where}: colour lookup present in one reader only");
                if (textures.TryGetValue("particleColorTexture", out var lookup)) { Assert.True(SamePath(theirs.ParticleColorTexturePath, lookup.Path)); compared++; }
                // the playback parser drops a palette it cannot use (no row count); ours lists the texture the bin names
                if (theirs.Palette is { TexturePath: { Length: > 0 } palettePath })
                { Assert.True(textures.TryGetValue("paletteDefinition.paletteTexture", out var palette) && SamePath(palettePath, palette.Path), $"{where}: palette"); compared++; }
                if (theirs.Reflection is { MapPath: { Length: > 0 } mapPath })
                {
                    Assert.True(textures.TryGetValue("reflectionDefinition.reflectionMapTexture", out var cube) && SamePath(mapPath, cube.Path) && cube.IsCubemap, $"{where}: reflection cubemap");
                    compared++;
                }
            }
        }
        _output.WriteLine($"{champion} skin {skin}: {inventory.Effects.Count} systems, {compared} values compared with the playback parser, all equal.");
        Assert.True(compared > 100);
    }

    /// <summary>A system two skins both list is shared between them, and each says so of the other - the sharing comparison
    /// is the same sets read from either side, so it cannot be one-sided.</summary>
    [Fact]
    public void ASystemTwoSkinsBothUseIsSharedBetweenThem_AndEachNamesTheOther()
    {
        using var files = Open("Aatrox");
        if (files is null) return;
        var skins = new[] { 0, 1, 3, 33, 37 };
        var inventories = skins.ToDictionary(n => n, n => Scan(files, "aatrox", n));
        string Label(int n) => SkinColorScanner.SkinLabel(n, Names.Value.Skin("aatrox", n)?.DisplayName);

        int pairsWithSharedSystems = 0;
        foreach (int a in skins)
            foreach (int b in skins)
            {
                if (a == b) continue;
                var both = inventories[a].Effects.Where(e => inventories[b].Effects.Any(x => x.PathHash == e.PathHash)).ToList();
                if (both.Count > 0) pairsWithSharedSystems++;
                Assert.All(both, e => Assert.Contains(Label(b), e.SharedWith));
                // and nothing is named that the other does not list
                Assert.All(inventories[a].Effects.Where(e => e.SharedWith.Contains(Label(b))),
                    e => Assert.Contains(inventories[b].Effects, x => x.PathHash == e.PathHash));
            }
        Assert.True(pairsWithSharedSystems >= 4, "these Aatrox skins no longer share systems pairwise - pick other skins");
        // 1 plays its own Skin01 effects: not one of them is on any other of these skins' lists
        Assert.DoesNotContain(inventories[0].Effects.Concat(inventories[3].Effects).Concat(inventories[33].Effects),
            e => e.Name.StartsWith("Aatrox_Skin01_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheMaskFilesAreRealInTheseBins_SoTheMaskTestIsNotVacuous()
    {
        using var files = Open("Aatrox");
        if (files is null) return;
        var (_, maskOnly) = VerifyAgainstRawBins(Scan(files, "aatrox", 0, sharing: false), files);
        Assert.True(maskOnly > 0, "Aatrox's base effects name no erosion/distortion/falloff/gloss/transition texture that is not also a colour one");
    }

    // ===================================================================== body masks, effects reach, warnings

    /// <summary>
    /// The materials a skin names that the material reader cannot resolve, by a walk that shares no code with the scanner or with
    /// <see cref="MaterialDocument"/>: the links are read from the skin block (the base mesh's and each override's), and one counts
    /// as resolved when the skin bin itself holds the object or a DIRECT dependency holds a StaticMaterialDef of that hash.
    /// </summary>
    private static List<(string Label, uint Hash)> RawUnresolvedMaterialLinks(ChampionFiles files, string skinBin,
        Dictionary<string, BinTree?> cache)
    {
        BinTree? Tree(string path)
        {
            if (cache.TryGetValue(path, out var known)) return known;
            BinTree? tree = null;
            try { if (files.Read(path) is { Length: > 0 } bytes) tree = SafeBinTree.Parse(bytes); } catch { }
            return cache[path] = tree;
        }

        var skin = Tree(skinBin)!;
        var block = skin.Objects.Values
            .Select(o => o.Properties.TryGetValue(H("skinMeshProperties"), out var p) ? p as BinTreeStruct : null)
            .FirstOrDefault(p => p is not null);
        if (block is null) return new List<(string, uint)>();

        var wanted = new List<(string Label, uint Hash)>();
        if (block.Properties.TryGetValue(H("material"), out var baseMaterial) && baseMaterial is BinTreeObjectLink { Value: not 0 } baseLink)
            wanted.Add(("the base mesh", baseLink.Value));
        if (block.Properties.TryGetValue(H("materialOverride"), out var overrides) && overrides is BinTreeContainer list)
            foreach (var element in list.Elements.OfType<BinTreeStruct>())
                if (element.Properties.TryGetValue(H("submesh"), out var sub) && sub is BinTreeString { Value.Length: > 0 } name
                    && element.Properties.TryGetValue(H("material"), out var mat) && mat is BinTreeObjectLink { Value: not 0 } link)
                    wanted.Add(($"submesh \"{name.Value}\"", link.Value));

        var defined = new HashSet<uint>(skin.Objects.Keys);
        foreach (string dependency in skin.Dependencies.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Tree(dependency) is { } tree)
                foreach (var o in tree.Objects.Values)
                    if (o.ClassHash == H("StaticMaterialDef")) defined.Add(o.PathHash);
        return wanted.Where(w => !defined.Contains(w.Hash)).ToList();
    }

    /// <summary>
    /// The scan warns about EXACTLY the material links the material reader leaves unresolved - not more, not fewer - on every skin of
    /// a champion WAD, companions and forms included. Measured over all 14,937 shipped skin bins: 74 links in 62 skins (Nunu's
    /// snowballs, Quinn's Valor, Nasus's Fury of the Sands, Irelia's blades, Orianna's ball, Yuumi, Rammus), none of which a
    /// transitive dependency would resolve.
    /// </summary>
    [Theory]
    [InlineData("Nunu", 10)]
    [InlineData("Quinn", 10)]
    [InlineData("Nasus", 5)]
    [InlineData("Aatrox", 0)]
    [InlineData("Lillia", 0)]
    [InlineData("Lux", 0)]
    public void TheScanWarnsAboutExactlyTheMaterialLinksTheMaterialReaderLeavesUnresolved(string champion, int atLeastThisManySkinsWithOne)
    {
        using var files = Open(champion);
        if (files is null) return;

        var scanner = new SkinColorScanner();
        var cache = new Dictionary<string, BinTree?>(StringComparer.OrdinalIgnoreCase);
        int skins = 0, withUnresolved = 0, links = 0;
        foreach (string bin in files.AllSkinBins())
        {
            SkinColorScanner.TryParseSkinPath(bin, out string folder, out int number);
            var expected = RawUnresolvedMaterialLinks(files, bin, cache);
            var inventory = scanner.Scan(Request(files, folder, number, sharing: false));
            skins++;

            Assert.Equal(expected.Count, inventory.UnresolvedMaterials.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.StartsWith(expected[i].Label + ":", inventory.UnresolvedMaterials[i]);
                Assert.EndsWith($"0x{expected[i].Hash:x8}" + (inventory.UnresolvedMaterials[i].EndsWith(")") ? ")" : ""), inventory.UnresolvedMaterials[i]);
            }
            if (expected.Count > 0)
            {
                withUnresolved++; links += expected.Count;
                var warning = Assert.Single(inventory.Warnings, w => w.Contains("material link(s)") && w.StartsWith($"{expected.Count} material link(s) of this skin"));
                Assert.Contains("not listed", warning);
            }
            else Assert.DoesNotContain(inventory.Warnings, w => w.Contains("material link(s) of this skin"));
        }
        _output.WriteLine($"{champion}: {skins} skin bins, {withUnresolved} with a material link the reader cannot resolve ({links} links).");
        Assert.True(skins >= 20, $"only {skins} skin bins were checked");
        Assert.True(withUnresolved >= atLeastThisManySkinsWithOne, $"only {withUnresolved} skins have an unresolved material link");
    }

    [Fact]
    public void NunusCapeIsAMaterialNoFileTheSkinLinksDefines_AndTheScanSaysSoBySubmesh()
    {
        using var files = Open("Nunu");
        if (files is null) return;
        var inventory = Scan(files, "nunu", 1, sharing: false);
        if (inventory.UnresolvedMaterials.Count == 0) { _output.WriteLine("nunu skin 1 no longer names a material nothing defines."); return; }

        var cape = Assert.Single(inventory.UnresolvedMaterials, u => u.StartsWith("submesh \"Cape\""));
        Assert.Contains("0x0d9bbdc7", cape);
        Assert.Contains(inventory.Warnings, w => w.Contains("material link(s) of this skin") && w.Contains("Cape"));
    }

    [Fact]
    public void TheSkinsOfCharactersWhoseMaterialsAreAllLinkedAreWhole_SoTheWarningIsNotNoise()
    {
        foreach (var (champion, character, skin) in new[] { ("Aatrox", "aatrox", 0), ("Aatrox", "aatrox", 37), ("Lillia", "lillia", 49), ("Lux", "lux", 7), ("Ahri", "ahri", 0) })
        {
            using var files = Open(champion);
            if (files is null) return;
            var inventory = Scan(files, character, skin, sharing: false);
            Assert.NotEmpty(inventory.BodyTextures);
            Assert.Empty(inventory.UnresolvedMaterials);
        }
    }

    [Theory]
    [InlineData("Aatrox", "aatrox", 0)]
    [InlineData("Aatrox", "aatrox", 1)]
    [InlineData("Aatrox", "aatrox", 37)]      // carries EmissionR_DistortionG_Texture
    [InlineData("Lillia", "lillia", 49)]
    [InlineData("Ahri", "ahri", 0)]
    public void NoMaskNormalOrChannelPackedSamplerIsListedAsBodyColour_AgainstTheMaterialsTheSkinReallyHas(string champion, string character, int skin)
    {
        using var files = Open(champion);
        if (files is null) return;
        var inventory = Scan(files, character, skin, sharing: false);

        // the independent view: every sampler of every material of the skin, straight from MaterialDocument with its own name
        var document = MaterialDocument.Parse(files.Read(inventory.SkinBinPath)!,
            h => Database.Value!.TryGetBinName(h, out var n) ? n : null, h => Database.Value!.TryGetPath(h, out var p) ? p : null, files.Read);
        var forbidden = new HashSet<(string Material, string Sampler)>();
        foreach (var material in document.Materials)
            foreach (var slot in material.Slots)
                // the packed name is spelled out, not asked of the scanner: the test must not agree with the code by construction
                if (slot.SamplerName.Contains("Mask", StringComparison.OrdinalIgnoreCase) || slot.IsNormal || slot.SamplerName == "EmissionR_DistortionG_Texture")
                    forbidden.Add((material.Name, slot.SamplerName));
        Assert.NotEmpty(forbidden);                                    // this skin does have masks: the check is not vacuous

        var listed = inventory.BodyTextures.SelectMany(t => t.Uses).Select(u => (u.Material, u.Sampler)).ToList();
        Assert.NotEmpty(listed);
        Assert.DoesNotContain(listed, u => forbidden.Contains(u));              // not one mask, normal map or packed map is a colour texture...
        var excluded = inventory.ExcludedSamplers.Select(e => (e.Material, e.Sampler)).ToHashSet();
        Assert.All(forbidden, f => Assert.Contains(f, excluded));      // ...and each is on the left-out list, with its reason
        Assert.All(listed, u => Assert.DoesNotContain("Mask", u.Sampler, StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"{champion} skin {skin}: {listed.Count} colour sampler uses listed, {forbidden.Count} mask/normal/packed samplers left out.");
    }

    [Fact]
    public void AChannelPackedEmissionMapOnARealSkinIsLeftOutOfTheColourList()
    {
        using var files = Open("Aatrox");
        if (files is null) return;

        // 1,016 entries of the install carry EmissionR_DistortionG_Texture; some Aatrox skin must (it is how the census found it)
        int found = 0;
        foreach (string bin in files.SkinBins("aatrox"))
        {
            var inventory = new SkinColorScanner().Scan(Request(files, "aatrox", SkinColorScanner.TryParseSkinPath(bin, out _, out int n) ? n : 0, sharing: false));
            var packed = inventory.ExcludedSamplers.Where(e => e.Sampler == "EmissionR_DistortionG_Texture").ToList();
            if (packed.Count == 0) continue;
            found++;
            Assert.All(packed, e => Assert.Contains("channel-packed", e.Reason));
            Assert.DoesNotContain(inventory.BodyTextures.SelectMany(t => t.Uses), u => u.Sampler == "EmissionR_DistortionG_Texture");
            if (found >= 3) break;
        }
        Assert.True(found > 0, "no Aatrox skin carries EmissionR_DistortionG_Texture any more - pick another character");
    }

    [Fact]
    public void ElementalistLuxPlaysHerFormsThroughGearUpgrades_SoTheirEffectsAreInferredNotPlain()
    {
        using var files = Open("Lux");
        if (files is null) return;

        var inventory = Scan(files, "lux", 7, sharing: false);
        var inferred = inventory.Effects.Where(e => e.IsInferred).ToList();
        _output.WriteLine($"Lux skin 7: {inferred.Count} of {inventory.Effects.Count} listed systems are inferred.");
        Assert.True(inferred.Count > 50, $"only {inferred.Count} inferred systems");
        Assert.True(inventory.Effects.Count - inferred.Count > 0);                              // and she still has plain ones
        Assert.All(inferred, e => Assert.DoesNotContain("effect key", e.ReachedBy));
        Assert.All(inferred, e => Assert.True(e.ReachedBy.Contains("gear upgrade") || e.ReachedBy.Any(r => r.StartsWith("child of ")),
            $"{e.Name}: {string.Join("; ", e.ReachedBy)}"));
        Assert.Empty(inventory.Warnings);
    }

    /// <summary>
    /// The preview resolves a child's effect key through EVERY resolver in the closure; the inventory does not (see
    /// <see cref="SkinColorScanner"/>). That is only safe if no skin reaches a system ONLY that way, so this walks the raw
    /// closure of every skin of three champions with the preview's merged map and requires every system it would add to be
    /// listed already. The M812 census says the same of all 14,937 shipped skin bins.
    /// </summary>
    [Theory]
    [InlineData("Lillia", "lillia")]
    [InlineData("Lux", "lux")]
    [InlineData("Aatrox", "aatrox")]
    public void NoSkinReachesASystemOnlyThroughAnotherResolver_SoNotMergingTheClosuresResolversLosesNothing(string champion, string character)
    {
        using var files = Open(champion);
        if (files is null) return;

        var trees = new Dictionary<string, BinTree>(StringComparer.OrdinalIgnoreCase);
        BinTree Tree(string path) => trees.TryGetValue(path, out var t) ? t : trees[path] = SafeBinTree.Parse(files.Read(path)!);

        uint resolverClass = H("ResourceResolver"), gearClass = H("GearSkinUpgrade"), systemClass = H("VfxSystemDefinitionData"), emitterClass = H("VfxEmitterDefinitionData");
        int skins = 0, keysChecked = 0, mappedOnlyElsewhere = 0;
        var scanner = new SkinColorScanner();
        foreach (string skinBin in files.SkinBins(character))
        {
            var inventory = scanner.Scan(Request(files, character, SkinColorScanner.TryParseSkinPath(skinBin, out _, out int n) ? n : 0, sharing: false));
            var listed = inventory.Effects.Select(e => e.PathHash).ToHashSet();
            var systems = new Dictionary<uint, BinTreeObject>();
            var resolvers = new List<(uint Path, Dictionary<uint, uint> Map)>();      // plain resolvers and gear upgrades', in closure order
            uint ownResolver = 0;
            foreach (string bin in inventory.Reach.ClosureBins)
                foreach (var o in Tree(bin).Objects.Values)
                {
                    if (o.ClassHash == systemClass) systems.TryAdd(o.PathHash, o);
                    else if (o.ClassHash == H("SkinCharacterDataProperties") && ownResolver == 0
                             && o.Properties.TryGetValue(H("mResourceResolver"), out var link) && link is BinTreeObjectLink l) ownResolver = l.Value;
                    else if (o.ClassHash == resolverClass || o.ClassHash == gearClass)
                    {
                        BinTreeProperty? map = null;
                        if (o.ClassHash == resolverClass) o.Properties.TryGetValue(H("resourceMap"), out map);
                        else if (o.Properties.TryGetValue(H("mGearData"), out var gear) && gear is BinTreeStruct g
                                 && g.Properties.TryGetValue(H("mVFXResourceResolver"), out var r) && r is BinTreeStruct rs)
                            rs.Properties.TryGetValue(H("resourceMap"), out map);
                        var entries = new Dictionary<uint, uint>();
                        if (map is BinTreeMap m)
                            foreach (var entry in m)
                                if (entry.Key is BinTreeHash key && entry.Value is BinTreeObjectLink target) entries[key.Value] = target.Value;
                        resolvers.Add((o.PathHash, entries));
                    }
                }
            // the preview's merged map: the skin's own resolver first, then every other resolver of the closure, the first answer wins
            var merged = new Dictionary<uint, uint>();
            foreach (var (path, map) in resolvers.Where(r => r.Path == ownResolver).Concat(resolvers.Where(r => r.Path != ownResolver)))
                foreach (var (key, target) in map) merged.TryAdd(key, target);

            foreach (uint parent in listed)
            {
                if (!systems.TryGetValue(parent, out var system)) continue;
                foreach (var emitter in system.Properties.Values.OfType<BinTreeContainer>().SelectMany(c => c.Elements).OfType<BinTreeStruct>().Where(e => e.ClassHash == emitterClass))
                {
                    if (!emitter.Properties.TryGetValue(H("childParticleSetDefinition"), out var set) || set is not BinTreeStruct setStruct
                        || !setStruct.Properties.TryGetValue(H("childrenIdentifiers"), out var ids) || ids is not BinTreeContainer idList) continue;
                    foreach (var id in idList.Elements.OfType<BinTreeStruct>())
                    {
                        if (id.Properties.TryGetValue(H("effect"), out var link) && link is BinTreeObjectLink { Value: not 0 }) continue;     // named by link: followed as a link
                        if (!id.Properties.TryGetValue(H("effectKey"), out var key) || key is not BinTreeHash k || k.Value == 0) continue;
                        keysChecked++;
                        if (!merged.TryGetValue(k.Value, out uint target) || !systems.ContainsKey(target)) continue;                        // unmapped, or leads out of the closure
                        if (!listed.Contains(target)) mappedOnlyElsewhere++;
                    }
                }
            }
            skins++;
        }
        _output.WriteLine($"{champion}: {skins} skins, {keysChecked} child keys checked against the merged map of every resolver in the closure; {mappedOnlyElsewhere} lead to a system the inventory does not list.");
        Assert.True(skins >= 20, $"only {skins} skins were checked");
        Assert.True(keysChecked > 100, $"only {keysChecked} child keys were checked");
        Assert.Equal(0, mappedOnlyElsewhere);
    }

    [Fact]
    public void EveryFileTheseSkinsNeedIsReadable_SoTheyCarryNoWarningEvenAgainstTheWadsOwnSkinList()
    {
        foreach (var (champion, character, skin) in new[] { ("Aatrox", "aatrox", 1), ("Lillia", "lillia", 49), ("Lux", "lux", 7), ("Yasuo", "yasuo", 0) })
        {
            using var files = Open(champion);
            if (files is null) return;
            var inventory = new SkinColorScanner().Scan(Request(files, character, skin, expectTheWadsSkins: true));
            Assert.NotEmpty(inventory.ComparedSkins);
            Assert.Empty(inventory.Warnings);
        }
    }

    [Fact]
    public void ASiblingTheReaderCannotFindAndABinItCannotOpenAreWarnings_OnARealChroma()
    {
        using var files = Open("Lillia");
        if (files is null) return;

        // the same skin, read through a reader that has lost skin 46 and the shared bin skin 49 links
        byte[]? Degraded(string path) =>
            path.EndsWith("/skin46.bin", StringComparison.OrdinalIgnoreCase) || path.Contains("Multi_Skins", StringComparison.OrdinalIgnoreCase) ? null : files.Read(path);
        var whole = new SkinColorScanner().Scan(Request(files, "lillia", 49, expectTheWadsSkins: true));
        var degraded = new SkinColorScanner().Scan(Request(files, "lillia", 49, read: Degraded, expectTheWadsSkins: true));

        Assert.Empty(whole.Warnings);
        Assert.Contains(whole.ComparedSkins, c => c.Contains("skin46"));
        Assert.DoesNotContain(degraded.ComparedSkins, c => c.Contains("skin46"));
        Assert.Contains(degraded.Warnings, w => w == "skin46.bin is listed for this character but was not found, so it was not compared.");
        var dependencies = Assert.Single(degraded.Warnings, w => w.Contains("dependency bin(s) of this skin were not available"));
        Assert.Contains("Multi_Skins", dependencies);
        Assert.NotEmpty(degraded.Reach.UnavailableBins);
        // what the warning protects: skin 46's textures read as unshared once skin 46 is gone - the list alone would not say so
        Assert.True(whole.SharedItemCount > degraded.SharedItemCount);
        _output.WriteLine($"Lillia 49: {whole.SharedItemCount} shared items with every file readable, {degraded.SharedItemCount} without skin 46 and its shared bin; {degraded.Warnings.Count} warnings say so.");
    }

    [Fact]
    public void ARealSkinThatNamesADependencyNoFileHoldsSaysSo()
    {
        // bardportalclickable/skin0.bin lists a dependency called just "Skin0.bin" - dangling in Riot's own data (the one skin of
        // 14,937 with an unavailable dependency, M812 census). If a patch repairs it the scan has nothing to report, and says nothing.
        using var files = Open("Bard");
        if (files is null) return;
        var inventory = new SkinColorScanner().Scan(Request(files, "bardportalclickable", 0, sharing: false));
        if (inventory.Warnings.Count == 0) { _output.WriteLine("bardportalclickable skin 0 no longer lists a missing dependency."); return; }
        Assert.Contains(inventory.Warnings, w => w.Contains("Skin0.bin (not found)"));
        Assert.Contains("Skin0.bin (not found)", inventory.Reach.UnavailableBins);
    }

    // ===================================================================== the format census (informs M814's writer)

    [Fact]
    public void TextureFormatCensus_OverTheColourTexturesOfTenChampionsAndAllTheirSkins()
    {
        // Heavy (about 650 skins and 20,000 textures, opened one by one): it informs M814's writer and is not a regression
        // test, so it runs only when asked for.
        if (System.Environment.GetEnvironmentVariable("REYENGINE_CENSUS") != "1")
        {
            _output.WriteLine("SKIPPED: set REYENGINE_CENSUS=1 to run the M812 texture-format census "
                              + "(dotnet test --filter FullyQualifiedName~TextureFormatCensus, with that variable set).");
            return;
        }
        string[] champions = { "Aatrox", "Ahri", "Lux", "Lillia", "Thresh", "Yasuo", "Jhin", "Kayn", "Viego", "Janna" };
        if (!Directory.Exists(Champions) || Database.Value is not { } database) return;

        // textures that live in shared archives (assets/shared/...) rather than the champion's own
        var shared = new List<WadArchive>();
        foreach (string path in new[] { Path.Combine(Final, "Global.wad.client"), Path.Combine(Final, "Maps", "Shipping", "Common.wad.client") })
            if (File.Exists(path)) shared.Add(WadArchive.Open(path, new WadPathResolver(database)));

        var perFormat = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var perFormatBody = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var perRole = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>();
        var seenRole = new HashSet<(string Champion, ulong Hash, string Role)>();
        int skinsScanned = 0, texturesFound = 0;
        long writable = 0;

        try
        {
            foreach (string champion in champions)
            {
                using var files = Open(champion);
                if (files is null) continue;
                string character = champion.ToLowerInvariant();
                var scanner = new SkinColorScanner();                                  // one per champion: the bins are shared between skins
                var seen = new Dictionary<ulong, string>();                             // texture hash -> format, once per champion

                foreach (string binPath in files.SkinBins(character))
                {
                    var inventory = scanner.Scan(new SkinColorRequest(binPath, files.Read,
                        h => database.TryGetBinName(h, out var n) ? n : null, h => database.TryGetPath(h, out var p) ? p : null,
                        IncludeSharing: false));
                    skinsScanned++;

                    var textures = inventory.BodyTextures.Select(t => (t.Path, t.Hash, Role: "body " + t.Role.ToString().ToLowerInvariant()))
                        .Concat(inventory.Effects.SelectMany(s => s.Emitters).SelectMany(e => e.Textures)
                            .Select(t => (t.Path, t.Hash, Role: "effect " + t.Role.ToLowerInvariant())));
                    foreach (var (path, hash, role) in textures)
                    {
                        if (!seen.TryGetValue(hash, out string? format))
                        {
                            byte[]? bytes = files.Read(path);
                            if (bytes is null)
                                foreach (var archive in shared)
                                    if (archive.TryGetEntry(hash, out _)) { try { bytes = archive.Extract(hash); } catch { } break; }
                            seen[hash] = format = Classify(bytes);
                            perFormat[format] = perFormat.GetValueOrDefault(format) + 1;
                            if (role.StartsWith("body")) perFormatBody[format] = perFormatBody.GetValueOrDefault(format) + 1;
                            examples.TryAdd(format, path);
                            if (format is "TEX BC1" or "TEX BC3") writable++;   // TexWriter.DetectFormat: 10 and 12
                            texturesFound++;
                        }
                        if (!seenRole.Add((champion, hash, role))) continue;
                        var byRole = perRole.TryGetValue(role, out var map) ? map : perRole[role] = new SortedDictionary<string, int>(StringComparer.Ordinal);
                        byRole[format] = byRole.GetValueOrDefault(format) + 1;
                    }
                }
            }
        }
        finally { foreach (var archive in shared) archive.Dispose(); }

        _output.WriteLine($"M812 texture-format census: {skinsScanned} skins of {champions.Length} champions, {texturesFound} distinct colour textures (unique per champion).");
        _output.WriteLine("format                       textures   (body)   example");
        foreach (var (format, count) in perFormat)
            _output.WriteLine($"{format,-28} {count,8}   {perFormatBody.GetValueOrDefault(format),6}   {examples[format]}");
        _output.WriteLine($"writable by TexWriter (TEX BC1 / BC3): {writable} of {texturesFound}");
        _output.WriteLine("by role (a texture counts once per champion and role):");
        foreach (var (role, map) in perRole)
            _output.WriteLine($"  {role,-24} " + string.Join(", ", map.Select(kv => $"{kv.Key} {kv.Value}")));

        if (skinsScanned == 0) return;           // none of the ten champions is installed
        Assert.True(texturesFound > 0, "the ten champions' inventories listed no colour texture at all");
    }

    /// <summary>The container and pixel format of a texture file, from its header alone. TEX is Riot's: 'TEX\0', u16 width,
    /// u16 height, u8 depth, u8 pixel format, u8 resource type, u8 flags (bit 0 = mip chain).</summary>
    private static string Classify(byte[]? bytes)
    {
        if (bytes is null) return "missing from the champion's and the shared archives";
        if (bytes.Length >= 12 && bytes[0] == 'T' && bytes[1] == 'E' && bytes[2] == 'X' && bytes[3] == 0)
        {
            string name = bytes[9] switch
            {
                10 => "BC1", 11 => "BC1 (fmt 11)", 12 => "BC3", 14 => "BC5", 20 => "BGRA8", _ => $"fmt {bytes[9]}",
            };
            return "TEX " + name + (bytes[10] != 0 ? " (non-2D)" : "");
        }
        if (bytes.Length >= 4 && bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S' && bytes[3] == ' ') return "DDS";
        return "other (" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(4, bytes.Length))) + ")";
    }

    // ===================================================================== independent checks

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty>? p, string name) =>
        p is not null && p.TryGetValue(H(name), out var v) ? v : null;

    private static IReadOnlyDictionary<uint, BinTreeProperty>? PropsOf(BinTreeProperty? p) => (p as BinTreeStruct)?.Properties;

    private static void Take(HashSet<ulong> into, BinTreeProperty? p)
    {
        if (p is BinTreeString s && s.Value.Length > 0) into.Add(BinTexturePath.HashOfReference(s.Value));
        else if (p is BinTreeWadChunkLink { Value: not 0 } w) into.Add(w.Value);
    }

    private static bool IsColorValue(BinTreeProperty? p) => p is BinTreeStruct or BinTreeVector4 or BinTreeColor;

    /// <summary>
    /// For every listed system: re-reads the object from the raw bin with a walk that shares no code with the reader and
    /// requires each emitter's listed colour textures to be EXACTLY the five whitelisted fields it authors, and its listed
    /// colour fields to be exactly the whitelisted ones it authors - so a mask or a mixer cannot be listed, and nothing
    /// colour-bearing is missed. Returns (colour textures checked, mask/data textures that were named and left out).
    /// </summary>
    private static (int Listed, int MaskOnly) VerifyAgainstRawBins(SkinColorInventory inventory, ChampionFiles files)
    {
        var objects = new Dictionary<uint, BinTreeObject>();
        foreach (string bin in inventory.Reach.ClosureBins)
            foreach (var o in SafeBinTree.Parse(files.Read(bin)!).Objects.Values)
                if (o.ClassHash == H("VfxSystemDefinitionData")) objects.TryAdd(o.PathHash, o);

        int listed = 0, maskOnlyTotal = 0;
        foreach (var system in inventory.Effects)
        {
            var raw = objects[system.PathHash];
            var rawEmitters = raw.Properties.Values.OfType<BinTreeContainer>()
                .SelectMany(c => c.Elements).OfType<BinTreeStruct>().Where(s => s.ClassHash == H("VfxEmitterDefinitionData")).ToList();
            Assert.Equal(rawEmitters.Count, system.Emitters.Count);

            for (int i = 0; i < rawEmitters.Count; i++)
            {
                var p = rawEmitters[i].Properties;
                var colour = new HashSet<ulong>();
                Take(colour, Get(p, "texture"));
                Take(colour, Get(PropsOf(Get(p, "textureMult")), "textureMult"));
                Take(colour, Get(p, "particleColorTexture"));
                Take(colour, Get(PropsOf(Get(p, "paletteDefinition")), "paletteTexture"));
                Take(colour, Get(PropsOf(Get(p, "reflectionDefinition")), "reflectionMapTexture"));

                var masks = new HashSet<ulong>();
                Take(masks, Get(PropsOf(Get(p, "alphaErosionDefinition")), "erosionMapName"));
                Take(masks, Get(PropsOf(Get(p, "distortionDefinition")), "normalMapTexture"));
                Take(masks, Get(p, "falloffTexture"));
                Take(masks, Get(p, "glossTexture"));
                Take(masks, Get(p, "transitionTexture"));

                var model = system.Emitters[i];
                var modelTextures = model.Textures.Select(t => t.Hash).ToHashSet();
                Assert.True(modelTextures.SetEquals(colour), $"{system.Name} emitter {i}: listed textures differ from the colour fields it authors");
                foreach (ulong mask in masks.Except(colour))
                {
                    Assert.DoesNotContain(mask, modelTextures);
                    maskOnlyTotal++;
                }
                listed += modelTextures.Count;

                var fields = new List<string>();
                if (IsColorValue(Get(p, "birthColor"))) fields.Add("birthColor");
                if (IsColorValue(Get(p, "color"))) fields.Add("color");
                if (IsColorValue(Get(PropsOf(Get(p, "Linger")), "SeparateLingerColor"))) fields.Add("Linger.SeparateLingerColor");
                var reflection = PropsOf(Get(p, "reflectionDefinition"));
                if (IsColorValue(Get(reflection, "fresnelColor"))) fields.Add("reflectionDefinition.fresnelColor");
                if (IsColorValue(Get(reflection, "reflectionFresnelColor"))) fields.Add("reflectionDefinition.reflectionFresnelColor");
                Assert.Equal(fields, model.Colors.Select(c => c.Field));
                Assert.DoesNotContain(model.Colors, c => c.Field.Contains("palleteSrcMixColor") || c.Field.Contains("erosionMapChannelMixer"));
            }
        }
        return (listed, maskOnlyTotal);
    }

    /// <summary>The raw bytes of a skin bin and every bin it links, transitively - for a byte-level "does this file name
    /// that" check that shares no code with the scanner.</summary>
    private static List<byte[]> ClosureBytes(ChampionFiles files, string skinBin)
    {
        var result = new List<byte[]>();
        var visited = new HashSet<ulong> { HashAlgorithms.WadPath(skinBin) };
        var queue = new Queue<string>();
        queue.Enqueue(skinBin);
        while (queue.Count > 0 && result.Count < 64)
        {
            byte[]? bytes = files.Read(queue.Dequeue());
            if (bytes is null) continue;
            result.Add(bytes);
            foreach (string dependency in SafeBinTree.Parse(bytes).Dependencies)
                if (visited.Add(HashAlgorithms.WadPath(dependency))) queue.Enqueue(dependency);
        }
        return result;
    }

    /// <summary>The path hashes of the VfxSystemDefinitionData objects in these bins whose particleName starts with a prefix.</summary>
    private static HashSet<uint> RawSystemsNamed(List<byte[]> closure, string prefix)
    {
        var found = new HashSet<uint>();
        foreach (var bytes in closure)
            foreach (var o in SafeBinTree.Parse(bytes).Objects.Values)
                if (o.ClassHash == H("VfxSystemDefinitionData") && Get(o.Properties, "particleName") is BinTreeString name
                    && name.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    found.Add(o.PathHash);
        return found;
    }

    private static bool MentionsHash(byte[] bytes, uint hash) => bytes.AsSpan().IndexOf(BitConverter.GetBytes(hash)) >= 0;

    /// <summary>A texture is named either as the u64 of a WadChunkLink or as its path in any letter case.</summary>
    private static bool MentionsTexture(byte[] bytes, string path, ulong hash)
    {
        if (bytes.AsSpan().IndexOf(BitConverter.GetBytes(hash)) >= 0) return true;
        var lower = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) lower[i] = bytes[i] is >= (byte)'A' and <= (byte)'Z' ? (byte)(bytes[i] + 32) : bytes[i];
        return lower.AsSpan().IndexOf(Encoding.UTF8.GetBytes(path.ToLowerInvariant())) >= 0;
    }
}
