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

    /// <summary>Three objects, so scoping and the merge have something to be wrong about.</summary>
    private static byte[] SampleBin()
    {
        var tree = new BinTree(new[]
        {
            Material("Test/Material", "first"),
            Material("Test/Second", "second"),
            Material("Test/Third", "third"),
        }, Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();

        static BinTreeObject Material(string path, string label) =>
            new(H(path), H("StaticMaterialDef"), new BinTreeProperty[]
            {
                new BinTreeString(H("name"), label),
                new BinTreeU32(H("type"), 0),
            });
    }

    /// <summary>The resolver matters: without one the writer emits hex field names, and a test that edits
    /// "type: u32" would silently match nothing and pass for the wrong reason. Real usage always has one.</summary>
    private static readonly Dictionary<uint, string> KnownNames =
        new[] { "Test/Material", "Test/Second", "Test/Third", "StaticMaterialDef", "name", "type" }
            .ToDictionary(H, s => s);

    private static RitobinTarget Target(byte[] bin, Action<byte[]>? onSave = null) =>
        new("test.bin", () => bin,
            bytes => { onSave?.Invoke(bytes); return Task.FromResult("project/test.bin"); },
            h => KnownNames.TryGetValue(h, out var n) ? n : null);

    [Fact]
    public void OpensOnASingleObjectRatherThanTheWholeFile()
    {
        // M499: the whole file in one TextBox is what made editing unusable — Avalonia's TextBox does not
        // virtualise, so every keystroke re-measures the entire document. Opening scoped is the fix, and it
        // has to be the DEFAULT or the first thing the user sees is the slow state.
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(SampleBin()));

        Assert.True(vm.HasTarget);
        Assert.StartsWith("#PROP_text", vm.Text);
        Assert.Contains("entries: map[hash,embed] = {", vm.Text);
        Assert.False(vm.IsDirty);

        Assert.False(vm.IsWholeFile);
        Assert.NotNull(vm.SelectedScope);
        Assert.False(vm.SelectedScope!.IsAll);

        // One object in the buffer, not three.
        Assert.Single(RitobinTextReader.Read(vm.Text, out _)!.Objects);
        // ...and all three are offered, plus the whole-file entry.
        Assert.Equal(4, vm.Scopes.Count);
        Assert.Single(vm.Scopes.Where(s => s.IsAll));
    }

    [Fact]
    public void TheWholeFileScopeShowsEverythingAndSaysItIsSlow()
    {
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(SampleBin()));

        vm.SelectedScope = vm.Scopes.Single(s => s.IsAll);

        Assert.True(vm.IsWholeFile);
        Assert.Equal(3, RitobinTextReader.Read(vm.Text, out _)!.Objects.Count);
        Assert.Contains("slow", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditingOneObjectLeavesTheOthersByteIdentical()
    {
        // The point of scoping is that Apply's blast radius is one object. If the merge dropped or rewrote
        // the untouched ones, a small edit would quietly rewrite the whole bin.
        byte[] original = SampleBin();
        byte[]? saved = null;
        var vm = new RitobinEditorViewModel();
        vm.Open(Target(original, b => saved = b));

        var scope = vm.Scopes.First(s => !s.IsAll && s.Label.Contains("Second"));
        vm.SelectedScope = scope;
        vm.Text = vm.Text.Replace("\"second\"", "\"second_edited\"");
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        var before = SafeBinTree.Parse(original);
        var after = SafeBinTree.Parse(saved!);
        Assert.Equal(before.Objects.Count, after.Objects.Count);

        int changed = 0;
        foreach (var (hash, obj) in before.Objects)
        {
            Assert.True(after.Objects.TryGetValue(hash, out var other), $"object {hash:x8} was lost");
            if (!BinPropEquality.ObjectsEqual(obj, other!)) changed++;
        }
        Assert.Equal(1, changed);
        Assert.Equal("second_edited",
            ((BinTreeString)after.Objects[H("Test/Second")].Properties[H("name")]).Value);
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

        // Edit a PROPERTY, not the file's own `type: string = "PROP"` header — the parser ignores that by
        // design, so an edit there changes nothing and would make this pass while proving nothing.
        vm.SelectedScope = vm.Scopes.First(s => !s.IsAll && s.Label.Contains("Material"));
        vm.Text = vm.Text.Replace("\"first\"", "\"first_edited\"");
        Assert.True(vm.IsDirty);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        var back = SafeBinTree.Parse(saved!);
        Assert.Equal("first_edited",
            ((BinTreeString)back.Objects[H("Test/Material")].Properties[H("name")]).Value);
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
