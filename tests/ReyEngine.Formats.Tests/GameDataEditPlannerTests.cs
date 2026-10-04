using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Meta;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M823: an edit of a bin the imported GameData targets, as the module that reproduces it (<see cref="GameDataEditPlanner"/>) - and, above all, what is refused. The module is the literal diff of the bin LTK makes
/// from the game and the package's GameData (<c>B</c>) and the bin the editor holds (<c>E</c>); it is proven by applying it to <c>B</c> with the apply engine and the class schema in use, and kept only when what comes out
/// holds the data of <c>E</c>.
/// </summary>
public sealed class GameDataEditPlannerTests
{
    private const string Path = "data/t/a.bin";

    private static readonly IDeclarationNames Names = new Known(
        ("Test/Obj/A", "Test/Obj/A"), ("Test/Obj/B", "Test/Obj/B"), ("Test/Obj/New", "Test/Obj/New"), ("TestClass", "TestClass"),
        ("tags", "tags"), ("count", "count"), ("name", "name"), ("extra", "extra"));

    private sealed class Known : IDeclarationNames
    {
        private readonly Dictionary<uint, string> _names = new();
        public Known(params (string Name, string Spelled)[] names) { foreach (var (name, _) in names) _names[H(name)] = name; }
        public string? Field(uint hash) => _names.GetValueOrDefault(hash);
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) => null;
    }

    /// <summary>A schema that knows <c>TestClass</c> and its three fields, as the installed patch's would.</summary>
    private sealed class TestClassSchema : IGameDataSchema
    {
        public GameDataShape? Expected(uint cls, uint field)
        {
            if (cls != TestClass) return null;
            if (field == H("tags")) return new GameDataShape(PropKind.Container, null, PropKind.String);
            if (field == H("name")) return new GameDataShape(PropKind.String);
            if (field == H("count")) return new GameDataShape(PropKind.I32);
            if (field == H("extra")) return new GameDataShape(PropKind.I32);
            return null;
        }

        public bool HasClass(uint cls) => cls == TestClass;
    }

    private static int CountOf(byte[] bytes, string path)
    {
        var obj = PropCodec.ReadObject(bytes, H(path))!;
        return (int)((PropInt)obj.Properties.ValueAt(obj.Properties.IndexOf(H("count")))).Signed;
    }

    private static GameDataEditOutcome Plan(byte[] imported, byte[] edited, IGameDataSchema? schema = null) =>
        GameDataEditPlanner.Plan(Path, imported, edited, Names, schema ?? NoSchema.Instance);

    // ===================================================== what is kept

    [Fact]
    public void An_edit_of_a_value_is_a_module_of_that_one_value_and_applying_it_to_the_bin_gives_the_edited_bin()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }, "kept", count: 1));
        byte[] e = Bin(Obj("Test/Obj/A", new[] { "g1" }, "kept", count: 5));

        var outcome = Plan(b, e);

        Assert.Equal(GameDataEditKind.Declared, outcome.Kind);
        Assert.Null(outcome.Reason);
        Assert.Equal(1, outcome.Properties);
        using var module = JsonDocument.Parse(outcome.ModuleText!);
        Assert.Equal(Path, module.RootElement.GetProperty("target").GetString());
        var body = module.RootElement.GetProperty("edits")[0].GetProperty("Test/Obj/A");
        Assert.Equal(5, body.GetProperty("count").GetInt32());
        Assert.Single(body.EnumerateObject());                                    // only what the edit changed is stated: the rest of the bin stays what Riot's next patch makes it
        Assert.Equal(0, module.RootElement.GetProperty("origin").GetProperty("module").GetInt32());
        Assert.Equal(5, CountOf(outcome.Expected!, "Test/Obj/A"));
    }

    [Fact]
    public void A_list_the_edit_changed_is_stated_whole_so_it_does_not_depend_on_where_Riots_next_patch_puts_an_element()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1", "g2" }));
        byte[] e = Bin(Obj("Test/Obj/A", new[] { "g1", "g2", "mine" }));

        var outcome = Plan(b, e);

        Assert.Equal(GameDataEditKind.Declared, outcome.Kind);
        using var module = JsonDocument.Parse(outcome.ModuleText!);
        var tags = module.RootElement.GetProperty("edits")[0].GetProperty("Test/Obj/A").GetProperty("tags");
        Assert.Equal(new[] { "g1", "g2", "mine" }, tags.EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal(new[] { "g1", "g2", "mine" }, TagsOf(outcome.Expected!, "Test/Obj/A"));
    }

    [Fact]
    public void A_bin_equal_to_the_one_the_GameData_makes_has_nothing_to_declare()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }));
        byte[] sameData = Bin(Obj("Test/Obj/A", new[] { "g1" }));

        var outcome = Plan(b, sameData);

        Assert.Equal(GameDataEditKind.Unchanged, outcome.Kind);
        Assert.Null(outcome.ModuleText);
        Assert.Null(outcome.Expected);
    }

    [Fact]
    public void The_bytes_of_the_two_bins_need_not_agree_only_the_data()
    {
        // the engine writes version 3; the editors write their own form. A version 2 file of the same data is the same bin.
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }));
        byte[] sameDataOtherVersion = V2(b);

        Assert.Equal(GameDataEditKind.Unchanged, Plan(b, sameDataOtherVersion).Kind);
    }

    [Fact]
    public void An_object_the_edit_added_needs_a_class_the_schema_knows_and_is_refused_for_want_of_one_with_that_said()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }));
        byte[] e = Bin(Obj("Test/Obj/A", new[] { "g1" }), Obj("Test/Obj/New", new[] { "n" }, "added"));

        var silent = Plan(b, e);                                                  // a schema that says nothing: LTK cannot construct a class it does not know
        var knows = Plan(b, e, new TestClassSchema());

        Assert.Equal(GameDataEditKind.Refused, silent.Kind);
        Assert.True(silent.NeedsSchema);
        Assert.Contains("LTK would skip", silent.Reason);
        Assert.Contains("UnknownClass", silent.Reason);
        Assert.Equal(GameDataEditKind.Declared, knows.Kind);
        Assert.Equal(1, knows.ObjectsAdded);
        using var module = JsonDocument.Parse(knows.ModuleText!);
        var created = module.RootElement.GetProperty("edits")[0].GetProperty("objects").GetProperty("Test/Obj/New");
        Assert.Equal("TestClass", created.GetProperty("class").GetString());
        Assert.NotNull(PropCodec.ReadObject(knows.Expected!, H("Test/Obj/New")));
    }

    [Fact]
    public void An_object_the_edit_removed_is_a_removal_and_the_applied_bin_lacks_it()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }), Obj("Test/Obj/B", new[] { "b" }));
        byte[] e = Bin(Obj("Test/Obj/A", new[] { "g1" }));

        var outcome = Plan(b, e);

        Assert.Equal(GameDataEditKind.Declared, outcome.Kind);
        Assert.Equal(1, outcome.ObjectsRemoved);
        using var module = JsonDocument.Parse(outcome.ModuleText!);
        Assert.True(module.RootElement.GetProperty("edits")[0].GetProperty("objects").GetProperty("Test/Obj/B").GetProperty("remove").GetBoolean());
        Assert.Null(PropCodec.ReadObject(outcome.Expected!, H("Test/Obj/B")));
        Assert.NotNull(PropCodec.ReadObject(outcome.Expected!, H("Test/Obj/A")));
    }

    [Fact]
    public void A_dependency_the_edit_added_or_dropped_is_a_link_edit()
    {
        byte[] b = Bin(new[] { "DATA/Keep.bin", "DATA/Drop.bin" }, Obj("Test/Obj/A", new[] { "g1" }));
        byte[] e = Bin(new[] { "DATA/Keep.bin", "DATA/New.bin" }, Obj("Test/Obj/A", new[] { "g1" }));

        var outcome = Plan(b, e);

        Assert.Equal(GameDataEditKind.Declared, outcome.Kind);
        Assert.Equal(2, outcome.LinksChanged);
        using var module = JsonDocument.Parse(outcome.ModuleText!);
        var edit = module.RootElement.GetProperty("edits")[0];
        Assert.Equal(new[] { "DATA/New.bin" }, edit.GetProperty("links").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { "DATA/Drop.bin" }, edit.GetProperty("-links").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    // ===================================================== what is refused

    [Fact]
    public void A_property_the_edit_removed_is_refused_because_no_declaration_removes_a_property()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }, "name", count: 3));
        var gone = Obj("Test/Obj/A", new[] { "g1" }, "name", count: 3);
        gone.Properties.Remove(H("count"));

        var outcome = Plan(b, Bin(gone));

        Assert.Equal(GameDataEditKind.Refused, outcome.Kind);
        Assert.Contains("no declaration removes a property", outcome.Reason);
        Assert.Contains("count", outcome.Reason);
        Assert.Null(outcome.ModuleText);
    }

    [Fact]
    public void An_object_whose_class_changed_is_refused()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }));
        var other = new PropObject(H("Test/Obj/A"), H("OtherClass"));
        other.Properties.Set(H("tags"), Strings("g1"));
        other.Properties.Set(H("count"), new PropInt(PropKind.I32, 1));

        var outcome = Plan(b, Bin(other));

        Assert.Equal(GameDataEditKind.Refused, outcome.Kind);
        Assert.Contains("changed class", outcome.Reason);
    }

    [Fact]
    public void An_embedded_struct_whose_class_changed_is_refused_because_an_embed_pin_cannot_change_class()
    {
        var before = Obj("Test/Obj/A", new[] { "g1" });
        before.Properties.Set(H("inner"), new PropStruct(PropKind.Embedded, H("InnerOne"), new OrderedMap<PropValue>()));
        var after = Obj("Test/Obj/A", new[] { "g1" });
        after.Properties.Set(H("inner"), new PropStruct(PropKind.Embedded, H("InnerTwo"), new OrderedMap<PropValue>()));

        var outcome = Plan(Bin(before), Bin(after));

        Assert.Equal(GameDataEditKind.Refused, outcome.Kind);
        Assert.Contains("embed pin cannot change class", outcome.Reason);
    }

    [Fact]
    public void A_property_the_bin_does_not_hold_is_refused_where_the_schema_cannot_type_it_and_kept_where_it_can()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }));
        var added = Obj("Test/Obj/A", new[] { "g1" });
        added.Properties.Set(H("extra"), new PropInt(PropKind.I32, 9));

        var silent = Plan(b, Bin(added));
        var knows = Plan(b, Bin(added), new TestClassSchema());

        Assert.Equal(GameDataEditKind.Refused, silent.Kind);                      // LTK skips an edit it cannot type, and says nothing the player sees
        Assert.True(silent.NeedsSchema);
        Assert.Contains("Untypable", silent.Reason);
        Assert.Equal(GameDataEditKind.Declared, knows.Kind);
        Assert.NotNull(knows.Expected);
    }

    [Fact]
    public void A_bin_that_is_a_PTCH_or_is_not_a_bin_at_all_is_refused()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }));

        var patch = Plan(Ptch(new[] { Obj("Test/Obj/A", new[] { "p" }) }), b);
        var patched = Plan(b, Ptch(new[] { Obj("Test/Obj/A", new[] { "p" }) }));
        var garbage = Plan(b, new byte[] { 1, 2, 3, 4, 5 });

        Assert.Equal(GameDataEditKind.Refused, patch.Kind);
        Assert.Contains("PTCH", patch.Reason);
        Assert.Equal(GameDataEditKind.Refused, patched.Kind);
        Assert.Contains("PTCH", patched.Reason);
        Assert.Equal(GameDataEditKind.Refused, garbage.Kind);
        Assert.Contains("did not parse", garbage.Reason);
    }

    [Fact]
    public void A_refusal_is_the_whole_answer_no_module_no_expected_bytes_so_nothing_can_be_kept_by_mistake()
    {
        byte[] b = Bin(Obj("Test/Obj/A", new[] { "g1" }, "name"));
        var gone = Obj("Test/Obj/A", new[] { "g1" });

        var outcome = Plan(b, Bin(gone));

        Assert.True(outcome.IsRefused);
        Assert.Null(outcome.ModuleText);
        Assert.Null(outcome.Expected);
        Assert.Equal(0, outcome.Properties);
    }

    // ===================================================== recomputed against the current bin

    [Fact]
    public void Saving_again_declares_the_difference_from_the_bin_as_it_is_now_not_from_the_one_of_the_first_save()
    {
        byte[] first = Bin(Obj("Test/Obj/A", new[] { "g1" }, count: 1));
        byte[] edited = Bin(Obj("Test/Obj/A", new[] { "g1" }, count: 5));
        var one = Plan(first, edited);

        // Riot's next patch changes the tags in the game's bin; the edit of the count is what is still stated
        byte[] patched = Bin(Obj("Test/Obj/A", new[] { "g1", "patched" }, count: 1));
        var applied = GameDataApplier.Apply(patched, GameDataDocument.Parse("{\"version\":1,\"modules\":[" + one.ModuleText + "]}").Modules[0].Edits!,
            _ => GameDataBytesRead.Failed("none"), _ => GameDataEntryRead.None, NoSchema.Instance);

        Assert.Equal(new[] { "g1", "patched" }, TagsOf(applied.Bytes, "Test/Obj/A"));           // the patch's change survives
        Assert.Equal(5, CountOf(applied.Bytes, "Test/Obj/A"));                                 // and so does the edit
    }

    // ===================================================== BinTreeEquivalence

    [Fact]
    public void Equivalence_ignores_the_order_of_objects_and_of_fields_and_the_case_of_a_dependency_and_finds_the_first_real_difference()
    {
        var a = Obj("Test/Obj/A", new[] { "x" }, "n");
        var b = Obj("Test/Obj/B", new[] { "y" });
        var reordered = new PropObject(a.PathHash, TestClass);
        reordered.Properties.Set(H("count"), new PropInt(PropKind.I32, 1));
        reordered.Properties.Set(H("name"), new PropString("n"));
        reordered.Properties.Set(H("tags"), Strings("x"));

        var left = SafeBinTree.Parse(Bin(new[] { "DATA/X.bin" }, a, b));
        var right = SafeBinTree.Parse(Bin(new[] { "data/x.bin" }, b, reordered));

        Assert.Null(BinTreeEquivalence.FirstDifference(left, right, Names));

        var different = SafeBinTree.Parse(Bin(new[] { "data/x.bin" }, b, Obj("Test/Obj/A", new[] { "x" }, "m")));
        Assert.Contains("Test/Obj/A.name differs", BinTreeEquivalence.FirstDifference(left, different, Names));
        var missing = SafeBinTree.Parse(Bin(new[] { "data/x.bin" }, b));
        Assert.Contains("Test/Obj/A is missing", BinTreeEquivalence.FirstDifference(left, missing, Names));
        var moreLinks = SafeBinTree.Parse(Bin(new[] { "data/x.bin", "data/y.bin" }, a, b));
        Assert.Contains("dependency data/y.bin is extra", BinTreeEquivalence.FirstDifference(left, moreLinks, Names));
    }
}
