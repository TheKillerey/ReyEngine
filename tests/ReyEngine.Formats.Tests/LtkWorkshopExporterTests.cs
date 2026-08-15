using System.Text.Json.Nodes;
using ReyEngine.Core.Build;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M470: sending a project into LTK Manager's workshop folder. Deliberately in Core so it CAN be tested —
/// the surrounding command lives in ReyEngine.App, which has no test project at all.
///
/// <para>The layout under test was read off the user's real workshop (oldriftday, mapforcer, …), not
/// invented: <c>&lt;slug&gt;\mod.config.json</c> plus <c>content\&lt;layer&gt;\&lt;Wad&gt;.wad.client\…</c>.</para>
/// </summary>
public class LtkWorkshopExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey_ltk_" + Guid.NewGuid().ToString("N"));
    private readonly string _src;

    public LtkWorkshopExporterTests()
    {
        Directory.CreateDirectory(_root);
        _src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(_src, "data", "maps"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Workshop
    {
        get { var w = Path.Combine(_root, "workshop"); Directory.CreateDirectory(w); return w; }
    }

    private LtkSendOptions Opts(string slug = "my-map") => new(
        WorkshopRoot: Workshop, Slug: slug, DisplayName: "My Map", Version: "1.2.3",
        Description: "a map", Author: "TheKillerey");

    private (string, string, string) File1(string rel = "data/maps/a.mapgeo", string body = "one")
    {
        string abs = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, body);
        return ("Map453", rel, abs);
    }

    [Fact]
    public void Creates_the_workshop_layout_the_manager_expects()
    {
        var r = LtkWorkshopExporter.Send(Opts(), new[] { File1() });

        Assert.True(r.Created);
        Assert.Equal(1, r.FilesWritten);
        string mod = Path.Combine(Workshop, "my-map");
        Assert.True(File.Exists(Path.Combine(mod, "mod.config.json")));
        Assert.True(File.Exists(Path.Combine(mod, "README.md")));
        // The mount folder is named after the WAD it targets, with the suffix appended.
        Assert.True(File.Exists(Path.Combine(mod, "content", "base", "Map453.wad.client", "data", "maps", "a.mapgeo")));
    }

    [Fact]
    public void Second_send_updates_in_place_instead_of_duplicating()
    {
        LtkWorkshopExporter.Send(Opts(), new[] { File1(body: "one") });
        var r = LtkWorkshopExporter.Send(Opts(), new[] { File1(body: "two") });

        Assert.False(r.Created);
        Assert.Single(Directory.GetDirectories(Workshop));
        Assert.Equal("two", File.ReadAllText(
            Path.Combine(Workshop, "my-map", "content", "base", "Map453.wad.client", "data", "maps", "a.mapgeo")));
    }

    /// <summary>A file removed from the project must vanish from the mod, or the next build silently ships
    /// an asset the user deleted.</summary>
    [Fact]
    public void Files_dropped_from_the_project_are_removed_from_the_mod()
    {
        LtkWorkshopExporter.Send(Opts(), new[] { File1("data/maps/a.mapgeo"), File1("data/maps/b.mapgeo") });
        var r = LtkWorkshopExporter.Send(Opts(), new[] { File1("data/maps/a.mapgeo") });

        string layer = Path.Combine(Workshop, "my-map", "content", "base", "Map453.wad.client", "data", "maps");
        Assert.True(File.Exists(Path.Combine(layer, "a.mapgeo")));
        Assert.False(File.Exists(Path.Combine(layer, "b.mapgeo")));
        Assert.Equal(2, r.FilesDeleted);
    }

    /// <summary>The whole reason the config is merged rather than serialized from a model: the user's real
    /// configs carry tags, maps and layer descriptions this exporter does not model, and a typed round-trip
    /// would drop every one of them.</summary>
    [Fact]
    public void Unmodelled_config_fields_survive_an_update()
    {
        LtkWorkshopExporter.Send(Opts(), new[] { File1() });
        string cfgPath = Path.Combine(Workshop, "my-map", "mod.config.json");
        var cfg = JsonNode.Parse(File.ReadAllText(cfgPath))!.AsObject();
        cfg["tags"] = new JsonArray("map-skin");
        cfg["maps"] = new JsonArray("summoners-rift");
        cfg["some_future_field"] = 42;
        File.WriteAllText(cfgPath, cfg.ToJsonString());

        LtkWorkshopExporter.Send(Opts() with { Version = "9.9.9" }, new[] { File1() });

        var back = JsonNode.Parse(File.ReadAllText(cfgPath))!.AsObject();
        Assert.Equal("map-skin", back["tags"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("summoners-rift", back["maps"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(42, back["some_future_field"]!.GetValue<int>());
        Assert.Equal("9.9.9", back["version"]!.GetValue<string>());   // ours IS updated
    }

    /// <summary>Match on the config's name, not the folder name — a mod the user renamed on disk must be
    /// updated, not duplicated.</summary>
    [Fact]
    public void A_renamed_folder_is_still_matched_by_its_config_name()
    {
        LtkWorkshopExporter.Send(Opts(), new[] { File1() });
        Directory.Move(Path.Combine(Workshop, "my-map"), Path.Combine(Workshop, "renamed-on-disk"));

        var r = LtkWorkshopExporter.Send(Opts(), new[] { File1() });

        Assert.False(r.Created);
        Assert.Single(Directory.GetDirectories(Workshop));
        Assert.EndsWith("renamed-on-disk", r.ModFolder);
    }

    /// <summary>A folder with no mod.config.json is not an LTK mod, so it is never adopted and never has
    /// its contents replaced — this is what keeps the delete confined to things we created.</summary>
    [Fact]
    public void A_folder_that_is_not_a_mod_is_not_adopted()
    {
        Directory.CreateDirectory(Path.Combine(Workshop, "not-a-mod", "precious"));
        File.WriteAllText(Path.Combine(Workshop, "not-a-mod", "precious", "keep.txt"), "keep");

        Assert.Null(LtkWorkshopExporter.FindExistingModFolder(Workshop, "not-a-mod-slug"));
        Assert.True(File.Exists(Path.Combine(Workshop, "not-a-mod", "precious", "keep.txt")));
    }

    [Fact]
    public void A_traversing_slug_is_refused_rather_than_written()
    {
        var bad = Opts(slug: Path.Combine("..", "escape"));
        Assert.Throws<InvalidOperationException>(() => LtkWorkshopExporter.Send(bad, new[] { File1() }));
    }

    [Theory]
    [InlineData("Map453", "Map453.wad.client")]
    [InlineData("Map11.wad.client", "Map11.wad.client")]   // already suffixed - left alone
    [InlineData("Map11.wad", "Map11.wad")]                 // mapforcer ships this form
    public void Mount_folders_follow_the_cslol_naming_convention(string input, string expected) =>
        Assert.Equal(expected, LtkWorkshopExporter.MountFolderName(input));

    [Theory]
    [InlineData("Old Summoner's Rift - Day", "old-summoners-rift-day")]
    [InlineData("winterriftseason25", "winterriftseason25")]
    [InlineData("   ", "reyengine-mod")]
    public void Slugs_match_the_shipped_workshop_convention(string input, string expected) =>
        Assert.Equal(expected, LtkWorkshopExporter.Slugify(input));
}
