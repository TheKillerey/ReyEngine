using System.Text.Json;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M814: the GameData documents Export .fantome writes, pinned byte for byte against documents that were checked
/// with league-mod's own crates.
///
/// <para><b>Where the fixtures came from.</b> <c>Fixtures/GameData/s1/base.json</c> and <c>fix.json</c> are
/// <c>Layers.base.GameData</c> and <c>Layers.fix.GameData</c> of the archive Project &gt; Export .fantome writes for
/// <see cref="GoldenGameDataProject"/>: a map folder and a champion folder whose bins change every kind of property
/// (the kitchen-sink bin <c>sink.bin</c>), exported with the declarations setting on, with ModuleNames off. They were
/// taken from that archive AFTER the M814 scratch harness had passed it: <c>fantomecheck</c> reads the archive with
/// <c>ltk_fantome</c> 0.15.1, applies each layer's document over the untouched game bins with <c>ltk_game_data</c> 0.8.0
/// (<c>apply</c>) and requires the result to be the project's bins, field for field. So these are not documents
/// this code wrote and found plausible; they are the documents league-mod accepted and applied to the intended result.</para>
///
/// <para><b>What the test asserts.</b> The same project, exported again through the real view model path
/// (<see cref="GoldenGameDataProject.Export"/>), produces exactly these documents, spelled the same - including every
/// number token (<c>1E-30</c>, <c>-0.0</c>, <c>123456790.0</c>) - and no layer has a document the fixtures lack. The
/// plaintext for hashes is the project's own recorded names, so the result does not depend on the machine's hash tables.</para>
///
/// <para><b>When this fails</b> a rendering or planning rule changed. If that was intended, re-verify and replace the fixtures,
/// do not edit them by hand: from <c>.codex_tmp/M814</c> (not committed) <c>./run-checks.sh golden</c> exports the
/// project with the new code, has <c>fantomecheck check</c> apply the archive's documents with <c>ltk_game_data</c> over the game
/// bins, and diffs the archive's documents against these files; <c>export814 golden-make</c> writes new candidates once
/// that passes.</para>
/// </summary>
public sealed class GameDataGoldenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-golden-" + Guid.NewGuid().ToString("N"));

    public GameDataGoldenTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "GameData", "s1");

    private static SortedDictionary<string, string> Fixtures()
    {
        Assert.True(Directory.Exists(FixtureDir), $"the golden fixtures were not copied to the output folder: {FixtureDir}");
        var fixtures = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(FixtureDir, "*.json"))
            fixtures[Path.GetFileNameWithoutExtension(file)] = GoldenGameDataProject.Normalize(File.ReadAllText(file));
        return fixtures;
    }

    private SortedDictionary<string, string> Exported()
    {
        var built = GoldenGameDataProject.Build(_root);
        string output = Path.Combine(_root, "out.fantome");
        GoldenGameDataProject.Export(built, output);
        return GoldenGameDataProject.GameDataOf(output);
    }

    [Fact]
    public void TheExporterProducesExactlyTheDocumentsLtkGameDataApplied()
    {
        var fixtures = Fixtures();
        var exported = Exported();

        // the same layers carry a document, and no other
        Assert.Equal(fixtures.Keys, exported.Keys);
        foreach (var (layer, expected) in fixtures)
            Assert.True(expected == exported[layer], $"Layers.{layer}.GameData differs from Fixtures/GameData/s1/{layer}.json.\n--- exported ---\n{exported[layer]}");
    }

    [Fact]
    public void TheFixturesAreNotAnEmptyShell()
    {
        var fixtures = Fixtures();
        Assert.Equal(new[] { "base", "fix" }, fixtures.Keys.ToArray());
        foreach (var (layer, text) in fixtures)
        {
            using var doc = JsonDocument.Parse(text);
            Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            var modules = doc.RootElement.GetProperty("modules");
            Assert.True(modules.GetArrayLength() > 0, layer);
            foreach (var module in modules.EnumerateArray())
            {
                // no module carries a name: ltk_game_data 0.6.0 refuses the layer for one
                Assert.Equal(new[] { "target", "edits", "origin" }, module.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.Single(module.GetProperty("edits").EnumerateArray());
            }
        }
    }

    /// <summary>The kitchen sink is in the fixture, and so is each spelling that is easy to get wrong: a number token that
    /// is not what a round trip through a double would write, a negative zero, an integer beyond 2^53, a string with
    /// escapes, and a hash spelled as the hash it is.</summary>
    [Fact]
    public void TheFixtureHoldsTheSpellingsThatAreEasyToGetWrong()
    {
        string text = Fixtures()["base"];

        Assert.Contains("data/maps/sink.bin", text);
        Assert.Contains("18446744073709551615", text);       // u64 max: beyond a double's exact integers, written as digits
        Assert.Contains("-9223372036854775808", text);       // i64 min
        Assert.Contains("-0.0", text);                       // a negative zero keeps its sign
        Assert.Contains("1E-30", text);                      // an exponent is not rewritten as 0.000...
        Assert.Contains("3.4028235E+38", text);              // f32 max
        Assert.Contains("123456790.0", text);                // 123456792f: the shortest text that reads back as that f32, with its point
        Assert.Contains("say \\\"hi\\\" \\\\ back\\ttab", text);   // the string, escaped once
    }
}
