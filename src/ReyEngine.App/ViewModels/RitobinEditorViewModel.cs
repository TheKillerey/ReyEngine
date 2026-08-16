using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>
/// M498: edit a .bin as ritobin text.
///
/// <para>The whole point of the format is that a bin becomes a thing you can read and change directly,
/// rather than through a property grid that only exposes what someone thought to model. So this is a plain
/// text buffer over the real file, with the two conversions from M496/M497 on either side and a validate
/// step in between.</para>
///
/// <para><b>Nothing is written unless the text parses AND re-serialises.</b> Applying edits to a bin the
/// game loads is the kind of operation where a half-valid write is worse than a refusal — see the map that
/// took six milestones to un-break. Errors carry line numbers so a typo is a place in the buffer rather
/// than a stack trace.</para>
/// </summary>
public sealed partial class RitobinEditorViewModel : ObservableObject
{
    private RitobinTarget? _target;
    private string _loadedText = "";

    /// <summary>Log through whatever the target supplied, so the window never reaches back into the main
    /// view model for it.</summary>
    private void _log(string message) => _target?.Log?.Invoke(message);

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _title = "No bin open";
    [ObservableProperty] private string _status = "Open a .bin to edit it as ritobin text.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasTarget;

    /// <summary>Parse problems, each with the line it sits on.</summary>
    public ObservableCollection<string> Errors { get; } = new();
    public bool HasErrors => Errors.Count > 0;

    /// <summary>The buffer differs from what was loaded.</summary>
    public bool IsDirty => HasTarget && !string.Equals(Text, _loadedText, StringComparison.Ordinal);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(IsDirty));

    /// <summary>Point the editor at a bin and convert it to text.</summary>
    public void Open(RitobinTarget target)
    {
        _target = target;
        HasTarget = true;
        Title = target.DisplayName;
        Errors.Clear();
        OnPropertyChanged(nameof(HasErrors));

        try
        {
            var tree = SafeBinTree.Parse(target.Read());
            Text = RitobinText.Write(tree, target.ResolveName, target.ResolveWadPath);
            _loadedText = Text;
            Status = $"{tree.Objects.Count:n0} object(s), {Text.Split('\n').Length:n0} lines.";
        }
        catch (Exception ex)
        {
            Text = "";
            _loadedText = "";
            Status = "Could not read this bin: " + ex.Message;
        }
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>Parse without writing anything — the safe button.</summary>
    [RelayCommand]
    private void Validate()
    {
        if (!TryBuild(out var tree, out int objects)) return;
        Status = $"Valid — {objects:n0} object(s).";
    }

    /// <summary>Parse, re-serialise and write back through the target's own save path.</summary>
    [RelayCommand]
    private async Task Apply()
    {
        if (_target is not { } target) { Status = "No bin is open."; return; }
        if (!TryBuild(out var tree, out int objects)) return;

        IsBusy = true;
        try
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                tree!.Write(ms);
                bytes = ms.ToArray();
            }

            // Re-read what we are about to write. A tree that serialises but does not parse back is a file
            // the game would choke on, and this is the last place it costs nothing to find that out.
            try { SafeBinTree.Parse(bytes); }
            catch (Exception ex)
            {
                Status = "The edited bin did not survive a read-back and was NOT saved: " + ex.Message;
                _log(Status);
                return;
            }

            string savedTo = await target.Save(bytes);
            _loadedText = Text;
            OnPropertyChanged(nameof(IsDirty));
            Status = $"Saved {objects:n0} object(s), {bytes.Length:n0} bytes → {savedTo}";
            _log(Status);
        }
        catch (Exception ex)
        {
            Status = "Save failed: " + ex.Message;
            _log(Status);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Throw away edits and re-read the bin.</summary>
    [RelayCommand]
    private void Revert()
    {
        if (_target is { } target) Open(target);
    }

    /// <summary>Write the text out as a .py file — the extension the community uses for ritobin text so
    /// editors syntax-highlight it.</summary>
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

    private bool TryBuild(out LeagueToolkit.Core.Meta.BinTree? tree, out int objects)
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
