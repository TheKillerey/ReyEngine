using System.Globalization;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M814: the bin selection Send to LTK Manager and Export .fantome share. M757 wrote it inside the main window's
/// view model; it is now one function of its inputs, so a project is given the same declarations by both and the
/// choices below can be tested without a window.
/// </summary>
public sealed class BinDeclarationPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rey-plan-tests-" + Guid.NewGuid().ToString("N"));
    private int _n;

    public BinDeclarationPlannerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private sealed class Names : IDeclarationNames
    {
        public string? Field(uint hash) => new[] { "speed", "count", "Maps/Test/Thing", "Thing" }.FirstOrDefault(n => H(n) == hash);
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) => null;
    }

    private static byte[] Bin(params BinTreeProperty[] props)
    {
        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Maps/Test/Thing"), H("Thing"), props) }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static byte[] Speed(float v) => Bin(new BinTreeF32(H("speed"), v));

    /// <summary>The game's copy of each bin, found by the chunk hash the planner computes for its path.</summary>
    private static Func<ulong, string, byte[]?> Game(params (string Rel, byte[] Bytes)[] bins)
    {
        var map = bins.ToDictionary(b => ChunkHash(b.Rel), b => b.Bytes);
        return (hash, _) => map.GetValueOrDefault(hash);
    }

    private static ulong ChunkHash(string rel)
    {
        string stem = Path.GetFileNameWithoutExtension(rel);
        return !rel.Contains('/') && stem.Length == 16 && ulong.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)
            ? h : HashAlgorithms.WadPath(rel);
    }

    private DeclarationFile Put(string layer, string folder, string rel, byte[] bytes)
    {
        string path = Path.Combine(_dir, (++_n).ToString(CultureInfo.InvariantCulture), rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new DeclarationFile(layer, folder, rel, path);
    }

    private static DeclarationPlan Plan(IEnumerable<DeclarationFile> files, Func<ulong, string, byte[]?> game) =>
        BinDeclarationPlanner.Plan(files, game, new Names());

    // ===================================================== the three outcomes

    [Fact]
    public void AChangedGameBinIsDeclaredAndLeavesTheFileList()
    {
        var f = Put("base", "Map11", "data/maps/a.bin", Speed(2f));
        var plan = Plan(new[] { f }, Game(("data/maps/a.bin", Speed(1f))));

        Assert.Empty(plan.Kept);
        Assert.Equal(new[] { f }, plan.Dropped);
        var chunk = Assert.Single(Assert.Single(plan.Modules).Value);
        Assert.Equal("data/maps/a.bin", chunk.Target);
        Assert.Equal("data/maps/a.bin", chunk.Label);
        Assert.Equal(1, plan.Declared);
        Assert.Equal(1, plan.Properties);
        Assert.Equal("base", Assert.Single(plan.DeclaredBins).Layer);
    }

    [Fact]
    public void AnUnchangedBinIsNotShippedAndHasNoModule()
    {
        var f = Put("base", "Map11", "data/maps/a.bin", Speed(1f));
        var plan = Plan(new[] { f }, Game(("data/maps/a.bin", Speed(1f))));

        Assert.Empty(plan.Kept);
        Assert.Equal(new[] { f }, plan.Dropped);
        Assert.Empty(plan.Modules);
        Assert.Equal(1, plan.Unchanged);
        Assert.Equal(0, plan.Declared);
    }

    [Fact]
    public void ABinThatCannotBeDeclaredShipsWholeWithItsReason()
    {
        // a removed property: no declaration removes one
        var f = Put("base", "Map11", "data/maps/a.bin", Bin());
        var plan = Plan(new[] { f }, Game(("data/maps/a.bin", Speed(1f))));

        Assert.Equal(new[] { f }, plan.Kept);
        Assert.Empty(plan.Dropped);
        Assert.Empty(plan.Modules);
        var why = Assert.Single(plan.Whole);
        Assert.StartsWith("Map11/data/maps/a.bin: ", why);
        Assert.Contains("no declaration removes a property", why);
    }

    [Fact]
    public void NewContentAndEveryNonBinFileShipAsFilesAsTheyAlwaysDid()
    {
        var newBin = Put("base", "Map11", "data/maps/new.bin", Speed(1f));   // no game copy: new content
        var tex = Put("base", "Map11", "assets/a.tex", new byte[] { 1, 2, 3 });
        var plan = Plan(new[] { newBin, tex }, Game());

        Assert.Equal(new[] { newBin, tex }, plan.Kept);
        Assert.Empty(plan.Dropped);
        Assert.Empty(plan.Whole);   // new content is not a failure to declare

        // but a setting that declared nothing says why: the only bin had no game copy to compare with
        Assert.Equal(new[] { "Map11/data/maps/new.bin" }, plan.NoGameCopy);
        Assert.Contains(plan.Report("shipped"), r => r.Level == 1 && r.Line.Contains("1 bin(s) have no game copy") && r.Line.Contains("shipped as files"));
    }

    [Fact]
    public void AGameCopyThatCannotBeReadIsNoGameCopy()
    {
        var f = Put("base", "Map11", "data/maps/a.bin", Speed(2f));
        var plan = Plan(new[] { f }, (_, _) => throw new IOException("reference WAD is gone"));
        Assert.Equal(new[] { f }, plan.Kept);
        Assert.Single(plan.NoGameCopy);
    }

    [Fact]
    public void APatchBinShipsWhole()
    {
        var ptch = new byte[] { (byte)'P', (byte)'T', (byte)'C', (byte)'H', 0, 0, 0, 0 };
        var f = Put("base", "Map11", "data/maps/a.bin", ptch);
        var plan = Plan(new[] { f }, Game(("data/maps/a.bin", ptch)));
        Assert.Equal(new[] { f }, plan.Kept);
        Assert.Contains("PTCH", Assert.Single(plan.Whole));
    }

    [Fact]
    public void ALooseHashNamedBinIsFoundByItsOwnHash()
    {
        var f = Put("base", "Map11", "0123456789abcdef.bin", Speed(2f));
        var plan = Plan(new[] { f }, Game(("0123456789abcdef.bin", Speed(1f))));
        Assert.Equal("0123456789abcdef", Assert.Single(Assert.Single(plan.Modules).Value).Target);
    }

    // ===================================================== layers and duplicates

    [Fact]
    public void ModulesAreGroupedByLayerAndOrderedByTarget()
    {
        var files = new[]
        {
            Put("base", "Map11", "data/maps/z.bin", Speed(2f)),
            Put("particle-fix", "Ahri", "data/characters/ahri.bin", Speed(2f)),
            Put("base", "Map11", "data/maps/a.bin", Speed(2f)),
            Put("base", "Map11", "data/maps/m.bin", Speed(2f)),
        };
        var game = Game(("data/maps/z.bin", Speed(1f)), ("data/characters/ahri.bin", Speed(1f)),
            ("data/maps/a.bin", Speed(1f)), ("data/maps/m.bin", Speed(1f)));
        var plan = Plan(files, game);

        Assert.Equal(new[] { "data/maps/a.bin", "data/maps/m.bin", "data/maps/z.bin" },
            plan.Modules["base"].Select(c => c.Target).ToArray());
        Assert.Equal(new[] { "data/characters/ahri.bin" }, plan.Modules["particle-fix"].Select(c => c.Target).ToArray());
        Assert.Equal(new[] { "data/maps/z.bin", "data/characters/ahri.bin", "data/maps/a.bin", "data/maps/m.bin" },
            plan.DeclaredBins.Select(d => d.RelPath).ToArray());   // the order the files were given
    }

    [Fact]
    public void TheLayerNameComparesWithoutRegardToCase()
    {
        var plan = Plan(new[] { Put("Particle-Fix", "Ahri", "data/a.bin", Speed(2f)) }, Game(("data/a.bin", Speed(1f))));
        Assert.True(plan.Modules.ContainsKey("particle-fix"));
    }

    [Fact]
    public void TheSameBinInTwoFoldersOfOneLayerDeclaresOnce()
    {
        var a = Put("base", "Map11", "data/shared.bin", Speed(2f));
        var b = Put("base", "Map12", "data/shared.bin", Speed(2f));
        var plan = Plan(new[] { a, b }, Game(("data/shared.bin", Speed(1f))));

        Assert.Single(plan.Modules["base"]);
        Assert.Equal(new[] { a, b }, plan.Dropped);   // neither ships as a file
        Assert.Equal(1, plan.Declared);
    }

    [Fact]
    public void TwoDifferentCopiesInOneLayerCannotBothBeDeclared()
    {
        var a = Put("base", "Map11", "data/shared.bin", Speed(2f));
        var b = Put("base", "Map12", "data/shared.bin", Speed(3f));
        var plan = Plan(new[] { a, b }, Game(("data/shared.bin", Speed(1f))));

        // the first copy is declared; the second cannot be, because the layer's one module for the chunk is the first's
        Assert.Equal(new[] { b }, plan.Kept);
        Assert.Contains("two different copies in one layer", Assert.Single(plan.Whole));
    }

    /// <summary>M814: dropping an identical second copy of a bin whose first copy ships whole left the second
    /// WAD without the override it carried.</summary>
    [Fact]
    public void AnIdenticalCopyOfABinThatShipsWholeShipsToo()
    {
        var a = Put("base", "Map11", "data/shared.bin", Bin());   // removes the property: ships whole
        var b = Put("base", "Map12", "data/shared.bin", Bin());
        var plan = Plan(new[] { a, b }, Game(("data/shared.bin", Speed(1f))));

        Assert.Equal(new[] { a, b }, plan.Kept);
        Assert.Empty(plan.Dropped);
        Assert.Equal(2, plan.Whole.Count);
        Assert.Contains("which ships whole", plan.Whole[1]);
    }

    [Fact]
    public void TheSameBinInTwoLayersDeclaresInEach()
    {
        var a = Put("base", "Map11", "data/shared.bin", Speed(2f));
        var b = Put("extras", "Map11b", "data/shared.bin", Speed(2f));
        var plan = Plan(new[] { a, b }, Game(("data/shared.bin", Speed(1f))));
        Assert.Single(plan.Modules["base"]);
        Assert.Single(plan.Modules["extras"]);
    }

    // ===================================================== the report Send to LTK Manager has always logged

    [Fact]
    public void TheReportHasOneSummaryLineThenUpToTwentyReasons()
    {
        var files = new List<DeclarationFile>();
        var game = new List<(string, byte[])>();
        for (int i = 0; i < 23; i++)
        {
            files.Add(Put("base", "Map11", $"data/w{i:00}.bin", Bin()));   // 23 bins that ship whole
            game.Add(($"data/w{i:00}.bin", Speed(1f)));
        }
        files.Add(Put("base", "Map11", "data/d.bin", Speed(2f)));
        game.Add(("data/d.bin", Speed(1f)));
        var plan = Plan(files, Game(game.ToArray()));

        var report = plan.Report();
        Assert.Equal(0, report[0].Level);
        Assert.Equal("Declarations: 1 game bin(s) sent as changes (1 propert(ies), 0 object(s) added, 0 removed), 0 unchanged bin(s) not sent, 23 sent whole.",
            report[0].Line);
        Assert.Equal(1 + 20 + 1, report.Count);
        Assert.StartsWith("  sent whole - Map11/data/w00.bin: ", report[1].Line);
        Assert.Equal("  ... and 3 more sent whole.", report[^1].Line);

        Assert.Contains("shipped as changes", plan.Report("shipped")[0].Line);
    }

    // ===================================================== the two renderings of the same plan

    [Fact]
    public void TheManifestAndTheDocumentCarryTheSameModulesInTheSameOrder()
    {
        var files = new[]
        {
            Put("base", "Map11", "data/b.bin", Speed(2f)),
            Put("base", "Map11", "data/a.bin", Speed(3f)),
        };
        var plan = Plan(files, Game(("data/b.bin", Speed(1f)), ("data/a.bin", Speed(1f))));
        var chunks = plan.Modules["base"];

        string yaml = BinDeclarations.Manifest(chunks);
        var doc = BinDeclarations.GameDataDocument(chunks);
        var targets = doc["modules"]!.AsArray().Select(m => m!["target"]!.GetValue<string>()).ToArray();

        Assert.Equal(new[] { "data/a.bin", "data/b.bin" }, targets);
        Assert.True(yaml.IndexOf("\"data/a.bin\"", StringComparison.Ordinal) < yaml.IndexOf("\"data/b.bin\"", StringComparison.Ordinal));
        Assert.Equal(chunks.Count, doc["modules"]!.AsArray().Count);
    }

    /// <summary>The plan is a function of its inputs: running it again over the same files gives the same
    /// declarations, which is what lets Send to LTK Manager and Export .fantome agree.</summary>
    [Fact]
    public void ThePlanIsDeterministic()
    {
        var files = new[]
        {
            Put("base", "Map11", "data/b.bin", Speed(2f)),
            Put("base", "Map11", "data/a.bin", Speed(3f)),
            Put("x", "Ahri", "data/c.bin", Bin()),
        };
        var game = Game(("data/b.bin", Speed(1f)), ("data/a.bin", Speed(1f)), ("data/c.bin", Speed(1f)));
        var first = Plan(files, game);
        var second = Plan(Enumerable.Reverse(files), game);

        Assert.Equal(first.Modules["base"].Select(c => c.Module).ToArray(), second.Modules["base"].Select(c => c.Module).ToArray());
        Assert.Equal(first.Report().Select(r => r.Line).ToArray(), second.Report().Select(r => r.Line).ToArray());
    }
}
