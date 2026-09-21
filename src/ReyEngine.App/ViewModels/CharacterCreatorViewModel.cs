using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Characters;

namespace ReyEngine.App.ViewModels;

/// <summary>One file the scan found, as a row: what it is, what it will become, why it is left out.</summary>
public sealed class CharacterImportRowViewModel
{
    public required CharacterImportFile File { get; init; }
    public string FileName => File.FileName;
    public string Role => File.Role switch
    {
        CharacterImportRole.Mesh => "MESH",
        CharacterImportRole.Skeleton => "SKELETON",
        CharacterImportRole.Clip => "CLIP",
        CharacterImportRole.Texture => "TEXTURE",
        _ => "",
    };
    public string Format => File.Format.Label;
    public string Size => File.Size < 1024 ? $"{File.Size} B" : File.Size < 1024 * 1024 ? $"{File.Size / 1024.0:0.#} KB" : $"{File.Size / (1024.0 * 1024.0):0.##} MB";
    public string Note => File.Note ?? "";
    public bool IsUsed => File.Role != CharacterImportRole.Ignored;
    public bool WillUpgrade => File.WillUpgrade;
    public bool IsUnreadable => !File.Format.IsReadable;
    /// <summary>What the combo shows for a texture choice.</summary>
    public override string ToString() => FileName;
}

/// <summary>One clip of the new graph, editable: its name in the graph, whether it loops, whether it goes in.</summary>
public sealed partial class CharacterClipRowViewModel : ObservableObject
{
    public required CharacterImportFile File { get; init; }
    public string FileName => File.FileName;
    public string Format => File.Format.Label;
    public string Note => File.Note ?? "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private bool _include = true;
}

/// <summary>
/// M697: the Character Creator. Point it at an old character folder; it reads the files, says which
/// is the mesh, the skeleton, the clips and the texture and what era each is in, proposes a name and
/// a clip table, and on Create upgrades what is outdated, writes the three bins, stages everything in
/// the open map's package and - if asked - places the prop at the gizmo. The folder reading and the
/// packaging are Formats' (<see cref="CharacterFolderImporter"/>); this holds the rows and the
/// choices, and hands the finished result to the host.
/// </summary>
public sealed partial class CharacterCreatorViewModel : ObservableObject
{
    /// <summary>Host folder picker; null when there is no dialog service (a headless check).</summary>
    public Func<Task<string?>>? PickFolder;
    /// <summary>Host decoder for image formats Core cannot read (.png); null refuses them.</summary>
    public Func<byte[], TextureImage?>? DecodeImage;
    /// <summary>Host: stage the result into the project and, when the flag is set, place it. Returns the
    /// status line to show. Throws with the reason when it cannot.</summary>
    public Func<CharacterImportResult, bool, Task<string>>? Create;

    /// <summary>Whether a map is open to place the prop in; set by the host.</summary>
    [ObservableProperty] private bool _canPlace;
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private float _skinScale = 1f;
    [ObservableProperty] private bool _alsoPlace = true;
    /// <summary>M747: place as a MapAnimatedProp (client-side) rather than a scenery character.</summary>
    [ObservableProperty] private bool _placeAsAnimatedProp = true;
    /// <summary>M748 (experimental): seconds of game time before the prop appears; 0 = always.</summary>
    [ObservableProperty] private decimal _appearAfterSeconds;
    [ObservableProperty] private string _status = "Pick the folder that holds the character's .skn, .skl, animations and texture.";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _created;
    [ObservableProperty] private CharacterImportRowViewModel? _selectedTexture;
    [ObservableProperty] private string _problems = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _summary = "";

    public ObservableCollection<CharacterImportRowViewModel> Files { get; } = new();
    public ObservableCollection<CharacterClipRowViewModel> Clips { get; } = new();
    public ObservableCollection<CharacterImportRowViewModel> Textures { get; } = new();

    public CharacterFolderScan? Scan { get; private set; }
    public CharacterImportResult? Result { get; private set; }

    public string? NameProblem => CharacterPackageBuilder.ValidateName(Name);
    public bool HasNameProblem => NameProblem is not null;
    public bool HasProblems => Problems.Length > 0;
    public bool HasWarnings => Warnings.Length > 0;
    public bool HasScan => Scan is not null;
    public bool CanCreate => !Busy && !Created && Scan is { CanImport: true } && NameProblem is null && Create is not null;
    public int UpgradeCount => Files.Count(f => f.WillUpgrade);
    public string CreateLabel => AlsoPlace && CanPlace ? "Create and place at the gizmo" : "Create in project";

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(NameProblem)); OnPropertyChanged(nameof(HasNameProblem)); OnPropertyChanged(nameof(CanCreate)); }
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnCreatedChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnProblemsChanged(string value) => OnPropertyChanged(nameof(HasProblems));
    partial void OnWarningsChanged(string value) => OnPropertyChanged(nameof(HasWarnings));
    partial void OnAlsoPlaceChanged(bool value) => OnPropertyChanged(nameof(CreateLabel));
    partial void OnCanPlaceChanged(bool value) => OnPropertyChanged(nameof(CreateLabel));
    partial void OnFolderChanged(string value) { Created = false; Result = null; }

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (PickFolder is null) return;
        string? picked;
        try { picked = await PickFolder(); }
        catch (Exception ex) { Status = "The folder picker failed: " + ex.Message; return; }
        if (string.IsNullOrWhiteSpace(picked)) return;
        Folder = picked;
        Rescan();
    }

    /// <summary>Read the folder again. Keeps the name if the user typed one for this folder.</summary>
    [RelayCommand]
    public void Rescan()
    {
        Files.Clear(); Clips.Clear(); Textures.Clear();
        Result = null; Created = false;
        if (string.IsNullOrWhiteSpace(Folder)) { Scan = null; Problems = ""; Warnings = ""; Summary = ""; OnScanChanged(); return; }
        CharacterFolderScan scan;
        try { scan = CharacterFolderImporter.Scan(Folder.Trim()); }
        catch (Exception ex)
        {
            Scan = null; Problems = ex.Message; Warnings = ""; Summary = "";
            Status = "The folder could not be read.";
            OnScanChanged();
            return;
        }
        Scan = scan;
        if (string.IsNullOrWhiteSpace(Name) || Name == _suggestedName) Name = scan.SuggestedName;
        _suggestedName = scan.SuggestedName;
        foreach (var f in scan.Files) Files.Add(new CharacterImportRowViewModel { File = f });
        foreach (var t in scan.Textures) Textures.Add(Files.First(r => ReferenceEquals(r.File, t)));
        SelectedTexture = Textures.FirstOrDefault(t => t.Note.StartsWith("the diffuse texture", StringComparison.Ordinal)) ?? Textures.FirstOrDefault();
        foreach (var clip in CharacterFolderImporter.ProposeClips(scan, scan.SuggestedName))
        {
            var file = scan.Clips.First(c => c.FileName.Equals(clip.AnmFileName, StringComparison.OrdinalIgnoreCase));
            Clips.Add(new CharacterClipRowViewModel { File = file, Name = clip.Name, Loop = clip.Loop });
        }
        Problems = string.Join("\n", scan.Problems);
        Warnings = string.Join("\n", scan.Warnings);
        Summary = scan.CanImport
            ? $"{scan.Mesh!.FileName} ({scan.Mesh.Format.Label})  ·  {scan.Skeleton!.FileName} ({scan.Skeleton.Format.Label})  ·  {scan.Clips.Count} clip(s)"
              + $"  ·  {(SelectedTexture is null ? "no texture" : SelectedTexture.FileName)}"
              + (UpgradeCount > 0 ? $"  ·  {UpgradeCount} file(s) will be upgraded" : "  ·  every file is current")
            : "";
        Status = scan.CanImport
            ? "Check the name and the clips, then create."
            : "This folder cannot become a character yet - see the problems.";
        OnScanChanged();
    }

    private string? _suggestedName;

    private void OnScanChanged()
    {
        OnPropertyChanged(nameof(HasScan));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(UpgradeCount));
    }

    /// <summary>The spec the rows describe right now: the mesh and skeleton the scan chose, the texture
    /// picked, the ticked clips under their edited names.</summary>
    public CharacterPackageSpec BuildSpec()
    {
        if (Scan is not { CanImport: true } scan) throw new InvalidOperationException("Nothing to create - pick a folder with a mesh and a skeleton.");
        if (NameProblem is { } problem) throw new InvalidOperationException(problem);
        var clips = new List<CharacterClipSpec>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Clips.Where(c => c.Include))
        {
            string name = CharacterFolderImporter.Sanitize(row.Name);
            if (name.Length == 0) throw new InvalidOperationException($"The clip for {row.FileName} needs a name (letters, digits, underscores).");
            if (!names.Add(name)) throw new InvalidOperationException($"Two clips are called '{name}'.");
            clips.Add(new CharacterClipSpec(name, row.FileName, row.Loop));
        }
        var proposed = CharacterFolderImporter.Propose(scan, Name.Trim(), SelectedTexture?.File, SkinScale <= 0f ? 1f : SkinScale);
        return proposed with { Clips = clips };
    }

    [RelayCommand]
    private async Task CreateCharacter()
    {
        if (!CanCreate || Scan is not { } scan || Create is null) return;
        Busy = true;
        try
        {
            var spec = BuildSpec();
            var texture = SelectedTexture?.File;
            var decode = DecodeImage;
            Status = $"Reading, checking and upgrading {scan.Files.Count(f => f.Role != CharacterImportRole.Ignored)} file(s)...";
            var result = await Task.Run(() => CharacterFolderImporter.Execute(scan, spec, texture, decode));
            Result = result;
            Warnings = string.Join("\n", result.Warnings);
            Status = await Create(result, AlsoPlace && CanPlace);
            Created = true;
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Busy = false; }
    }
}
