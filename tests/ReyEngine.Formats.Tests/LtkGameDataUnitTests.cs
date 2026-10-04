using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M817: the pure functions the apply engine rests on, against the Rust side's own answers (<c>Fixtures/LtkGameData/units.json</c>: a sample of
/// the unit corpora the harness compared line for line - lowercase table, hashes, JSON numbers, paths, Rust's number parsing). The answers
/// are what the crates (<c>ltk_hash</c>, <c>ltk_meta</c>, <c>serde_json</c>, the standard library) returned, not what a reader expects them to return.
/// </summary>
public class LtkGameDataUnitTests
{
    private static readonly Lazy<JsonObject> Units = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LtkGameData", "units.json");
        Assert.True(File.Exists(path), $"the unit fixture was not copied to the output folder: {path}");
        return (JsonObject)JsonNode.Parse(File.ReadAllBytes(path))!;
    });

    private static IEnumerable<(string Input, JsonNode Expected)> Pairs(string name) =>
        ((JsonArray)Units.Value[name]!).Select(p => (p![0]!.GetValue<string>(), p[1]!.DeepClone()));

    private static void AssertAll(string name, Func<string, string> run)
    {
        var failures = new List<string>();
        int count = 0;
        foreach (var (input, expected) in Pairs(name))
        {
            count++;
            string want = expected.GetValue<string>();
            string got = run(input);
            if (got != want) failures.Add($"{Describe(input)}: oracle {want}, engine {got}");
        }
        Assert.True(count > 40, $"{name}: only {count} samples");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} differ:\n" + string.Join("\n", failures.Take(8)));
    }

    /// <summary>The input with every non-printable or non-ASCII character spelled out, so a failure shows what was hashed.</summary>
    private static string Describe(string input)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in input.Length > 60 ? input[..60] : input) sb.Append(c is >= ' ' and < (char)127 ? c.ToString() : $"\\u{(int)c:x4}");
        return sb.Append('"').ToString();
    }

    /// <summary>Plain FNV-1a over UTF-8 with no lowercasing: what the hash of the Rust lowercase of a character is.</summary>
    private static uint Fnv(string text)
    {
        uint hash = 0x811c9dc5;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) { hash ^= b; hash *= 0x01000193; }
        return hash;
    }

    [Fact]
    public void The_lowercase_of_every_code_point_is_rusts_char_to_lowercase()
    {
        // rustc 1.92.0, Unicode 17: the table lists the characters that change and what they become (U+0130 becomes two). .NET's ToLowerInvariant differs.
        var table = new Dictionary<int, int[]>();
        foreach (string line in ((JsonArray)Units.Value["lowercase"]!).Select(l => l!.GetValue<string>()))
        {
            string[] parts = line.Split(' ');
            table[int.Parse(parts[0], NumberStyles.HexNumber)] = parts.Skip(1).Select(p => int.Parse(p, NumberStyles.HexNumber)).ToArray();
        }
        Assert.True(table.Count > 1400);
        var failures = new List<string>();
        for (int code = 0; code <= 0x10FFFF; code++)
        {
            if (code is >= 0xD800 and <= 0xDFFF) continue;
            string text = char.ConvertFromUtf32(code);
            int[] lower = table.TryGetValue(code, out var l) ? l : new[] { code };
            uint want = Fnv(string.Concat(lower.Select(char.ConvertFromUtf32)));
            uint got = LtkHash.Fnv1aLower(text);
            if (got != want) failures.Add($"U+{code:X4}: engine {got:x8}, oracle {want:x8}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(8)));
    }

    [Fact]
    public void Hashes_are_ltk_hashs_fnv1a_over_rust_lowercase_and_xxh64_over_ascii_lowercase()
        => AssertAll("hashes", s => $"{LtkHash.Fnv1aLower(s):x8} {LtkHash.Xxh64Path(s):x16}");

    [Fact]
    public void Json_numbers_are_integers_or_floats_the_way_serde_json_reads_them()
        => AssertAll("numbers", text =>
        {
            try
            {
                return GameDataJson.Parse(text) switch
                {
                    GdInteger i => i.Value >= 0 ? $"u:{i.Value}" : $"i:{i.Value}",
                    GdFloat f => $"f:{BitConverter.DoubleToInt64Bits(f.Value):x16}",
                    _ => "other",
                };
            }
            catch (GameDataJsonException) { return "err"; }
        });

    [Fact]
    public void Paths_parse_as_ltk_metas_property_paths_do()
    {
        var failures = new List<string>();
        int count = 0;
        foreach (var (input, expected) in Pairs("paths"))
        {
            count++;
            string want = expected.ToJsonString();
            string got = PathJson(input).ToJsonString();
            if (got != want) failures.Add($"{Describe(input)}: oracle {want}, engine {got}");
        }
        Assert.True(count > 40, $"only {count} samples");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} differ:\n" + string.Join("\n", failures.Take(8)));
    }

    private static JsonObject PathJson(string text)
    {
        if (!PropertyPath.TryParse(text, out var path, out var error)) return new JsonObject { ["err"] = error!.Message };
        var segments = new JsonArray();
        foreach (var segment in path!.Segments)
        {
            string? sub = segment.Index is { } i ? $"[{i}]"
                : segment.Key is { } k ? k.Kind switch
                {
                    KeyLiteralKind.Number => $"{{n:{k.Text}}}",
                    KeyLiteralKind.Bool => $"{{b:{(k.Flag ? "true" : "false")}}}",
                    _ => $"{{s:{k.Text}}}",
                }
                : null;
            segments.Add(new JsonArray(segment.Name, sub, segment.ToString()));
        }
        return new JsonObject { ["ok"] = segments };
    }

    [Fact]
    public void Integers_and_floats_parse_as_rusts_from_str_does()
    {
        var kinds = new[] { PropKind.I8, PropKind.U8, PropKind.I16, PropKind.U16, PropKind.I32, PropKind.U32, PropKind.I64, PropKind.U64 };
        AssertAll("parse", t =>
        {
            var row = new List<string>();
            foreach (var kind in kinds)
            {
                if (!RustParse.TryParseInteger(t, kind, out ulong bits)) { row.Add("-"); continue; }
                bool signed = kind is PropKind.I8 or PropKind.I16 or PropKind.I32 or PropKind.I64;
                row.Add(signed ? ((long)bits).ToString(CultureInfo.InvariantCulture) : bits.ToString(CultureInfo.InvariantCulture));
            }
            // the f32 column is asked only of text a JSON number can be
            row.Add(IsJsonNumber(t) ? (RustParse.TryParseSingle(t, out float single) ? BitConverter.SingleToUInt32Bits(single).ToString("x8") : "-") : "?");
            row.Add(RustParse.TryParseDouble(t, out double d) ? BitConverter.DoubleToInt64Bits(d).ToString("x16") : "-");
            row.Add(RustParse.TryParseInt128(t, out var big) ? big.ToString(CultureInfo.InvariantCulture) : "-");
            return string.Join(' ', row);
        });
    }

    private static bool IsJsonNumber(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"\A-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\z");

    // LTK Manager's own examples (problems/build.rs): the game's version string and a table's filename stem
    [Theory]
    [InlineData("16.16.8049184+branch.releases-16-16.content.release", 16u, 16u, 8049184u)]
    [InlineData("16.17.8087655", 16u, 17u, 8087655u)]
    [InlineData("16.19.8230722.1234", 16u, 19u, 8230722u)]
    public void A_game_build_is_the_first_three_numbers_of_the_version(string text, uint major, uint minor, uint content)
    {
        Assert.True(GameBuild.TryParse(text, out var build));
        Assert.Equal(new GameBuild(major, minor, content), build);
        Assert.Equal($"{major}.{minor}.{content}", build.ToString());
        Assert.Equal($"{major}.{minor}", build.Patch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("live")]
    [InlineData("16.17")]
    [InlineData("16.19.-1")]
    public void A_version_that_is_not_three_numbers_is_no_build(string text) => Assert.False(GameBuild.TryParse(text, out _));

    [Fact]
    public void Builds_order_by_major_then_minor_then_content()
    {
        Assert.True(new GameBuild(16, 16, 8_049_184).CompareTo(new GameBuild(16, 17, 8_087_655)) < 0);
        Assert.True(new GameBuild(16, 17, 8_087_655).CompareTo(new GameBuild(17, 1, 1)) < 0);
        Assert.True(new GameBuild(16, 17, 8_087_654).CompareTo(new GameBuild(16, 17, 8_087_655)) < 0);
    }
}
