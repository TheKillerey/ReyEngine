using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M817: the engine against a package nobody trusts. Every test here is a bin, an edit or a document that is small to write and costs the
/// engine a great deal if it is let: a tree deepened past what the stack holds by many edits that each look harmless, a few bytes that ask for
/// gigabytes, work with no end, a length that does not fit its field. The ground rule of each guard is that it changes what the engine does for
/// input like this and for nothing else: <c>LtkGameDataOracleTests</c> and the corpora of the Rust crate hold the rest of the behaviour.
/// None of the tests needs the game, the Rust crate or more than a moment.
/// </summary>
public class LtkGameDataHostileInputTests
{
    private const byte KindU8 = 3, KindString = 16, KindContainer = 0x80, KindEmbedded = 0x83, KindMap = 0x86;
    private const uint Class = 0x0C1A55, ObjectPath = 0xAAAA0001;

    // What the algorithmic tests below may allocate. Measured (16.4 MB, 4.5 MB, 8.2 MB and 0.6 MB) and given a margin of ten or more; the way each
    // of them must not be done is a gigabyte or more, which is what separates them. No clock is asked.
    private const long LinkAllocLimit = 160_000_000, DeleteAllocLimit = 60_000_000, RecordAllocLimit = 100_000_000, MapAllocLimit = 20_000_000;

    // What deleting 4,000 of 12,000 objects, newest first, costs in work units: 36,000 for the decode, the removals and the write, and 248,000 for the shift
    // of the removal list (measured: 284,000 by an override file and 280,000 by name). Without the shift's charge it is the 36,000.
    private const long WorkNewestFirstLow = 250_000, WorkNewestFirstHigh = 400_000;

    private static uint H(string name) => LtkHash.Fnv1aLower(name);

    // ================================================================================================ bytes by hand

    /// <summary>The little-endian bytes of a bin, written without the engine's own writer: so that a bin the writer refuses to write can still be read.</summary>
    private static class Raw
    {
        public static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
        public static byte[] U32(uint v) => BitConverter.GetBytes(v);
        public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        public static byte[] Property(uint name, byte kind, byte[] value) => Cat(U32(name), new[] { kind }, value);

        /// <summary>A struct value: its class, the size of what follows, the count and the properties.</summary>
        public static byte[] Struct(uint cls, params byte[][] properties)
        {
            byte[] body = Cat(properties);
            return Cat(U32(cls), U32((uint)(2 + body.Length)), U16(properties.Length), body);
        }

        public static byte[] Object(uint path, params byte[][] properties)
        {
            byte[] body = Cat(properties);
            return Cat(U32((uint)(4 + 2 + body.Length)), U32(path), U16(properties.Length), body);
        }

        /// <summary>A PROP version 3 with one object.</summary>
        public static byte[] Prop(params byte[][] properties) => Cat(U32(0x504F5250), U32(3), U32(0), U32(1), U32(Class), Object(ObjectPath, properties));

        /// <summary>A container value: its item kind, the size of what follows, the count and the items.</summary>
        public static byte[] List(byte itemKind, uint count, byte[] items) => Cat(new[] { itemKind }, U32((uint)(4 + items.Length)), U32(count), items);

        /// <summary>A chain of <paramref name="structs"/> nested embeds, each holding the next as <c>n</c>, the last holding a leaf.</summary>
        public static byte[] Chain(int structs)
        {
            byte[] value = Struct(Class, Property(H("x"), KindU8, new byte[] { 1 }));
            for (int i = 1; i < structs; i++) value = Struct(Class, Property(H("n"), KindEmbedded, value));
            return value;
        }
    }

    /// <summary>The same chain as a value of the model, built from the inside out so that no recursion is needed to make it.</summary>
    private static PropStruct ChainValue(int structs, uint cls = Class, ulong leaf = 1)
    {
        var innermost = new OrderedMap<PropValue>();
        innermost.Set(H("x"), new PropInt(PropKind.U8, leaf));
        var s = new PropStruct(PropKind.Embedded, cls, innermost);
        for (int i = 1; i < structs; i++)
        {
            var props = new OrderedMap<PropValue>();
            props.Set(H("n"), s);
            s = new PropStruct(PropKind.Embedded, cls, props);
        }
        return s;
    }

    private static byte[] PropOf(Action<PropObject> fill)
    {
        var obj = new PropObject(ObjectPath, Class);
        fill(obj);
        var bin = new PropBin();
        bin.Objects.Set(ObjectPath, obj);
        return PropCodec.WriteProp(bin);
    }

    private static GameDataApplyResult Apply(byte[] baseBytes, GameDataEdit edit, IGameDataSchema? schema = null, GameDataLimits? limits = null,
        CancellationToken token = default, ReadGameEntry? readEntry = null, ReadOverrideFile? readOverride = null) =>
        GameDataApplier.Apply(baseBytes, new[] { edit }, readOverride ?? (_ => GameDataBytesRead.Failed("none")), readEntry ?? (_ => GameDataEntryRead.None),
            schema ?? NoSchema.Instance, limits, token);

    private static GameDataEdit Edit(string key, string json)
    {
        var edit = new GameDataEdit();
        edit.Entries.Add(new(EntryName.Create("0x" + ObjectPath.ToString("x8")), new List<PropertyEdit> { PropertyEdit.Parse(key, GameDataJson.Parse(json)) }));
        return edit;
    }

    /// <summary>
    /// The bytes the calling thread allocates while <paramref name="work"/> runs. A test of what an algorithm costs asks this and not a clock: it
    /// counts what the algorithm does with memory, which a machine under load does not change as it changes a stopwatch. An index or a set rebuilt for
    /// each of thousands of edits is gigabytes of it, and the same work done once is a few megabytes.
    /// </summary>
    private static long Allocated(Action work)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Knows every class and types every field as an embed, so a pin can name any class and nest as it likes.</summary>
    private sealed class EmbedEverywhere : IGameDataSchema
    {
        public int Asked;
        public Action? OnAsked;

        public GameDataShape? Expected(uint cls, uint field)
        {
            Asked++;
            OnAsked?.Invoke();
            return new GameDataShape(PropKind.Embedded);
        }

        public bool HasClass(uint cls) => true;
    }

    // ================================================================================================ depth: what is read

    [Theory]
    [InlineData(256, true)]    // 256 structs: the last at depth 255, its leaf at 256
    [InlineData(257, false)]   // the 257th would be a container at depth 256
    public void A_value_nests_at_most_256_containers_deep_when_it_is_read(int structs, bool accepted)
    {
        byte[] bytes = Raw.Prop(Raw.Property(H("a"), KindEmbedded, Raw.Chain(structs)));
        if (accepted)
        {
            var bin = PropCodec.ReadProp(bytes);
            Assert.Equal(1, bin.Objects.Count);
            Assert.Equal(bytes.Length, PropCodec.WriteProp(bin).Length);   // and what was read is written back
        }
        else
        {
            var error = Assert.Throws<PropDecodeException>(() => PropCodec.ReadProp(bytes));
            Assert.Contains("too deep", error.Message);
        }
    }

    [Fact]
    public void Every_reader_of_the_codec_refuses_a_bin_with_the_one_exception_and_nothing_else()
    {
        byte[] deep = Raw.Prop(Raw.Property(H("a"), KindEmbedded, Raw.Chain(300)));
        Assert.Throws<PropDecodeException>(() => PropCodec.ReadProp(deep));
        Assert.Throws<PropDecodeException>(() => PropCodec.Mount(deep).Read());
        Assert.Throws<PropDecodeException>(() => PropCodec.ReadObject(deep, ObjectPath));
        Assert.Throws<PropDecodeException>(() => PropCodec.Mount(deep).ReadObject(ObjectPath));

        // a PTCH whose one record carries the same value
        byte[] record = Raw.Cat(new[] { KindEmbedded }, BitConverter.GetBytes((ushort)1), "a"u8.ToArray(), Raw.Chain(300));
        byte[] ptch = Raw.Cat(Raw.U32(0x48435450), Raw.U32(1), Raw.U32(0), Raw.U32(0x504F5250), Raw.U32(3), Raw.U32(0), Raw.U32(0), Raw.U32(1),
                              Raw.U32(ObjectPath), Raw.U32((uint)record.Length), record);
        Assert.Throws<PropDecodeException>(() => PropCodec.ReadPtch(ptch));

        // bytes that are not a bin at all, cut and flipped every which way: whatever the reader does, it does with that one type
        var random = new Random(817);
        byte[] good = PropOf(o => { o.Properties.Set(H("a"), ChainValue(5)); o.Properties.Set(H("s"), new PropString("text")); });
        for (int round = 0; round < 400; round++)
        {
            byte[] bytes = (byte[])good.Clone();
            for (int flips = random.Next(1, 5); flips > 0; flips--) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            if (round % 3 == 0) bytes = bytes[..random.Next(bytes.Length)];
            try { PropCodec.ReadProp(bytes); } catch (PropDecodeException) { }
            try { PropCodec.ReadObject(bytes, ObjectPath); } catch (PropDecodeException) { }
            try { PropCodec.ReadPtch(bytes); } catch (PropDecodeException) { }
        }
    }

    // ================================================================================================ depth: what an edit makes

    [Fact]
    public void A_ptch_override_cannot_deepen_the_tree_past_what_the_codec_reads()
    {
        // each record inserts a 256-deep chain under the previous record's leaf: forty of them make a tree ten thousand deep, and the writer
        // that recurses into it ends the process. The second record is refused instead.
        byte[] baseBytes = PropOf(o => o.Properties.Set(H("z"), new PropInt(PropKind.U8, 7)));
        var patch = new PtchBin();
        string path = "a";
        for (int i = 0; i < 40; i++)
        {
            patch.Patches.Add(new PatchRecord(ObjectPath, PropertyPath.Parse(path), ChainValue(256)));
            path += string.Concat(Enumerable.Repeat(".n", 255)) + ".b";
        }
        byte[] ptch = PropCodec.WritePtch(patch);

        var edit = new GameDataEdit();
        edit.Overrides.Add(new OverridePath("o.ptch"));
        var error = Assert.Throws<GameDataException>(() => Apply(baseBytes, edit, readOverride: _ => GameDataBytesRead.Found(ptch)));
        Assert.Equal(GameDataErrorKind.Bin, error.Kind);
        Assert.Contains("a value inserted", error.Message);   // refused where it is inserted, not where the writer would have met it
        Assert.Contains("inserted 256 containers down", error.Message);   // and at the second record, the first that does not fit: the first 256 levels went in
    }

    [Fact]
    public void Edits_cannot_deepen_a_recursive_field_past_what_the_codec_reads()
    {
        // a = embed { n = embed {} }; edit k replaces the innermost embed with a 40-level chain: every edit is valid by itself
        var inner = new PropStruct(PropKind.Embedded, H("C"), new OrderedMap<PropValue>());
        var outer = new OrderedMap<PropValue>();
        outer.Set(H("n"), inner);
        byte[] baseBytes = PropOf(o => o.Properties.Set(H("a"), new PropStruct(PropKind.Embedded, H("C"), outer)));

        string chain = "{\"embed\": {\"class\": \"C\"}}";
        for (int level = 1; level < 40; level++) chain = "{\"embed\": {\"class\": \"C\", \"set\": {\"n\": " + chain + "}}}";
        var edits = new List<GameDataEdit>();
        for (int i = 0; i < 40; i++) edits.Add(Edit("a" + string.Concat(Enumerable.Repeat(".n", 1 + 39 * i)), chain));

        var schema = new EmbedEverywhere();
        var error = Assert.Throws<GameDataException>(() => GameDataApplier.Apply(baseBytes, edits, _ => GameDataBytesRead.Failed("none"), _ => GameDataEntryRead.None, schema));
        Assert.Equal(GameDataErrorKind.Bin, error.Kind);
        Assert.Contains("a value inserted", error.Message);
    }

    [Fact]
    public void An_edit_that_stays_inside_the_depth_still_lands()
    {
        // the refusal is for the depth only: a chain that fits is inserted, in a bin that already nests deeply
        byte[] baseBytes = PropOf(o => o.Properties.Set(H("a"), ChainValue(100)));
        string path = "a" + string.Concat(Enumerable.Repeat(".n", 99)) + ".b";
        var edit = new GameDataEdit();
        edit.Entries.Add(new(EntryName.Create("0x" + ObjectPath.ToString("x8")), new List<PropertyEdit> { PropertyEdit.Parse(path, GameDataJson.Parse("{\"embed\": {\"class\": \"C\"}}")) }));
        var result = Apply(baseBytes, edit, new EmbedEverywhere());
        Assert.Equal(1, result.Applied.Properties);
    }

    [Fact]
    public void Nothing_that_recurses_over_a_tree_can_be_made_to_overflow_the_stack()
    {
        // a hand-built tree of twenty thousand containers: past the depth the codec reads, which is what each of these refuses at, long before the stack
        var deep = ChainValue(20_000);
        Assert.Throws<GameDataException>(() => deep.Clone());
        Assert.Throws<GameDataException>(() => PropValue.Equal(deep, ChainValue(20_000)));
        var obj = new PropObject(ObjectPath, Class);
        obj.Properties.Set(H("a"), deep);
        var bin = new PropBin();
        bin.Objects.Set(ObjectPath, obj);
        var error = Assert.Throws<GameDataException>(() => PropCodec.WriteProp(bin));
        Assert.Equal(GameDataErrorKind.Bin, error.Kind);
        Assert.Throws<GameDataException>(() => obj.Clone());

        // and a game entry a host hands back that deep is refused by a copy of it, whatever edit asked for the copy
        var edit = Edit("b", "{\"ref\": \"0x" + ObjectPath.ToString("x8") + ":a\"}");
        byte[] baseBytes = PropOf(o => o.Properties.Set(H("b"), ChainValue(3)));
        var schema = new EmbedEverywhere();
        Assert.Throws<GameDataException>(() => Apply(baseBytes, edit, schema, readEntry: _ => GameDataEntryRead.Found(obj)));
    }

    [Fact]
    public void A_stack_with_too_little_left_is_a_refusal_and_not_a_crash()
    {
        // the same refusal by the other guard: a thread whose stack is almost gone while the depth is still within the limit
        byte[] bytes = Raw.Prop(Raw.Property(H("a"), KindEmbedded, Raw.Chain(250)));
        object? outcome = null;
        var thread = new Thread(() =>
        {
            try { outcome = PropCodec.ReadProp(bytes); }
            catch (Exception e) { outcome = e; }
        }, 192 * 1024);
        thread.Start();
        thread.Join();
        // 250 levels fit the limit and not this stack (they decode on 512 KB): the runtime wants 128 KB left for the next frame, and the codec asks it
        var error = Assert.IsType<PropDecodeException>(outcome);
        Assert.Contains("for the stack", error.Message);
    }

    // ================================================================================================ entries that take no bytes

    [Fact]
    public void A_decode_may_declare_65536_entries_of_a_kind_that_takes_no_bytes_in_all()
    {
        byte[] ok = Raw.Prop(Raw.Property(1, KindContainer, Raw.List(0, 1u << 16, Array.Empty<byte>())));
        Assert.Equal(1, PropCodec.ReadProp(ok).Objects.Count);

        byte[] over = Raw.Prop(Raw.Property(1, KindContainer, Raw.List(0, (1u << 16) + 1, Array.Empty<byte>())));
        var error = Assert.Throws<PropDecodeException>(() => PropCodec.ReadProp(over));
        Assert.Contains("takes no bytes", error.Message);
    }

    [Fact]
    public void The_budget_is_for_the_whole_bin_and_a_map_of_nothing_spends_it_too()
    {
        // two lists of 40,000 and a map of 30,000 pairs: each under the budget, all of them over it
        byte[] list = Raw.List(0, 40_000, Array.Empty<byte>());
        byte[] two = Raw.Prop(Raw.Property(1, KindContainer, list), Raw.Property(2, KindContainer, list));
        Assert.Throws<PropDecodeException>(() => PropCodec.ReadProp(two));

        byte[] map = Raw.Cat(new byte[] { 0, 0 }, Raw.U32(4), Raw.U32(30_000));
        byte[] mixed = Raw.Prop(Raw.Property(1, KindContainer, list), Raw.Property(2, KindMap, map));
        Assert.Throws<PropDecodeException>(() => PropCodec.ReadProp(mixed));
        byte[] justTheMap = Raw.Prop(Raw.Property(2, KindMap, map));
        Assert.Equal(1, PropCodec.ReadProp(justTheMap).Objects.Count);
    }

    [Fact]
    public void A_few_bytes_cannot_be_made_to_hold_gigabytes()
    {
        // a hundred containers that each declare 1<<20 entries of nothing: 1,400 bytes that took 800 MB before the budget
        var properties = Enumerable.Range(0, 100).Select(i => Raw.Property((uint)(0x1000 + i), KindContainer, Raw.List(0, 1u << 20, Array.Empty<byte>()))).ToArray();
        byte[] bytes = Raw.Prop(properties);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PropDecodeException>(() => PropCodec.ReadProp(bytes));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 * 1024 * 1024, $"{allocated} bytes allocated");
    }

    // ================================================================================================ reservations

    [Fact]
    public void A_count_is_not_reserved_for_beyond_the_bytes_that_back_it()
    {
        // a struct that declares 65,535 properties and holds none
        byte[] bytes = Raw.Prop(Raw.Property(H("a"), KindEmbedded, Raw.Cat(Raw.U32(Class), Raw.U32(2), Raw.U16(65_535))));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PropDecodeException>(() => PropCodec.ReadProp(bytes));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 200_000, $"{allocated} bytes allocated");
    }

    [Fact]
    public void A_list_that_declares_more_than_it_holds_reserves_at_most_a_megabyte_ahead_of_it()
    {
        // 1,000,000 strings declared, a megabyte of bytes behind them, and the first of them is not UTF-8: nothing is read, so little may be reserved
        byte[] items = Raw.Cat(Raw.U16(2), new byte[] { 0xFF, 0xFF }, new byte[1_000_000]);
        byte[] bytes = Raw.Prop(Raw.Property(1, KindContainer, Raw.List(KindString, 1_000_000, items)));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PropDecodeException>(() => PropCodec.ReadProp(bytes));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_600_000, $"{allocated} bytes allocated");   // the megabyte of the reservation, and not the eight it was
    }

    [Fact]
    public void An_ordered_map_asked_for_a_huge_capacity_reserves_a_megabyte()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var map = new OrderedMap<int>(int.MaxValue);
        map.Set(1, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 2 * 1024 * 1024, $"{allocated} bytes allocated");
    }

    // ================================================================================================ json

    private static string Nested(int depth) => new string('[', depth) + new string(']', depth);

    [Fact]
    public void A_document_may_nest_127_containers_and_not_128_whatever_limit_it_is_asked_to_read_with()
    {
        // serde_json starts a deserializer at 128 and each container takes one: 127 deep is the most a document read on its own may be
        GameDataJson.Parse(Nested(127));
        var error = Assert.Throws<GameDataJsonException>(() => GameDataJson.Parse(Nested(128)));
        Assert.Contains("recursion limit exceeded", error.Message);

        // read as the GameData of a .fantome's info.json, four containers in: 123 is the most
        GameDataJson.Parse(Nested(123), 124);
        Assert.Throws<GameDataJsonException>(() => GameDataJson.Parse(Nested(124), 124));

        // a limit above 128 is 128 (and a document ten thousand deep is refused, not recursed into)
        GameDataJson.Parse(Nested(127), int.MaxValue);
        Assert.Throws<GameDataJsonException>(() => GameDataJson.Parse(Nested(128), 100_000));
        Assert.Throws<GameDataJsonException>(() => GameDataJson.Parse(Nested(10_000), int.MaxValue));
        Assert.Throws<GameDataException>(() => GameDataDocument.Parse(Nested(10_000), int.MaxValue));
    }

    [Fact]
    public void A_literal_built_by_hand_and_nested_far_past_json_is_refused_by_the_stack_guard()
    {
        GameDataLiteral literal = GdNull.Instance;
        for (int i = 0; i < 200_000; i++) literal = new GdList(new List<GameDataLiteral> { literal });

        // a document whose one edit holds it: the pins are checked as the document is read, and the literal is not recursed into past the stack
        GdMapping Mapping(params (string Key, GameDataLiteral Value)[] entries) => new(entries.Select(e => new KeyValuePair<string, GameDataLiteral>(e.Key, e.Value)).ToList());
        var origin = Mapping(("manifest", new GdString("m")), ("source", GdNull.Instance), ("module", new GdInteger(0)));
        var edit = Mapping(("Test/Obj1", Mapping(("k", literal))));
        var module = Mapping(("target", new GdString("t")), ("edits", new GdList(new List<GameDataLiteral> { edit })), ("origin", origin));
        var root = Mapping(("version", new GdInteger(1)), ("modules", new GdList(new List<GameDataLiteral> { module })));
        var error = Assert.Throws<GameDataException>(() => GameDataDocument.FromLiteral(root));
        Assert.Equal(GameDataErrorKind.Syntax, error.Kind);

        // the method on the literal itself says it with the exception the runtime does
        var held = literal;
        Assert.Throws<InsufficientExecutionStackException>(() => held.CheckPins());
    }

    // ================================================================================================ what a 16-bit field cannot hold

    [Fact]
    public void A_string_that_does_not_fit_its_length_is_refused_where_the_crate_writes_a_bin_nothing_reads()
    {
        byte[] baseBytes = PropOf(o => o.Properties.Set(H("s"), new PropString("short")));
        string big = new string('x', 70_000);
        var error = Assert.Throws<GameDataException>(() => Apply(baseBytes, Edit("s", "{\"string\": \"" + big + "\"}")));
        Assert.Equal(GameDataErrorKind.Bin, error.Kind);
        Assert.Contains("16-bit length", error.Message);

        // 65,535 bytes is the most a length holds, and it is written
        var fits = Apply(baseBytes, Edit("s", "{\"string\": \"" + new string('x', 65_535) + "\"}"));
        Assert.Equal(65_535, ((PropString)PropCodec.ReadProp(fits.Bytes).Objects.ValueAt(0).Properties.ValueAt(0)).Value.Length);
    }

    [Fact]
    public void More_properties_than_a_count_holds_are_refused_by_the_writer()
    {
        var props = new OrderedMap<PropValue>();
        for (uint i = 0; i < 65_536; i++) props.Set(i, new PropInt(PropKind.U8, 1));
        var obj = new PropObject(ObjectPath, Class, props);
        var bin = new PropBin();
        bin.Objects.Set(ObjectPath, obj);
        var error = Assert.Throws<GameDataException>(() => PropCodec.WriteProp(bin));
        Assert.Equal(GameDataErrorKind.Bin, error.Kind);
        Assert.Contains("16-bit count", error.Message);

        props.Remove(0);   // 65,535 is the most, and is written
        Assert.NotEmpty(PropCodec.WriteProp(bin));
    }

    [Fact]
    public void A_property_path_longer_than_a_record_can_hold_is_refused_where_the_crate_panics()
    {
        var left = PropertyPath.Parse(new string('a', 40_000));
        var right = PropertyPath.Parse(new string('b', 30_000));
        var error = Assert.Throws<GameDataException>(() => PropertyPath.Join(left, right));
        Assert.Equal(GameDataErrorKind.Bin, error.Kind);
        Assert.Equal(40_000 + 1 + 25_000, PropertyPath.Join(left, PropertyPath.Parse(new string('b', 25_000))).Text.Length);

        // through an edit: a block under a struct whose inner key joins to more than 65,535 bytes
        string name = new string('a', 40_000);
        byte[] baseBytes = PropOf(o => o.Properties.Set(H(name), new PropStruct(PropKind.Embedded, Class, new OrderedMap<PropValue>())));
        var edit = Edit(name, "{\"" + new string('b', 30_000) + "\": 1}");
        Assert.Throws<GameDataException>(() => Apply(baseBytes, edit));
    }

    [Fact]
    public void A_block_of_bad_keys_costs_no_exception_a_key()
    {
        var struct0 = new PropStruct(PropKind.Embedded, Class, new OrderedMap<PropValue>());
        byte[] baseBytes = PropOf(o => o.Properties.Set(H("a"), struct0));
        var entries = new List<KeyValuePair<string, GameDataLiteral>>();
        for (int i = 0; i < 20_000; i++) entries.Add(new($"bad[{i}", GdNull.Instance));   // an unbalanced bracket: not a path
        var edit = new GameDataEdit();
        edit.Entries.Add(new(EntryName.Create("0x" + ObjectPath.ToString("x8")), new List<PropertyEdit> { new(PropertyPath.Parse("a"), Sign.Set, new GdMapping(entries)) }));

        int thrown = 0;
        int me = System.Environment.CurrentManagedThreadId;
        void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) { if (System.Environment.CurrentManagedThreadId == me) thrown++; }
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            var result = Apply(baseBytes, edit);
            Assert.Equal(20_000, result.Diagnostics.Count(d => d.Property?.Reason == PropertySkipReason.InvalidPath));
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Count; }
        Assert.Equal(0, thrown);
    }

    // ================================================================================================ limits and cancellation

    private static byte[] ManyProperties(int count) => PropOf(o =>
    {
        for (uint i = 0; i < count; i++) o.Properties.Set(i, new PropInt(PropKind.U32, i));
    });

    [Fact]
    public void The_defaults_are_the_ones_the_installed_game_stays_far_inside()
    {
        Assert.Equal(1L << 28, GameDataLimits.Default.MaxWork);
        Assert.Equal(256L << 20, GameDataLimits.Default.MaxOutputBytes);
        Assert.Equal(32L << 20, GameDataLimits.Default.MaxOverrideBytes);
        // an application costs about 1.5 units a value: the largest bin of the game (4.2 million values, 6.3 million units) is forty times inside the work default
        byte[] bytes = ManyProperties(30_000);
        var result = Apply(bytes, Edit("0x00000005", "{\"u32\": 9}"));
        Assert.InRange(result.WorkUsed, 30_000, 4 * 30_000);
    }

    [Fact]
    public void An_application_that_does_more_work_than_it_may_ends_with_its_own_kind_of_error()
    {
        byte[] bytes = ManyProperties(5_000);
        var error = Assert.Throws<GameDataException>(() => Apply(bytes, Edit("0x00000005", "{\"u32\": 9}"), limits: new GameDataLimits { MaxWork = 1_000 }));
        Assert.Equal(GameDataErrorKind.LimitExceeded, error.Kind);
        Assert.NotEqual(GameDataErrorKind.Bin, error.Kind);
        Assert.Contains("work limit", error.Message);

        // with room enough it applies, and with none counted at all it applies too
        Assert.Equal(1, Apply(bytes, Edit("0x00000005", "{\"u32\": 9}"), limits: new GameDataLimits { MaxWork = 100_000 }).Applied.Properties);
        Assert.Equal(1, Apply(bytes, Edit("0x00000005", "{\"u32\": 9}"), limits: GameDataLimits.Unlimited).Applied.Properties);
    }

    [Fact]
    public void Output_is_limited_in_what_the_tree_may_grow_to_and_in_what_is_written()
    {
        byte[] bytes = PropOf(o => o.Properties.Set(H("s"), new PropString("x")));
        var tight = new GameDataLimits { MaxOutputBytes = 2_000 };

        // a value inserted that the limit cannot hold is refused before the tree holds it, and the writer holds the same line
        var grown = Assert.Throws<GameDataException>(() => Apply(bytes, Edit("s", "{\"string\": \"" + new string('y', 5_000) + "\"}"), limits: tight));
        Assert.Equal(GameDataErrorKind.LimitExceeded, grown.Kind);
        Assert.Contains("output limit", grown.Message);
        Assert.Equal(1, Apply(bytes, Edit("s", "{\"string\": \"" + new string('y', 500) + "\"}"), limits: tight).Applied.Properties);

        // a target that is over the limit already is refused before anything is read
        var small = new GameDataLimits { MaxOutputBytes = bytes.Length - 1 };
        Assert.Equal(GameDataErrorKind.LimitExceeded, Assert.Throws<GameDataException>(() => Apply(bytes, new GameDataEdit(), limits: small)).Kind);
    }

    [Fact]
    public void A_clone_of_an_object_counts_toward_the_output_before_it_is_made()
    {
        byte[] bytes = PropOf(o => { for (uint i = 0; i < 3_000; i++) o.Properties.Set(i, new PropString(new string('z', 40))); });
        var edit = new GameDataEdit();
        for (uint i = 1; i <= 200; i++)
            edit.Objects.Add(new(EntryName.Create("0x" + (0xBBBB0000u + i).ToString("x8")), new CloneObjectEdit(EntryName.Create("0x" + ObjectPath.ToString("x8")), new List<PropertyEdit>())));
        // 200 copies of a 140 KB object: 28 MB asked of a 2 MB limit, by an edit of a few kilobytes
        var error = Assert.Throws<GameDataException>(() => Apply(bytes, edit, limits: new GameDataLimits { MaxOutputBytes = 2L << 20 }));
        Assert.Equal(GameDataErrorKind.LimitExceeded, error.Kind);
    }

    /// <summary>A PTCH holding one object that holds a list of <paramref name="items"/> single bytes: the densest thing a file can be, forty bytes of tree to each one of file.</summary>
    private static byte[] PtchWithBytes(int items)
    {
        var list = new List<PropValue>(items);
        for (int i = 0; i < items; i++) list.Add(new PropInt(PropKind.U8, (ulong)(i & 0xFF)));
        var props = new OrderedMap<PropValue>();
        props.Set(H("l"), new PropList(PropKind.Container, PropKind.U8, list));
        var patch = new PtchBin();
        patch.Objects.Set(0xBBBB0001, new PropObject(0xBBBB0001, Class, props));
        return PropCodec.WritePtch(patch);
    }

    private static GameDataEdit OverrideEdit(params string[] paths)
    {
        var edit = new GameDataEdit();
        foreach (string path in paths) edit.Overrides.Add(new OverridePath(path));
        return edit;
    }

    [Fact]
    public void An_override_file_over_the_limit_is_refused_before_it_is_decoded()
    {
        byte[] bytes = ManyProperties(10);
        byte[] notAPtch = new byte[100_000];

        // read, it is an invalid override and a diagnostic; the limit turns it away before it is read at all, and as a limit and not as bad bytes
        var open = Apply(bytes, OverrideEdit("o.ptch"), readOverride: _ => GameDataBytesRead.Found(notAPtch));
        Assert.Equal(GameDataDiagnosticKind.OverrideInvalid, Assert.Single(open.Diagnostics).Kind);

        var error = Assert.Throws<GameDataException>(() => Apply(bytes, OverrideEdit("o.ptch"), limits: new GameDataLimits { MaxOverrideBytes = 50_000 },
            readOverride: _ => GameDataBytesRead.Found(notAPtch)));
        Assert.Equal(GameDataErrorKind.LimitExceeded, error.Kind);
        Assert.Contains("override limit", error.Message);
    }

    [Fact]
    public void The_override_limit_is_for_every_file_of_the_application_together()
    {
        byte[] bytes = ManyProperties(10);
        byte[] file = PtchWithBytes(30_000);
        var limits = new GameDataLimits { MaxOverrideBytes = 50_000 };

        Assert.Equal(1, Apply(bytes, OverrideEdit("a.ptch"), limits: limits, readOverride: _ => GameDataBytesRead.Found(file)).Applied.Objects);
        var error = Assert.Throws<GameDataException>(() => Apply(bytes, OverrideEdit("a.ptch", "b.ptch"), limits: limits, readOverride: _ => GameDataBytesRead.Found(file)));
        Assert.Equal(GameDataErrorKind.LimitExceeded, error.Kind);
        Assert.Contains("override limit", error.Message);
    }

    [Fact]
    public void A_ptch_target_is_counted_against_the_override_limit_too()
    {
        byte[] ptch = PtchWithBytes(30_000);
        var error = Assert.Throws<GameDataException>(() => Apply(ptch, new GameDataEdit(), limits: new GameDataLimits { MaxOverrideBytes = 10_000 }));
        Assert.Equal(GameDataErrorKind.LimitExceeded, error.Kind);
        Assert.Contains("override limit", error.Message);

        // a PROP target is the game's own and is not counted: its limit is the output's
        Apply(ManyProperties(5_000), new GameDataEdit(), limits: new GameDataLimits { MaxOverrideBytes = 10 });
        Apply(ptch, new GameDataEdit(), limits: new GameDataLimits { MaxOverrideBytes = 100_000 });
    }

    [Fact]
    public void A_token_that_is_cancelled_ends_the_application_with_its_own_exception()
    {
        byte[] bytes = ManyProperties(100);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Apply(bytes, Edit("0x00000005", "{\"u32\": 9}"), token: cts.Token));
    }

    [Fact]
    public void A_token_cancelled_while_the_host_reads_an_entry_is_seen_when_the_host_returns()
    {
        byte[] bytes = PropOf(o => o.Properties.Set(H("b"), new PropInt(PropKind.U32, 1)));
        using var cts = new CancellationTokenSource();
        var edit = Edit("b", "{\"ref\": \"0x12345678:a\"}");
        Assert.Throws<OperationCanceledException>(() => Apply(bytes, edit, token: cts.Token, readEntry: _ =>
        {
            cts.Cancel();
            return GameDataEntryRead.None;
        }));
    }

    [Fact]
    public void A_token_is_looked_at_inside_the_loops_and_not_only_between_phases()
    {
        // ten thousand edits in one batch to one entry, each asking the schema for its field's type; the token is cancelled at the 500th
        byte[] bytes = PropOf(o => { for (uint i = 0; i < 10_000; i++) o.Properties.Set(i, new PropInt(PropKind.U32, i)); });
        var edit = new GameDataEdit();
        var properties = new List<PropertyEdit>();
        for (int i = 0; i < 10_000; i++) properties.Add(new PropertyEdit(PropertyPath.Parse("0x" + i.ToString("x8")), Sign.Set, new GdInteger(i)));
        edit.Entries.Add(new(EntryName.Create("0x" + ObjectPath.ToString("x8")), properties));

        using var cts = new CancellationTokenSource();
        var schema = new EmbedEverywhere();
        schema.OnAsked = () => { if (schema.Asked == 500) cts.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => Apply(bytes, edit, schema, token: cts.Token));
        Assert.InRange(schema.Asked, 500, 4_000);   // it stopped within the units between two looks, and did not run the ten thousand
    }

    // ================================================================================================ work that grew with the square of the input

    [Fact]
    public void Thousands_of_link_edits_do_not_rebuild_the_set_of_held_links_for_each()
    {
        byte[] bytes = PropOf(o => o.Properties.Set(H("a"), new PropInt(PropKind.U8, 1)));
        var edits = new List<GameDataEdit>();
        for (int i = 0; i < 20_000; i++)
        {
            var edit = new GameDataEdit();
            edit.Links.Add.Add(new LinkPath($"data/links/{i}.bin"));
            edits.Add(edit);
        }
        GameDataApplyResult? result = null;
        long allocated = Allocated(() => result = GameDataApplier.Apply(bytes, edits, _ => GameDataBytesRead.Failed("none"), _ => GameDataEntryRead.None, NoSchema.Instance));
        Assert.Equal(20_000, result!.Applied.LinksAdded);
        // a set rebuilt for each edit is the table of every link held so far, twenty thousand times: gigabytes; held once, the whole application is megabytes
        Assert.True(allocated < LinkAllocLimit, $"{allocated:N0} bytes");
        Assert.True(result.WorkUsed < 40_000, $"{result.WorkUsed:N0} units");   // and a link is a unit of work, not a table's worth: 20,002 measured
    }

    [Fact]
    public void A_ptch_override_that_deletes_thousands_of_objects_removes_them_without_rebuilding_the_index_for_each()
    {
        var bin = new PropBin();
        for (uint i = 0; i < 12_000; i++) bin.Objects.Set(0x1000 + i, new PropObject(0x1000 + i, Class));
        byte[] baseBytes = PropCodec.WriteProp(bin);
        var patch = new PtchBin();
        for (uint i = 0; i < 11_000; i++) patch.Deleted.Add(0x1000 + i);
        byte[] ptch = PropCodec.WritePtch(patch);

        var edit = new GameDataEdit();
        edit.Overrides.Add(new OverridePath("o.ptch"));
        GameDataApplyResult? result = null;
        long allocated = Allocated(() => result = Apply(baseBytes, edit, readOverride: _ => GameDataBytesRead.Found(ptch)));
        Assert.Equal(11_000, result!.Applied.Objects);
        Assert.Equal(1_000, PropCodec.ReadProp(result.Bytes).Objects.Count);
        // an index rebuilt for each removal is the table of the objects left, eleven thousand times: gigabytes
        Assert.True(allocated < DeleteAllocLimit, $"{allocated:N0} bytes");
        Assert.True(result.WorkUsed < 5_000_000, $"{result.WorkUsed:N0} units");   // the shifts of the table are charged, and the whole is a fiftieth of the default budget: 2.3 million measured
    }

    [Fact]
    public void A_ptch_target_takes_thousands_of_records_for_one_object_without_working_out_every_held_path_for_each()
    {
        // the game's object holds 6,000 properties and the edit sets each of them: every one is a record of the PTCH, and each new record asks which held ones it covers
        var game = new PropObject(0x77, Class);
        for (int i = 0; i < 6_000; i++) game.Properties.Set(H($"p{i}"), new PropInt(PropKind.U32, (uint)i));
        var edit = new GameDataEdit();
        var properties = new List<PropertyEdit>();
        for (int i = 0; i < 6_000; i++) properties.Add(new PropertyEdit(PropertyPath.Parse($"p{i}"), Sign.Set, new GdInteger(i + 100)));
        edit.Entries.Add(new(EntryName.Create("0x00000077"), properties));

        byte[] ptch = PropCodec.WritePtch(new PtchBin());
        GameDataApplyResult? result = null;
        long allocated = Allocated(() => result = Apply(ptch, edit, readEntry: _ => GameDataEntryRead.Found(game)));
        Assert.Equal(6_000, result!.Applied.Properties);
        Assert.Equal(6_000, PropCodec.ReadPtch(result.Bytes).Patches.Count);
        // the identity of a held record is worked out once; worked out again for every held record at every new one it is eighteen million strings: gigabytes
        Assert.True(allocated < RecordAllocLimit, $"{allocated:N0} bytes");
        Assert.True(result.WorkUsed < 40_000_000, $"{result.WorkUsed:N0} units");   // each record scans the records its object holds, as charged: 18 million measured
    }

    // ================================================================================================ keys a file chooses

    private static IEqualityComparer<uint> SaltedComparer() =>
        (IEqualityComparer<uint>)typeof(PropCodec).Assembly.GetType("ReyEngine.Formats.LtkGameData.KeyHash", throwOnError: true)!
            .GetField("Comparer", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    [Theory]
    [InlineData(75_431u)]    // the prime a table of fifty-odd thousand entries finds its buckets by: keys that are its multiples all shared one
    [InlineData(90_523u)]
    [InlineData(108_631u)]
    [InlineData(130_363u)]
    public void Keys_that_share_a_remainder_against_a_table_prime_do_not_share_a_bucket(uint prime)
    {
        var comparer = SaltedComparer();
        const int n = 20_000;
        var load = new Dictionary<uint, int>();
        for (uint i = 1; i <= n; i++)
        {
            // where a table of that many buckets puts a key that is a multiple of the prime
            uint bucket = (uint)comparer.GetHashCode(i * prime) % prime;
            load[bucket] = load.GetValueOrDefault(bucket) + 1;
        }
        // as its own hash code the key puts every one of them in bucket 0; salted, the twenty thousand spread over some seventeen thousand buckets
        Assert.True(load.Count > n / 2, $"{load.Count} buckets");
        Assert.True(load.Values.Max() <= 16, $"{load.Values.Max()} in one bucket");
    }

    [Fact]
    public void An_ordered_maps_index_is_keyed_with_the_salted_comparer()
    {
        var map = new OrderedMap<int>();
        for (uint i = 0; i < 20; i++) map.Set(i * 75_431u, (int)i);
        Assert.Equal(7, map.IndexOf(7 * 75_431u));   // the first lookup in a map this size builds the index
        object slots = typeof(OrderedMap<int>).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map)!;
        var index = (Dictionary<uint, int>)slots.GetType().GetField("Of")!.GetValue(slots)!;
        Assert.Same(SaltedComparer(), index.Comparer);
    }

    [Fact]
    public void A_map_of_keys_chosen_to_collide_keeps_its_order_and_finds_every_key()
    {
        // 56,000 properties whose names are all multiples of 75,431: the order is the order they were added in, whatever the buckets are
        var map = new OrderedMap<int>();
        for (uint i = 1; i <= 56_000; i++) Assert.False(map.Set(i * 75_431u, (int)i));
        Assert.Equal(56_000, map.Count);
        for (uint i = 1; i <= 56_000; i += 997)
        {
            Assert.Equal((int)i - 1, map.IndexOf(i * 75_431u));
            Assert.Equal(i * 75_431u, map.KeyAt((int)i - 1));
        }
        Assert.Equal(-1, map.IndexOf(1));
        var copy = new PropObject(ObjectPath, Class);
        for (uint i = 1; i <= 2_000; i++) copy.Properties.Set(i * 75_431u, new PropInt(PropKind.U32, i));
        Assert.Equal(2_000, copy.Clone().Properties.Count);
    }

    [Fact]
    public void Every_table_the_engine_keys_by_a_hash_is_made_by_KeyHash()
    {
        string? root = RepoRoot();
        if (root is null) return;   // the tests are not running under a checkout; the tests above always run
        string directory = Path.Combine(root, "src", "ReyEngine.Formats", "LtkGameData");
        Assert.True(Directory.Exists(directory), directory);

        // a set or a dictionary of uint made with `new`, either spelled out or as the initializer of a declaration of that type
        var plain = new Regex(@"new\s+(HashSet|Dictionary|SortedSet|SortedDictionary)<uint\b|(HashSet|Dictionary|SortedSet|SortedDictionary)<uint\b[^;]*=\s*new\b");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            int number = 0;
            foreach (string line in File.ReadLines(file))
            {
                number++;
                if (plain.IsMatch(line)) offenders.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0, "a table keyed by a hash a file chose is made by KeyHash, which salts it: " + string.Join("; ", offenders));
    }

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) return dir.FullName;
        return null;
    }

    // ================================================================================================ what a removal moves, and what a loop leaves behind

    private static (byte[] Base, byte[] Ptch) NewestFirst()
    {
        // 12,000 objects, the newest 4,000 of them deleted newest first. Each removal is of the last entry, so nothing after it moves, and its number
        // goes in front of every number removed before it: 4,000 * 3,999 / 2 = 8 million moves of the removal list, a thirty-second of a unit each.
        var bin = new PropBin();
        for (uint i = 0; i < 12_000; i++) bin.Objects.Set(0x1000 + i, new PropObject(0x1000 + i, Class));
        var patch = new PtchBin();
        for (uint i = 12_000; i > 8_000; i--) patch.Deleted.Add(0x1000 + i - 1);
        return (PropCodec.WriteProp(bin), PropCodec.WritePtch(patch));
    }

    [Fact]
    public void Deleting_the_newest_objects_first_is_charged_for_the_numbers_the_removal_list_moves()
    {
        var (baseBytes, ptch) = NewestFirst();
        var result = Apply(baseBytes, OverrideEdit("o.ptch"), readOverride: _ => GameDataBytesRead.Found(ptch));
        Assert.Equal(4_000, result.Applied.Objects);
        Assert.Equal(8_000, PropCodec.ReadProp(result.Bytes).Objects.Count);
        Assert.InRange(result.WorkUsed, WorkNewestFirstLow, WorkNewestFirstHigh);   // without the removal list's moves it is a third of that
    }

    [Fact]
    public void Removing_the_newest_objects_by_name_is_charged_the_same_way()
    {
        var (baseBytes, _) = NewestFirst();
        var edit = new GameDataEdit();
        for (uint i = 12_000; i > 8_000; i--) edit.Objects.Add(new(EntryName.Create("0x" + (0x1000 + i - 1).ToString("x8")), RemoveObjectEdit.Instance));
        var result = Apply(baseBytes, edit);
        Assert.Equal(4_000, result.Applied.Objects);
        Assert.InRange(result.WorkUsed, WorkNewestFirstLow, WorkNewestFirstHigh);
    }

    [Fact]
    public void A_link_removal_that_names_nothing_held_is_charged_for_the_diagnostic_it_leaves()
    {
        byte[] bytes = ManyProperties(10);
        var edit = new GameDataEdit();
        for (int i = 0; i < 5_000; i++) edit.Links.Remove.Add(new LinkPath($"data/missing/{i}.bin"));
        var result = Apply(bytes, edit);
        Assert.Equal(5_000, result.Diagnostics.Count(d => d.Kind == GameDataDiagnosticKind.LinkRemovalUnmatched));
        Assert.InRange(result.WorkUsed, 5_000 * 9, 5_000 * 9 + 1_000);   // one unit for the removal and eight for what it leaves
        var error = Assert.Throws<GameDataException>(() => Apply(bytes, edit, limits: new GameDataLimits { MaxWork = 20_000 }));
        Assert.Equal(GameDataErrorKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void Override_files_that_cannot_be_read_or_decoded_are_charged_for_the_diagnostics_they_leave()
    {
        byte[] bytes = ManyProperties(10);
        string[] paths = Enumerable.Range(0, 3_000).Select(i => $"o{i}.ptch").ToArray();

        var gone = Apply(bytes, OverrideEdit(paths), readOverride: _ => GameDataBytesRead.Failed("gone"));
        Assert.Equal(3_000, gone.Diagnostics.Count(d => d.Kind == GameDataDiagnosticKind.OverrideUnreadable));
        Assert.InRange(gone.WorkUsed, 3_000 * 8, 3_000 * 8 + 1_000);

        byte[] notAPtch = new byte[8];
        var broken = Apply(bytes, OverrideEdit(paths), readOverride: _ => GameDataBytesRead.Found(notAPtch));
        Assert.Equal(3_000, broken.Diagnostics.Count(d => d.Kind == GameDataDiagnosticKind.OverrideInvalid));
        Assert.InRange(broken.WorkUsed, 3_000 * 8, 3_000 * 8 + 1_000);

        var error = Assert.Throws<GameDataException>(() => Apply(bytes, OverrideEdit(paths), limits: new GameDataLimits { MaxWork = 5_000 },
            readOverride: _ => GameDataBytesRead.Failed("gone")));
        Assert.Equal(GameDataErrorKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void A_reference_the_host_cannot_read_is_charged_for_the_diagnostic_it_leaves()
    {
        byte[] bytes = ManyProperties(10);
        var properties = new List<PropertyEdit>();
        for (int i = 0; i < 2_000; i++) properties.Add(PropertyEdit.Parse($"k{i}", GameDataJson.Parse($"{{\"ref\": \"0x{0x1000 + i:x8}:a\"}}")));
        var edit = new GameDataEdit();
        edit.Entries.Add(new(EntryName.Create("0x" + ObjectPath.ToString("x8")), properties));

        // references to entries the game lacks leave no diagnostic of their own; the ones it cannot read leave one each, which is eight units
        var absent = Apply(bytes, edit, readEntry: _ => GameDataEntryRead.None);
        var unreadable = Apply(bytes, edit, readEntry: _ => GameDataEntryRead.Failed("unreadable"));
        Assert.Equal(2_000, unreadable.Diagnostics.Count(d => d.Kind == GameDataDiagnosticKind.ReferenceUnreadable));
        Assert.Equal(0, absent.Diagnostics.Count(d => d.Kind == GameDataDiagnosticKind.ReferenceUnreadable));
        Assert.Equal(2_000 * 8, unreadable.WorkUsed - absent.WorkUsed);
    }

    // ================================================================================================ the depth of a comparison, and the limit of a writer

    [Fact]
    public void A_leaf_at_the_deepest_level_the_codec_reads_is_compared_like_any_other()
    {
        // 256 structs: the last at depth 255 and its leaf at 256, where the codec reads one
        Assert.True(PropValue.Equal(ChainValue(256), ChainValue(256)));
        Assert.False(PropValue.Equal(ChainValue(256), ChainValue(256, leaf: 2)));
        // the 257th struct is a container at depth 256, which the codec does not read, and so is not compared
        Assert.Throws<GameDataException>(() => PropValue.Equal(ChainValue(257), ChainValue(257)));
    }

    [Fact]
    public void The_writer_holds_the_output_limit_for_every_write_and_not_only_when_its_buffer_grows()
    {
        // the buffer starts at 4 KB and doubles: an output of 6,000 bytes under a limit of 5,000 fits the 8 KB the buffer has by then, and was let through
        var obj = new PropObject(ObjectPath, Class);
        for (uint i = 0; i < 700; i++) obj.Properties.Set(i, new PropInt(PropKind.U32, i));
        var bin = new PropBin();
        bin.Objects.Set(ObjectPath, obj);
        Assert.InRange(PropCodec.WriteProp(bin).Length, 6_000, 8_000);

        var assembly = typeof(PropCodec).Assembly;
        object meter = Activator.CreateInstance(assembly.GetType("ReyEngine.Formats.LtkGameData.WorkMeter", throwOnError: true)!,
            new GameDataLimits { MaxOutputBytes = 5_000 }, CancellationToken.None, 0L)!;
        var write = typeof(PropCodec).GetMethod("WriteProp", BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(PropBin), meter.GetType() })!;
        var thrown = Assert.Throws<TargetInvocationException>(() => write.Invoke(null, new object[] { bin, meter }));
        var error = Assert.IsType<GameDataException>(thrown.InnerException);
        Assert.Equal(GameDataErrorKind.LimitExceeded, error.Kind);
        Assert.Contains("output limit", error.Message);
    }

    // ================================================================================================ the ordered map

    /// <summary>Runs <paramref name="work"/> on <paramref name="threads"/> threads of their own that begin at once (never on the pool, which a barrier could starve), and rethrows what any of them threw.</summary>
    private static void Concurrently(int threads, Action work)
    {
        using var barrier = new Barrier(threads);
        var failures = new List<Exception>();
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            try { barrier.SignalAndWait(); work(); }
            catch (Exception e) { lock (failures) failures.Add(e); }
        })).ToList();
        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) worker.Join();
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    [Fact]
    public void An_ordered_map_read_by_many_threads_at_once_answers_every_one_of_them_right()
    {
        // thirteen entries: the first size at which the index is built, lazily, by whichever reader asks first
        for (int round = 0; round < 300; round++)
        {
            var map = new OrderedMap<int>();
            for (int i = 0; i < 13; i++) map.Set((uint)(i * 7919 + 1), i);
            Concurrently(8, () =>
            {
                for (int i = 0; i < 13; i++)
                {
                    Assert.True(map.TryGetValue((uint)(i * 7919 + 1), out int value) && value == i);
                    Assert.Equal(i, map.IndexOf((uint)(i * 7919 + 1)));
                }
                Assert.False(map.ContainsKey(0xDEAD));
            });
        }
    }

    [Fact]
    public void An_ordered_map_that_has_had_an_entry_removed_is_as_safe_to_read_from_many_threads()
    {
        for (int round = 0; round < 300; round++)
        {
            var map = new OrderedMap<int>();
            for (int i = 0; i < 14; i++) map.Set((uint)(i + 100), i);
            Assert.True(map.Remove(105));
            Concurrently(8, () =>
            {
                for (int i = 0; i < 14; i++)
                {
                    if (i == 5) { Assert.False(map.ContainsKey(105)); continue; }
                    Assert.True(map.TryGetValue((uint)(i + 100), out int value) && value == i);
                    Assert.Equal(i < 5 ? i : i - 1, map.IndexOf((uint)(i + 100)));
                }
            });
        }
    }

    [Fact]
    public void An_ordered_map_whose_index_removals_have_dropped_is_rebuilt_safely_by_the_threads_that_read_it()
    {
        // 2,100 keys and 1,025 removals: the removal that passes half the numbers drops the index, and the readers are the ones to build the next
        for (int round = 0; round < 40; round++)
        {
            var map = new OrderedMap<int>();
            for (uint i = 0; i < 2_100; i++) map.Set(i * 2 + 1, (int)i);
            for (uint i = 0; i < 1_025; i++) Assert.True(map.Remove(i * 2 + 1));
            Concurrently(8, () =>
            {
                for (uint i = 1_025; i < 2_100; i++)
                {
                    Assert.True(map.TryGetValue(i * 2 + 1, out int value) && value == (int)i);
                    Assert.Equal((int)i - 1_025, map.IndexOf(i * 2 + 1));
                }
                Assert.False(map.ContainsKey(1));
            });
        }
    }

    [Fact]
    public void An_ordered_map_removes_thousands_of_entries_without_being_rebuilt_for_each()
    {
        const int n = 12_000;
        var map = new OrderedMap<int>();
        for (int i = 0; i < n; i++) map.Set((uint)i * 3 + 1, i);
        var order = Enumerable.Range(0, n).OrderBy(i => (i * 2654435761u) & 0xFFFF).Take(11_500).ToList();
        long allocated = Allocated(() => { foreach (int i in order) Assert.True(map.Remove((uint)i * 3 + 1)); });
        // a rebuild of the index for each removal is the table of the keys left, eleven thousand times: gigabytes
        Assert.True(allocated < MapAllocLimit, $"{allocated:N0} bytes");
        Assert.Equal(n - 11_500, map.Count);
    }

    [Fact]
    public void An_ordered_map_does_what_an_index_map_does_over_a_long_run_of_operations()
    {
        var random = new Random(8171);
        var map = new OrderedMap<int>();
        var model = new List<(uint Key, int Value)>();
        for (int step = 0; step < 30_000; step++)
        {
            // a population that grows past the index threshold, shrinks below it and grows again
            int target = (step / 3_000) % 2 == 0 ? 400 : 6;
            uint key = (uint)random.Next(0, 600);
            if (random.Next(1000) < 500 && model.Count < target)
            {
                bool had = map.Set(key, step);
                int at = model.FindIndex(e => e.Key == key);
                Assert.Equal(at >= 0, had);
                if (at >= 0) model[at] = (key, step); else model.Add((key, step));
            }
            else if (model.Count > 0)
            {
                uint victim = random.Next(4) == 0 ? key : model[random.Next(model.Count)].Key;
                bool removed = map.Remove(victim);
                int at = model.FindIndex(e => e.Key == victim);
                Assert.Equal(at >= 0, removed);
                if (at >= 0) model.RemoveAt(at);
            }
            Assert.Equal(model.Count, map.Count);
            if (step % 50 == 0)
            {
                for (int i = 0; i < model.Count; i++)
                {
                    Assert.Equal(model[i].Key, map.KeyAt(i));
                    Assert.Equal(model[i].Value, map.ValueAt(i));
                    Assert.Equal(i, map.IndexOf(model[i].Key));
                }
                Assert.Equal(-1, map.IndexOf(1_000_000));
            }
        }
    }

    // ================================================================================================ the one that was named

    [Fact]
    public void A_zero_width_container_of_a_real_size_still_reads_and_writes_as_it_always_did()
    {
        // the budget is for what nothing can bound; a list of a handful of None is legal and round-trips
        byte[] bytes = Raw.Prop(Raw.Property(1, KindContainer, Raw.List(0, 3, Array.Empty<byte>())));
        var bin = PropCodec.ReadProp(bytes);
        Assert.Equal(bytes.Length, PropCodec.WriteProp(bin).Length);
    }
}
