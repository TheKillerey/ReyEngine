using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeagueToolkit.Core.Meta;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>The bin the editor is working on, and the two things it needs to do with it.</summary>
public sealed record RitobinTarget(
    string DisplayName,
    Func<byte[]> Read,
    Func<byte[], Task<string>> Save,
    Func<uint, string?>? ResolveName = null,
    Func<ulong, string?>? ResolveWadPath = null,
    Action<string>? Log = null);

/// <summary>One entry in the object picker.</summary>
public sealed record RitobinScope(uint PathHash, string Label, bool IsAll)
{
    public override string ToString() => Label;
}

/// <summary>
/// M498: edit a .bin as ritobin text.
///
/// <para>The whole point of the format is that a bin becomes a thing you can read and change directly,
/// rather than through a property grid that only exposes what someone thought to model.</para>
///
/// <para><b>M499: the buffer holds ONE OBJECT by default, not the whole file.</b> Editing was unusable
/// otherwise, and the reason is structural rather than tunable: Avalonia's TextBox does not virtualise, so
/// every keystroke re-measures the entire document. Map453's materials.bin is 36,697 lines and 1.1 MB;
/// a single material out of it is about thirty. Scoping the buffer is a three-orders-of-magnitude
/// reduction, and it narrows what an Apply can damage at the same time — only the edited object is
/// replaced, the rest of the tree is carried through untouched.</para>
///
/// <para><b>Nothing is written unless the text parses AND re-serialises.</b> Applying edits to a bin the
/// game loads is the kind of operation where a half-valid write is worse than a refusal.</para>
/// </summary>
public sealed partial class RitobinEditorViewModel : ObservableObject
{
    private RitobinTarget? _target;
    private BinTree? _tree;              // the WHOLE bin; the buffer is a window onto one of its objects
    private bool _loading;

    private void _log(string message) => _target?.Log?.Invoke(message);

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _title = "No bin open";
    [ObservableProperty] private string _status = "Open a .bin to edit it as ritobin text.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasTarget;
    [ObservableProperty] private bool _isDirty;

    /// <summary>Objects in the bin, plus an "everything" entry.</summary>
    public ObservableCollection<RitobinScope> Scopes { get; } = new();

    [ObservableProperty] private RitobinScope? _selectedScope;

    /// <summary>Parse problems, each with the line it sits on.</summary>
    public ObservableCollection<string> Errors { get; } = new();
    public bool HasErrors => Errors.Count > 0;

    /// <summary>Shown when the whole file is in the buffer, because that is the slow mode and the user
    /// should know why rather than conclude the editor is broken.</summary>
    public bool IsWholeFile => SelectedScope?.IsAll == true;

    partial void OnTextChanged(string value)
    {
        // Cheap: any keystroke marks it dirty. Comparing against the loaded text would be a full compare of
        // the whole buffer on EVERY keystroke, which is exactly the sort of thing that made this slow.
        if (!_loading) IsDirty = true;
    }

    partial void OnSelectedScopeChanged(RitobinScope? value)
    {
        OnPropertyChanged(nameof(IsWholeFile));
        LoadScope();
    }

    /// <summary>Point the editor at a bin, read it once, and show its first object.</summary>
    public void Open(RitobinTarget target)
    {
        _target = target;
        HasTarget = true;
        Title = target.DisplayName;
        Errors.Clear();
        OnPropertyChanged(nameof(HasErrors));

        try
        {
            _tree = SafeBinTree.Parse(target.Read());
        }
        catch (Exception ex)
        {
            _tree = null;
            Scopes.Clear();
            Text = "";
            Status = "Could not read this bin: " + ex.Message;
            IsDirty = false;
            return;
        }

        BuildScopes();
        // Start on a single object rather than the whole file: opening straight into a 36,697-line buffer
        // is the state that made this feel broken. Prefer one whose name RESOLVED, so the first thing shown
        // is recognisable rather than "0x0c01cd6c".
        SelectedScope = Scopes.FirstOrDefault(s => !s.IsAll && !s.Label.StartsWith("0x", StringComparison.Ordinal))
                        ?? Scopes.FirstOrDefault(s => !s.IsAll)
                        ?? Scopes.FirstOrDefault();
        if (SelectedScope is null) LoadScope();
    }

    private void BuildScopes()
    {
        Scopes.Clear();
        if (_tree is null) return;

        foreach (var (hash, obj) in _tree.Objects.OrderBy(o => Label(o.Key, o.Value), StringComparer.OrdinalIgnoreCase))
            Scopes.Add(new RitobinScope(hash, Label(hash, obj), IsAll: false));

        Scopes.Insert(0, new RitobinScope(0, $"◆ Whole file ({_tree.Objects.Count:n0} objects) — slow to edit", IsAll: true));

        string Label(uint hash, BinTreeObject obj)
        {
            string name = _target?.ResolveName?.Invoke(hash) ?? "";
            string cls = _target?.ResolveName?.Invoke(obj.ClassHash) ?? $"0x{obj.ClassHash:x8}";
            return (string.IsNullOrEmpty(name) ? $"0x{hash:x8}" : Short(name)) + $"   [{cls}]";
        }

        static string Short(string n) { int i = n.LastIndexOf('/'); return i >= 0 ? n[(i + 1)..] : n; }
    }

    /// <summary>Fill the buffer from whatever scope is selected. The text is always a COMPLETE ritobin
    /// document — header and all — so the same parser reads it back with no special-casing.</summary>
    private void LoadScope()
    {
        if (_tree is null || _target is null) { Text = ""; return; }

        var subject = SelectedScope is { IsAll: false } scope && _tree.Objects.TryGetValue(scope.PathHash, out var one)
            ? new BinTree(new[] { one }, _tree.Dependencies)
            : _tree;

        _loading = true;
        try
        {
            Text = RitobinText.Write(subject, _target.ResolveName, _target.ResolveWadPath);
            IsDirty = false;
        }
        finally { _loading = false; }

        int lines = Text.Split('\n').Length;
        Status = SelectedScope is { IsAll: true }
            ? $"Whole file — {_tree.Objects.Count:n0} objects, {lines:n0} lines. Editing this much text is slow; pick an object instead."
            : $"{lines:n0} lines. Apply replaces just this object; the other {Math.Max(0, _tree.Objects.Count - 1):n0} are carried through untouched.";
    }

    /// <summary>Parse without writing anything — the safe button.</summary>
    [RelayCommand]
    private void Validate()
    {
        if (!TryBuild(out _, out int objects)) return;
        Status = $"Valid — {objects:n0} object(s).";
    }

    /// <summary>Parse the buffer, merge it into the full tree, re-serialise and save.</summary>
    [RelayCommand]
    private async Task Apply()
    {
        if (_target is not { } target || _tree is null) { Status = "No bin is open."; return; }
        if (!TryBuild(out var edited, out int objects)) return;

        IsBusy = true;
        try
        {
            // Merge rather than replace: in single-object scope the buffer only describes ONE object, and
            // everything else in the bin has to survive verbatim.
            var merged = Merge(_tree, edited!);

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                merged.Write(ms);
                bytes = ms.ToArray();
            }

            // Re-read what we are about to write. A tree that serialises but does not parse back is a file
            // the game would choke on, and this is the last place it costs nothing to find that out.
            BinTree readBack;
            try { readBack = SafeBinTree.Parse(bytes); }
            catch (Exception ex)
            {
                Status = "The edited bin did not survive a read-back and was NOT saved: " + ex.Message;
                _log(Status);
                return;
            }

            // M649: the client dispatches on a property's one-byte TYPE TAG and silently SKIPS anything
            // tagged unexpectedly - the file loads and the field is simply gone. Editing ritobin text is
            // the easiest way to re-tag something by accident: write a texture path as `string` where Riot
            // ships `file` and the map loads with no textures, which is what a user reported on 16.17.
            // So the tags are compared against the bin that was opened, not against a schema - the file
            // Riot shipped is the authority, and this catches forms this build has never heard of.
            var retagged = BinWireForm.Compare(_tree, readBack, target.ResolveName);
            if (BinWireForm.Describe(retagged) is { } warning)
            {
                Status = "Saved, but CHECK THIS: " + warning;
                _log(Status);
            }

            string savedTo = await target.Save(bytes);
            _tree = readBack;
            IsDirty = false;
            Status = $"Saved {objects:n0} edited object(s) into {merged.Objects.Count:n0}, "
                   + $"{bytes.Length:n0} bytes → {savedTo}"
                   + (BinWireForm.Describe(retagged) is { } note ? "  |  CHECK THIS: " + note : "");
            _log(Status);
        }
        catch (Exception ex)
        {
            Status = "Save failed: " + ex.Message;
            _log(Status);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Objects from <paramref name="edited"/> replace same-hash objects in <paramref name="whole"/>;
    /// anything else is carried through as it was.</summary>
    private static BinTree Merge(BinTree whole, BinTree edited)
    {
        var objects = new List<BinTreeObject>();
        foreach (var (hash, obj) in whole.Objects)
            objects.Add(edited.Objects.TryGetValue(hash, out var replacement) ? replacement : obj);

        // An object the buffer introduced that the file did not have is an addition, not a mistake.
        foreach (var (hash, obj) in edited.Objects)
            if (!whole.Objects.ContainsKey(hash)) objects.Add(obj);

        return new BinTree(objects, edited.Dependencies.Count > 0 ? edited.Dependencies : whole.Dependencies);
    }

    /// <summary>Throw away edits and re-read the current scope from the tree.</summary>
    [RelayCommand]
    private void Revert()
    {
        Errors.Clear();
        OnPropertyChanged(nameof(HasErrors));
        LoadScope();
    }

    /// <summary>Write the buffer out as a .py file — the extension the community uses for ritobin text.</summary>
    public async Task ExportAsync(Func<string, string?, Task<string?>> pickSavePath)
    {
        if (_target is not { } target) { Status = "No bin is open."; return; }
        string suggested = Path.GetFileNameWithoutExtension(target.DisplayName) + ".py";
        string? path = await pickSavePath("Export ritobin text", suggested);
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, Text);
            Status = $"Exported to {path}";
            _log(Status);
        }
        catch (Exception ex) { Status = "Export failed: " + ex.Message; }
    }

    /// <summary>Load ritobin text from a file into the buffer. It is not applied — the user still has to
    /// press Apply, which is what makes an import reviewable rather than immediate.</summary>
    public async Task ImportAsync(Func<Task<string?>> pickOpenPath)
    {
        string? path = await pickOpenPath();
        if (path is null) return;
        try
        {
            Text = await File.ReadAllTextAsync(path);
            Status = $"Loaded {Path.GetFileName(path)} — press Apply to write it into the bin.";
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
    }

    private bool TryBuild(out BinTree? tree, out int objects)
    {
        Errors.Clear();
        tree = RitobinTextReader.Read(Text, out var errors);
        foreach (var e in errors.Take(200)) Errors.Add(e.ToString());
        OnPropertyChanged(nameof(HasErrors));

        objects = tree?.Objects.Count ?? 0;
        if (tree is null)
        {
            Status = $"{errors.Count} problem(s) — nothing was written.";
            return false;
        }
        return true;
    }
}
