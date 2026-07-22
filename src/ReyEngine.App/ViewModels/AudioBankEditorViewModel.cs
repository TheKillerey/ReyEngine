using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Audio;

namespace ReyEngine.App.ViewModels;

/// <summary>One media entry row.</summary>
public sealed partial class AudioEntryRowViewModel : ObservableObject
{
    public required AudioBankEntry Entry { get; init; }
    public uint Id => Entry.Id;
    public string SizeText => Entry.Size >= 1024 ? $"{Entry.Size / 1024.0:n0} KB" : $"{Entry.Size} B";
    public string InfoText => Entry.Info.ToString();
    /// <summary>Events (from the sibling events bank) that play this wem — the human-readable name.</summary>
    [ObservableProperty] private string _usedBy = "";
    public bool HasUsedBy => UsedBy.Length > 0;
    partial void OnUsedByChanged(string value) => OnPropertyChanged(nameof(HasUsedBy));

    public string StateText => Entry.IsAdded ? "added" : Entry.IsRenamed && Entry.IsReplaced ? "renamed + replaced"
        : Entry.IsRenamed ? $"renamed from {Entry.OriginalId}" : Entry.IsReplaced ? "replaced" : "";
    public bool IsChanged => Entry.IsAdded || Entry.IsRenamed || Entry.IsReplaced;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Id)); OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(InfoText)); OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsChanged));
    }
}

/// <summary>
/// M137: the Audio Bank Editor — open a .bnk/.wpk and work with its embedded sounds directly:
/// play, export, replace, add, rename (= change the Wwise id), delete, duplicate and copy/paste
/// between open files. Saving rewrites the container (every non-media section preserved) into the
/// project, after a round-trip validation.
/// </summary>
public sealed partial class AudioBankEditorViewModel : ObservableObject
{
    /// <summary>Cross-window clipboard so wems can be pasted from one bank into another.</summary>
    private static (uint Id, byte[] Data)? _clipboard;

    public AudioBankDocument? Document { get; private set; }
    public WadAssetEntry? Entry { get; private set; }

    public ObservableCollection<AudioEntryRowViewModel> Rows { get; } = new();
    [ObservableProperty] private AudioEntryRowViewModel? _selected;
    [ObservableProperty] private string _title = "No file open";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isEditable = true;
    [ObservableProperty] private string _readOnlyReason = "";
    public bool HasReadOnlyReason => ReadOnlyReason.Length > 0;
    partial void OnReadOnlyReasonChanged(string value) => OnPropertyChanged(nameof(HasReadOnlyReason));
    public bool HasSelection => Selected is not null;
    partial void OnSelectedChanged(AudioEntryRowViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    // host hooks
    public Func<uint, byte[], string?>? DecodeToWav;          // wem id + bytes -> wav path
    public Action<string>? PlayWav;
    public Action? StopAll;
    public Action<uint>? ClearDecodeCache;
    public Func<string, Task<string?>>? PickImportFile;       // title -> path
    public Func<string, Task<string?>>? PickExportFile;       // suggested name -> path
    /// <summary>M138: convert an ordinary audio file to .wem bytes (null + reason when unavailable).</summary>
    public Func<string, (byte[]? Data, string? Error)>? ConvertToWem;
    public Func<bool>? ConverterAvailable;
    public Func<string, string, Task<string?>>? PromptText;   // title, initial -> value
    public Func<AudioBankDocument, WadAssetEntry, byte[], Task<bool>>? SaveAsync;
    public Action<string>? Info;
    public Action<string>? Warn;

    public bool Load(WadAssetEntry entry, byte[] bytes, Func<uint, string[]>? usedByLookup = null)
    {
        var doc = AudioBankDocument.Parse(bytes, entry.Path);
        if (doc is null) return false;
        Document = doc; Entry = entry;
        Title = entry.DisplayName;
        Subtitle = $"{doc.Kind}{(doc.BankVersion > 0 ? $" v{doc.BankVersion}" : "")} · {doc.Entries.Count} sound(s) · {bytes.Length / 1024.0:n0} KB";
        IsEditable = doc.IsEditable && !entry.ReadOnly;
        ReadOnlyReason = !doc.IsEditable ? doc.ReadOnlyReason ?? ""
            : entry.ReadOnly ? "Read-only Riot asset — right-click it in the Content Browser ▸ Copy Asset To Project to edit." : "";
        RebuildRows(usedByLookup);
        IsDirty = false;
        Status = doc.Entries.Count == 0 ? "This bank carries no embedded audio (its sounds live in the matching *_audio.bnk/.wpk)." : "";
        return true;
    }

    private void RebuildRows(Func<uint, string[]>? usedByLookup)
    {
        Rows.Clear();
        foreach (var e in Document!.Entries.OrderBy(e => e.Id))
        {
            var row = new AudioEntryRowViewModel { Entry = e };
            if (usedByLookup?.Invoke(e.Id) is { Length: > 0 } names)
                row.UsedBy = string.Join(", ", names.Take(3)) + (names.Length > 3 ? $" +{names.Length - 3}" : "");
            Rows.Add(row);
        }
    }

    private void AfterEdit(uint? select = null)
    {
        IsDirty = Document!.IsDirty;
        var keep = select ?? Selected?.Id;
        RebuildRows(_usedBy);
        Selected = Rows.FirstOrDefault(r => r.Id == keep) ?? Rows.FirstOrDefault();
    }

    private Func<uint, string[]>? _usedBy;
    public void SetUsedByLookup(Func<uint, string[]> lookup) { _usedBy = lookup; RebuildRows(lookup); }

    // ---- playback ----

    [RelayCommand]
    private void Play()
    {
        if (Selected is not { } row || DecodeToWav is null) return;
        var wav = DecodeToWav(row.Id, row.Entry.Data);
        if (wav is null) { Status = "Decode failed — vgmstream-cli is needed to play Wwise audio."; return; }
        PlayWav?.Invoke(wav);
        Status = $"Playing {row.Id} ({row.InfoText}).";
    }

    [RelayCommand] private void Stop() { StopAll?.Invoke(); Status = ""; }

    // ---- file in/out ----

    [RelayCommand]
    private async Task Import()
    {
        if (Selected is not { } row || !RequireEditable()) return;
        if (PickImportFile is null) return;
        var file = await PickImportFile($"Replace sound {row.Id} — .wem or ordinary audio");
        if (file is null) return;
        var data = await LoadAsWemAsync(file);
        if (data is null) return;
        Document!.Replace(row.Id, data);
        ClearDecodeCache?.Invoke(row.Id);
        AfterEdit(row.Id);
        Status = $"Replaced {row.Id} with {System.IO.Path.GetFileName(file)} ({data.Length / 1024.0:n0} KB).";
    }

    /// <summary>M138: read a picked file as wem bytes — .wem is used as-is, anything else is encoded
    /// to Wwise Vorbis first (League ships no other codec). Sets <see cref="Status"/> and returns null
    /// on failure.</summary>
    private async Task<byte[]?> LoadAsWemAsync(string file)
    {
        var data = await System.IO.File.ReadAllBytesAsync(file);
        bool isWem = file.EndsWith(".wem", StringComparison.OrdinalIgnoreCase);
        if (isWem && IsRiff(data)) return data;

        if (ConvertToWem is null || ConverterAvailable?.Invoke() == false)
        {
            Status = isWem
                ? "That .wem isn't a valid RIFF file."
                : $"{System.IO.Path.GetFileName(file)} needs converting to .wem, but no Wwise encoder is configured — see Preferences ▸ Audio.";
            return null;
        }
        Status = $"Converting {System.IO.Path.GetFileName(file)} to Wwise Vorbis…";
        var (wem, err) = await Task.Run(() => ConvertToWem(file));
        if (wem is null) { Status = $"Conversion failed: {err}"; Warn?.Invoke($"{Title}: conversion failed — {err}"); return null; }
        Info?.Invoke($"Converted {System.IO.Path.GetFileName(file)} → {wem.Length / 1024.0:n0} KB wem.");
        return wem;
    }

    [RelayCommand]
    private async Task Export()
    {
        if (Selected is not { } row || PickExportFile is null) return;
        var dest = await PickExportFile($"{row.Id}.wem");
        if (dest is null) return;
        await System.IO.File.WriteAllBytesAsync(dest, row.Entry.Data);
        Status = $"Exported {row.Id} → {dest}";
    }

    [RelayCommand]
    private async Task Add()
    {
        if (!RequireEditable() || PickImportFile is null) return;
        var file = await PickImportFile("Add a sound — .wem or ordinary audio");
        if (file is null) return;
        var data = await LoadAsWemAsync(file);
        if (data is null) return;
        uint id = await AskIdAsync("Add sound — Wwise id", Document!.SuggestFreeId());
        if (id == 0) return;
        if (!Document.Add(id, data, out var err)) { Status = err ?? "Add failed."; return; }
        AfterEdit(id);
        Status = $"Added {id} ({data.Length / 1024.0:n0} KB). Nothing plays it until an event references this id.";
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (Selected is not { } row || !RequireEditable()) return;
        uint id = await AskIdAsync($"Change id of {row.Id}", row.Id);
        if (id == 0 || id == row.Id) return;
        if (!Document!.ChangeId(row.Id, id, out var err)) { Status = err ?? "Rename failed."; return; }
        AfterEdit(id);
        Status = $"Id changed to {id} — the sound now answers to whatever event references that id.";
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } row || !RequireEditable()) return;
        Document!.Remove(row.Id);
        AfterEdit();
        Status = $"Removed {row.Id}." + (row.HasUsedBy ? $" It was used by {row.UsedBy} — that event will fall silent." : "");
    }

    [RelayCommand]
    private async Task Duplicate()
    {
        if (Selected is not { } row || !RequireEditable()) return;
        uint id = await AskIdAsync($"Duplicate {row.Id} as", Document!.SuggestFreeId());
        if (id == 0) return;
        if (!Document.Duplicate(row.Id, id, out var err)) { Status = err ?? "Duplicate failed."; return; }
        AfterEdit(id);
        Status = $"Duplicated {row.Id} → {id}.";
    }

    [RelayCommand]
    private void Copy()
    {
        if (Selected is not { } row) return;
        _clipboard = (row.Id, (byte[])row.Entry.Data.Clone());
        Status = $"Copied {row.Id} ({row.SizeText}) — paste it here or into another bank.";
    }

    /// <summary>Paste as a NEW entry (keeps the copied id when free, else the next free one).</summary>
    [RelayCommand]
    private void Paste()
    {
        if (!RequireEditable()) return;
        if (_clipboard is not { } clip) { Status = "Nothing copied yet."; return; }
        uint id = Document!.Find(clip.Id) is null ? clip.Id : Document.SuggestFreeId();
        if (!Document.Add(id, (byte[])clip.Data.Clone(), out var err)) { Status = err ?? "Paste failed."; return; }
        AfterEdit(id);
        Status = $"Pasted as {id}" + (id != clip.Id ? $" (id {clip.Id} was taken)." : ".");
    }

    /// <summary>Paste OVER the selected entry — the usual "put this sound here" move.</summary>
    [RelayCommand]
    private void PasteInto()
    {
        if (Selected is not { } row || !RequireEditable()) return;
        if (_clipboard is not { } clip) { Status = "Nothing copied yet."; return; }
        Document!.Replace(row.Id, (byte[])clip.Data.Clone());
        ClearDecodeCache?.Invoke(row.Id);
        AfterEdit(row.Id);
        Status = $"Pasted {clip.Id}'s audio into {row.Id}.";
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!RequireEditable() || Document is null || Entry is null || SaveAsync is null) return;
        if (!Document.IsDirty) { Status = "No changes to save."; return; }
        byte[] bytes;
        try { bytes = Document.Serialize(); }
        catch (Exception ex) { Status = $"Rebuild failed: {ex.Message}"; return; }
        if (!Document.Validate(bytes, out var err))
        { Status = $"Rebuilt file failed validation ({err}) — NOT saved."; Warn?.Invoke($"{Title}: {Status}"); return; }
        if (await SaveAsync(Document, Entry, bytes))
        {
            Document.MarkSaved();
            IsDirty = false;
            Status = $"Saved {bytes.Length / 1024.0:n0} KB to the project.";
        }
    }

    private bool RequireEditable()
    {
        if (IsEditable) return true;
        Status = ReadOnlyReason.Length > 0 ? ReadOnlyReason : "This file is read-only.";
        return false;
    }

    private async Task<uint> AskIdAsync(string title, uint initial)
    {
        if (PromptText is null) return initial;
        var text = await PromptText(title, initial.ToString());
        if (text is null) return 0;
        return uint.TryParse(text.Trim(), out var id) ? id : 0;
    }

    private static bool IsRiff(byte[] d) =>
        d.Length >= 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F';
}
