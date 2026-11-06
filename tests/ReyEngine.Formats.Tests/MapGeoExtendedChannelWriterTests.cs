using System.Numerics;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meta;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M859: mesh moves and bucket-grid saves on a mapgeo whose meshes carry the 48-byte channel block.
///
/// <para>The writer parsed the file with LeagueToolkit's <c>EnvironmentAsset</c>, which reads the 40-byte block
/// and throws on a 48-byte one, so every save on a Mantis map failed. The writer now reads a stripped copy and
/// puts the extended entries back per mesh, exactly as they were.</para>
///
/// <para>No shipped mapgeo exercises the 48-byte block (see <see cref="ExtendedChannelRule"/>), so the file under
/// test is a REAL shipped mapgeo (Map453 jade_container) converted to the 48-byte layout with the rule's own
/// writer. It is skipped without the install.</para>
/// </summary>
public sealed class MapGeoExtendedChannelWriterTests(ITestOutputHelper output)
{
    private const string Wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
    private const string GeoPath = "data/maps/mapgeometry/map453/jade_container.mapgeo";
    private const uint Marker = 0x12345678;   // a non-zero extended value, to prove entries are restored and not regenerated

    private static byte[]? Shipped()
    {
        if (!File.Exists(Wad)) return null;
        using var wad = WadArchive.Open(Wad);
        ulong hash = HashAlgorithms.WadPath(GeoPath);
        return wad.TryGetEntry(hash, out _) ? wad.Extract(hash) : null;
    }

    /// <summary>The shipped file rewritten with the extended entry on every mesh that uses one material, and that
    /// material's name. The chosen material is the first submesh material of a mesh that is neither first nor last.</summary>
    private static (byte[] Bytes, IReadOnlySet<string> Ext, int ExtendedMeshes, int MeshCount)? Extended48(byte[] shipped)
    {
        if (!MapGeoBinary.TryReadEditable(shipped, out var bin)) return null;
        if (bin.Meshes.Count < 3) return null;
        string material = bin.Meshes[bin.Meshes.Count / 2].Submeshes[0].Material;
        var ext = new HashSet<string>(StringComparer.Ordinal) { material };
        int changed = ExtendedChannelRule.Apply(bin, ext);
        foreach (var m in bin.Meshes.Where(m => m.HasExtendedChannel)) m.ExtendedChannelValue = Marker;
        return (bin.Write(), ext, changed, bin.Meshes.Count);
    }

    private static MapGeoMesh MeshNotUsing(MapGeoAsset asset, byte[] bytes, IReadOnlySet<string> ext)
    {
        MapGeoBinary.TryReadEditable(bytes, out var bin, ext);
        int j = Enumerable.Range(0, bin.Meshes.Count).Last(i => !bin.Meshes[i].HasExtendedChannel && i < bin.Meshes.Count - 1);
        return asset.Meshes.First(m => m.Index == j);
    }

    [Fact]
    public void Moving_a_mesh_in_a_48_byte_mapgeo_saves_and_keeps_every_extended_entry()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var made = Extended48(shipped);
        Assert.NotNull(made);
        var (ext48, ext, extendedMeshes, meshCount) = made!.Value;
        Assert.True(extendedMeshes > 0);
        Assert.Equal(shipped.Length + 8 * extendedMeshes, ext48.Length);   // the block is 8 bytes longer per mesh

        // THE BUG: LeagueToolkit cannot open the file as it is, so the old writer threw before it patched anything
        Assert.ThrowsAny<Exception>(() => new EnvironmentAsset(new MemoryStream(ext48, false)));

        var asset = MapGeoDecoder.Decode(ext48, ext);
        Assert.Equal(meshCount, asset.Meshes.Count);
        var target = MeshNotUsing(asset, ext48, ext);
        var delta = new Vector3(120, 0, -80);
        asset.TranslateMesh(target, delta);
        Assert.True(target.IsMoved);

        byte[] saved = MapGeoWriter.WriteWithMoves(ext48, asset.Meshes, extendedChannelMaterials: ext);

        // every extended entry is back, with the value it had (not regenerated from the material)
        Assert.True(MapGeoBinary.TryReadEditable(saved, out var back, ext));
        Assert.True(MapGeoBinary.TryReadEditable(ext48, out var before, ext));
        Assert.Equal(meshCount, back.Meshes.Count);
        for (int i = 0; i < meshCount; i++)
        {
            Assert.Equal(before.Meshes[i].HasExtendedChannel, back.Meshes[i].HasExtendedChannel);
            Assert.Equal(before.Meshes[i].ExtendedChannelValue, back.Meshes[i].ExtendedChannelValue);
        }
        Assert.Equal(extendedMeshes, back.Meshes.Count(m => m.HasExtendedChannel && m.ExtendedChannelValue == Marker));

        // the move landed on that mesh and on no other
        var reloaded = MapGeoDecoder.Decode(saved, ext);
        Assert.Equal(meshCount, reloaded.Meshes.Count);
        Assert.True(Vector3.Distance(target.Transform.Translation + delta, reloaded.Meshes[target.Index].Transform.Translation) < 0.01f);
        foreach (var other in asset.Meshes.Where(m => m.Index != target.Index).Take(25))
            Assert.True(Vector3.Distance(other.Transform.Translation, reloaded.Meshes[other.Index].Transform.Translation) < 0.01f);

        // the bucket grids were rebuilt (the stripped form re-opens through LeagueToolkit)
        using var env = new EnvironmentAsset(new MemoryStream(ExtendedChannelRule.WithoutExtendedEntries(saved, ext), false));
        Assert.NotEmpty(env.SceneGraphs);
        output.WriteLine($"ran: {meshCount} meshes, {extendedMeshes} with the 48-byte block ({string.Join(",", ext)}); moved mesh {target.Index} by {delta}; "
            + $"saved {saved.Length:n0} bytes ({saved.Length - ext48.Length:+#;-#;0} vs the input), {env.SceneGraphs.Count} grid(s)");
    }

    [Fact]
    public void A_bucket_grid_rebuild_on_a_48_byte_mapgeo_keeps_the_extended_entries()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var (ext48, ext, extendedMeshes, meshCount) = Extended48(shipped)!.Value;

        var asset = MapGeoDecoder.Decode(ext48, ext);
        byte[] rebuilt = MapGeoWriter.WriteWithRegeneratedBucketGrids(ext48, asset, extendedChannelMaterials: ext);

        Assert.True(MapGeoBinary.TryReadEditable(rebuilt, out var back, ext));
        Assert.Equal(meshCount, back.Meshes.Count);
        Assert.Equal(extendedMeshes, back.Meshes.Count(m => m.HasExtendedChannel && m.ExtendedChannelValue == Marker));
        using var env = new EnvironmentAsset(new MemoryStream(ExtendedChannelRule.WithoutExtendedEntries(rebuilt, ext), false));
        Assert.NotEmpty(env.SceneGraphs);
    }

    [Fact]
    public void Without_the_material_set_the_writer_is_exactly_what_it_was()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var asset = MapGeoDecoder.Decode(shipped);
        var target = asset.Meshes[0];
        asset.TranslateMesh(target, new Vector3(10, 0, 0));

        byte[] withNull = MapGeoWriter.WriteWithMoves(shipped, asset.Meshes);
        byte[] withEmpty = MapGeoWriter.WriteWithMoves(shipped, asset.Meshes, extendedChannelMaterials: new HashSet<string>());
        Assert.Equal(withNull, withEmpty);
    }

    [Fact]
    public void A_file_whose_mesh_count_changed_cannot_be_restored_by_index()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var (ext48, ext, _, _) = Extended48(shipped)!.Value;
        // an edited file with one mesh fewer: restoring entries by index would put them on the wrong meshes
        Assert.True(MapGeoBinary.TryReadEditable(shipped, out var fewer));
        fewer.Meshes.RemoveAt(fewer.Meshes.Count - 1);
        Assert.Throws<InvalidDataException>(() => ExtendedChannelRule.WithExtendedEntries(fewer.Write(), ext48, ext));
    }

    // ---- the second issue: a custom material name no hash table knows --------------------------------------

    private const string Custom = "Maps/Mods/MyMap/Materials/My_Custom_Mantis_MAT";

    private static readonly Dictionary<uint, string> Known = new[]
    {
        "StaticMaterialDef", "Shaders/Mantis_Env_Baked_PBR", "name", "techniques", "passes", "shader",
        "StaticMaterialTechniqueDef", "StaticMaterialPassDef",
    }.ToDictionary(n => HashAlgorithms.Fnv1a(n), n => n);
    private static string? Resolve(uint h) => Known.GetValueOrDefault(h);   // deliberately does NOT know Custom

    private static byte[] MaterialsBin(params BinTreeProperty[] extraOnMaterial)
    {
        uint H(string s) => HashAlgorithms.Fnv1a(s);
        var pass = new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
            { new BinTreeObjectLink(H("shader"), H("Shaders/Mantis_Env_Baked_PBR")) });
        var tech = new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
            { new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[] { pass }) });
        var props = new List<BinTreeProperty>
        {
            new BinTreeU64(H("name"), 0xDEADBEEFCAFEBABEul),   // 16.21: the name is a hash, never text
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[] { tech }),
        };
        props.AddRange(extraOnMaterial);
        var mat = new BinTreeObject(H(Custom), H("StaticMaterialDef"), props.ToArray());
        using var ms = new MemoryStream();
        new BinTree(new[] { mat }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static byte[] GeoWithString(string text)
    {
        var bytes = new List<byte> { 1, 2, 3, 4, 0xFF, 0xFF };
        bytes.AddRange(BitConverter.GetBytes((uint)text.Length));
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
        bytes.AddRange(new byte[] { 9, 9, 9 });
        return bytes.ToArray();
    }

    [Fact]
    public void A_custom_named_mantis_material_is_found_from_the_strings_in_the_mapgeo()
    {
        var set = ExtendedChannelRule.From(MaterialsBin(), Resolve, () => GeoWithString(Custom));
        Assert.Equal(new[] { Custom }, set);
    }

    [Fact]
    public void A_custom_named_mantis_material_is_found_from_the_strings_in_the_materials_bin()
    {
        // the path also appears as a string value inside the project's own materials.bin
        var set = ExtendedChannelRule.From(MaterialsBin(new BinTreeString(HashAlgorithms.Fnv1a("comment"), Custom)), Resolve, readMapGeo: null);
        Assert.Equal(new[] { Custom }, set);
    }

    [Fact]
    public void Without_a_string_that_hashes_to_the_material_nothing_is_guessed()
    {
        Assert.Empty(ExtendedChannelRule.From(MaterialsBin(), Resolve, () => GeoWithString("Maps/Mods/MyMap/Materials/Some_Other_MAT")));
        Assert.Empty(ExtendedChannelRule.From(MaterialsBin(), Resolve, readMapGeo: null));
    }

    [Fact]
    public void The_mapgeo_is_not_read_when_every_mantis_material_already_has_a_name()
    {
        var named = new Dictionary<uint, string>(Known) { [HashAlgorithms.Fnv1a(Custom)] = Custom };
        bool read = false;
        var set = ExtendedChannelRule.From(MaterialsBin(), h => named.GetValueOrDefault(h), () => { read = true; return null; });
        Assert.Equal(new[] { Custom }, set);
        Assert.False(read);
    }

    /// <summary>The scanner on a REAL file: the material strings of the jade_container mapgeo (read with the real 48-byte
    /// layout, where parsing the mesh section needs the very answer being looked for) are found by hash alone.</summary>
    [Fact]
    public void The_string_scan_finds_a_real_meshs_material_in_a_48_byte_mapgeo()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var (ext48, ext, _, _) = Extended48(shipped)!.Value;
        string material = ext.Single();
        var found = ExtendedChannelRule.NamesFromStrings(new HashSet<uint> { HashAlgorithms.Fnv1a(material) }, MaterialsBin(), () => ext48);
        Assert.Equal(material, found[HashAlgorithms.Fnv1a(material)]);
        output.WriteLine($"ran: '{material}' recovered from the raw {ext48.Length:n0}-byte mapgeo by its FNV-1a key");
    }
}
