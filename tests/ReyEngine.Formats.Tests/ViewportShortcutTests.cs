using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M553: the viewport's delete / copy / cut / paste shortcuts.
///
/// <para>The key handler reaches the view model by NAME through generated command properties, so a
/// rename compiles on the view-model side and silently breaks the shortcut. These walk the handler's
/// own source for the commands it invokes.</para>
/// </summary>
public sealed class ViewportShortcutTests
{
    private static string? RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void TheClipboardCommandsExistOnTheViewModel()
    {
        var vm = typeof(MainWindowViewModel);
        foreach (string name in new[]
        {
            "CopyMapContentSelectionCommand", "CutMapContentSelectionCommand",
            "PasteMapContentCommand", "DeleteMapContentSelectionCommand",
        })
            Assert.True(vm.GetProperty(name) is not null, $"{name} is missing");
    }

    [Fact]
    public void EveryCommandTheKeyHandlerInvokesExists()
    {
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs") is not { } file) return;
        string source = File.ReadAllText(file);
        int start = source.IndexOf("Global editor shortcuts", StringComparison.Ordinal);
        if (start < 0) return;

        var vm = typeof(MainWindowViewModel);
        var missing = new List<string>();
        foreach (Match m in Regex.Matches(source[start..], @"vm\.([A-Za-z0-9_]+Command)\b"))
            if (vm.GetProperty(m.Groups[1].Value) is null) missing.Add(m.Groups[1].Value);

        Assert.True(missing.Count == 0,
            "the key handler calls commands that do not exist: " + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void TypingInATextBoxDoesNotReachTheEditorShortcuts()
    {
        // Ctrl+C and Delete belong to a focused text field first. Without this guard, renaming a mesh and
        // pressing Delete would flag the map objects behind the field instead of editing the text.
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs") is not { } file) return;
        string source = File.ReadAllText(file);
        int start = source.IndexOf("Global editor shortcuts", StringComparison.Ordinal);
        if (start < 0) return;
        string handler = source[start..];

        Assert.Contains("e.Source is TextBox", handler);
        // and the guard has to come before any command runs
        int guard = handler.IndexOf("e.Source is TextBox", StringComparison.Ordinal);
        int firstCommand = handler.IndexOf("vm.", StringComparison.Ordinal);
        Assert.True(guard < firstCommand, "the TextBox guard must run before any command");
    }

    [Fact]
    public void DeleteIsUnmodifiedAndTheClipboardKeysAreControl()
    {
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs") is not { } file) return;
        string handler = File.ReadAllText(file);
        int start = handler.IndexOf("Global editor shortcuts", StringComparison.Ordinal);
        if (start < 0) return;
        handler = handler[start..];

        Assert.Contains("Key.Delete && e.KeyModifiers == KeyModifiers.None", handler);
        foreach (string key in new[] { "Key.C", "Key.X", "Key.V" })
            Assert.Contains(key, handler);
        // the Control gate must sit between the Delete branch and the clipboard branches
        int control = handler.IndexOf("!e.KeyModifiers.HasFlag(KeyModifiers.Control)", StringComparison.Ordinal);
        Assert.True(control > 0 && control < handler.IndexOf("Key.C", StringComparison.Ordinal),
            "the clipboard keys must be gated behind Control");
    }
}
