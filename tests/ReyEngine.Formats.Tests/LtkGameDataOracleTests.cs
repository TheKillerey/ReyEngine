using System.Globalization;
using System.IO.Hashing;
using System.Text.Json.Nodes;
using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M817: the C# port of ltk_game_data's apply engine, pinned against the engine it ports.
///
/// <para><b>Where the expectations came from.</b> <c>Fixtures/LtkGameData/oracle.json</c> holds cases whose expectation is the
/// result <c>ltk_game_data</c> 0.8.0 itself produced (a Rust harness, <c>.codex_tmp/M817/rs</c>, calling <c>apply</c>): the output
/// bytes (length and a 128-bit xxHash64 digest), the counts, the dependency list and every diagnostic - kind, edit, path, reason, detail. Each case
/// was produced identically by the Rust crate and this engine before it was kept; nothing in the file was written by hand. The
/// cases were chosen from a 56,000-case crafted corpus (every diagnostic reason, every coercion row, the traps of the port spec's
/// section 9) by the oracle's own outcome, so each distinct outcome stays represented. The bases are tiny bins the harness built;
/// no game file is involved.</para>
///
/// <para><b>When this fails</b> the engine's output differs from the Rust crate's for that case: fix the engine, not the fixture.
/// If the Rust crate itself changed (a newer <c>ltk_game_data</c>), regenerate the file with <c>.codex_tmp/M817/export_fixture.py</c>
/// after re-running the comparison there (<c>cmp.sh</c>).</para>
/// </summary>
public class LtkGameDataOracleTests
{
    /// <summary>The cases that carry a rule tag of the port spec's section 9 (see <see cref="LtkOracleFixture"/>).</summary>
    private static void AssertRule(string rule)
    {
        var cases = LtkOracleFixture.Cases.Where(c => c.Rules.Contains(rule)).ToList();
        Assert.True(cases.Count > 0, $"the fixture holds no case for rule {rule}");
        LtkOracleFixture.AssertEqual(cases);
    }

    [Fact] public void Rule01_hash_lowercasing() => AssertRule("01");
    [Fact] public void Rule02_hash_form_is_honoured_in_paths_and_names_not_in_subscripts() => AssertRule("02");
    [Fact] public void Rule03_value_paths_and_property_paths_hash_and_compare_differently() => AssertRule("03");
    [Fact] public void Rule04_float_keys_compare_bitwise_to_navigate_and_by_value_to_remove() => AssertRule("04");
    [Fact] public void Rule05_numbers_become_f32_only_when_exact() => AssertRule("05");
    [Fact] public void Rule06_check_order_decides_which_reason_is_reported() => AssertRule("06");
    [Fact] public void Rule07_grouping_and_operation_order() => AssertRule("07");
    [Fact] public void Rule08_removal_by_value_removes_every_equal_element() => AssertRule("08");
    [Fact] public void Rule09_map_additions_replace_in_place_and_a_set_keeps_duplicates() => AssertRule("09");
    [Fact] public void Rule10_a_block_is_flattened_against_the_object_before_the_entry() => AssertRule("10");
    [Fact] public void Rule11_the_schema_outranks_the_base_and_subscripts_never_ask_it() => AssertRule("11");
    [Fact] public void Rule12_a_target_is_rewritten_only_when_something_changed() => AssertRule("12");
    [Fact] public void Rule13_the_writer_canonicalises_what_it_reads() => AssertRule("13");
    [Fact] public void Rule14_a_clone_rewrites_its_own_name_in_top_level_hash_and_string_values() => AssertRule("14");
    [Fact] public void Rule15_references_are_read_once_before_the_edits() => AssertRule("15");
    [Fact] public void Rule16_ptch_targets_carry_the_whole_settled_value_and_drop_covered_records() => AssertRule("16");
    [Fact] public void Rule17_each_application_decodes_the_previous_output() => AssertRule("17");

    // not a rule of the port spec's section 9, but what its first line asks: a document that does not load is refused with the crate's own words
    [Fact] public void Refusals_name_what_serde_json_names_and_where() => AssertRule("refusal");

    [Fact]
    public void Every_case_of_the_fixture_matches_the_oracle()
    {
        Assert.True(LtkOracleFixture.Cases.Count > 300);
        LtkOracleFixture.AssertEqual(LtkOracleFixture.Cases);
    }

    [Fact]
    public void The_fixture_still_reaches_every_diagnostic_kind_and_reason()
    {
        // a regenerated fixture that stopped exercising a code would pass every case and pin nothing
        var kinds = new HashSet<string>();
        var reasons = new HashSet<string>();
        foreach (var c in LtkOracleFixture.Cases)
        {
            if (c.Expect["steps"] is not JsonArray steps) continue;
            foreach (var step in steps)
            {
                if (step!["diagnostics"] is not JsonArray diagnostics) continue;
                foreach (var d in diagnostics)
                {
                    kinds.Add(d!["kind"]!.GetValue<string>());
                    foreach (string key in new[] { "record", "property", "object" })
                        if (d[key]?["reason"] is JsonNode reason) reasons.Add(key + ":" + reason.GetValue<string>());
                }
            }
        }
        foreach (var kind in Enum.GetValues<GameDataDiagnosticKind>().Where(k => k != GameDataDiagnosticKind.Unknown))
            Assert.Contains(Camel(kind.ToString()), kinds);
        foreach (var r in Enum.GetValues<RecordSkipReason>().Where(r => r != RecordSkipReason.Unknown))
            Assert.Contains("record:" + Camel(r.ToString()), reasons);
        foreach (var r in Enum.GetValues<PropertySkipReason>().Where(r => r != PropertySkipReason.Unknown))
            Assert.Contains("property:" + Camel(r.ToString()), reasons);
        foreach (var r in Enum.GetValues<ObjectSkipReason>().Where(r => r != ObjectSkipReason.Unknown))
            Assert.Contains("object:" + Camel(r.ToString()), reasons);
    }

    [Fact]
    public void The_fatal_errors_are_the_ones_the_crate_has()
    {
        // Bin and UnsupportedBase come back as a refusal of the whole application; every other failure is a diagnostic
        var errors = LtkOracleFixture.Cases.SelectMany(c => (c.Expect["steps"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Where(s => s["ok"]!.GetValue<bool>() == false).Select(s => s["error"]!["kind"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("Bin", errors);
        Assert.Contains("UnsupportedBase", errors);
        Assert.DoesNotContain("Other", errors);
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>One case of <c>oracle.json</c> with its section 9 tags.</summary>
internal sealed record LtkOracleCase(string Id, string[] Rules, JsonObject Node, JsonNode Expect);

/// <summary>
/// The fixture and the runner: the same inputs the oracle was given, through the C# engine, shaped as the oracle's result so the two compare as
/// JSON. A case is a base (PROP or PTCH), a document, the files the references and override paths resolve to, a schema (none, or the fixture's
/// own small meta database at a build), and one or more applications of the document's modules in order.
/// </summary>
internal static class LtkOracleFixture
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "LtkGameData");

    private static readonly Lazy<JsonObject> Root = new(() =>
    {
        string path = Path.Combine(Dir, "oracle.json");
        Assert.True(File.Exists(path), $"the oracle fixture was not copied to the output folder: {path}");
        return (JsonObject)JsonNode.Parse(File.ReadAllBytes(path))!;
    });

    public static IReadOnlyList<LtkOracleCase> Cases { get; } = ((JsonArray)Root.Value["cases"]!).OfType<JsonObject>().Select(c => new LtkOracleCase(
        c["id"]!.GetValue<string>(),
        c["rules"] is JsonArray r ? r.Select(x => x!.GetValue<string>()).ToArray() : Array.Empty<string>(),
        c,
        c["expect"]!)).ToList();

    private static readonly Dictionary<string, byte[]> Files = ((JsonObject)Root.Value["files"]!).ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value!.GetValue<string>()));
    private static readonly Dictionary<string, (byte[] Bytes, HashSet<uint> Declared)> Dumps = new();
    private static readonly Dictionary<string, LtkMetaSchema> Metas = new();

    private static (byte[] Bytes, HashSet<uint> Declared) Dump(string key)
    {
        lock (Dumps)
        {
            if (Dumps.TryGetValue(key, out var found)) return found;
            var declared = new HashSet<uint>();
            try { foreach (var (hash, _) in PropCodec.Mount(Files[key]).Declarations()) declared.Add(hash); }
            catch (PropDecodeException) { }
            return Dumps[key] = (Files[key], declared);
        }
    }

    private static LtkMetaSchema Meta(string key)
    {
        lock (Metas)
        {
            if (Metas.TryGetValue(key, out var found)) return found;
            return Metas[key] = LtkMetaSchema.Parse(System.Text.Encoding.UTF8.GetBytes(((JsonObject)Root.Value["metas"]!)[key]!.GetValue<string>()));
        }
    }

    public static void AssertEqual(IEnumerable<LtkOracleCase> cases)
    {
        var failures = new List<string>();
        int count = 0;
        foreach (var c in cases)
        {
            count++;
            string expected = Normalize(c.Expect).ToJsonString();
            string actual;
            try { actual = Normalize(Run(c.Node)).ToJsonString(); }
            catch (Exception e) { actual = "threw " + e.GetType().Name + ": " + e.Message; }
            if (expected != actual) failures.Add($"{c.Id}\n   oracle: {expected}\n   engine: {actual}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} cases differ from the oracle:\n" + string.Join("\n", failures.Take(6)));
    }

    /// <summary>Objects with their keys in order, so two JSON values compare as text.</summary>
    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Normalize(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalize).ToArray()),
        _ => node?.DeepClone(),
    };

    private static string Digest(byte[] bytes) =>
        XxHash64.HashToUInt64(bytes, 0).ToString("x16", CultureInfo.InvariantCulture)
        + XxHash64.HashToUInt64(bytes, unchecked((long)0x9E3779B97F4A7C15UL)).ToString("x16", CultureInfo.InvariantCulture);

    public static JsonNode Run(JsonObject node)
    {
        byte[] baseBytes = Files[node["base"]!.GetValue<string>()];
        string text = node["document"] is JsonNode inline
            ? inline.GetValue<string>()
            : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", node["documentFile"]!.GetValue<string>()));
        GameDataDocument document;
        try { document = GameDataDocument.Parse(text); }
        catch (GameDataException e) { return new JsonObject { ["refused"] = e.Message }; }

        var overrides = new Dictionary<string, byte[]>();
        if (node["overrides"] is JsonObject map)
            foreach (var (path, key) in map) overrides[path] = Files[key!.GetValue<string>()];
        var dumps = new List<(byte[] Bytes, HashSet<uint> Declared)>();
        if (node["entries"] is JsonArray list)
            foreach (var key in list) dumps.Add(Dump(key!.GetValue<string>()));
        var unreadable = new HashSet<uint>();
        if (node["entryErrors"] is JsonArray errors)
            foreach (var name in errors) unreadable.Add(EntryName.Create(name!.GetValue<string>()).ObjectHash);

        IGameDataSchema schema = NoSchema.Instance;
        if (node["schema"]!["mode"]!.GetValue<string>() == "patch")
            schema = Meta(node["schema"]!["meta"]!.GetValue<string>()).At(node["schema"]!["build"]?.GetValue<uint>());

        var applications = node["applications"] as JsonArray ?? new JsonArray(new JsonObject { ["module"] = 0 });
        byte[] bytes = baseBytes;
        var steps = new JsonArray();
        foreach (var application in applications)
        {
            int moduleIndex = application!["module"]!.GetValue<int>();
            var module = document.Modules[moduleIndex];
            List<GameDataEdit> edits;
            if (module.IsTarget) edits = module.Edits!;
            else
            {
                string wanted = application["entry"]!.GetValue<string>();
                var (name, entry) = module.Entries!.First(e => e.Key.Text == wanted);
                edits = new List<GameDataEdit> { GameDataEdit.ForEntry(name, entry) };
            }

            GameDataBytesRead ReadOverride(OverridePath path) =>
                overrides.TryGetValue(path.Text, out var found) ? GameDataBytesRead.Found(found) : GameDataBytesRead.Failed(path.Text + ": override file is missing");

            GameDataEntryRead ReadEntry(EntryName name)
            {
                uint hash = name.ObjectHash;
                if (unreadable.Contains(hash)) return GameDataEntryRead.Failed(name.Text + ": entry unreadable (harness)");
                foreach (var dump in dumps)
                {
                    if (!dump.Declared.Contains(hash)) continue;
                    try
                    {
                        var obj = PropCodec.ReadObject(dump.Bytes, hash);
                        return obj is null ? GameDataEntryRead.Failed(name.Text + ": chunk does not hold the object") : GameDataEntryRead.Found(obj);
                    }
                    catch (PropDecodeException e) { return GameDataEntryRead.Failed(name.Text + ": " + e.Message); }
                }
                return GameDataEntryRead.None;
            }

            try
            {
                var result = GameDataApplier.Apply(bytes, edits, ReadOverride, ReadEntry, schema);
                steps.Add(new JsonObject
                {
                    ["module"] = moduleIndex,
                    ["ok"] = true,
                    ["changed"] = result.Changed,
                    ["len"] = result.Bytes.Length,
                    ["h"] = Digest(result.Bytes),
                    ["applied"] = new JsonObject
                    {
                        ["records"] = result.Applied.Records,
                        ["objects"] = result.Applied.Objects,
                        ["properties"] = result.Applied.Properties,
                        ["linksAdded"] = result.Applied.LinksAdded,
                        ["linksRemoved"] = result.Applied.LinksRemoved,
                    },
                    ["dependencies"] = new JsonArray(result.Dependencies.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
                    ["diagnostics"] = new JsonArray(result.Diagnostics.Select(d => (JsonNode?)DiagnosticJson(d)).ToArray()),
                });
                if (result.Changed) bytes = result.Bytes;
            }
            catch (GameDataException e)
            {
                string kind = e.Kind switch
                {
                    GameDataErrorKind.Bin => "Bin",
                    GameDataErrorKind.UnsupportedBase => "UnsupportedBase",
                    GameDataErrorKind.DependencyOverflow => "DependencyOverflow",
                    _ => "Other",
                };
                steps.Add(new JsonObject { ["module"] = moduleIndex, ["ok"] = false, ["error"] = new JsonObject { ["kind"] = kind, ["message"] = e.Message } });
            }
        }
        return new JsonObject { ["steps"] = steps, ["final"] = new JsonObject { ["len"] = bytes.Length, ["h"] = Digest(bytes) } };
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static JsonObject DiagnosticJson(GameDataDiagnostic d)
    {
        var json = new JsonObject { ["kind"] = d.KindName, ["edit"] = d.Edit, ["path"] = d.Path };
        if (d.Record is { } r)
            json["record"] = new JsonObject { ["index"] = r.Index, ["object"] = (long)r.Object, ["property"] = r.Property, ["reason"] = Camel(r.Reason.ToString()) };
        if (d.Property is { } p)
            json["property"] = new JsonObject { ["entry"] = p.Entry.Text, ["reason"] = Camel(p.Reason.ToString()) };
        if (d.Object is { } o)
            json["object"] = new JsonObject { ["name"] = o.Name.Text, ["reason"] = Camel(o.Reason.ToString()) };
        if (d.Detail is not null) json["detail"] = d.Detail;
        return json;
    }
}
