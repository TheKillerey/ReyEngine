using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M498: the bin-as-text editor's view model.
///
/// <para>In Formats.Tests because ReyEngine.App has no suite of its own. The behaviour that matters here is
/// what the editor REFUSES to do: a bin the game loads must not be written from text that does not parse,
/// and must not be written from a tree that cannot be read back. Both are silent failures otherwise.</para>
/// </summary>
public sealed class RitobinEditorViewModelTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] SampleBin()
    {
        var tree = new BinTree(new[]
        {
            new BinTreeObject(H("Test/Material"), H("StaticMaterialDef"), new BinTreeProperty[]
            {
                new BinTreeString(H("name"), "Test/Material"),
                new BinTreeU32(H("type"), 0),
            }),
        }, Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    /// <summary>The resolver matters: without one the writer emits hex field names, and a test that edits
    /// "type: u32" would silently match nothing and pass for the wrong reason. Real usage always has one.</summary>
    private static readonly Dictionary<uint, string> KnownNames =
        new[] { "Test/Material", "StaticMaterialDef", "name", "type" }.ToDictionary(H, s => s);

    private static RitobinTarget Target(byte[] bin, Action<byte[]>? onSave = null) =>
        new("test.bin", () => bin,
            bytes => { onSave?.Invoke(bytes); return Task.FromResult("project/test.bin"); },
            h => KnownNames.TryGetValue(h, out var n) ? n : null);

    [Fact]
    public void OpeningABinShowsItAsRitobinText()
    {
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(SampleBin()));

        Assert.True(vm.HasTarget);
        Assert.StartsWith("#PROP_text", vm.Text);
        Assert.Contains("entries: map[hash,embed] = {", vm.Text);
        Assert.False(vm.IsDirty);
        Assert.Contains("1 object", vm.Status);
    }

    [Fact]
    public async Task ApplyingUnchangedTextWritesTheSameBinBack()
    {
        byte[] original = SampleBin();
        byte[]? saved = null;
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(original, b => saved = b));

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal(original, saved);           // byte-exact, which is the whole premise of the editor
        Assert.False(vm.IsDirty);
        Assert.Contains("Saved", vm.Status);
    }

    [Fact]
    public async Task AnEditReachesTheSavedBytes()
    {
        byte[]? saved = null;
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(SampleBin(), b => saved = b));

        vm.Text = vm.Text.Replace("\"Test/Material\"", "\"Edited/Material\"");
        Assert.True(vm.IsDirty);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        var back = SafeBinTree.Parse(saved!);
        var obj = back.Objects.Values.Single();
        Assert.Equal("Edited/Material", ((BinTreeString)obj.Properties[H("name")]).Value);
    }

    [Fact]
    public async Task BrokenTextIsRefusedAndNothingIsWritten()
    {
        bool saveCalled = false;
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(SampleBin(), _ => saveCalled = true));

        vm.Text = vm.Text.Replace("type: u32 = 0", "type: nonsense = 0");
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(saveCalled);
        Assert.True(vm.HasErrors);
        Assert.Contains(vm.Errors, e => e.Contains("nonsense"));
        Assert.Contains("nothing was written", vm.Status);
    }

    [Fact]
    public void ValidateReportsProblemsWithoutTouchingTheBin()
    {
        bool saveCalled = false;
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(SampleBin(), _ => saveCalled = true));

        vm.Text = vm.Text.Replace("type: u32 = 0", "type: nope = 0");
        vm.ValidateCommand.Execute(null);

        Assert.False(saveCalled);
        Assert.True(vm.HasErrors);

        // ...and a good buffer clears them again.
        vm.Text = vm.Text.Replace("type: nope = 0", "type: u32 = 0");
        vm.ValidateCommand.Execute(null);
        Assert.False(vm.HasErrors);
        Assert.Contains("Valid", vm.Status);
    }

    [Fact]
    public void RevertRestoresTheFileAndClearsDirty()
    {
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(SampleBin()));
        string loaded = vm.Text;

        vm.Text = "garbage";
        Assert.True(vm.IsDirty);

        vm.RevertCommand.Execute(null);
        Assert.Equal(loaded, vm.Text);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void AnUnreadableBinIsReportedRatherThanThrowing()
    {
        var vm = new RitobinEditorViewModel();
        vm.Open(new RitobinTarget("broken.bin",
            () => new byte[] { 1, 2, 3, 4 },
            _ => Task.FromResult("")));

        Assert.Equal("", vm.Text);
        Assert.Contains("Could not read", vm.Status);
    }

    [Fact]
    public async Task ApplyWithNoBinOpenDoesNothing()
    {
        var vm = new RitobinEditorViewModel();
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("No bin", vm.Status);
    }
}
