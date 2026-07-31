using System.Text.Json;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

public sealed class PatchAutoUpdateTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "rey-patch-tests-" + Guid.NewGuid().ToString("N"));

    public PatchAutoUpdateTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { try { Directory.Delete(_temp, recursive: true); } catch { } }

    [Fact]
    public void Detects_installed_patch_from_Riot_content_metadata()
    {
        File.WriteAllText(Path.Combine(_temp, "content-metadata.json"),
            "{\"version\":\"16.15.8013452+branch.releases-16-15.content.release\"}");

        var detected = RiotPatchVersionDetector.Detect(_temp);

        Assert.NotNull(detected);
        Assert.Equal("16.15", detected.Patch);
        Assert.Equal("content-metadata.json", detected.Source);
    }

    [Theory]
    [InlineData("16.10.0", "16.15", "16.10")]
    [InlineData("1.0.0", "16.15", null)]
    [InlineData("17.1.0", "16.15", null)]
    public void Legacy_baseline_is_only_inferred_from_a_plausible_patch_version(
        string modVersion, string current, string? expected)
        => Assert.Equal(expected, RiotPatchVersionDetector.InferProjectBaseline(modVersion, current));

    [Fact]
    public void Folder_report_path_does_not_nest_the_metadata_directory_twice()
    {
        var project = new ReyProject
        {
            RootPath = _temp,
            ProjectFilePath = Path.Combine(_temp, ".reyengine", "project.json"),
        };

        Assert.Equal(Path.Combine(_temp, ".reyengine", "reports"), ProjectWorkspace.ReportsDir(project));
    }

    [Fact]
    public void Legacy_project_json_enables_safe_automatic_defaults()
    {
        var project = JsonSerializer.Deserialize<ReyProject>("{\"Name\":\"Legacy\",\"ProjectVersion\":1}");

        Assert.NotNull(project);
        Assert.True(project.AutoUpdateOnRiotPatch);
        Assert.True(project.AutoBuildAfterPatchUpdate);
        Assert.Null(project.RiotPatchVersion);
    }

    [Fact]
    public async Task Failed_batch_write_rolls_back_every_earlier_file()
    {
        var old = new Dictionary<ulong, byte[]> { [1] = [1], [2] = [2] };
        var installed = new Dictionary<ulong, byte[]> { [1] = [11], [2] = [22] };
        var project = old.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        int updatedWrites = 0, completed = 0;
        var vm = BuildVm(old, installed, project);
        vm.RunCompleted = _ => { completed++; return Task.CompletedTask; };
        vm.SaveBytes = (entry, bytes) =>
        {
            bool isUpdate = bytes.AsSpan().SequenceEqual(installed[entry.PathHash]);
            if (isUpdate && ++updatedWrites == 2) return Task.FromResult(false);
            project[entry.PathHash] = bytes.ToArray();
            return Task.FromResult(true);
        };

        await vm.InitAsync();
        var result = await vm.RunUpdateAsync();

        Assert.NotNull(result);
        Assert.True(result.RolledBack);
        Assert.True(result.Failed > 0);
        Assert.Equal(old[1], project[1]);
        Assert.Equal(old[2], project[2]);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Preparation_failure_writes_nothing()
    {
        var old = new Dictionary<ulong, byte[]> { [1] = [1], [2] = [2] };
        var installed = new Dictionary<ulong, byte[]> { [1] = [11], [2] = [22] };
        var project = old.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        int writes = 0;
        var vm = BuildVm(old, installed, project);
        vm.DownloadOld = (_, rel) => rel == "data/two.bin"
            ? throw new IOException("archive unavailable")
            : Task.FromResult<byte[]?>(old[1]);
        vm.SaveBytes = (_, _) => { writes++; return Task.FromResult(true); };

        await vm.InitAsync();
        var result = await vm.RunUpdateAsync();

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, writes);
        Assert.Contains("No project files were changed", result.Summary);
    }

    [Fact]
    public async Task Successful_batch_backs_up_then_updates_every_file()
    {
        var old = new Dictionary<ulong, byte[]> { [1] = [1], [2] = [2] };
        var installed = new Dictionary<ulong, byte[]> { [1] = [11], [2] = [22] };
        var project = old.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var backups = new HashSet<ulong>();
        var vm = BuildVm(old, installed, project);
        vm.Backup = (row, bytes) => backups.Add(row.Entry.PathHash) ? row.ProjectRel : null;

        await vm.InitAsync();
        var result = await vm.RunUpdateAsync();

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(2, result.Updated);
        Assert.Equal(new byte[] { 11 }, project[1]);
        Assert.Equal(new byte[] { 22 }, project[2]);
        Assert.Equal(new ulong[] { 1, 2 }, backups.Order());
        Assert.True(vm.RunFinished);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public async Task Validation_failure_keeps_successful_update_and_requires_review()
    {
        var old = new Dictionary<ulong, byte[]> { [1] = [1], [2] = [2] };
        var installed = new Dictionary<ulong, byte[]> { [1] = [11], [2] = [22] };
        var project = old.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        int completed = 0;
        var vm = BuildVm(old, installed, project);
        vm.ValidateAfter = true;
        vm.RunValidate = () => throw new InvalidDataException("validator unavailable");
        vm.RunCompleted = _ => { completed++; return Task.CompletedTask; };

        await vm.InitAsync();
        var result = await vm.RunUpdateAsync();

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.True(result.ValidationFailed);
        Assert.True(result.NeedsReview);
        Assert.Equal(new byte[] { 11 }, project[1]);
        Assert.Equal(new byte[] { 22 }, project[2]);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Updater_blocks_missing_target_same_patch_and_downgrade()
    {
        var old = new Dictionary<ulong, byte[]> { [1] = [1], [2] = [2] };
        var installed = new Dictionary<ulong, byte[]> { [1] = [11], [2] = [22] };
        var project = old.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var vm = BuildVm(old, installed, project);

        vm.TargetPatch = null;
        await vm.InitAsync();
        Assert.False(vm.CanRun);

        vm.TargetPatch = "16.14";
        Assert.False(vm.CanRun);

        vm.TargetPatch = "16.13";
        Assert.False(vm.CanRun);
    }

    private static PatchUpdateWindowViewModel BuildVm(
        IReadOnlyDictionary<ulong, byte[]> old,
        IReadOnlyDictionary<ulong, byte[]> installed,
        IDictionary<ulong, byte[]> project)
    {
        var vm = new PatchUpdateWindowViewModel
        {
            SelectedPatch = "16.14",
            TargetPatch = "16.15",
            ListPatches = () => Task.FromResult<IReadOnlyList<string>>(["16.15", "16.14"]),
            DownloadOld = (_, rel) => Task.FromResult<byte[]?>(old[rel.EndsWith("one.bin") ? 1UL : 2UL]),
            ReadCurrentOriginal = entry => installed[entry.PathHash],
            ReadProjectBytes = hash => project[hash],
            SaveBytes = (entry, bytes) =>
            {
                project[entry.PathHash] = bytes.ToArray();
                return Task.FromResult(true);
            },
            Backup = (row, _) => row.ProjectRel,
            ValidateAfter = false,
        };
        vm.Bins.Add(Row(1, "data/one.bin"));
        vm.Bins.Add(Row(2, "data/two.bin"));
        return vm;
    }

    private static PatchUpdateBinRowViewModel Row(ulong hash, string rel) => new()
    {
        Rel = rel,
        ProjectRel = "Map11/" + rel,
        Entry = new WadAssetEntry { PathHash = hash, Path = rel, IsResolved = true },
    };
}
