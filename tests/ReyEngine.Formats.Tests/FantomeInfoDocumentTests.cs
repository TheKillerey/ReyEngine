using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: <c>META/info.json</c> read the way <c>ltk_fantome</c> reads it, but field by field - league-mod's reader makes any mistake
/// fail the whole mod, an import can take what is there.
/// </summary>
public sealed class FantomeInfoDocumentTests
{
    private static FantomeInfoDocument Read(string json) => FantomeInfoDocument.TryRead(json)!;

    [Fact]
    public void EveryFieldAPackageCarriesIsRead()
    {
        var info = Read("""
            {
              "Name": "Winter Rift 2025", "Author": "Crauzer", "Version": "0.3.0", "Description": "d",
              "Heart": "https://h", "Home": "https://o",
              "License": {"Name": "MIT", "Url": "https://l"},
              "Tags": ["map-skin"], "Champions": ["Ahri"], "Maps": ["summoners-rift"],
              "Layers": {"base": {"Name": "base", "Priority": 0}},
              "Hashtables": [{"Path": "META/hashes/g.txt", "Category": "game", "Algorithm": "xxh64", "Bits": 64}],
              "Generator": "ltk_mod_project 0.16.2"
            }
            """);

        Assert.Equal(("Winter Rift 2025", "Crauzer", "0.3.0", "d"), (info.Name, info.Author, info.Version, info.Description));
        Assert.Equal(("https://h", "https://o", "ltk_mod_project 0.16.2"), (info.Heart, info.Home, info.Generator));
        Assert.Equal(("MIT", "https://l", true), (info.License!.Name, info.License.Url, info.License.AsObject));
        Assert.Equal(new[] { "map-skin" }, info.Tags);
        Assert.Equal(new[] { "Ahri" }, info.Champions);
        Assert.Equal(new[] { "summoners-rift" }, info.Maps);
        Assert.Equal(new FantomeInfoHashtable("META/hashes/g.txt", "game", "xxh64", 64), Assert.Single(info.Hashtables));
        Assert.Equal("base", Assert.Single(info.Layers).Key);
        Assert.Empty(info.Problems);
        Assert.Empty(info.UnknownKeys);
    }

    [Fact]
    public void ACslolPackageWithJustTheOldFieldsReadsAsItAlwaysDid()
    {
        var info = Read("{\"Name\":\"Old\",\"Author\":\"A\",\"Version\":\"1.0\",\"Description\":\"d\",\"Heart\":\"h\",\"Home\":\"o\"}");
        Assert.Equal(("Old", "A", "1.0", "d", "h", "o"), (info.Name, info.Author, info.Version, info.Description, info.Heart, info.Home));
        Assert.Null(info.License);
        Assert.Empty(info.Tags);
        Assert.Empty(info.Layers);
        Assert.Empty(info.Hashtables);
        Assert.Empty(info.Problems);
    }

    [Fact]
    public void AFieldNobodyWroteIsNull()
    {
        var info = Read("{}");
        Assert.Null(info.Name);
        Assert.Null(info.Author);
        Assert.Null(info.Version);
        Assert.Null(info.Description);
        Assert.Null(info.Heart);
        Assert.Null(info.Generator);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"Name\":")]
    public void WhatIsNotAJsonObjectIsNotAnInfoJson(string json) => Assert.Null(FantomeInfoDocument.TryRead(json));

    [Fact]
    public void AVersionThatIsANumberIsKeptAsItsTextWhereItUsedToDropTheWholeFile()
    {
        var info = Read("{\"Name\":\"N\",\"Version\":1}");
        Assert.Equal("N", info.Name);
        Assert.Equal("1", info.Version);
    }

    [Theory]
    [InlineData("{\"Name\":5}", "Name is not text")]
    [InlineData("{\"Author\":[]}", "Author is not text")]
    [InlineData("{\"License\":5}", "License is neither text nor an object")]
    [InlineData("{\"License\":{\"Url\":\"u\"}}", "License is an object without a Name")]
    [InlineData("{\"License\":{\"Name\":\"n\",\"Ur1\":\"u\"}}", "License has a 'Ur1' that ltk_fantome does not accept")]
    [InlineData("{\"Tags\":\"map-skin\"}", "Tags is not a list")]
    [InlineData("{\"Tags\":[\"a\",5]}", "Tags holds a value that is not text")]
    [InlineData("{\"Layers\":[]}", "Layers is not a table")]
    [InlineData("{\"Hashtables\":{}}", "Hashtables is not a list")]
    [InlineData("{\"Hashtables\":[{\"Path\":\"p\"}]}", "A Hashtables entry lacks")]
    public void WhatIsOfTheWrongTypeIsLeftOutAndSaidSo(string json, string said)
    {
        var info = Read(json);
        Assert.Contains(info.Problems, p => p.Contains(said, StringComparison.Ordinal));
    }

    [Fact]
    public void TheLicenseShapeIsKept()
    {
        var text = Read("{\"License\":\"MIT\"}").License!;
        Assert.Equal(("MIT", null, false), (text.Name, text.Url, text.AsObject));
        var obj = Read("{\"License\":{\"Name\":\"X\"}}").License!;
        Assert.Equal(("X", null, true), (obj.Name, obj.Url, obj.AsObject));
        Assert.Null(Read("{\"License\":null}").License);
        Assert.Equal("l", Read("{\"License\":{\"Name\":\"X\",\"Url\":\"l\"}}").License!.Url);
        Assert.Null(Read("{\"License\":{\"Name\":\"X\",\"Url\":null}}").License!.Url);
    }

    [Fact]
    public void KeysThatNoToolOfThisFamilyWritesAreListed()
    {
        var info = Read("{\"Name\":\"N\",\"Zeta\":1,\"Alpha\":{}}");
        Assert.Equal(new[] { "Zeta", "Alpha" }, info.UnknownKeys);
        Assert.Empty(info.Problems);
    }

    // ===================================================== layers

    [Fact]
    public void ALayersGameDataIsTheTextOfTheFileFromItsBraceToItsBrace()
    {
        string doc = "{ \"version\":1 ,\n\t\"modules\": [ ] }";
        var info = Read("{\"Layers\":{\"fx\":{\"Name\":\"fx\",\"GameData\":" + doc + ",\"Priority\":2}}}");

        var layer = Assert.Single(info.Layers);
        Assert.Equal(doc, layer.GameDataText);
        Assert.Equal(("fx", "fx", 2), (layer.Key, layer.Name, layer.Priority));
    }

    [Fact]
    public void ALayerTakesItsNameFromItsKeyWhenItHasNoneAndNullFieldsAreAbsent()
    {
        var info = Read("{\"Layers\":{\"a\":{\"Priority\":1},\"b\":{\"Name\":\"\",\"DisplayName\":null,\"GameData\":null,\"StringOverrides\":null},\"c\":{\"Name\":\"see\"}}}");

        Assert.Equal(new[] { "a", "b", "see" }, info.Layers.Select(l => l.Name));
        Assert.Equal(new[] { 1, 0, 0 }, info.Layers.Select(l => l.Priority));
        Assert.All(info.Layers, l => Assert.Null(l.GameDataText));
        Assert.All(info.Layers, l => Assert.Null(l.DisplayName));
        Assert.Empty(info.Problems);
    }

    [Fact]
    public void StringOverridesKeepTheirKeyOrderAndAnEmptyTableIsNone()
    {
        var info = Read("{\"Layers\":{\"a\":{\"Name\":\"a\",\"Priority\":1,\"StringOverrides\":{\"z\":{\"2\":\"b\",\"1\":\"a\"},\"a\":{}}},\"b\":{\"Name\":\"b\",\"Priority\":2,\"StringOverrides\":{}}}}");

        Assert.Equal(new[] { "z", "a" }, info.Layers[0].StringOverrides!.Select(p => p.Key));
        Assert.Equal(new[] { "2", "1" }, info.Layers[0].StringOverrides!["z"]!.AsObject().Select(p => p.Key));
        Assert.Null(info.Layers[1].StringOverrides);
    }

    [Fact]
    public void LayersAreOrderedAsALoaderAppliesThemWithBaseFirstAtPriorityZero()
    {
        var info = Read("{\"Layers\":{\"l10\":{\"Name\":\"l10\",\"Priority\":1},\"l9\":{\"Name\":\"l9\",\"Priority\":1},\"high\":{\"Name\":\"high\",\"Priority\":-5},\"BASE\":{\"Name\":\"BASE\",\"Priority\":9}}}");

        var ordered = info.LayersInApplyOrder(out var dropped);

        Assert.Equal(new[] { "base", "high", "l9", "l10" }, ordered.Select(l => l.Name));
        Assert.Equal(new[] { 0, -5, 1, 1 }, ordered.Select(l => l.Priority));
        Assert.Empty(dropped);
    }

    [Fact]
    public void ARepeatedNameIsKeptOnceAndTheLossIsReported()
    {
        var info = Read("{\"Layers\":{\"x\":{\"Name\":\"fx\",\"Priority\":3},\"y\":{\"Name\":\"FX\",\"Priority\":1}}}");

        var ordered = info.LayersInApplyOrder(out var dropped);

        Assert.Equal("FX", Assert.Single(ordered).Name);                   // the lower priority is applied first, so it is the one that stays
        Assert.Contains("Layer 'x' repeats the name 'fx'", Assert.Single(dropped));
    }

    [Fact]
    public void ALayerEntryThatIsNotAnObjectAndAKeyItDoesNotCarryAreReported()
    {
        var info = Read("{\"Layers\":{\"a\":7,\"b\":{\"Name\":\"b\",\"Mystery\":1,\"Priority\":true}}}");

        Assert.Equal("b", Assert.Single(info.Layers).Name);
        Assert.Equal(new[] { "Mystery" }, info.Layers[0].UnknownKeys);
        Assert.Contains(info.Problems, p => p.Contains("Layer 'a' is not an object"));
        Assert.Contains(info.Problems, p => p.Contains("Priority that is not a whole number"));
    }

    // ===================================================== review, round 3: string overrides are locale -> field -> text, and nothing else

    private static string Nested(int levels) =>
        string.Concat(Enumerable.Repeat("{\"a\":", levels)) + "1" + string.Concat(Enumerable.Repeat("}", levels));

    private static FantomeInfoLayer LayerWithOverrides(string overrides, out IReadOnlyList<string> problems)
    {
        var info = Read("{\"Layers\":{\"words\":{\"Name\":\"words\",\"Priority\":3,\"DisplayName\":\"Words\",\"StringOverrides\":" + overrides + "}}}");
        problems = info.Problems;
        return Assert.Single(info.Layers);
    }

    [Fact]
    public void StringOverridesInLTKsShapeAreReadWithTheirKeyOrder()
    {
        var layer = LayerWithOverrides("{\"en_us\":{\"zz\":\"2\",\"aa\":\"1 \\u2013 caf\\u00e9\"},\"default\":{\"k\":\"v\",\"empty\":\"\"},\"fr_fr\":{}}", out var problems);

        Assert.Empty(problems);
        var so = layer.StringOverrides!;
        Assert.Equal(new[] { "en_us", "default", "fr_fr" }, so.Select(x => x.Key));
        Assert.Equal(new[] { "zz", "aa" }, so["en_us"]!.AsObject().Select(x => x.Key));
        Assert.Equal("1 \u2013 caf\u00e9", (string?)so["en_us"]!["aa"]);
        Assert.Equal("", (string?)so["default"]!["empty"]);
        Assert.Empty(so["fr_fr"]!.AsObject());                                                       // a locale with no fields is still a locale
    }

    [Theory]
    [InlineData("{\"en_us\":{\"title\":{\"nested\":\"object\"}}}", "'en_us' / 'title' is a table, not text")]      // a field that is a table
    [InlineData("{\"en_us\":[\"a\",\"b\"]}", "locale 'en_us' is a list, not a table of fields")]                    // a locale that is a list
    [InlineData("{\"en_us\":\"text\"}", "locale 'en_us' is text, not a table of fields")]                           // a locale that is text
    [InlineData("{\"en_us\":{\"count\":3}}", "'en_us' / 'count' is a number, not text")]                            // a number value
    [InlineData("{\"en_us\":{\"on\":true}}", "'en_us' / 'on' is a boolean, not text")]
    [InlineData("{\"en_us\":{\"gone\":null}}", "'en_us' / 'gone' is null, not text")]
    [InlineData("{\"en_us\":{\"list\":[\"a\"]}}", "'en_us' / 'list' is a list, not text")]
    public void StringOverridesOfAnotherShapeAreANoteAndTheLayerIsOtherwiseRead(string overrides, string where)
    {
        var layer = LayerWithOverrides(overrides, out var problems);

        Assert.Null(layer.StringOverrides);                                                          // LTK would refuse the file: they are not read ...
        Assert.Equal((3, "Words"), (layer.Priority, layer.DisplayName));                             // ... and the rest of the layer is
        var problem = Assert.Single(problems);
        Assert.Equal($"Layer 'words' has StringOverrides that are not locale -> field -> text as LTK reads them ({where}), so they were not read.", problem);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("7")]
    [InlineData("true")]
    public void StringOverridesThatAreNotATableAtAllKeepTheOlderNote(string overrides)
    {
        var layer = LayerWithOverrides(overrides, out var problems);

        Assert.Null(layer.StringOverrides);
        Assert.Equal("Layer 'words' has StringOverrides that are not an object, so they were not read.", Assert.Single(problems));
        Assert.Equal(3, layer.Priority);
    }

    [Fact]
    public void NoStringOverridesAndNullAreNothingToSay()
    {
        Assert.Empty(Read("{\"Layers\":{\"words\":{\"Name\":\"words\"}}}").Problems);
        var layer = LayerWithOverrides("null", out var problems);
        Assert.Null(layer.StringOverrides);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(70)]
    [InlineData(200)]
    public void ADeeplyNestedTableIsTheSameNoteNoMatterHowDeep(int levels)
    {
        // it used to be read as it was: 64 levels threw out of the import, and 63 passed and then threw when the project was saved, because
        // project.json and mod.config.json add levels of their own. The shape is two deep, so how deep the file goes is not asked.
        var layer = LayerWithOverrides(Nested(levels), out var problems);

        Assert.Null(layer.StringOverrides);
        Assert.Equal("Layer 'words' has StringOverrides that are not locale -> field -> text as LTK reads them ('a' / 'a' is a table, not text), so they were not read.",
            Assert.Single(problems));
    }

    [Fact]
    public void AKeyRepeatedInOneObjectKeepsItsFirstPlaceAndItsLastValueAsAnIndexMapDoes()
    {
        var fields = LayerWithOverrides("{\"en_us\":{\"k\":\"1\",\"other\":\"x\",\"k\":\"2\"}}", out var problems).StringOverrides!;
        Assert.Empty(problems);
        Assert.Equal(new[] { "k", "other" }, fields["en_us"]!.AsObject().Select(x => x.Key));
        Assert.Equal("2", (string?)fields["en_us"]!["k"]);

        var locales = LayerWithOverrides("{\"default\":{\"a\":\"1\"},\"fr_fr\":{\"b\":\"2\"},\"default\":{\"z\":\"9\"}}", out problems).StringOverrides!;
        Assert.Empty(problems);
        Assert.Equal(new[] { "default", "fr_fr" }, locales.Select(x => x.Key));                     // "default" keeps its first place ...
        Assert.Equal(new[] { "z" }, locales["default"]!.AsObject().Select(x => x.Key));              // ... and its last table
    }

    [Fact]
    public void AStringThatIsNotValidUnicodeIsANoteToo()
    {
        var layer = LayerWithOverrides("{\"en_us\":{\"k\":\"\\ud800\"}}", out var problems);

        Assert.Null(layer.StringOverrides);
        Assert.Contains("a text in it is not valid Unicode", Assert.Single(problems));
    }

    // ===================================================== review: an entry is read by what it holds

    /// <summary>A zip whose one entry holds six bytes and whose headers say it holds <paramref name="claimed"/>.</summary>
    private static byte[] ZipClaiming(uint claimed)
    {
        var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("META/hashes/table.txt", System.IO.Compression.CompressionLevel.NoCompression).Open();
            s.Write("a/b.c\n"u8);
        }
        byte[] bytes = stream.ToArray();
        void PatchAll(uint signature, int offset)
        {
            for (int i = 0; i + offset + 4 <= bytes.Length; i++)
                if (BitConverter.ToUInt32(bytes, i) == signature) BitConverter.GetBytes(claimed).CopyTo(bytes, i + offset);
        }
        PatchAll(0x04034b50, 22);      // the local header's uncompressed size
        PatchAll(0x02014b50, 24);      // the central directory's, which is the one entry.Length reports
        return bytes;
    }

    [Fact]
    public void AnEntryIsReadByWhatItHoldsAndNotByTheLengthItsHeaderClaims()
    {
        // ReadBytes sized its buffer from entry.Length: a few bytes of header can claim a gigabyte and a half, and that was allocated
        // before the first byte was read. league-mod's read_entry sizes nothing from the header either.
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(ZipClaiming(0x5FFFFFFF)), System.IO.Compression.ZipArchiveMode.Read);
        var entry = archive.GetEntry("META/hashes/table.txt")!;
        Assert.True(entry.Length > 1_000_000_000, "the header claims what the test needs it to");
        var read = typeof(ReyEngine.Core.Projects.FantomeImporter).GetMethod("ReadBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[]? bytes = null;
        try { bytes = (byte[])read.Invoke(null, new object[] { entry })!; }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }       // a runtime that checks the claim may refuse it
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 100_000_000, $"{allocated:n0} bytes were allocated to read a six-byte entry");
        if (bytes is not null) Assert.Equal("a/b.c\n"u8.ToArray(), bytes);
    }

}
