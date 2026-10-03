using System.Numerics;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Primitives;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M814: the synthetic project behind the golden GameData fixtures in <c>Fixtures/GameData/s1/</c>.
///
/// <para>One map folder and one champion folder, a reference WAD standing for the installed game, and a "kitchen sink" bin
/// that changes one property of every kind ReyEngine can write (integers of every width and sign, floats with awkward
/// spellings, strings with escapes, vectors, a matrix, a colour, hashes and links, lists, sets, maps, options, embedded
/// structs and pointers that change class, gain a class or lose it). Exported with Project Settings &gt; Send game bin edits
/// as declarations on, it gives the <c>Layers.base.GameData</c> and <c>Layers.fix.GameData</c> documents the fixtures hold.</para>
///
/// <para><b>One source, two users.</b> This file is compiled into the xUnit project and, by a link, into the scratch harness
/// (<c>.codex_tmp/M814/export814</c>) that exports the same project and hands the archive to <c>fantomecheck</c>, the Rust
/// program that reads it with league-mod's own crates (<c>ltk_fantome</c>, <c>ltk_game_data</c>) and applies each layer's
/// document over the untouched game bins. So the JSON the suite pins is the JSON that was verified, from the same input.
/// It uses no xUnit, for that reason.</para>
///
/// <para>The plaintext for the hashes is recorded as the project is built - every name it hashes - so the document spells
/// the same on every machine whatever hash tables are installed.</para>
/// </summary>
internal static class GoldenGameDataProject
{
    // ------------------------------------------------------------------ the names a document spells

    private static readonly Dictionary<uint, string> Fields = new();
    private static readonly Dictionary<ulong, string> Files = new();

    /// <summary>FNV-1a of a name, remembering the name so the document can spell it.</summary>
    private static uint H(string name)
    {
        uint hash = HashAlgorithms.Fnv1a(name);
        lock (Fields) Fields[hash] = name;
        return hash;
    }

    /// <summary>The WAD path hash of a file, remembering the path.</summary>
    private static ulong W(string path)
    {
        ulong hash = HashAlgorithms.WadPath(path);
        lock (Files) Files[hash] = path;
        return hash;
    }

    private sealed class RecordedNames : IDeclarationNames
    {
        public string? Field(uint hash) { lock (Fields) return Fields.GetValueOrDefault(hash); }
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) { lock (Files) return Files.GetValueOrDefault(hash); }
    }

    /// <summary>The names of everything <see cref="Build"/> hashed. Complete once <see cref="Build"/> has run.</summary>
    public static IDeclarationNames Names { get; } = new RecordedNames();

    // ------------------------------------------------------------------ bins

    /// <summary>An empty option that keeps its element type, as Riot writes it (the private field BinTreeCloner sets).</summary>
    private static BinTreeOptional Empty(string name, BinPropertyType type)
    {
        var option = new BinTreeOptional(H(name), null);
        typeof(BinTreeOptional).GetField("_valueType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(option, type);
        return option;
    }

    private static byte[] Write(BinTree tree)
    {
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static BinTree One(string entry, string cls, IEnumerable<BinTreeProperty> props, params string[] dependencies) =>
        new(new[] { new BinTreeObject(H(entry), H(cls), props.ToList()) }, dependencies);

    private static void Put(string folder, string rel, byte[] bytes)
    {
        string path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static KeyValuePair<BinTreeProperty, BinTreeProperty> Kv(BinTreeProperty key, BinTreeProperty value) => new(key, value);

    /// <summary>Every value kind, in a Riot state and a project state that changes each.</summary>
    private static (byte[] Riot, byte[] Mod) KitchenSink()
    {
        BinTreeProperty[] Make(bool mod)
        {
            var inner = new BinTreeProperty[] { new BinTreeF32(H("speed"), mod ? 9.5f : 1f), new BinTreeU32(H("count"), 2) };
            return new BinTreeProperty[]
            {
                new BinTreeU8(H("u8"), (byte)(mod ? 200 : 1)),
                new BinTreeI8(H("i8"), (sbyte)(mod ? -100 : 1)),
                new BinTreeU16(H("u16"), (ushort)(mod ? 65000 : 1)),
                new BinTreeI16(H("i16"), (short)(mod ? -30000 : 1)),
                new BinTreeU32(H("u32"), mod ? uint.MaxValue : 1),
                new BinTreeI32(H("i32"), mod ? int.MinValue : 1),
                new BinTreeU64(H("u64"), mod ? ulong.MaxValue : 1),
                new BinTreeI64(H("i64"), mod ? long.MinValue : 1),
                new BinTreeBool(H("b"), mod),
                new BinTreeBitBool(H("flag"), mod),
                new BinTreeF32(H("f_neg0"), mod ? -0f : 1f),
                new BinTreeF32(H("f_exp"), mod ? 1e-30f : 1f),
                new BinTreeF32(H("f_big"), mod ? 123456792f : 1f),
                new BinTreeF32(H("f_max"), mod ? 3.4028235E+38f : 1f),
                new BinTreeVector2(H("v2"), mod ? new Vector2(1.5f, -2.25f) : Vector2.One),
                new BinTreeVector3(H("v3"), mod ? new Vector3(1, 2, 3) : Vector3.One),
                new BinTreeVector4(H("v4"), mod ? new Vector4(1, 2, 3, 4) : Vector4.One),
                new BinTreeMatrix44(H("m44"), mod ? new Matrix4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16) : Matrix4x4.Identity),
                new BinTreeColor(H("rgba"), mod ? new Color((byte)255, (byte)128, (byte)0, (byte)64) : new Color((byte)1, (byte)2, (byte)3, (byte)4)),
                new BinTreeString(H("str"), mod ? "say \"hi\" \\ back\ttab" : "old"),
                new BinTreeHash(H("hash"), mod ? H("speed") : 0x12345678),
                new BinTreeObjectLink(H("link"), mod ? H("Maps/Test/Thing") : 0x0badf00d),
                new BinTreeWadChunkLink(H("file"), mod ? W("assets/a.tex") : 0x1111222233334444UL),
                new BinTreeContainer(H("list"), BinPropertyType.U32,
                    mod ? new BinTreeProperty[] { new BinTreeU32(0, 1), new BinTreeU32(0, 2), new BinTreeU32(0, 3), new BinTreeU32(0, 4) }
                        : new BinTreeProperty[] { new BinTreeU32(0, 1), new BinTreeU32(0, 2), new BinTreeU32(0, 3) }),
                new BinTreeUnorderedContainer(H("list2"), BinPropertyType.Hash,
                    mod ? new BinTreeProperty[] { new BinTreeHash(0, H("speed")) }
                        : new BinTreeProperty[] { new BinTreeHash(0, 1), new BinTreeHash(0, 2) }),
                new BinTreeContainer(H("vecs"), BinPropertyType.Vector2,
                    mod ? new BinTreeProperty[] { new BinTreeVector2(0, new Vector2(1, 2)) }
                        : new BinTreeProperty[] { new BinTreeVector2(0, new Vector2(3, 4)), new BinTreeVector2(0, new Vector2(5, 6)) }),
                mod ? new BinTreeOptional(H("opt_filled"), new BinTreeU32(0, 7)) : Empty("opt_filled", BinPropertyType.U32),
                mod ? Empty("opt_emptied", BinPropertyType.U32) : new BinTreeOptional(H("opt_emptied"), new BinTreeU32(0, 5)),
                mod ? new BinTreeOptional(H("opt_list"), new BinTreeVector2(0, new Vector2(1, 2))) : Empty("opt_list", BinPropertyType.Vector2),
                new BinTreeMap(H("map"), BinPropertyType.String, BinPropertyType.U32,
                    mod ? new[] { Kv(new BinTreeString(0, "a"), new BinTreeU32(0, 2)), Kv(new BinTreeString(0, "b"), new BinTreeU32(0, 3)) }
                        : new[] { Kv(new BinTreeString(0, "a"), new BinTreeU32(0, 1)) }),
                new BinTreeMap(H("hmap"), BinPropertyType.Hash, BinPropertyType.F32,
                    mod ? new[] { Kv(new BinTreeHash(0, 0x0badf00d), new BinTreeF32(0, 2.5f)) }
                        : new[] { Kv(new BinTreeHash(0, 0x0badf00d), new BinTreeF32(0, 1f)) }),
                new BinTreeEmbedded(H("embed"), H("InnerData"), inner),                                    // a nested field changes: a dotted key
                new BinTreeStruct(H("ptr_swap"), mod ? H("OtherData") : H("InnerData"),
                    new BinTreeProperty[] { new BinTreeU32(H("count"), mod ? 1u : 2u) }),                    // a pointer of another class: set whole
                mod ? new BinTreeStruct(H("ptr_set"), H("InnerData"), new BinTreeProperty[] { new BinTreeU32(H("count"), 3) })
                    : new BinTreeStruct(H("ptr_set"), 0u, Array.Empty<BinTreeProperty>()),                   // the null pointer gains a class
                mod ? new BinTreeStruct(H("ptr_clear"), 0u, Array.Empty<BinTreeProperty>())
                    : new BinTreeStruct(H("ptr_clear"), H("InnerData"), new BinTreeProperty[] { new BinTreeU32(H("count"), 3) }),   // and loses it
                new BinTreeContainer(H("embeds"), BinPropertyType.Embedded,
                    mod ? new BinTreeProperty[] { new BinTreeEmbedded(0, H("InnerData"), inner), new BinTreeEmbedded(0, H("InnerData"), inner) }
                        : new BinTreeProperty[] { new BinTreeEmbedded(0, H("InnerData"), inner) }),
            };
        }

        var riot = One("Maps/Test/Thing", "Thing", Make(false), "DATA/A.bin");
        var mod = One("Maps/Test/Thing", "Thing", Make(true), "DATA/B.bin");
        return (Write(riot), Write(mod));
    }

    // ------------------------------------------------------------------ the project

    /// <summary>The synthetic project and the game it is compared with, written under <paramref name="root"/>.</summary>
    /// <param name="Project">Folders <c>Map11</c> (base) and <c>Ahri</c> (layer "fix"), a second layer "layer9" with no
    /// folder, the setting on, and the reference WAD set.</param>
    /// <param name="ReferenceWad">The game's copy of the bins, packed.</param>
    /// <param name="MapFolder">The project's <c>Map11</c> folder.</param>
    /// <param name="ChampionFolder">The project's <c>Ahri</c> folder.</param>
    public sealed record Built(ReyProject Project, string ReferenceWad, string MapFolder, string ChampionFolder);

    /// <summary>
    /// Writes the project under <paramref name="root"/> (which must not hold an earlier one).
    ///
    /// <para>Map11: <c>a.bin</c> changed (declared), <c>same.bin</c> identical to the game's (not shipped), <c>whole.bin</c> lost
    /// a property (ships whole), <c>sink.bin</c> the kitchen sink (declared), <c>embedswap.bin</c> an embedded struct of another
    /// class (ships whole), <c>new.bin</c> (no game copy), and two assets. Ahri: <c>skin0.bin</c> changed (declared) and a
    /// texture.</para>
    /// </summary>
    public static Built Build(string root)
    {
        string project = Path.Combine(root, "project");
        string refTree = Path.Combine(root, "reftree");
        Directory.CreateDirectory(root);

        byte[] Speed(float v) => Write(One("Maps/Test/Thing", "Thing", new BinTreeProperty[] { new BinTreeF32(H("speed"), v) }));
        byte[] Embed(string cls) => Write(One("Maps/Test/Thing", "Thing",
            new BinTreeProperty[] { new BinTreeEmbedded(H("inner"), H(cls), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) }) }));
        var sink = KitchenSink();

        // what Riot ships
        Put(refTree, "data/maps/a.bin", Speed(1f));
        Put(refTree, "data/maps/same.bin", Speed(1f));
        Put(refTree, "data/maps/whole.bin", Write(One("Maps/Test/Thing", "Thing",
            new BinTreeProperty[] { new BinTreeF32(H("speed"), 1f), new BinTreeU32(H("count"), 3) })));
        Put(refTree, "data/maps/sink.bin", sink.Riot);
        Put(refTree, "data/maps/embedswap.bin", Embed("InnerData"));
        Put(refTree, "data/characters/ahri/skins/skin0.bin", Speed(1f));
        string refWad = Path.Combine(root, "Map11.wad.client");
        if (!WadPackService.Pack(refTree, refWad).Success) throw new InvalidOperationException("the reference WAD did not pack");

        // what the project holds
        string map = Path.Combine(project, "Map11");
        Put(map, "data/maps/a.bin", Speed(2f));
        Put(map, "data/maps/same.bin", Speed(1f));
        Put(map, "data/maps/whole.bin", Write(One("Maps/Test/Thing", "Thing", new BinTreeProperty[] { new BinTreeF32(H("speed"), 1f) })));
        Put(map, "data/maps/sink.bin", sink.Mod);
        Put(map, "data/maps/embedswap.bin", Embed("OtherData"));
        Put(map, "data/maps/new.bin", Speed(7f));
        Put(map, "assets/x.tex", new byte[] { 1, 2, 3 });
        Put(map, "assets/Mixed/Case.DDS", new byte[] { 3, 2, 1 });
        string ahri = Path.Combine(project, "Ahri");
        Put(ahri, "data/characters/ahri/skins/skin0.bin", Speed(5f));
        Put(ahri, "assets/y.dds", new byte[] { 4, 5, 6 });

        var p = new ReyProject
        {
            Name = "Synthetic",
            RootPath = project,
            OutputDirectory = Path.Combine(root, "build"),
            ShipBinEditsAsDeclarations = true,
            ProjectFolders = { "Map11", "Ahri" },
            ReferenceWads = { refWad },
        };
        p.Layers.Add(new ProjectLayer { Name = "fix", Priority = 10, Folders = { "Ahri" } });
        p.Layers.Add(new ProjectLayer { Name = "layer9", Priority = 20 });
        return new Built(p, refWad, map, ahri);
    }

    // ------------------------------------------------------------------ the fixture text

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A layer's GameData document as the fixture file holds it: two-space indentation, LF, one trailing LF.
    /// Number tokens are kept as written (<see cref="JsonElement"/> writes the raw token), which is the point - a
    /// <c>1E-30</c> or a <c>-0.0</c> that changed spelling would change the bits league-mod reads.</summary>
    public static string FixtureText(JsonElement gameData) => JsonSerializer.Serialize(gameData, Pretty) + "\n";

    /// <summary>A fixture as read from disk: Git may have checked it out with CRLF.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Each layer of an archive's <c>META/info.json</c> that carries a GameData document, as fixture text.</summary>
    public static SortedDictionary<string, string> GameDataOf(string fantome)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(fantome);
        using var entry = (zip.GetEntry("META/info.json") ?? throw new InvalidOperationException("no META/info.json")).Open();
        using var doc = JsonDocument.Parse(entry);
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in doc.RootElement.GetProperty("Layers").EnumerateObject())
            if (layer.Value.TryGetProperty("GameData", out var gameData))
                found[layer.Name] = FixtureText(gameData);
        return found;
    }

    /// <summary>Runs the export the Project &gt; Export .fantome command runs over <paramref name="built"/>, with this project's
    /// recorded names for the declarations instead of whatever hash tables the machine has. Needs the app's view model;
    /// reflection because the pieces are private.</summary>
    public static void Export(Built built, string output)
    {
        var vm = new ReyEngine.App.ViewModels.MainWindowViewModel { Project = built.Project };
        const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(ReyEngine.App.ViewModels.MainWindowViewModel).GetField("_declarationNames", NonPublic)!.SetValue(vm, Names);
        Directory.CreateDirectory(built.Project.OutputDirectory!);
        var meta = new FantomeMeta { Name = "Synthetic", Author = "Test", Version = "1.0.0", Description = "d" };
        var method = typeof(ReyEngine.App.ViewModels.MainWindowViewModel).GetMethod("ExportFantomeCore", NonPublic)!;
        try
        {
            method.Invoke(vm, new object?[]
            {
                output, meta, null, built.Project.OutputDirectory, new Progress<(double, string)>(_ => { }), "Export", "No WAD was produced.", "Zipping",
            });
        }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); }
    }
}
