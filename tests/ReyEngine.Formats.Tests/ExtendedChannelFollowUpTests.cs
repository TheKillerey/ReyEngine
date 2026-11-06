using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>Follow-up to M859/M856/M857: the combined-save refusal, the mapgeo's spelling of a recovered name, and the
/// source-level guards for the view-model wiring (the view model itself needs a running app).</summary>
public sealed class ExtendedChannelFollowUpTests
{
    private static IReadOnlySet<string> Ext() => new HashSet<string>(StringComparer.Ordinal) { "Maps/Mods/X_MAT" };

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir is null ? "" : File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void A_48_byte_map_refuses_each_edit_whose_writer_assumes_the_40_byte_block(bool added, bool layers, bool faces, bool grows, bool reshapes)
    {
        string? why = ExtendedChannelRule.RefuseCombinedSave(Ext(), added, layers, faces, grows, reshapes);
        Assert.NotNull(why);
        Assert.Contains("Nothing was written", why);
        Assert.Contains("Maps/Mods/X_MAT", why);
    }

    [Fact]
    public void Moves_and_grid_only_saves_stay_allowed_and_a_plain_map_is_never_refused()
    {
        Assert.Null(ExtendedChannelRule.RefuseCombinedSave(Ext(), false, false, false, false, false));
        Assert.Null(ExtendedChannelRule.RefuseCombinedSave(new HashSet<string>(), true, true, true, true, true));
        Assert.Null(ExtendedChannelRule.RefuseCombinedSave(null, true, true, true, true, true));
    }

    /// <summary>The file is untouched because the refusal returns before the first writer runs.</summary>
    [Fact]
    public void The_save_command_refuses_before_any_writer_runs()
    {
        string vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (vm.Length == 0) return;
        int body = vm.IndexOf("private async Task SaveMeshMoves()", StringComparison.Ordinal);
        int refuse = vm.IndexOf("ExtendedChannelRule.RefuseCombinedSave(", body, StringComparison.Ordinal);
        Assert.True(body > 0 && refuse > body);
        foreach (string writer in new[] { "MapGeoMaterialWriter.TryWriteMaterialEdits(", "MapGeoWriter.TryWriteWithMoves(", "MapGeoMeshAppender.Append(", "MapGeoLayerWriter.TryWriteLayerEdits(", "MapGeoFaceGrower.TryApply(" })
        {
            int at = vm.IndexOf(writer, body, StringComparison.Ordinal);
            Assert.True(at < 0 || at > refuse, writer + " runs before the refusal");
        }
    }

    [Fact]
    public void The_recovered_name_takes_the_mapgeos_spelling_over_the_materials_bins()
    {
        const string inBin = "maps/mods/mymap/materials/my_mat";
        const string inGeo = "Maps/Mods/MyMap/Materials/My_MAT";
        uint key = HashAlgorithms.Fnv1a(inGeo);
        Assert.Equal(key, HashAlgorithms.Fnv1a(inBin));

        var bytes = new List<byte> { 1, 2, 3, 4, 0xFF };
        bytes.AddRange(BitConverter.GetBytes((uint)inGeo.Length));
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(inGeo));
        bytes.AddRange(new byte[] { 9, 9 });

        uint H(string s) => HashAlgorithms.Fnv1a(s);
        var bin = new BinTreeObject(H("a"), H("StaticMaterialDef"), new BinTreeProperty[] { new BinTreeString(H("comment"), inBin) });
        using var ms = new MemoryStream();
        new BinTree(new[] { bin }, Array.Empty<string>()).Write(ms);

        var found = ExtendedChannelRule.NamesFromStrings(new HashSet<uint> { key }, ms.ToArray(), () => bytes.ToArray());
        Assert.Equal(inGeo, found[key]);
    }

    [Fact]
    public void The_host_cache_and_the_name_cache_are_single_reference_records_and_the_host_follows_the_drawn_bin()
    {
        string ch = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs");
        string mb = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.MapBanners.cs");
        string vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (ch.Length == 0) return;
        Assert.Contains("volatile CharacterHostEntry? _characterHostCache", ch);
        Assert.Contains("CharacterHostBin(binPath, bin)", ch);          // the bin being drawn, edited copy included
        Assert.Contains("ContentStamp(mapBytes)", ch);                  // invalidated by a map-bin edit
        Assert.Contains("volatile MapKeyNames _namesByMapKey", vm);
        Assert.Contains("map.Groups.ToArray()", vm);
        Assert.True(mb.IndexOf("OutlineCategoryClass", StringComparison.Ordinal) < mb.IndexOf("if (keep.Count == 0)", StringComparison.Ordinal),
            "outline categories must be added before the nothing-needed return");
    }

    [Fact]
    public void An_unplayed_animation_fraction_says_it_is_an_assumption()
    {
        Assert.Equal(0f, MaterialDriverState.Rest.FractionFor(123u));
        Assert.Contains("assumed fraction 0", Source("src", "ReyEngine.Formats", "Materials", "MaterialDrivers.cs"));
    }
}
