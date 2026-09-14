using ReyEngine.Core.Hashing;
using ReyEngine.Core.Meta;
using ReyEngine.Core.Settings;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M731: "Sync failed: Object reference not set to an instance of an object" on every hash sync while a project
/// was open - the post-sync refresh dereferenced the single-WAD archive, which a folder project does not have -
/// and the automatic update that replaces pressing Sync, with the Settings toggle that turns it off.
/// </summary>
public sealed class HashAutoUpdateTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "rey-hash-update-" + Guid.NewGuid().ToString("N"));

    public HashAutoUpdateTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { try { Directory.Delete(_temp, recursive: true); } catch { } }

    // ===================================================== the setting

    [Fact]
    public void AutomaticHashUpdatesAreOnByDefaultAndTheDialogCanTurnThemOff()
    {
        Assert.True(new EditorSettings().AutoUpdateHashes);
        var target = new EditorSettings();
        target.CopyFrom(new EditorSettings { AutoUpdateHashes = false });
        Assert.False(target.AutoUpdateHashes);
    }

    // ===================================================== release folders

    [Fact]
    public void ATableIsReadFromItsReleaseFolderFirstThenFromTheFlatLayoutOlderInstallsUsed()
    {
        string root = _temp;
        string current = "hashes-2026-09-14", next = "hashes-2026-09-15";
        Directory.CreateDirectory(MimirSyncService.TableDir(root, current));
        File.WriteAllText(Path.Combine(MimirSyncService.TableDir(root, current), "game.lhdb"), "versioned");
        File.WriteAllText(Path.Combine(root, "game.lhdb"), "flat");

        // the release's own copy wins over a flat one
        Assert.Equal("versioned", File.ReadAllText(MimirSyncService.ResolveTablePath(root, current, "game.lhdb")));
        // a release with no folder yet falls back to the flat file an older install left
        Assert.Equal("flat", File.ReadAllText(MimirSyncService.ResolveTablePath(root, next, "game.lhdb")));
        // nothing anywhere: the release's folder is where a download would land
        Assert.Equal(Path.Combine(MimirSyncService.TableDir(root, next), "lcu.lhdb"),
            MimirSyncService.ResolveTablePath(root, next, "lcu.lhdb"));
        // no tag known at all: the flat path
        Assert.Equal(Path.Combine(root, "game.lhdb"), MimirSyncService.ResolveTablePath(root, null, "game.lhdb"));
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    [Fact]
    public void AnUnchangedTableIsCopiedFromThePreviousReleaseEvenThoughItsNameCarriesTheDate()
    {
        // Mimir names every table by release date, so the SAME content ships as game-2026-09-07.lhdb one week
        // and game-2026-09-14.lhdb the next. Sizes on the user's machine: 5 of 6 tables identical between two
        // releases. Fetching 44 MB again for a byte-identical game table is the thing this avoids.
        string root = _temp;
        string previousFile = Path.Combine(root, "game-2026-09-07.lhdb");   // the flat layout older installs used
        File.WriteAllText(previousFile, "the game table");
        var previous = new MimirManifest
        {
            ReleaseTag = "hashes-2026-09-07",
            Tables = { ["game"] = new MimirTable { File = "game-2026-09-07.lhdb", Sha256 = Sha256Of(previousFile) } },
        };
        var next = new MimirTable { File = "game-2026-09-14.lhdb", Sha256 = Sha256Of(previousFile) };
        string target = Path.Combine(MimirSyncService.TableDir(root, "hashes-2026-09-14"), next.File);

        Assert.Equal(previousFile, MimirSyncService.ReusableCopy(root, previous, next, target));

        // different content: nothing to reuse
        var changed = new MimirTable { File = "game-2026-09-14.lhdb", Sha256 = new string('0', 64) };
        Assert.Null(MimirSyncService.ReusableCopy(root, previous, changed, target));
        // and a table never listed before, with no flat copy either
        Assert.Null(MimirSyncService.ReusableCopy(root, null, next, target));
    }

    [Fact]
    public void ASyncRemovesEveryReleaseButTheCurrentOneAndLeavesEverythingElseAlone()
    {
        string root = _temp;
        Directory.CreateDirectory(MimirSyncService.TableDir(root, "hashes-2026-09-14"));
        File.WriteAllText(Path.Combine(MimirSyncService.TableDir(root, "hashes-2026-09-14"), "game-2026-09-14.lhdb"), "keep");
        Directory.CreateDirectory(MimirSyncService.TableDir(root, "hashes-2026-09-07"));
        File.WriteAllText(Path.Combine(MimirSyncService.TableDir(root, "hashes-2026-09-07"), "game-2026-09-07.lhdb"), "old");
        File.WriteAllText(Path.Combine(root, "game-2026-08-31.lhdb"), "older flat");
        File.WriteAllText(Path.Combine(root, "lcu-2026-09-14.lhdb"), "current, still flat");
        File.WriteAllText(Path.Combine(root, "binfields-2026-09-14.lhdb.part"), "interrupted download");
        File.WriteAllText(Path.Combine(root, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "not ours");
        var current = new MimirManifest
        {
            ReleaseTag = "hashes-2026-09-14",
            Tables =
            {
                ["game"] = new MimirTable { File = "game-2026-09-14.lhdb" },
                ["lcu"] = new MimirTable { File = "lcu-2026-09-14.lhdb" },
            },
        };

        int removed = MimirSyncService.RemoveOtherReleases(root, "hashes-2026-09-14", current);

        Assert.Equal(3, removed);   // the old folder, the older flat table, the stray .part
        Assert.True(Directory.Exists(MimirSyncService.TableDir(root, "hashes-2026-09-14")));
        Assert.False(Directory.Exists(MimirSyncService.TableDir(root, "hashes-2026-09-07")));
        Assert.False(File.Exists(Path.Combine(root, "game-2026-08-31.lhdb")));
        Assert.True(File.Exists(Path.Combine(root, "lcu-2026-09-14.lhdb")));   // named by the current manifest
        Assert.False(File.Exists(Path.Combine(root, "binfields-2026-09-14.lhdb.part")));
        Assert.True(File.Exists(Path.Combine(root, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(root, "notes.txt")));
    }

    [Fact]
    public void AReleaseTagIsAFolderNameWhateverGitHubPutsInIt()
    {
        Assert.Equal("hashes-2026-09-14", MimirSyncService.SafeTag("hashes-2026-09-14"));
        string odd = MimirSyncService.SafeTag("v1/2:3*4");
        Assert.DoesNotContain(odd, c => Path.GetInvalidFileNameChars().Contains(c));
        Assert.Equal("release", MimirSyncService.SafeTag("   "));
    }

    [Fact]
    public void ASyncNeverWritesATableOverTheFlatPathTheRunningDatabaseMayHaveMapped()
    {
        // The whole point of the release folders: CreateFromFile maps with FileShare.Read, so a File.Move onto a
        // mapped table fails on Windows, and disposing the map under a lookup is M508's torn-read race.
        string? source = Source("src", "ReyEngine.Core", "Hashing", "MimirSyncService.cs");
        if (source is null) return;
        Assert.Contains("string tableDir = TableDir(root, tag);", source);
        Assert.Contains("string target = Path.Combine(tableDir, table.File);", source);
        Assert.DoesNotContain("string target = Path.Combine(ReyPaths.MimirDir, table.File);", source);
        // the manifest is switched last, and readers resolve through the release folder
        int lastMove = source.LastIndexOf("File.Move(temp, target, overwrite: true);", StringComparison.Ordinal);
        int manifest = source.IndexOf("File.WriteAllText(ReyPaths.MimirManifestFile, manifest.ToJson());", StringComparison.Ordinal);
        Assert.True(lastMove > 0 && manifest > 0);
        Assert.Contains("ResolveTablePath(ReyPaths.MimirDir, manifest.ReleaseTag, table.File)", source);
    }

    // ===================================================== CommunityDragon: only what changed

    [Fact]
    public void OnlyChangedCommunityDragonFilesAreFetched()
    {
        var listing = new[]
        {
            new HashSyncService.RemoteFile("hashes.game.txt.0", "u0", 10, "aaa"),
            new HashSyncService.RemoteFile("hashes.game.txt.1", "u1", 10, "bbb"),
            new HashSyncService.RemoteFile("hashes.lcu.txt", "u2", 10, "ccc"),
            new HashSyncService.RemoteFile("hashes.binentries.txt", "u3", 10, "ddd"),
        };
        var synced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hashes.game.txt.0"] = "AAA",   // same content, other case
            ["hashes.game.txt.1"] = "old",   // changed upstream
            ["hashes.lcu.txt"] = "ccc",      // same, but the file is gone locally
        };
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hashes.game.txt.0", "hashes.game.txt.1" };

        var changed = HashSyncService.ChangedFiles(listing, synced, onDisk.Contains).Select(f => f.Name).ToList();

        Assert.Equal(new[] { "hashes.game.txt.1", "hashes.lcu.txt", "hashes.binentries.txt" }, changed);
    }

    [Fact]
    public void TheCommunityDragonManifestRoundTripsAndAMissingOneMeansSyncEverything()
    {
        string path = Path.Combine(_temp, "communitydragon.manifest.json");
        HashSyncService.SaveManifest(path, new[]
        {
            new HashSyncService.RemoteFile("hashes.game.txt.0", "u", 1, "aaa"),
            new HashSyncService.RemoteFile("hashes.lcu.txt", "u", 1, "ccc"),
        });
        var back = HashSyncService.LoadManifest(path);
        Assert.Equal("aaa", back["HASHES.GAME.TXT.0"]);
        Assert.Equal(2, back.Count);

        Assert.Empty(HashSyncService.LoadManifest(Path.Combine(_temp, "nope.json")));
        File.WriteAllText(path, "{ not json");
        Assert.Empty(HashSyncService.LoadManifest(path));
    }

    // ===================================================== meta classes: the ETag

    [Fact]
    public void TheMetaEtagIsKeptBesideTheDatabaseAndForgottenWhenTheServerSendsNone()
    {
        string path = Path.Combine(_temp, "meta.db.json.etag");
        Assert.Null(MetaClassSyncService.ReadEtag(path));
        MetaClassSyncService.WriteEtag(path, "\"abc123\"");
        Assert.Equal("\"abc123\"", MetaClassSyncService.ReadEtag(path));
        MetaClassSyncService.WriteEtag(path, null);
        Assert.Null(MetaClassSyncService.ReadEtag(path));
        Assert.False(File.Exists(path));
    }

    // ===================================================== the wiring

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) continue;
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return null;
    }

    [Fact]
    public void ApplyingNewHashesWorksOnAProjectNotOnlyOnASingleWad()
    {
        string? main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (main is null) return;
        int start = main.IndexOf("private void ApplyHashesToOpenWad()", StringComparison.Ordinal);
        Assert.True(start > 0);
        string body = main.Substring(start, main.IndexOf("[RelayCommand]", start, StringComparison.Ordinal) - start);
        // the project branch: every WAD mount re-resolved, the index and tree rebuilt
        Assert.Contains("mounts.Mounts.OfType<WadMount>()", body);
        Assert.Contains("_resolver.RefreshArchive(wad.Archive)", body);
        Assert.Contains("mounts.Rebuild();", body);
        Assert.Contains("BuildProjectTree();", body);
        // and the archive is never touched without a check
        Assert.Contains("if (_archive is null) return;", body);
        Assert.DoesNotContain("_resolver.RefreshArchive(_archive);\n        RebuildTree();\n        _log.Success(\"Hashes\", $\"Resolved {resolved:n0} / {_archive.Entries.Count", body);
    }

    [Fact]
    public void TheAutomaticUpdateIsWiredAtStartupAndGatedBySettings()
    {
        string? main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        string? window = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        string? settingsVm = Source("src", "ReyEngine.App", "ViewModels", "SettingsViewModel.cs");
        string? settingsXaml = Source("src", "ReyEngine.App", "Views", "SettingsWindow.axaml");
        if (main is null || window is null || settingsVm is null || settingsXaml is null) return;

        Assert.Contains("_ = vm.AutoUpdateHashesAsync();", window);
        Assert.Contains("if (!Settings.AutoUpdateHashes) return;", main);
        // whichever source the install uses, and never a new one
        Assert.Contains("if (_mimir.Local is not null)", main);
        Assert.Contains("else if (HashSyncService.HasLocalRaw)", main);
        Assert.Contains("_metaSync.SyncIfChangedAsync(", main);
        // the dialog reads and writes it
        Assert.Contains("AutoUpdateHashes = s.AutoUpdateHashes;", settingsVm);
        Assert.Contains("AutoUpdateHashes = AutoUpdateHashes,", settingsVm);
        Assert.Contains("IsChecked=\"{Binding AutoUpdateHashes}\"", settingsXaml);
    }
}
