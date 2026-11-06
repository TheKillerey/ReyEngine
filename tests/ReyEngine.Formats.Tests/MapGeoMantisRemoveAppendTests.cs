using System.Numerics;
using LeagueToolkit.Core.Environment;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M865: deleting and adding meshes on a mapgeo whose meshes carry the 48-byte Mantis channel block.
///
/// <para><see cref="MapGeoMeshRemover.Remove"/> and <see cref="MapGeoMeshAppender.Append"/> locate the mesh records with a raw byte walker
/// that read every record as 40 bytes. On the first Mantis mesh it desynced and threw ("Specified argument was out of the range of valid
/// values"), so the editor could not delete a mesh on any map with a Mantis material (found building the Shader Test Map; the user's
/// RTX Rift is such a map). The walker now takes the extended-channel material set, the remover's final LeagueToolkit check reads the
/// stripped form, and an added mesh on a Mantis material is refused (the appender writes the 40-byte record only).</para>
///
/// <para>The file under test is a real shipped mapgeo (Map453 jade_container) converted to the 48-byte layout by the rule's own writer, as
/// in <see cref="MapGeoExtendedChannelWriterTests"/>. Skipped without the install.</para>
/// </summary>
public sealed class MapGeoMantisRemoveAppendTests(ITestOutputHelper output)
{
    private const string Wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
    private const string GeoPath = "data/maps/mapgeometry/map453/jade_container.mapgeo";
    private const uint Marker = 0x12345678;

    private static byte[]? Shipped()
    {
        if (!File.Exists(Wad)) return null;
        using var wad = WadArchive.Open(Wad);
        ulong hash = HashAlgorithms.WadPath(GeoPath);
        return wad.TryGetEntry(hash, out _) ? wad.Extract(hash) : null;
    }

    private static (byte[] Bytes, IReadOnlySet<string> Ext, int Extended, int Count, string Material) Extended48(byte[] shipped)
    {
        Assert.True(MapGeoBinary.TryReadEditable(shipped, out var bin));
        string material = bin.Meshes[bin.Meshes.Count / 2].Submeshes[0].Material;
        var ext = new HashSet<string>(StringComparer.Ordinal) { material };
        int changed = ExtendedChannelRule.Apply(bin, ext);
        foreach (var m in bin.Meshes.Where(m => m.HasExtendedChannel)) m.ExtendedChannelValue = Marker;
        return (bin.Write(), ext, changed, bin.Meshes.Count, material);
    }

    [Fact]
    public void Removing_meshes_from_a_48_byte_mapgeo_keeps_the_other_extended_entries()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var (ext48, ext, extended, count, _) = Extended48(shipped);
        Assert.True(MapGeoBinary.TryReadEditable(ext48, out var before, ext));
        int mantis = Enumerable.Range(0, count).First(i => before.Meshes[i].HasExtendedChannel);
        int plain = Enumerable.Range(0, count).Last(i => !before.Meshes[i].HasExtendedChannel);

        byte[]? removed = MapGeoMeshRemover.Remove(ext48, new[] { mantis, plain }, out var error, ext);

        Assert.True(removed is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(removed!, out var back, ext));
        Assert.Equal(count - 2, back.Meshes.Count);
        Assert.Equal(extended - 1, back.Meshes.Count(m => m.HasExtendedChannel && m.ExtendedChannelValue == Marker));
        // the survivors are the originals, in order
        var expected = Enumerable.Range(0, count).Where(i => i != mantis && i != plain).ToList();
        for (int k = 0; k < expected.Count; k++)
        {
            Assert.Equal(before.Meshes[expected[k]].VertexCount, back.Meshes[k].VertexCount);
            Assert.Equal(before.Meshes[expected[k]].HasExtendedChannel, back.Meshes[k].HasExtendedChannel);
            Assert.Equal(before.Meshes[expected[k]].Submeshes[0].Material, back.Meshes[k].Submeshes[0].Material);
        }
        Assert.Empty(back.UnreferencedVertexBuffers());
        using var env = new EnvironmentAsset(new MemoryStream(ExtendedChannelRule.WithoutExtendedEntries(removed!, ext), false));
        Assert.Equal(count - 2, env.Meshes.Count);
        output.WriteLine($"ran: removed meshes {mantis} (48-byte) and {plain} of {count}; {extended - 1} extended entries kept");
    }

    [Fact]
    public void Without_the_material_set_the_walker_cannot_read_a_48_byte_mapgeo()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var (ext48, _, _, count, _) = Extended48(shipped);

        // the bug as it was: every record read as 40 bytes
        Assert.Null(MapGeoMeshRemover.Remove(ext48, new[] { count - 1 }, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_40_byte_mapgeo_is_removed_from_exactly_as_before()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        Assert.True(MapGeoBinary.TryReadEditable(shipped, out var bin));
        var withoutSet = MapGeoMeshRemover.Remove(shipped, new[] { 1 }, out var e1);
        var withEmptySet = MapGeoMeshRemover.Remove(shipped, new[] { 1 }, out var e2, new HashSet<string>());
        Assert.True(withoutSet is not null, e1);
        Assert.True(withEmptySet is not null, e2);
        Assert.Equal(withoutSet, withEmptySet);
        Assert.True(MapGeoBinary.TryReadEditable(withoutSet!, out var back));
        Assert.Equal(bin.Meshes.Count - 1, back.Meshes.Count);
    }

    private static NewMapMesh Quad(string material) => new(material,
        new float[] { 0, 0, 0, 100, 0, 0, 100, 0, 100, 0, 0, 100 }, null, new float[] { 0, 0, 1, 0, 1, 1, 0, 1 },
        new ushort[] { 0, 1, 2, 0, 2, 3 }, Matrix4x4.CreateTranslation(500, 50, 500));

    [Fact]
    public void Appending_a_mesh_to_a_48_byte_mapgeo_keeps_every_extended_entry()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var (ext48, ext, extended, count, _) = Extended48(shipped);
        Assert.True(MapGeoBinary.TryReadEditable(ext48, out var before, ext));
        string other = before.Meshes.First(m => !m.HasExtendedChannel).Submeshes[0].Material;

        byte[]? appended = MapGeoMeshAppender.Append(ext48, new[] { Quad(other) }, out var error, ext);

        Assert.True(appended is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(appended!, out var back, ext));
        Assert.Equal(count + 1, back.Meshes.Count);
        Assert.Equal(extended, back.Meshes.Count(m => m.HasExtendedChannel && m.ExtendedChannelValue == Marker));
        // M866: the added mesh goes before the final mesh, which stays last
        Assert.False(back.Meshes[^2].HasExtendedChannel);
        Assert.Equal(other, back.Meshes[^2].Submeshes[0].Material);
        Assert.Equal(before.Meshes[^1].Submeshes[0].Material, back.Meshes[^1].Submeshes[0].Material);
        // and LeagueToolkit (the client-side reader the editor validates against) reads the spliced file through its stripped form
        using var env = new EnvironmentAsset(new MemoryStream(ExtendedChannelRule.WithoutExtendedEntries(appended!, ext), false));
        Assert.Equal(count + 1, env.Meshes.Count);
    }

    /// <summary>Review follow-up: a material swap across the Mantis set rewrote the name but kept the record layout, so the file no longer
    /// matched the set and a delete in the same save failed. The swap now adds or drops the 48-byte entry with the material.</summary>
    [Fact]
    public void A_material_swap_across_the_mantis_set_moves_the_extended_entry_and_a_delete_still_works()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var (ext48, ext, extended, count, material) = Extended48(shipped);
        Assert.True(MapGeoBinary.TryReadEditable(ext48, out var before, ext));
        int toPlain = Enumerable.Range(0, count).First(i => before.Meshes[i].HasExtendedChannel && before.Meshes[i].Submeshes.Count == 1);
        int toMantis = Enumerable.Range(0, count).First(i => !before.Meshes[i].HasExtendedChannel && before.Meshes[i].Submeshes.Count == 1);
        string plain = before.Meshes[toMantis].Submeshes[0].Material;

        var asset = MapGeoDecoder.Decode(ext48, ext);
        asset.Meshes.First(m => m.Index == toPlain).MaterialEdit = plain;
        asset.Meshes.First(m => m.Index == toMantis).MaterialEdit = material;
        byte[]? swapped = MapGeoMaterialWriter.TryWriteMaterialEdits(ext48, asset.Meshes, ext, out var error);

        Assert.True(swapped is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(swapped!, out var back, ext));
        Assert.False(back.Meshes[toPlain].HasExtendedChannel);
        Assert.True(back.Meshes[toMantis].HasExtendedChannel);
        Assert.Equal(extended, back.Meshes.Count(m => m.HasExtendedChannel));

        int victim = Enumerable.Range(0, count).Last(i => i != toPlain && i != toMantis);
        byte[]? removed = MapGeoMeshRemover.Remove(swapped!, new[] { victim }, out var removeError, ext);
        Assert.True(removed is not null, removeError);
        Assert.True(MapGeoBinary.TryReadEditable(removed!, out var after, ext));
        Assert.Equal(count - 1, after.Meshes.Count);
    }

    [Fact]
    public void An_added_mesh_on_a_mantis_material_is_refused_rather_than_written_without_its_block()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map453 is not installed"); return; }
        var (ext48, ext, _, _, material) = Extended48(shipped);

        Assert.Null(MapGeoMeshAppender.Append(ext48, new[] { Quad(material) }, out var error, ext));
        Assert.Contains("Mantis", error);
    }
}
