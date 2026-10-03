using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M812: one line of the Character window's CHROMA card - a texture, a colour parameter, an effect system, an emitter,
/// a colour value or an effect texture. Plain text and flags: the view draws a title, a dim detail line and, when the
/// item is shared with another skin, a SHARED badge whose tooltip names them; a file that lives outside the character's own
/// folder also carries an OUTSIDE badge, because the comparison does not reach what else uses it.
/// </summary>
public sealed class ChromaRowViewModel
{
    /// <summary>How many sharers the tooltip names before it says "and N more": a base skin's gradient is shared by
    /// every skin the champion has, and a hundred names is not a tooltip.</summary>
    public const int MaxNamedSharers = 12;

    public ChromaRowViewModel(string title, string detail = "", string tag = "", int level = 0, bool isHeader = false,
        IReadOnlyList<string>? sharedWith = null, string tip = "", string? outside = null)
    {
        Title = title;
        Detail = detail;
        Tag = tag;
        Level = level;
        IsHeader = isHeader;
        SharedWith = sharedWith ?? Array.Empty<string>();
        Tip = tip.Length == 0 ? null : tip;   // a null tooltip shows nothing; an empty one shows an empty box
        OutsideTip = outside;
    }

    public string Title { get; }
    public string Detail { get; }
    /// <summary>A short lower-case label at the left: "diffuse", "param", "colour", "sprite"...</summary>
    public string Tag { get; }
    public int Level { get; }
    public bool IsHeader { get; }
    public IReadOnlyList<string> SharedWith { get; }
    /// <summary>The row's hover text - the full path and hash, say - or null when it has none.</summary>
    public string? Tip { get; }
    /// <summary>The OUTSIDE badge's tooltip, or null for a file inside the character's own folder (and for rows that are not files).</summary>
    public string? OutsideTip { get; }

    public bool HasDetail => Detail.Length > 0;
    public bool HasTag => Tag.Length > 0;
    public bool HasTip => Tip is not null;
    public bool IsShared => SharedWith.Count > 0;
    public bool IsOutside => OutsideTip is not null;
    public Thickness Indent => new(Level * 14, 0, 0, 0);

    /// <summary>The badge: "SHARED", with the count when more than one skin is on it.</summary>
    public string SharedBadge => SharedWith.Count > 1 ? $"SHARED x{SharedWith.Count}" : "SHARED";

    public string OutsideBadge => "OUTSIDE";

    /// <summary>"Also used by 3 other skins: A, B, C" - the names the badge stands for.</summary>
    public string SharedTip
    {
        get
        {
            if (SharedWith.Count == 0) return "";
            string names = string.Join(", ", SharedWith.Take(MaxNamedSharers));
            int more = SharedWith.Count - MaxNamedSharers;
            return $"Also used by {SharedWith.Count} other skin{(SharedWith.Count == 1 ? "" : "s")} of this character: {names}"
                   + (more > 0 ? $" and {more} more" : "")
                   + ". Changing this file changes it for all of them.";
        }
    }
}

/// <summary>
/// M812: the Character window's read-only CHROMA card - everything colour-bearing the open skin or chroma draws, grouped
/// Body and Effects, each item marked shared or unshared with the champion's other skins.
///
/// <para><b>On demand only.</b> Opening a skin does not scan it: the scan reads every skin bin of the champion and
/// parses their dependency closures, which is a second or so of work nobody asked for when they opened a skin. The
/// Scan colours button runs it through <see cref="ScanSkinColours"/>, which the host wires to a
/// <see cref="SkinColorScanner"/> reading the same mount-aware files the rest of the window reads. A new skin, a
/// prop, or a second press cancels the scan in flight and the card starts from empty.</para>
///
/// <para><b>What the card says about its own reach.</b> SHARED means another skin of THIS character uses the item;
/// companions, other champions, maps and global assets are not compared, and the card says so beside every result. A file
/// that lives outside the character's folder is marked OUTSIDE, an effect only a gear upgrade plays is marked
/// (inferred), and whatever the scan could not read is a warning above the lists - never a list that looks complete when it
/// is not.</para>
///
/// <para><b>Writes nothing.</b> There is no edit, save or apply here; M814 grows the card, this milestone only reads.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    /// <summary>The card's tab in the right-hand panel: after Material, Animate, Play and Scene.</summary>
    public const int ChromaTab = 4;

    /// <summary>
    /// Host hook: the inventory of one skin bin. Called ON THE UI THREAD when Scan colours is pressed and returns a task the
    /// card awaits - so the host can take what it will read through (its mounts, the files list) at a moment when nothing is
    /// rebuilding them, and do the reading on a worker. A hook that does the file work before it returns freezes the window.
    /// It must honour the token. Null until wired.
    /// </summary>
    public Func<string, CancellationToken, Task<SkinColorInventory>>? ScanSkinColours
    {
        get => _scanSkinColours;
        set { _scanSkinColours = value; ScanColoursCommand.NotifyCanExecuteChanged(); }
    }
    private Func<string, CancellationToken, Task<SkinColorInventory>>? _scanSkinColours;

    /// <summary>Whether the window holds a champion skin the card can scan. False hides the tab.</summary>
    [ObservableProperty] private bool _hasChromaCard;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ScanColoursCommand))] private bool _chromaScanning;

    /// <summary>What the card is about to scan, then what it scanned: "skin49.bin (lillia)", then the skin's client name.</summary>
    [ObservableProperty] private string _chromaSkinLabel = "";

    /// <summary>The one-line summary: counts of body textures, colour parameters, effect systems, colour values, effect
    /// textures and shared items.</summary>
    [ObservableProperty] private string _chromaSummary = "";

    /// <summary>What the scan could not read or compare, so the lists may be incomplete - in a block of its own, in the warning
    /// colour, above everything else. Empty (and hidden) when every file the scan wanted was read.</summary>
    [ObservableProperty] private string _chromaWarnings = "";

    /// <summary>How far the comparison reached and what was left out, in words. Empty when there is nothing to say.</summary>
    [ObservableProperty] private string _chromaNotes = "";

    [ObservableProperty] private bool _hasChromaResult;

    /// <summary>Replaced as a whole, not added to row by row: an inventory runs to thousands of rows and a bound list
    /// redraws on every add.</summary>
    [ObservableProperty] private IReadOnlyList<ChromaRowViewModel> _chromaBodyRows = Array.Empty<ChromaRowViewModel>();
    [ObservableProperty] private IReadOnlyList<ChromaRowViewModel> _chromaEffectRows = Array.Empty<ChromaRowViewModel>();

    public bool HasChromaNotes => ChromaNotes.Length > 0;
    partial void OnChromaNotesChanged(string value) => OnPropertyChanged(nameof(HasChromaNotes));

    public bool HasChromaWarnings => ChromaWarnings.Length > 0;
    partial void OnChromaWarningsChanged(string value) => OnPropertyChanged(nameof(HasChromaWarnings));

    /// <summary>The last inventory, for a test or a later milestone; null before the first scan.</summary>
    public SkinColorInventory? ChromaInventory { get; private set; }

    private string? _chromaSkinBin;
    private CancellationTokenSource? _chromaScan;

    private const string ChromaHint = "Press Scan colours to list everything this skin draws in colour, and what it shares with the champion's other skins. Nothing is written.";

    /// <summary>What SHARED covers, said beside every result: the comparison is between the skins of one character, nothing wider.</summary>
    internal const string SharingScopeNote =
        "SHARED means another skin of this character uses the same file or system. Companions in the same WAD, other champions, "
        + "maps and global assets are not compared, so an item without the badge may still be used elsewhere.";

    /// <summary>Called by the host once a champion skin is on screen, and by <see cref="Show"/> with null when the window
    /// moves on to something else. Cancels a scan in flight and empties the card.</summary>
    public void SetChromaSkin(string? skinBinPath)
    {
        // The scan in flight is cancelled AND let go: its late result, or its late failure, belongs to a card that no longer
        // exists, and the token it holds is what tells it so (see ScanColours).
        _chromaScan?.Cancel();
        _chromaScan = null;
        _chromaSkinBin = string.IsNullOrWhiteSpace(skinBinPath) ? null : skinBinPath.Replace('\\', '/');
        ChromaInventory = null;
        ChromaScanning = false;
        HasChromaResult = false;
        ChromaBodyRows = Array.Empty<ChromaRowViewModel>();
        ChromaEffectRows = Array.Empty<ChromaRowViewModel>();
        ChromaNotes = "";
        ChromaWarnings = "";
        ChromaSummary = ChromaHint;
        ChromaSkinLabel = _chromaSkinBin is null ? "" : DescribeSkinBin(_chromaSkinBin);
        HasChromaCard = _chromaSkinBin is not null;
        ScanColoursCommand.NotifyCanExecuteChanged();   // the button follows the skin, not only the scan
    }

    private static string DescribeSkinBin(string path) =>
        SkinColorScanner.TryParseSkinPath(path, out string folder, out int number)
            ? $"skin{number}.bin of {folder}"
            : System.IO.Path.GetFileName(path);

    partial void OnHasChromaCardChanged(bool value)
    {
        // A hidden tab must not stay selected: Avalonia shows the empty content of a collapsed TabItem.
        if (!value && PanelTab == ChromaTab) PanelTab = AnimateTab;
    }

    private bool CanScanColours() => !ChromaScanning && _chromaSkinBin is not null && ScanSkinColours is not null;

    [RelayCommand(CanExecute = nameof(CanScanColours))]
    private async Task ScanColours()
    {
        if (_chromaSkinBin is not { } skinBin || ScanSkinColours is not { } scan) return;

        _chromaScan?.Cancel();
        var cts = _chromaScan = new CancellationTokenSource();
        ChromaScanning = true;
        ChromaSummary = "Scanning " + DescribeSkinBin(skinBin) + "...";
        try
        {
            // The hook takes its readers here, on the UI thread, and reads on a worker; the rows are built back here, where
            // the lists are bound. SetChromaSkin cancels this token whenever the card moves on - for another skin, the SAME
            // skin opened again, or a prop - so the token alone says whether this scan still owns the card.
            var inventory = await scan(skinBin, cts.Token);
            if (cts.IsCancellationRequested) return;
            ApplyChromaInventory(inventory);
        }
        catch (OperationCanceledException)
        {
            // a newer scan, or another skin, took over: it owns the card now
        }
        catch (Exception ex)
        {
            // A failure that arrives after the card moved on is not about this card: it must not overwrite the new skin's summary.
            // It is still caught - a filter here would let it escape the command.
            if (!cts.IsCancellationRequested) ChromaSummary = "The scan failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_chromaScan, cts)) { _chromaScan = null; ChromaScanning = false; }
            cts.Dispose();   // the hook has finished with the token: the await above returned or threw
        }
    }

    private void ApplyChromaInventory(SkinColorInventory inventory)
    {
        ChromaInventory = inventory;
        ChromaSkinLabel = inventory.SkinNumber >= 0
            ? $"{inventory.SkinLabel} - {DescribeSkinBin(inventory.SkinBinPath)}"
            : inventory.SkinBinPath;
        ChromaSummary = inventory.Summary;
        ChromaBodyRows = BuildBodyRows(inventory);
        ChromaEffectRows = BuildEffectRows(inventory);
        ChromaWarnings = BuildWarnings(inventory);
        ChromaNotes = string.Join("\n", BuildNotes(inventory));
        HasChromaResult = true;
    }

    /// <summary>The warning block: one line each, under a heading that says what a warning means for the lists below it.
    /// Empty when there are none.</summary>
    internal static string BuildWarnings(SkinColorInventory inventory) =>
        inventory.Warnings.Count == 0
            ? ""
            : "Not everything could be read, so the lists may be incomplete:\n" + string.Join("\n", inventory.Warnings.Select(w => "- " + w));

    /// <summary>The lines under the warnings: how far the comparison reached, then what was left out. The sibling count is always
    /// there, zero included - "0" and "not shown" must not look alike.</summary>
    internal static IReadOnlyList<string> BuildNotes(SkinColorInventory inventory)
    {
        var notes = new List<string>();
        if (inventory.SharingComputed)
        {
            notes.Add($"Compared with {inventory.ComparedSkins.Count} other skin(s) of this character.");
            notes.Add(SharingScopeNote);
        }
        notes.AddRange(inventory.Notes);

        if (inventory.ExcludedSamplers.Count > 0)
        {
            // by the reason the scan gave, short: "mask", "normal map", "not named as a colour map"
            var reasons = inventory.ExcludedSamplers.GroupBy(e => ShortReason(e.Reason)).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}");
            notes.Add($"Not listed: {inventory.ExcludedSamplers.Count} sampler(s) - {string.Join(", ", reasons)}.");
        }
        int unreached = inventory.Reach.ClosureSystems.Count - inventory.Effects.Count;
        if (unreached > 0)
            notes.Add($"{unreached} effect system(s) in this skin's files are not reached by this skin and are not listed (they can be leftovers).");
        if (inventory.Effects.Any(e => e.IsInferred))
            notes.Add($"{inventory.Effects.Count(e => e.IsInferred)} effect system(s) are marked (inferred): nothing the skin names reaches them, only a gear upgrade in its files.");
        return notes;
    }

    private static string ShortReason(string reason)
    {
        int cut = reason.IndexOfAny(new[] { ':', '(' });
        return (cut > 0 ? reason[..cut] : reason).Trim();
    }

    // ---- rows ----------------------------------------------------------------------------------------

    private static string FileName(string path)
    {
        int cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return cut >= 0 && cut + 1 < path.Length ? path[(cut + 1)..] : path;
    }

    /// <summary>The OUTSIDE badge's tooltip for a file outside the character's folder; null for one inside it.</summary>
    private static string? OutsideTip(SkinColorInventory inventory, string path) =>
        SkinColorInventory.IsOutsideCharacter(path, inventory.CharacterFolder)
            ? $"Outside this character, not compared. This file is not under assets/characters/{inventory.CharacterFolder}/, so other characters, "
              + "maps and global assets that use it are not checked: changing it can change more than this character's skins."
            : null;

    private const string OutsideDetail = " · outside this character, not compared";

    internal static IReadOnlyList<ChromaRowViewModel> BuildBodyRows(SkinColorInventory inventory)
    {
        var rows = new List<ChromaRowViewModel>();
        foreach (var t in inventory.BodyTextures)
        {
            string? outside = OutsideTip(inventory, t.Path);
            rows.Add(new ChromaRowViewModel(FileName(t.Path),
                $"{t.Role.ToString().ToLowerInvariant()} · {t.Source}" + (t.IsLinked ? " · from a linked bin, read-only" : "") + (outside is null ? "" : OutsideDetail),
                t.Role.ToString().ToLowerInvariant(), sharedWith: t.SharedWith,
                tip: $"{t.Path}\n0x{t.Hash:x16}" + (t.LinkedFromBin is { } bin ? "\nlinked from " + bin : ""), outside: outside));
        }
        foreach (var p in inventory.BodyParameters)
            rows.Add(new ChromaRowViewModel(ShortMaterialName(p.Material) + " . " + p.Name,
                SkinColorInventory.Format(p.Value) + (p.ValueOmitted ? " · no value written (zero)" : "") + (p.IsLinked ? " · linked bin" : ""),
                "param", tip: $"{p.Material}\n{p.TypeName}"));
        return rows;
    }

    private static string ShortMaterialName(string material)
    {
        int cut = material.LastIndexOf('/');
        return cut >= 0 && cut + 1 < material.Length ? material[(cut + 1)..] : material;
    }

    internal static string FieldLabel(string field) => field switch
    {
        "birthColor" => "Birth colour",
        "color" => "Colour over life",
        "Linger.SeparateLingerColor" => "Linger colour",
        "reflectionDefinition.fresnelColor" => "Fresnel colour",
        "reflectionDefinition.reflectionFresnelColor" => "Reflection fresnel colour",
        _ => field,
    };

    private const string InferredTip =
        "Inferred. Nothing the skin names reaches this system - not its resolver, not a link, not a child of those. A gear upgrade "
        + "in the skin's files plays it (or a child of one), and that the upgrade is this skin's is read from how Riot groups the shared files.";

    internal static IReadOnlyList<ChromaRowViewModel> BuildEffectRows(SkinColorInventory inventory)
    {
        var textureSharers = inventory.EffectTextures.ToDictionary(t => t.Hash, t => t.SharedWith);
        var rows = new List<ChromaRowViewModel>();
        foreach (var s in inventory.Effects)
        {
            rows.Add(new ChromaRowViewModel(s.Name + (s.IsInferred ? " (inferred)" : ""),
                $"{s.ColorValueCount} colour value(s) · {s.TextureCount} texture(s) · via {string.Join(", ", s.ReachedBy)}",
                "system", isHeader: true, sharedWith: s.SharedWith,
                tip: $"0x{s.PathHash:x8}\n{s.ParticlePath}\n{s.Bin}" + (s.IsInferred ? "\n\n" + InferredTip : "")));
            foreach (var e in s.Emitters)
            {
                if (e.Colors.Count == 0 && e.Textures.Count == 0) continue;
                rows.Add(new ChromaRowViewModel(e.Name + (e.Disabled ? " (disabled)" : ""), "", "emitter", level: 1));
                foreach (var c in e.Colors)
                    rows.Add(new ChromaRowViewModel(FieldLabel(c.Field), SkinColorInventory.Describe(c), "colour", level: 2, tip: c.Field));
                foreach (var t in e.Textures)
                {
                    string? outside = OutsideTip(inventory, t.Path);
                    rows.Add(new ChromaRowViewModel(t.Role, FileName(t.Path) + (outside is null ? "" : OutsideDetail), t.IsCubemap ? "cubemap" : "texture", level: 2,
                        sharedWith: textureSharers.TryGetValue(t.Hash, out var shared) ? shared : null,
                        tip: $"{t.Field}\n{t.Path}\n0x{t.Hash:x16}", outside: outside));
                }
            }
        }
        return rows;
    }
}
