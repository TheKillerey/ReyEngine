using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M826: one effect colour field in the EFFECTS RECOLOUR list - an include switch, what the field is (the emitter and which colour of it), Riot's colour and the colour the
/// sliders make of it. A field a recolour must not act on (a negative colour, one whose bin changed shape) is listed with the reason and cannot be switched on.
/// The include state lives in the card (<see cref="MeshPreviewViewModel"/>), not here: rows are created only when a system is expanded, and the state must not depend on that.
/// </summary>
public sealed partial class ChromaEffectFieldRowViewModel : ObservableObject
{
    private readonly MeshPreviewViewModel _owner;

    public ChromaEffectFieldRowViewModel(MeshPreviewViewModel owner, ChromaEffectFieldInfo info, bool include)
    {
        _owner = owner;
        Info = info;
        var f = info.Riot;
        Name = f.EmitterName + (f.EmitterDisabled ? " (disabled)" : "") + " . " + MeshPreviewViewModel.FieldLabel(f.Key.Field);
        Detail = MeshPreviewViewModel.DescribeEffectField(f);
        Tip = $"{f.SystemName}\nemitter {f.Key.Emitter} - {f.EmitterName}\n{f.Key.Field}\n{info.Bin}";
        OriginalBrush = ChromaParameterRowViewModel.Swatch(Representative(f));
        _resultBrush = OriginalBrush;
        CanInclude = info.Recolourable;
        _isIncluded = include && info.Recolourable;
        if (!info.Recolourable) _note = info.Reason;
        IsEditedOutside = info.EditedOutside || info.OwnedEdited;
    }

    public ChromaEffectFieldInfo Info { get; }
    public EffectColorKey Key => Info.Key;
    public string Name { get; }
    public string Detail { get; }
    public string Tip { get; }
    public IBrush OriginalBrush { get; }
    public bool CanInclude { get; }
    public bool IsEditedOutside { get; set; }

    [ObservableProperty] private IBrush _resultBrush;
    [ObservableProperty] private bool _isIncluded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNote))] private string _note = "";
    public bool HasNote => Note.Length > 0;

    partial void OnIsIncludedChanged(bool value) => _owner.OnChromaEffectFieldChanged(this);

    /// <summary>The colour that stands for the field in a swatch: its constant, else its first key.</summary>
    internal static Vector4 Representative(EffectColorField f) => f.Constant ?? (f.Keys.Count > 0 ? f.Keys[0] : Vector4.One);

    /// <summary>Set the switch without telling the card (the card moved it itself).</summary>
    internal void SetSilently(bool value)
    {
        _silent = true;
        try { IsIncluded = value; }
        finally { _silent = false; }
    }

    private bool _silent;
    internal bool Silent => _silent;
}

/// <summary>M826: one effect system in the list: a switch for ALL its colour fields, its name, how many colours it holds and which other skins use it. Its fields are listed when it is expanded.</summary>
public sealed partial class ChromaEffectSystemRowViewModel : ObservableObject
{
    private readonly MeshPreviewViewModel _owner;
    private bool _loaded, _silent;

    public ChromaEffectSystemRowViewModel(MeshPreviewViewModel owner, ChromaEffectSystemInfo info, IReadOnlyList<ChromaEffectFieldInfo> fields, bool include)
    {
        _owner = owner;
        Info = info;
        FieldInfos = fields;
        Name = info.Name + (info.IsInferred ? " (inferred)" : "");
        int emitters = fields.Select(f => f.Key.Emitter).Distinct().Count();
        Detail = $"{fields.Count} colour value(s) in {emitters} emitter(s) · {ShortBin(info.Bin)}";
        Tip = $"0x{info.Hash:x8}\n{info.ParticlePath}\n{info.Bin}\nvia {info.ReachedBy}" + (info.IsInferred ? "\n\n" + InferredTip : "");
        CanInclude = fields.Any(f => f.Recolourable);
        _isIncluded = include && CanInclude;
        SharedWith = info.SharedWith;
    }

    private const string InferredTip =
        "Inferred. Nothing the skin names reaches this system - only a gear upgrade in the skin's files plays it (or a child of one), and that the upgrade is this skin's is read from how Riot groups the shared files.";

    public ChromaEffectSystemInfo Info { get; }
    public IReadOnlyList<ChromaEffectFieldInfo> FieldInfos { get; }
    public string Name { get; }
    public string Detail { get; }
    public string Tip { get; }
    public bool CanInclude { get; }
    public IReadOnlyList<string> SharedWith { get; }
    public bool IsShared => SharedWith.Count > 0;
    public string SharedBadge => SharedWith.Count > 1 ? $"SHARED x{SharedWith.Count}" : "SHARED";
    public string SharedTip => SharedWith.Count == 0 ? "" : "Also used by: " + string.Join(", ", SharedWith.Take(ChromaRowViewModel.MaxNamedSharers))
        + (SharedWith.Count > ChromaRowViewModel.MaxNamedSharers ? $" and {SharedWith.Count - ChromaRowViewModel.MaxNamedSharers} more" : "")
        + ". Recolouring this system recolours it for all of them.";

    /// <summary>The system's colour fields, created the first time it is expanded.</summary>
    public ObservableCollection<ChromaEffectFieldRowViewModel> Fields { get; } = new();

    [ObservableProperty] private bool _isIncluded;
    [ObservableProperty] private bool _isExpanded;

    partial void OnIsIncludedChanged(bool value) { if (!_silent) _owner.OnChromaEffectSystemChanged(this); }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded) { _loaded = true; _owner.LoadChromaEffectFieldRows(this); }
    }

    internal void SetSilently(bool value)
    {
        _silent = true;
        try { IsIncluded = value; }
        finally { _silent = false; }
    }

    private static string ShortBin(string bin)
    {
        int cut = Math.Max(bin.LastIndexOf('/'), bin.LastIndexOf('\\'));
        return cut >= 0 && cut + 1 < bin.Length ? bin[(cut + 1)..] : bin;
    }
}

/// <summary>M826: one effect texture in the list. Like the body's texture row, but its include state is the effects' own.</summary>
public sealed partial class ChromaEffectTextureRowViewModel : ObservableObject
{
    private readonly MeshPreviewViewModel _owner;

    public ChromaEffectTextureRowViewModel(MeshPreviewViewModel owner, ChromaTarget target, string detail, string tip, bool isOutside, IReadOnlyList<string> sharedWith, bool include)
    {
        _owner = owner;
        Target = target;
        Detail = detail;
        Tip = tip;
        IsOutside = isOutside;
        SharedWith = sharedWith;
        _isIncluded = include;
        Name = MeshPreviewViewModel.FileNameOf(target.Path);
    }

    public ChromaTarget Target { get; }
    public ulong Hash => Target.Hash;
    public string Name { get; }
    public string Detail { get; }
    public string Tip { get; }
    public bool IsOutside { get; }
    public IReadOnlyList<string> SharedWith { get; }
    public bool IsShared => SharedWith.Count > 0;
    public string SharedBadge => SharedWith.Count > 1 ? $"SHARED x{SharedWith.Count}" : "SHARED";
    public string SharedTip => SharedWith.Count == 0 ? "" : "Also used by: " + string.Join(", ", SharedWith.Take(ChromaRowViewModel.MaxNamedSharers))
        + (SharedWith.Count > ChromaRowViewModel.MaxNamedSharers ? $" and {SharedWith.Count - ChromaRowViewModel.MaxNamedSharers} more" : "")
        + ". Recolouring this file recolours it for all of them.";
    public string OutsideBadge => "OUTSIDE";

    [ObservableProperty] private bool _isIncluded;
    [ObservableProperty] private bool _canInclude = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNote))] private string _note = "";
    public bool HasNote => Note.Length > 0;
    [ObservableProperty] private bool _isEditedOutside;

    partial void OnIsIncludedChanged(bool value) => _owner.OnChromaEffectTextureChanged(this);
}

/// <summary>
/// M826: Chroma Studio C4 - the Character window's EFFECTS RECOLOUR.
///
/// <para>The same sliders that recolour the body recolour the effects the skin plays: the colour VALUES of its particle systems (birth colour, colour over life, linger and
/// fresnel colours: every constant and every curve key) and the colour TEXTURES they draw with (sprites, multiplier maps, colour lookups, palettes). The host reads and writes
/// the bins and files (<c>MainWindowViewModel.ChromaEffects.cs</c>); this half holds the lists, the switches, the pending state and the live preview.</para>
///
/// <para><b>The rules</b> are documented where they live: <see cref="SkinEffectColors"/> (the product rule: one carrier of brightness and saturation per emitter, hue on every
/// factor, textures hue only) and docs/plans/chroma-studio.md. Whatever is not recoloured is listed with the reason (masks, data maps, cubemaps).</para>
///
/// <para><b>The live preview.</b> A working copy of each bin that holds a system the skin plays is recoloured with the very same function the save runs
/// (<see cref="EffectColorWorkingSet"/>), the systems it touched are read back through the resolver, and the card publishes them: the playing items are rebuilt with the new
/// definitions (and recoloured copies of the textures they draw with), so both viewports restart the effects with the new colours. Requests collapse into the newest, like the body's.
/// A Direct3D 11 pool texture is also overwritten in place, as the body's textures are.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    // ---- hooks (the host's) ---------------------------------------------------------------------------------

    /// <summary>The skin's effect colour fields with Riot's value and the project's, and the preview's working set: (skin bin, the systems the scan found, the fields the saved recipe owns, the
    /// transform it was saved with) -> snapshot. On a worker, under the readers' lease. Null: this host lists none.</summary>
    public Func<string, IReadOnlyList<EffectSystemEntry>, IReadOnlySet<EffectColorKey>, ColorTransform, ChromaEffectSnapshot?>? ReadChromaEffects { get; set; }

    /// <summary>Writes the colours: (skin bin, transform, fields to recolour, fields of the earlier recipe no longer part of it). Throws when it cannot write at all.</summary>
    public Func<string, ColorTransform, IReadOnlyList<ChromaEffectColorRef>, IReadOnlyList<ChromaEffectColorRef>, Task<ChromaEffectSaveResult>>? SaveChromaEffectColors { get; set; }

    public Func<string, IReadOnlyList<ChromaEffectColorRef>, Task<int>>? RevertChromaEffectColors { get; set; }

    /// <summary>Writes the effect textures (the host gives them their hue-only transform).</summary>
    public Func<string, ColorTransform, IReadOnlyList<ChromaTarget>, IReadOnlyList<ChromaTarget>, Task<ChromaSaveResult>>? SaveChromaEffectTextures { get; set; }

    public Func<string, IReadOnlyList<ChromaTarget>, int>? RevertChromaEffectTextures { get; set; }

    /// <summary>The bin as the project serves it now (by WAD path), or null: how the live preview's working copy is rebased after a save or a revert changed the project.</summary>
    public Func<string, byte[]?>? ReadChromaBin { get; set; }

    // ---- the lists and the state -----------------------------------------------------------------------------

    public ObservableCollection<ChromaEffectSystemRowViewModel> ChromaEffectSystems { get; } = new();
    public ObservableCollection<ChromaEffectTextureRowViewModel> ChromaEffectTextures { get; } = new();

    /// <summary>What the recolour leaves alone, one line per field kind with how many there are: the channel mixers typed as colours, the data maps, the cubemaps.</summary>
    [ObservableProperty] private IReadOnlyList<ChromaRowViewModel> _chromaEffectExcluded = Array.Empty<ChromaRowViewModel>();

    [ObservableProperty] private bool _hasChromaEffects;
    [ObservableProperty] private bool _hasChromaEffectTextures;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChromaEffectProblem))] private string _chromaEffectProblem = "";
    public bool HasChromaEffectProblem => ChromaEffectProblem.Length > 0;

    /// <summary>One line on what the effect lists hold: systems, colours, textures, and how many are switched on.</summary>
    [ObservableProperty] private string _chromaEffectSummary = "";

    /// <summary>"This also changes 3 other skins..." for the effects switched on; empty when nothing included is shared.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChromaEffectShareWarning))] private string _chromaEffectShareWarning = "";
    public bool HasChromaEffectShareWarning => ChromaEffectShareWarning.Length > 0;

    private readonly Dictionary<EffectColorKey, ChromaEffectFieldInfo> _effectFields = new();
    private readonly HashSet<EffectColorKey> _effectIncluded = new();
    private readonly List<ChromaEffectSystemRowViewModel> _effectSystemRows = new();
    private readonly List<ChromaEffectTextureRowViewModel> _effectTextureRows = new();
    private EffectColorWorkingSet? _effectSet;
    private readonly ChromaPreviewBuffers _effectBuffers = new();
    private Task _chromaEffectPrepare = Task.CompletedTask;

    private ColorTransform _chromaSavedEffectTransform = ColorTransform.Identity;
    private IReadOnlyList<ChromaEffectColorRef> _chromaSavedEffectRefs = Array.Empty<ChromaEffectColorRef>();
    private HashSet<EffectColorKey> _chromaSavedEffectKeys = new();
    private IReadOnlyList<ChromaTarget> _chromaSavedEffectTargets = Array.Empty<ChromaTarget>();
    private HashSet<ulong> _chromaSavedEffectHashes = new();

    private static EffectColorKey KeyOf(ChromaEffectColorRef r) => new(r.System, r.Emitter, r.Field);

    internal static string FileNameOf(string path) => FileName(path);

    /// <summary>"constant (1, 0.3, 0.027, 1)" or "curve, 4 keys (0, 0, 0, 1) -> (1, 0.3, 0.027, 0)", with the randomisation said.</summary>
    internal static string DescribeEffectField(EffectColorField f)
    {
        string text = f.IsCurve
            ? $"curve, {f.Keys.Count} key{(f.Keys.Count == 1 ? "" : "s")} {SkinColorInventory.Format(f.Keys[0])} -> {SkinColorInventory.Format(f.Keys[^1])}"
            : f.Constant is { } c ? $"constant {SkinColorInventory.Format(c)}" : "no value";
        if (f.IsCurve && f.Constant is null) text += " (no constant written)";
        text += f.Probability switch
        {
            EffectProbability.Chromatic => " · randomised per channel: the scatter stays on the same RGB channels",
            EffectProbability.Uniform => " · randomised brightness",
            EffectProbability.AlphaOnly => " · randomised alpha",
            _ => "",
        };
        return text;
    }

    private IEnumerable<EffectColorKey> RecolourableEffectKeys => _effectFields.Values.Where(f => f.Recolourable).Select(f => f.Key);

    private IEnumerable<ChromaEffectTextureRowViewModel> IncludedEffectTextureRows => _effectTextureRows.Where(r => r.IsIncluded && r.CanInclude);

    // ---- the card's lifecycle -----------------------------------------------------------------------------------

    /// <summary>Forget the previous skin's effect lists and take the saved recipe's (called by <see cref="ResetChromaRecolour"/>).</summary>
    private void ResetChromaEffects(ChromaSavedRecipe? saved)
    {
        _effectFields.Clear();
        _effectIncluded.Clear();
        _effectReplaced.Clear();
        _effectSystemRows.Clear();
        _effectTextureRows.Clear();
        ChromaEffectSystems.Clear();
        ChromaEffectTextures.Clear();
        ChromaEffectExcluded = Array.Empty<ChromaRowViewModel>();
        HasChromaEffects = false;
        HasChromaEffectTextures = false;
        ChromaEffectProblem = "";
        ChromaEffectSummary = "";
        ChromaEffectShareWarning = "";
        _effectSet = null;
        _effectBuffers.Clear();
        _chromaEffectPrepare = Task.CompletedTask;
        lock (_chromaGate) { _effectTexShown = new HashSet<ulong>(); _effectTexRestore = new HashSet<ulong>(); _effectRequest = ChromaEffectRequest.None; }
        ClearChromaEffectPreview(republish: false);
        _chromaSavedEffectTransform = ColorTransform.Identity;
        _chromaSavedEffectRefs = Array.Empty<ChromaEffectColorRef>();
        _chromaSavedEffectKeys = new HashSet<EffectColorKey>();
        _chromaSavedEffectTargets = Array.Empty<ChromaTarget>();
        _chromaSavedEffectHashes = new HashSet<ulong>();
        if (saved?.Effects is not { } effects) return;
        _chromaSavedEffectTransform = effects.Transform;
        _chromaSavedEffectRefs = effects.Colors;
        _chromaSavedEffectKeys = effects.Colors.Select(KeyOf).ToHashSet();
        _chromaSavedEffectTargets = effects.Textures;
        _chromaSavedEffectHashes = effects.Textures.Select(t => t.Hash).ToHashSet();
    }

    /// <summary>
    /// Whether a skin that has no saved recipe starts with its effects switched ON. It does not: the body is the skin's own, but over 85% of the effect systems a skin plays are shared with
    /// the champion's other skins (M812), the recolour is in place until "this skin only" (C5), and switching them on puts whole shared bins into the project. So the sliders recolour the
    /// body at once, and the effects when the person asks (All colours / All textures, or a system's own switch) - and a saved recipe restores exactly the switches it was saved with.
    /// </summary>
    internal const bool EffectsStartOn = false;

    private bool HasEffectRecipe => _chromaSavedEffectKeys.Count > 0 || _chromaSavedEffectHashes.Count > 0;

    /// <summary>The scan arrived: list the systems the skin plays with their colour fields, and the colour textures they draw with; read both on a worker.</summary>
    private Task PrepareChromaEffectsAsync(int generation, SkinColorInventory inventory)
    {
        _effectFields.Clear();
        _effectIncluded.Clear();
        _effectReplaced.Clear();
        _effectSystemRows.Clear();
        _effectTextureRows.Clear();
        ChromaEffectSystems.Clear();
        ChromaEffectTextures.Clear();
        HasChromaEffects = false;
        HasChromaEffectTextures = false;
        ChromaEffectProblem = "";
        _effectSet = null;
        _effectBuffers.Clear();
        lock (_chromaGate) { _effectTexShown = new HashSet<ulong>(); _effectTexRestore = new HashSet<ulong>(); _effectRequest = ChromaEffectRequest.None; }
        ClearChromaEffectPreview(republish: true);   // a scan again: the playing effects go back to the skin's own, the sliders draw them again below
        if (_chromaSkinBin is not { } skinBin) return Task.CompletedTask;

        // ---- the textures: listed at once, classified on the worker below
        bool recipe = HasRecipeAnywhere;
        var bodyHashes = inventory.BodyTextures.Select(t => t.Hash).ToHashSet();
        var seen = new HashSet<ulong>();
        bool building = _chromaRestoring;
        _chromaRestoring = true;   // the switches below are the classification speaking, not the user
        foreach (var t in inventory.EffectTextures)
        {
            if (!seen.Add(t.Hash)) continue;
            bool outside = SkinColorInventory.IsOutsideCharacter(t.Path, inventory.CharacterFolder);
            bool include = recipe ? _chromaSavedEffectHashes.Contains(t.Hash) : EffectsStartOn && !outside && !t.IsCubemap && !bodyHashes.Contains(t.Hash);
            var row = new ChromaEffectTextureRowViewModel(this, new ChromaTarget(t.Hash, t.Path),
                string.Join("/", t.Roles).ToLowerInvariant() + $" · {t.SystemCount} system(s)" + (outside ? " · outside this character" : ""),
                $"{t.Path}\n0x{t.Hash:x16}", outside, t.SharedWith, include);
            if (t.IsCubemap) { row.CanInclude = false; row.IsIncluded = false; row.Note = "A reflection cubemap (DDS, six faces): the project writer handles TEX BC1/BC3 only, so it is left as it is."; }
            else if (bodyHashes.Contains(t.Hash)) { row.CanInclude = false; row.IsIncluded = false; row.Note = "The body draws this file too: switch it on in the TEXTURES list (it then takes the body's transform)."; }
            _effectTextureRows.Add(row);
        }
        _chromaRestoring = building;
        foreach (var r in _effectTextureRows) ChromaEffectTextures.Add(r);
        HasChromaEffectTextures = _effectTextureRows.Count > 0;

        var read = ReadChromaEffects;
        var readOriginal = ReadChromaOriginal;
        if (read is null && readOriginal is null) return Task.CompletedTask;
        return LoadAsync();

        async Task LoadAsync()
        {
            using var readers = AcquireChromaReaders?.Invoke();   // on this thread, before the worker starts
            var token = _chromaCts.Token;
            var ownedNow = _chromaSavedEffectKeys.ToHashSet();    // copies, taken here on the UI thread
            var recipeTransform = _chromaSavedEffectTransform;
            var rows = _effectTextureRows.Where(r => r.CanInclude).ToArray();
            var wanted = rows.Where(r => r.IsIncluded).Select(r => r.Hash).ToHashSet();
            var savedHashes = _chromaSavedEffectHashes;
            var isEdited = IsChromaProjectEdited;
            ChromaEffectSnapshot? snapshot = null;
            List<EffectTextureClass> classified = new();
            try
            {
                await Task.Run(() =>
                {
                    if (read is not null)
                    {
                        try { snapshot = read(skinBin, inventory.Effects, ownedNow, recipeTransform); }
                        catch (Exception ex) { snapshot = ChromaEffectSnapshot.None("The effect colours could not be read: " + ex.Message); }
                    }
                    if (readOriginal is null) return;
                    var dataMaps = DataMapUses(snapshot);
                    foreach (var row in rows)
                    {
                        if (token.IsCancellationRequested) break;
                        // a file the skin's systems ALSO read as a mask or a data map (erosion, falloff, gloss, transition, distortion normals): a hue change would break that use
                        if (dataMaps.TryGetValue(row.Hash, out var use))
                        {
                            classified.Add(new EffectTextureClass(row, false, $"This file is also read as a data map ({ExcludedLabel(use.Field).ToLowerInvariant()}, e.g. emitter \"{use.EmitterName}\" of {use.SystemName}): "
                                                                              + "recolouring it would change that use, so it is left as it is.", null));
                            continue;
                        }
                        byte[]? bytes;
                        try { bytes = readOriginal(row.Target); }
                        catch (Exception ex) { classified.Add(new EffectTextureClass(row, false, "The original could not be read: " + ex.Message, null)); continue; }
                        if (bytes is null) { classified.Add(new EffectTextureClass(row, false, "The original texture was not found in the game files or the project.", null)); continue; }
                        var verdict = TextureRecolor.Classify(bytes);
                        if (!verdict.Ok) { classified.Add(new EffectTextureClass(row, false, DescribeUnsupported(bytes, verdict), null)); continue; }
                        bool edited = false;
                        try { edited = !savedHashes.Contains(row.Hash) && isEdited is not null && isEdited(row.Target); } catch { /* unknown is not edited */ }
                        TextureImage? image = null;
                        if (wanted.Contains(row.Hash) && !edited)
                        {
                            try { image = TextureDecoder.Decode(bytes); }
                            catch (Exception ex) { classified.Add(new EffectTextureClass(row, false, "The texture could not be decoded: " + ex.Message, null)); continue; }
                        }
                        classified.Add(new EffectTextureClass(row, true, "", image, edited));
                    }
                }, token);
            }
            catch (OperationCanceledException) { return; }
            if (generation != Volatile.Read(ref _chromaGeneration) || token.IsCancellationRequested) return;

            bool was = _chromaRestoring;
            _chromaRestoring = true;
            try
            {
                foreach (var c in classified)
                {
                    if (!c.Supported) { c.Row.CanInclude = false; c.Row.IsIncluded = false; c.Row.Note = c.Reason; continue; }
                    if (c.Edited)
                    {
                        c.Row.IsEditedOutside = true;
                        c.Row.IsIncluded = false;
                        c.Row.Note = "The project's copy of this texture was edited outside the recolour. Switch it on only to replace that edit.";
                        continue;
                    }
                    if (c.Decoded is { } image) _effectBuffers.Set(c.Row.Hash, image);
                    if (_chromaSavedEffectHashes.Contains(c.Row.Hash)) c.Row.Note = "recoloured in the project";
                }
                ApplyEffectSnapshot(snapshot, inventory);
            }
            finally { _chromaRestoring = was; }
            UpdateChromaDirty();
            RequestChromaPreview();   // sliders restored from the project, or moved before the lists were in
        }
    }

    /// <summary>The excluded data-map fields of the scan by the chunk of the file they name: a texture listed as a colour texture that is also one of these is not safe to recolour.</summary>
    internal static Dictionary<ulong, EffectExcludedField> DataMapUses(ChromaEffectSnapshot? snapshot)
    {
        var map = new Dictionary<ulong, EffectExcludedField>();
        if (snapshot is null) return map;
        foreach (var e in snapshot.Excluded)
            if (e.Field is "alphaErosionDefinition.erosionMapName" or "distortionDefinition.normalMapTexture" or "falloffTexture" or "glossTexture" or "transitionTexture")
                map.TryAdd(BinTexturePath.HashOfReference(e.Value), e);
        return map;
    }

    private sealed record EffectTextureClass(ChromaEffectTextureRowViewModel Row, bool Supported, string Reason, TextureImage? Decoded, bool Edited = false);

    /// <summary>A recipe exists for this skin in any part (textures, colour parameters, effects): its items are the ones switched on, nothing else is.</summary>
    private bool HasRecipeAnywhere => _chromaSavedHashes.Count > 0 || _chromaSavedParamKeys.Count > 0 || HasEffectRecipe;

    private void ApplyEffectSnapshot(ChromaEffectSnapshot? snapshot, SkinColorInventory inventory)
    {
        if (snapshot is null) { UpdateEffectSummary(inventory.Effects.Count); return; }
        _effectSet = snapshot.Preview;
        ChromaEffectProblem = snapshot.Problem;
        bool recipe = HasRecipeAnywhere;
        foreach (var f in snapshot.Fields) _effectFields.TryAdd(f.Key, f);

        foreach (var info in snapshot.Systems)
        {
            var fields = snapshot.Fields.Where(f => f.Key.System == info.Hash).OrderBy(f => f.Key.Emitter).ThenBy(f => f.Key.Field, StringComparer.Ordinal).ToList();
            if (fields.Count == 0) continue;
            foreach (var f in fields)
            {
                bool owned = _chromaSavedEffectKeys.Contains(f.Key);
                bool include = recipe
                    ? owned && !f.OwnedEdited
                    : EffectsStartOn && f.Recolourable && !f.EditedOutside && f.DefaultOffReason is null && !f.Riot.EmitterDisabled;
                if (include && f.Recolourable) _effectIncluded.Add(f.Key);
            }
            bool any = fields.Any(f => _effectIncluded.Contains(f.Key));
            var row = new ChromaEffectSystemRowViewModel(this, info, fields, any);
            _effectSystemRows.Add(row);
        }
        foreach (var r in _effectSystemRows) ChromaEffectSystems.Add(r);
        HasChromaEffects = _effectSystemRows.Count > 0;
        ChromaEffectExcluded = BuildEffectExcludedRows(snapshot.Excluded);
        UpdateEffectSummary(inventory.Effects.Count);
    }

    private void UpdateEffectSummary(int systems)
    {
        int fields = _effectFields.Count, recolourable = _effectFields.Values.Count(f => f.Recolourable);
        ChromaEffectSummary = $"{systems} system(s) used · {fields} colour value(s) ({recolourable} can be recoloured, {_effectIncluded.Count} on) · "
                              + $"{_effectTextureRows.Count} colour texture(s) ({IncludedEffectTextureRows.Count()} on)";
    }

    /// <summary>The excluded fields, one line each kind: what, how many and why.</summary>
    internal static IReadOnlyList<ChromaRowViewModel> BuildEffectExcludedRows(IReadOnlyList<EffectExcludedField> excluded)
    {
        var rows = new List<ChromaRowViewModel>();
        foreach (var group in excluded.GroupBy(e => e.Field).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string sample = string.Join(", ", group.Select(e => e.Value).Distinct().Take(3));
            rows.Add(new ChromaRowViewModel(ExcludedLabel(group.Key) + $" x{group.Count()}", group.First().Reason, "left alone",
                tip: group.Key + "\ne.g. " + sample));
        }
        return rows;
    }

    private static string ExcludedLabel(string field) => field switch
    {
        "paletteDefinition.palleteSrcMixColor" => "Palette channel mixer",
        "alphaErosionDefinition.erosionMapChannelMixer" => "Erosion channel mixer",
        "alphaErosionDefinition.erosionMapName" => "Erosion maps",
        "distortionDefinition.normalMapTexture" => "Distortion normal maps",
        "falloffTexture" => "Falloff maps",
        "glossTexture" => "Gloss maps",
        "transitionTexture" => "Transition maps",
        "reflectionDefinition.reflectionMapTexture" => "Reflection cubemaps",
        _ => field,
    };

    // ---- switches ---------------------------------------------------------------------------------------------

    internal void LoadChromaEffectFieldRows(ChromaEffectSystemRowViewModel system)
    {
        foreach (var f in system.FieldInfos)
        {
            bool included = _effectIncluded.Contains(f.Key);
            var row = new ChromaEffectFieldRowViewModel(this, f, included);
            if (f.Recolourable)
            {
                if (_chromaSavedEffectKeys.Contains(f.Key) && f.OwnedEdited) row.Note = "Edited in the Particle Editor since the recolour: kept as it is. Switch it on to recolour it again (that replaces the edit).";
                else if (f.EditedOutside && !_chromaSavedEffectKeys.Contains(f.Key)) row.Note = "The project's value was edited outside the recolour. Switch it on only to replace that edit.";
                else if (f.DefaultOffReason is not null && !_chromaSavedEffectKeys.Contains(f.Key)) row.Note = f.DefaultOffReason;
                else if (f.Riot.EmitterDisabled && !included) row.Note = "A disabled emitter: the game draws nothing from it.";
                else if (_chromaSavedEffectKeys.Contains(f.Key)) row.Note = "recoloured in the project";
            }
            system.Fields.Add(row);
        }
        RefreshChromaEffectSwatches(ChromaSettings.ToTransform());
    }

    internal void OnChromaEffectFieldChanged(ChromaEffectFieldRowViewModel row)
    {
        if (_chromaRestoring || row.Silent) return;
        if (row.IsIncluded && row.CanInclude) _effectIncluded.Add(row.Key); else _effectIncluded.Remove(row.Key);
        var system = _effectSystemRows.FirstOrDefault(s => s.Info.Hash == row.Key.System);
        system?.SetSilently(system.FieldInfos.Any(f => _effectIncluded.Contains(f.Key)));
        UpdateEffectSummary(ChromaInventory?.Effects.Count ?? _effectSystemRows.Count);
        UpdateChromaDirty();
        RequestChromaPreview();
    }

    /// <summary>A bulk switch (a system's, or All colours) turns on only the fields nobody changed: a value somebody edited (outside the recolour, or in the Particle Editor since it) or one the
    /// card leaves off by default is switched on by hand, one at a time - as the bulk switch of the textures does. Switching OFF takes everything.</summary>
    private static bool BulkSwitchable(ChromaEffectFieldInfo f) => f.Recolourable && !f.EditedOutside && !f.OwnedEdited && f.DefaultOffReason is null;

    internal void OnChromaEffectSystemChanged(ChromaEffectSystemRowViewModel system)
    {
        if (_chromaRestoring) return;
        foreach (var f in system.FieldInfos)
        {
            if (!f.Recolourable) continue;
            if (!system.IsIncluded) _effectIncluded.Remove(f.Key);
            else if (BulkSwitchable(f)) _effectIncluded.Add(f.Key);
        }
        foreach (var row in system.Fields) row.SetSilently(row.CanInclude && _effectIncluded.Contains(row.Key));
        system.SetSilently(system.FieldInfos.Any(f => _effectIncluded.Contains(f.Key)));   // a system with nothing switchable stays off
        UpdateEffectSummary(ChromaInventory?.Effects.Count ?? _effectSystemRows.Count);
        UpdateChromaDirty();
        RequestChromaPreview();
    }

    internal void OnChromaEffectTextureChanged(ChromaEffectTextureRowViewModel row)
    {
        if (_chromaRestoring) return;
        UpdateEffectSummary(ChromaInventory?.Effects.Count ?? _effectSystemRows.Count);
        UpdateChromaDirty();
        if (row.IsIncluded && row.CanInclude && !_effectBuffers.Has(row.Hash)) _chromaEffectPrepare = DecodeEffectOriginalAsync(row, Volatile.Read(ref _chromaGeneration));
        else RequestChromaPreview();
    }

    private async Task DecodeEffectOriginalAsync(ChromaEffectTextureRowViewModel row, int generation)
    {
        var read = ReadChromaOriginal;
        if (read is null) return;
        using var readers = AcquireChromaReaders?.Invoke();
        var token = _chromaCts.Token;
        TextureImage? image = null;
        string? error = null;
        try
        {
            image = await Task.Run(() => read(row.Target) is { } bytes ? TextureDecoder.Decode(bytes) : null, token);
            if (image is null) error = "The original texture was not found.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { error = "The texture could not be decoded: " + ex.Message; }
        if (generation != Volatile.Read(ref _chromaGeneration)) return;
        if (image is null)
        {
            bool was = _chromaRestoring;
            _chromaRestoring = true;
            try { row.CanInclude = false; row.IsIncluded = false; row.Note = error ?? ""; }
            finally { _chromaRestoring = was; }
            UpdateChromaDirty();
            return;
        }
        _effectBuffers.Set(row.Hash, image);
        RequestChromaPreview();
    }

    [RelayCommand]
    private void SelectAllEffectColors() => SetAllEffectColors(true);

    [RelayCommand]
    private void SelectNoEffectColors() => SetAllEffectColors(false);

    private void SetAllEffectColors(bool on)
    {
        foreach (var f in _effectFields.Values)
        {
            if (!f.Recolourable) continue;
            if (!on) _effectIncluded.Remove(f.Key);
            else if (BulkSwitchable(f)) _effectIncluded.Add(f.Key);
        }
        foreach (var s in _effectSystemRows)
        {
            s.SetSilently(s.FieldInfos.Any(f => _effectIncluded.Contains(f.Key)));
            foreach (var r in s.Fields) r.SetSilently(r.CanInclude && _effectIncluded.Contains(r.Key));
        }
        UpdateEffectSummary(ChromaInventory?.Effects.Count ?? _effectSystemRows.Count);
        UpdateChromaDirty();
        RequestChromaPreview();
    }

    [RelayCommand]
    private void SelectAllEffectTextures() => SetAllEffectTextures(true);

    [RelayCommand]
    private void SelectNoEffectTextures() => SetAllEffectTextures(false);

    private void SetAllEffectTextures(bool on)
    {
        bool was = _chromaRestoring;
        _chromaRestoring = true;
        // "all" is the files of this character's own folder: a shared sprite outside it is switched on one by one, never by a bulk switch
        try { foreach (var r in _effectTextureRows) if (r.CanInclude) r.IsIncluded = on && !r.IsEditedOutside && !r.IsOutside; }
        finally { _chromaRestoring = was; }
        UpdateEffectSummary(ChromaInventory?.Effects.Count ?? _effectSystemRows.Count);
        UpdateChromaDirty();
        var missing = _effectTextureRows.Where(r => r.IsIncluded && r.CanInclude && !_effectBuffers.Has(r.Hash)).ToList();
        if (missing.Count > 0) _chromaEffectPrepare = DecodeEffectOriginalsAsync(missing, Volatile.Read(ref _chromaGeneration));
        else RequestChromaPreview();
    }

    private async Task DecodeEffectOriginalsAsync(IReadOnlyList<ChromaEffectTextureRowViewModel> rows, int generation)
    {
        foreach (var row in rows) await DecodeEffectOriginalAsync(row, generation);
    }

    // ---- state ------------------------------------------------------------------------------------------------

    /// <summary>The effect colour fields the saved recipe owns that this card has no switch for (the scan did not list them, or the host says they cannot be recoloured now). CARRIED, like the
    /// parameters a scan omits: part of the saved state, never a target and never stale.</summary>
    private List<ChromaEffectColorRef> CarriedChromaEffects() =>
        _chromaSavedEffectRefs.Where(c => !_effectFields.TryGetValue(KeyOf(c), out var f) || !f.Recolourable
                                          || (f.OwnedEdited && !_effectIncluded.Contains(f.Key))).ToList();   // a field the Particle Editor changed since stays as it was edited until it is switched on again

    private List<ChromaTarget> CarriedChromaEffectTextures() =>
        _chromaSavedEffectTargets.Where(t => !_effectTextureRows.Any(r => r.Hash == t.Hash && r.CanInclude)).ToList();

    /// <param name="Replace">Owned fields the Particle Editor changed since the recolour that the person switched on again: the next save replaces that edit with the recolour of Riot's
    /// value - a change of state even though the set of fields and the transform are the saved ones.</param>
    private readonly record struct ChromaEffectState(ColorTransform Transform, HashSet<EffectColorKey> Colors, HashSet<ulong> Textures, HashSet<EffectColorKey> Replace)
    {
        public bool Differs(ColorTransform savedTransform, HashSet<EffectColorKey> savedColors, HashSet<ulong> savedTextures) =>
            Replace.Count > 0 || !(Transform.Equals(savedTransform) && Colors.SetEquals(savedColors) && Textures.SetEquals(savedTextures));
        public bool SameAs(ChromaEffectState other) =>
            Transform.Equals(other.Transform) && Colors.SetEquals(other.Colors) && Textures.SetEquals(other.Textures) && Replace.SetEquals(other.Replace);
    }

    /// <summary>Kept edits the person has already replaced by saving them (their field info still says "edited since": that was true when the card was made).</summary>
    private readonly HashSet<EffectColorKey> _effectReplaced = new();

    private ChromaEffectState ChromaEffectStateNow(ColorTransform transform)
    {
        var colors = _effectIncluded.Where(k => _effectFields.TryGetValue(k, out var f) && f.Recolourable).ToHashSet();
        var textures = IncludedEffectTextureRows.Select(r => r.Hash).ToHashSet();
        if (!transform.IsIdentity)
        {
            foreach (var c in CarriedChromaEffects()) colors.Add(KeyOf(c));
            foreach (var t in CarriedChromaEffectTextures()) textures.Add(t.Hash);
        }
        // the textures take the hue only: a move of brightness or saturation alone changes none of them, so the texture part is EMPTY and what was saved of it goes back to Riot's
        if (SkinEffectColors.TextureTransform(transform).IsIdentity) textures.Clear();
        bool none = transform.IsIdentity || (colors.Count == 0 && textures.Count == 0);
        var replace = none ? new HashSet<EffectColorKey>()
            : _effectIncluded.Where(k => _chromaSavedEffectKeys.Contains(k) && _effectFields.TryGetValue(k, out var f) && f.OwnedEdited && !_effectReplaced.Contains(k)).ToHashSet();
        return new ChromaEffectState(none ? ColorTransform.Identity : transform, none ? new HashSet<EffectColorKey>() : colors, none ? new HashSet<ulong>() : textures, replace);
    }

    /// <summary>The "this also changes N other skins" text for the effects switched on.</summary>
    internal static string BuildChromaEffectShareWarning(IReadOnlyList<ChromaEffectSystemRowViewModel> systems, IReadOnlyList<ChromaEffectTextureRowViewModel> textures,
        IReadOnlyCollection<ChromaEffectFieldInfo>? editedColors = null)
    {
        var skins = systems.SelectMany(s => s.SharedWith).Concat(textures.SelectMany(t => t.SharedWith)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int sharedSystems = systems.Count(s => s.IsShared), sharedFiles = textures.Count(t => t.IsShared);
        int outside = textures.Count(t => t.IsOutside), edited = textures.Count(t => t.IsEditedOutside);
        var parts = new List<string>();
        if (skins.Count > 0)
        {
            int more = skins.Count - ChromaRowViewModel.MaxNamedSharers;
            parts.Add($"This also changes {skins.Count} other skin(s) or chroma(s) of this character that use the same effect system(s) ({sharedSystems}) or texture file(s) ({sharedFiles}): "
                      + string.Join(", ", skins.Take(ChromaRowViewModel.MaxNamedSharers)) + (more > 0 ? $" and {more} more" : "")
                      + ". The recolour is in place; a copy for this skin only comes in a later step.");
        }
        if (outside > 0)
            parts.Add($"{outside} effect texture(s) lie outside this character's own folder: other characters, maps or global effects that use them change too, and they were not compared.");
        if (edited > 0)
            parts.Add($"{edited} effect texture(s) are project files somebody edited outside the recolour: saving replaces that edit with the recolour of Riot's original.");
        if (editedColors is { Count: > 0 })
            parts.Add($"{editedColors.Count} effect colour value(s) were edited since Riot's (outside the recolour, or in the Particle Editor): saving replaces that edit with the recolour of Riot's value.");
        return string.Join("\n", parts);
    }

    private void RefreshChromaEffectShareWarning() =>
        ChromaEffectShareWarning = BuildChromaEffectShareWarning(
            _effectSystemRows.Where(s => s.FieldInfos.Any(f => _effectIncluded.Contains(f.Key))).ToList(), IncludedEffectTextureRows.ToList(),
            _effectFields.Values.Where(f => _effectIncluded.Contains(f.Key) && (f.EditedOutside || (f.OwnedEdited && !_effectReplaced.Contains(f.Key)))).ToList());

    // ---- the swatches --------------------------------------------------------------------------------------------

    /// <summary>The swatch beside each listed field: what the sliders make of Riot's colour (Riot's own when the field is off).</summary>
    private void RefreshChromaEffectSwatches(ColorTransform transform)
    {
        if (_effectSystemRows.Count == 0) return;
        HashSet<EffectColorKey>? carriers = null;
        foreach (var system in _effectSystemRows)
            foreach (var row in system.Fields)
            {
                var riot = ChromaEffectFieldRowViewModel.Representative(row.Info.Riot);
                if (!(row.IsIncluded && row.CanInclude) || transform.IsIdentity) { row.ResultBrush = row.OriginalBrush; continue; }
                carriers ??= SkinEffectColors.Carriers(_effectIncluded.Where(k => _effectFields.TryGetValue(k, out var f) && f.Recolourable).Select(k => _effectFields[k].Riot));
                row.ResultBrush = ChromaParameterRowViewModel.Swatch(SkinEffectColors.TransformFor(row.Key, transform, carriers).Apply(riot));
            }
    }

    // ---- the live preview ---------------------------------------------------------------------------------------------

    /// <summary>What one preview pass of the effects needs, captured on the UI thread.</summary>
    private sealed record ChromaEffectRequest(ColorTransform Transform, EffectColorKey[] Recolour, EffectColorKey[] Restore, ColorTransform? Recipe, EffectColorKey[] RecipeKeys,
        ulong[] Textures, HashSet<ulong> SavedTextures, EffectColorWorkingSet? Set, Dictionary<ulong, string[]> Keys, Dictionary<ulong, HashSet<string>> Drawn)
    {
        public static readonly ChromaEffectRequest None = new(ColorTransform.Identity, Array.Empty<EffectColorKey>(), Array.Empty<EffectColorKey>(), null, Array.Empty<EffectColorKey>(),
            Array.Empty<ulong>(), new HashSet<ulong>(), null, new Dictionary<ulong, string[]>(), new Dictionary<ulong, HashSet<string>>());
    }

    private ChromaEffectRequest _effectRequest = ChromaEffectRequest.None;
    private HashSet<ulong> _effectTexShown = new();      // chunks the renderers draw as a recolour right now (under _chromaGate)
    private HashSet<ulong> _effectTexRestore = new();    // chunks to draw as their originals once (a revert; under _chromaGate)

    private ChromaEffectRequest CurrentChromaEffectPreview(ColorTransform transform)
    {
        if (_effectFields.Count == 0 && _effectTextureRows.Count == 0) return ChromaEffectRequest.None;
        var included = _effectIncluded.Where(k => _effectFields.TryGetValue(k, out var f) && f.Recolourable).ToArray();
        // identity: nothing is written over what the project holds; the saved recipe's fields are given back to Riot's, as saving would
        var recolour = transform.IsIdentity ? Array.Empty<EffectColorKey>() : included;
        var inSet = included.ToHashSet();
        var restore = _chromaSavedEffectKeys.Where(k => transform.IsIdentity || !inSet.Contains(k)).ToArray();
        var keys = new Dictionary<ulong, string[]>();
        foreach (var r in _effectTextureRows)
            keys[r.Hash] = new[] { r.Target.Path.ToLowerInvariant() };
        var textures = IncludedEffectTextureRows.Select(r => r.Hash).ToArray();
        _effectWanted = SkinEffectColors.TextureTransform(transform).IsIdentity ? new HashSet<ulong>() : textures.ToHashSet();
        return new ChromaEffectRequest(transform, recolour, restore, _chromaSavedEffectKeys.Count > 0 ? _chromaSavedEffectTransform : null,
            _chromaSavedEffectKeys.ToArray(), textures, _chromaSavedEffectHashes, _effectSet, keys, PlayingEffectTextureHashes());
    }

    /// <summary>The texture chunks the items playing now (idle effects, the event composite, whatever is published, their children) draw: only these are worth a copy and a push. Each with the
    /// spellings the definitions name it by, lower-cased - the key the Direct3D 11 particle pipeline binds it under in the shared pool.</summary>
    private Dictionary<ulong, HashSet<string>> PlayingEffectTextureHashes()
    {
        var hashes = new Dictionary<ulong, HashSet<string>>();
        void Walk(IEnumerable<VfxPlaybackItem>? items)
        {
            if (items is null) return;
            foreach (var item in items)
            {
                foreach (var e in item.System.Emitters)
                    foreach (var path in new[] { e.TexturePath, e.TextureMultPath, e.ParticleColorTexturePath, e.Palette?.TexturePath })
                        if (path is { Length: > 0 })
                        {
                            ulong h = BinTexturePath.HashOfReference(path);
                            if (!hashes.TryGetValue(h, out var spellings)) hashes[h] = spellings = new HashSet<string>(StringComparer.Ordinal);
                            spellings.Add(path.ToLowerInvariant());
                        }
                if (item.EmitterChildren is { } kids) foreach (var list in kids) Walk(list);
            }
        }
        Walk(_idleItems);
        Walk(_eventBundle);
        Walk(Playback?.Items);
        return hashes;
    }

    /// <summary>One batch for the UI thread: the new definitions of the systems whose colours moved, and the recoloured effect textures.</summary>
    private sealed record ChromaEffectBatch(IReadOnlyDictionary<uint, VfxSystemDefinition> Defs, List<ChromaEffectPush> Images)
    {
        public bool IsEmpty => Defs.Count == 0 && Images.Count == 0;
    }

    private ChromaEffectBatch RenderChromaEffects(ChromaEffectRequest request, HashSet<ulong> shown, HashSet<ulong> restore, out HashSet<ulong> nowShown, CancellationToken ct)
    {
        IReadOnlyDictionary<uint, VfxSystemDefinition> defs = new Dictionary<uint, VfxSystemDefinition>();
        if (request.Set is { } set && (request.Recolour.Length > 0 || request.Restore.Length > 0 || _effectPreviewTouched))
        {
            var preview = set.Apply(request.Transform, request.Recolour, request.Restore, request.Recipe, request.RecipeKeys);
            defs = preview.Changed;
            _effectPreviewTouched = true;
        }

        var images = new List<ChromaEffectPush>();
        var textureTransform = SkinEffectColors.TextureTransform(request.Transform);
        var inSet = request.Textures.ToHashSet();
        nowShown = new HashSet<ulong>();
        foreach (ulong hash in request.Textures)
        {
            // as the body's textures: under no change a texture is drawn only to take a recolour OFF the screen
            if (textureTransform.IsIdentity && !shown.Contains(hash) && !restore.Contains(hash) && !request.SavedTextures.Contains(hash)) continue;
            // a copy and a push only for a texture an item plays draws (or one already on screen as a recolour, which is kept in step); a newly played item asks for the rest
            if (!request.Drawn.ContainsKey(hash) && !shown.Contains(hash) && !restore.Contains(hash)) continue;
            if (Render(hash, textureTransform) && !textureTransform.IsIdentity) nowShown.Add(hash);
        }
        foreach (ulong hash in shown.Concat(restore).Distinct())
            if (!inSet.Contains(hash)) Render(hash, ColorTransform.Identity);
        return new ChromaEffectBatch(defs, images);

        bool Render(ulong hash, ColorTransform t)
        {
            if (_effectBuffers.Render(hash, t, ct) is not { } rgba || !_effectBuffers.TrySize(hash, out int w, out int h)) return false;
            // a copy: the buffer is reused by the next render, and the CPU gradient reads its array for as long as the item lives
            var spellings = request.Keys.TryGetValue(hash, out var keys) ? keys : Array.Empty<string>();
            if (request.Drawn.TryGetValue(hash, out var named)) spellings = spellings.Concat(named).Distinct(StringComparer.Ordinal).ToArray();
            var copy = spellings.Length > 0 ? (byte[])rgba.Clone() : null;   // one copy for every spelling (the pool copies it again; the items only read it)
            foreach (string key in spellings)
                images.Add(new ChromaEffectPush(hash, key, copy!, w, h));
            return true;
        }
    }

    private bool _effectPreviewTouched;

    // ---- publishing: definitions and textures into the playing items ------------------------------------------------

    private IReadOnlyDictionary<uint, VfxSystemDefinition> _vfxDefsBase = new Dictionary<uint, VfxSystemDefinition>();
    private readonly Dictionary<uint, VfxSystemDefinition> _effectOverlay = new();
    private readonly Dictionary<ulong, TextureImage> _effectImages = new();
    // recoloured copy -> the instance the host cached. WEAK on the copy: every tick makes new copies and the items drop the old ones, so nothing here outlives the item that holds it.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextureImage, TextureImage> _effectOriginal = new();
    private bool _effectHasOriginals;
    private HashSet<ulong> _effectWanted = new();   // chunks the preview recolours right now (UI thread): an item that draws one without a copy asks for it
    private bool _effectAskPending;

    internal int EffectOriginalCount => System.Linq.Enumerable.Count(_effectOriginal);

    /// <summary>Drop what the preview published: the skin's own definitions and textures are what plays again. <paramref name="republish"/>: also rebuild the playing items on them
    /// (a scan again of the same skin); not when the window is moving on to another skin, whose definitions replace these anyway.</summary>
    private void ClearChromaEffectPreview(bool republish)
    {
        bool had = _effectOverlay.Count > 0 || _effectImages.Count > 0;
        _effectOverlay.Clear();
        _effectImages.Clear();
        _effectOriginal.Clear();
        _effectHasOriginals = false;
        _effectWanted = new HashSet<ulong>();
        _effectPreviewTouched = false;
        if (!had) return;
        _vfxDefs = _vfxDefsBase;
        if (republish) RepublishChromaEffectPlayback();
    }

    internal void SetVfxBaseDefinitions(IReadOnlyDictionary<uint, VfxSystemDefinition> systems)
    {
        _vfxDefsBase = systems;
        _effectOverlay.Clear();
        _effectImages.Clear();
        _effectOriginal.Clear();
        _effectHasOriginals = false;
        _effectWanted = new HashSet<ulong>();
        _effectPreviewTouched = false;
        lock (_chromaGate) { _effectTexShown = new HashSet<ulong>(); _effectTexRestore = new HashSet<ulong>(); }
    }

    /// <summary>Called on the UI thread with a batch: the definitions replace the skin's in <c>_vfxDefs</c>, the images replace the textures of the playing items, and the playing items are
    /// published again, so both viewports rebuild their simulators with the new colours.</summary>
    private void PushChromaEffectBatch(ChromaEffectBatch batch, int generation)
    {
        if (batch.IsEmpty || generation != Volatile.Read(ref _chromaGeneration)) return;
        foreach (var (hash, def) in batch.Defs) _effectOverlay[hash] = def;
        foreach (var image in batch.Images) _effectImages[image.Hash] = new TextureImage(image.Width, image.Height, image.Rgba);
        if (batch.Defs.Count > 0)
        {
            var merged = new Dictionary<uint, VfxSystemDefinition>(_vfxDefsBase);
            foreach (var (hash, def) in _effectOverlay) merged[hash] = def;
            _vfxDefs = merged;
        }
        RepublishChromaEffectPlayback();

        // Direct3D 11: the pool entry the particle pipeline bound is overwritten in place too (the new item's image is only used for a key the pool does not hold yet)
        if (batch.Images.Count > 0 && UseDx11Preview && PushChromaDx11 is { } dx)
            dx(batch.Images.Select(i => (i.Key, i.Rgba, i.Width, i.Height)).ToList());
    }

    /// <summary>The playing items - the idle effects, the event composite and whatever is published - rebuilt on the current overlay.</summary>
    private void RepublishChromaEffectPlayback()
    {
        if (_idleItems.Count > 0) _idleItems = _idleItems.Select(RecolourItem).ToList();
        if (_eventBundle is { Count: > 0 }) _eventBundle = _eventBundle.Select(RecolourItem).ToList();
        if (Playback is { } playback)
            Playback = new VfxPlayback(playback.Items.Select(RecolourItem).ToList(), playback.CullByCamera);
    }

    /// <summary>The item with the recoloured definition of its system and the recoloured copies of the textures it draws with (children included). The item itself when nothing applies.</summary>
    private VfxPlaybackItem RecolourItem(VfxPlaybackItem item)
    {
        if (_vfxDefsBase.Count == 0) return item;
        // the recoloured definition when the preview made one, else the skin's own (so a preview that was dropped takes the item back to it)
        var system = _effectOverlay.TryGetValue(item.System.PathHash, out var def) ? def
                   : _vfxDefsBase.TryGetValue(item.System.PathHash, out var own) ? own : item.System;
        var result = ReferenceEquals(system, item.System) ? item : item with { System = system };

        if (_effectImages.Count > 0 || _effectHasOriginals || _effectWanted.Count > 0)
        {
            var sprites = ReplaceImages(result.EmitterTextures, system, e => e.TexturePath);
            var mult = ReplaceImages(result.EmitterMultTextures, system, e => e.TextureMultPath);
            var color = ReplaceImages(result.EmitterColorTextures, system, e => e.ParticleColorTexturePath);
            var palette = ReplaceImages(result.EmitterPaletteTextures, system, e => e.Palette?.TexturePath);
            if (!ReferenceEquals(sprites, result.EmitterTextures) || !ReferenceEquals(mult, result.EmitterMultTextures)
                || !ReferenceEquals(color, result.EmitterColorTextures) || !ReferenceEquals(palette, result.EmitterPaletteTextures))
                result = result with { EmitterTextures = sprites!, EmitterMultTextures = mult, EmitterColorTextures = color, EmitterPaletteTextures = palette };
        }

        if (result.EmitterChildren is { } kids)
        {
            List<IReadOnlyList<VfxPlaybackItem>?>? changed = null;
            for (int i = 0; i < kids.Count; i++)
            {
                if (kids[i] is not { } list) continue;
                var mapped = list.Select(RecolourItem).ToList();
                bool same = true;
                for (int k = 0; k < mapped.Count; k++) if (!ReferenceEquals(mapped[k], list[k])) { same = false; break; }
                if (same) continue;
                changed ??= kids.ToList();
                changed[i] = mapped;
            }
            if (changed is not null) result = result with { EmitterChildren = changed };
        }
        return result;
    }

    /// <summary>The recoloured copy of a recoloured texture to each of its places in the list; where the preview no longer recolours it, the original instance is put back.</summary>
    private IReadOnlyList<TextureImage?>? ReplaceImages(IReadOnlyList<TextureImage?>? list, VfxSystemDefinition system, Func<VfxEmitterDefinition, string?> pathOf)
    {
        if (list is null) return null;
        List<TextureImage?>? copy = null;
        for (int i = 0; i < list.Count && i < system.Emitters.Count; i++)
        {
            var current = list[i];
            if (pathOf(system.Emitters[i]) is not { Length: > 0 } path) continue;
            ulong hash = BinTexturePath.HashOfReference(path);
            if (_effectImages.TryGetValue(hash, out var replacement))
            {
                if (ReferenceEquals(current, replacement)) continue;
                // remember the instance the host cached, so the preview can hand it back
                var original = current is not null && _effectOriginal.TryGetValue(current, out var first) ? first : current;
                if (original is not null) { _effectOriginal.AddOrUpdate(replacement, original); _effectHasOriginals = true; }
                (copy ??= list.ToList())[i] = replacement;
            }
            else if (current is not null && _effectOriginal.TryGetValue(current, out var back))
                (copy ??= list.ToList())[i] = back;
            else if (_effectWanted.Contains(hash) && _effectBuffers.Has(hash))
                AskForEffectTexture();
        }
        return copy ?? list;
    }

    /// <summary>An item that was just built draws a texture the preview recolours but holds no copy of yet (the preview only copies what the items playing then drew): one more pass, once the
    /// item is published.</summary>
    private void AskForEffectTexture()
    {
        if (_effectAskPending) return;
        _effectAskPending = true;
        _ = ChromaUiPost(() => { _effectAskPending = false; RequestChromaPreview(); });
    }

    // ---- saving the effects part -------------------------------------------------------------------------------------

    /// <summary>The project's bins changed: the preview's working copy is read again from them, and the systems the preview had touched (and <paramref name="extra"/>) are published as the
    /// rebased copy holds them.</summary>
    private async Task RebaseEffectPreviewAsync(IEnumerable<uint> extra)
    {
        if (_effectSet is not { } set || ReadChromaBin is not { } read) return;
        var more = extra.ToList();
        IReadOnlyCollection<uint> touched;
        using (var readers = AcquireChromaReaders?.Invoke())
            touched = await Task.Run(() => set.Rebase(read));
        var defs = new Dictionary<uint, VfxSystemDefinition>();
        foreach (uint system in touched.Concat(more).Distinct())
            if (set.Definition(system) is { } def) defs[system] = def;
        if (defs.Count == 0) return;
        // the project's bins hold these colours now: they are the skin's own from here on, so a scan again (which drops the overlay) plays them, not the colours read before the save
        var rebased = new Dictionary<uint, VfxSystemDefinition>(_vfxDefsBase);
        foreach (var (hash, def) in defs) if (rebased.ContainsKey(hash)) rebased[hash] = def;
        _vfxDefsBase = rebased;
        PushChromaEffectBatch(new ChromaEffectBatch(defs, new List<ChromaEffectPush>()), Volatile.Read(ref _chromaGeneration));
    }

    /// <summary>The effects half of a save (colours first, then textures); each part only when it differs from what the project holds. Returns the line the status shows.</summary>
    private async Task<string> SaveChromaEffectsPartAsync(string skinBin, ChromaEffectState state)
    {
        var notes = new List<string>();
        var transform = state.Transform;
        var keys = state.Colors;
        var hashes = state.Textures;

        // ---- the colour values
        if (SaveChromaEffectColors is { } saveColors)
        {
            var targets = _effectIncluded.Where(k => keys.Contains(k) && _effectFields.ContainsKey(k)).Select(k => RefOf(_effectFields[k])).ToList();
            var carried = keys.Count == 0 ? new List<ChromaEffectColorRef>() : CarriedChromaEffects();
            var stale = _chromaSavedEffectRefs.Where(c => !keys.Contains(KeyOf(c))).ToList();
            if (targets.Count > 0 || stale.Count > 0 || _chromaSavedEffectKeys.Count > 0)
            {
                var result = await saveColors(skinBin, transform, targets, stale);
                await RebaseEffectPreviewAsync(targets.Select(t => t.System).Concat(stale.Select(c => c.System)).Distinct().ToList());   // the project's bins moved: the preview's baseline with them
                var settled = new HashSet<EffectColorKey>(result.Settled);
                _effectReplaced.UnionWith(state.Replace.Where(settled.Contains));   // the edit is replaced: from here the field is the recolour's like any other
                foreach (var keep in carried) settled.Add(KeyOf(keep));
                bool complete = targets.All(t => settled.Contains(KeyOf(t)));
                if (!complete) _chromaFailedState = ChromaStateNow();
                _chromaSavedEffectRefs = targets.Where(t => settled.Contains(KeyOf(t))).Concat(carried).ToList();
                _chromaSavedEffectKeys = settled;
                _chromaSavedEffectTransform = settled.Count == 0 && _chromaSavedEffectHashes.Count == 0 ? ColorTransform.Identity : transform;
                foreach (var system in _effectSystemRows)
                    foreach (var row in system.Fields)
                    {
                        bool owned = _chromaSavedEffectKeys.Contains(row.Key);
                        if (owned) row.Note = "recoloured in the project";
                        else if (stale.Any(c => KeyOf(c) == row.Key)) row.Note = row.CanInclude ? "" : row.Info.Reason;
                    }
                if (!complete && result.Notes.Count == 0) throw new InvalidOperationException(result.Summary);
                notes.Add(result.Summary + "." + (result.Notes.Count > 0 ? " " + result.Notes[0] : ""));
            }
        }

        // ---- the colour textures
        if (SaveChromaEffectTextures is { } saveTextures && (hashes.Count > 0 || _chromaSavedEffectHashes.Count > 0))
        {
            var textureTargets = IncludedEffectTextureRows.Where(r => hashes.Contains(r.Hash)).Select(r => r.Target).ToList();
            var carried = hashes.Count == 0 ? new List<ChromaTarget>() : CarriedChromaEffectTextures();
            var stale = _chromaSavedEffectTargets.Where(t => !hashes.Contains(t.Hash)).ToList();
            var result = await saveTextures(skinBin, transform, textureTargets, stale);
            if (result.Failed > 0 && result.Written == 0 && textureTargets.Count > 0)
                throw new InvalidOperationException(result.Summary + (result.Notes.Count > 0 ? ": " + result.Notes[0] : ""));
            if (result.Failed > 0) _chromaFailedState = ChromaStateNow();
            var settled = new HashSet<ulong>(result.Settled);
            var written = new HashSet<ulong>(result.WrittenHashes);
            foreach (var keep in carried) settled.Add(keep.Hash);
            _chromaSavedEffectTargets = textureTargets.Where(t => written.Contains(t.Hash)).Concat(carried).ToList();
            _chromaSavedEffectHashes = settled;
            _chromaSavedEffectTransform = settled.Count == 0 && _chromaSavedEffectKeys.Count == 0 ? ColorTransform.Identity : transform;
            foreach (var row in _effectTextureRows)
                row.Note = written.Contains(row.Hash) ? "recoloured in the project" : (row.CanInclude ? "" : row.Note);
            notes.Add(result.Summary + ".");
        }
        HasSavedChromaRecolour = HasAnySavedRecolour;
        return string.Join(" ", notes.Where(n => n.Length > 0));
    }

    private static ChromaEffectColorRef RefOf(ChromaEffectFieldInfo f) => new()
    {
        System = f.Key.System, Emitter = f.Key.Emitter, Field = f.Key.Field, Bin = f.Bin,
        SystemName = f.Riot.SystemName, EmitterName = f.Riot.EmitterName,
    };

    private bool HasAnySavedRecolour => _chromaSavedTargets.Count > 0 || _chromaSavedParamRefs.Count > 0 || _chromaSavedEffectRefs.Count > 0 || _chromaSavedEffectTargets.Count > 0;

    /// <summary>Revert's effects half: the colours and textures back to Riot's, each reported on its own.</summary>
    private async Task<(int Colors, int Textures, string? Failure)> RevertChromaEffectsAsync(string skinBin)
    {
        var colors = _chromaSavedEffectRefs.ToList();
        var targets = _chromaSavedEffectTargets.ToList();
        int restoredColors = 0, restoredTextures = 0;
        string? failure = null;
        if (targets.Count > 0 && RevertChromaEffectTextures is { } revertTextures)
            restoredTextures = revertTextures(skinBin, targets);
        if (colors.Count > 0 && RevertChromaEffectColors is { } revertColors)
        {
            try { restoredColors = await revertColors(skinBin, colors); }
            catch (Exception ex) { failure = ex.Message; }
            if (failure is null) await RebaseEffectPreviewAsync(colors.Select(c => c.System).Distinct());   // the reverted systems play Riot's colours again
        }
        if (failure is null)
        {
            _chromaSavedEffectTransform = ColorTransform.Identity;
            _chromaSavedEffectRefs = Array.Empty<ChromaEffectColorRef>();
            _chromaSavedEffectKeys = new HashSet<EffectColorKey>();
            _chromaSavedEffectTargets = Array.Empty<ChromaTarget>();
            _chromaSavedEffectHashes = new HashSet<ulong>();
            foreach (var system in _effectSystemRows)
                foreach (var row in system.Fields) row.Note = row.CanInclude ? "" : row.Info.Reason;
            foreach (var row in _effectTextureRows) if (row.CanInclude) row.Note = "";
            lock (_chromaGate) { _effectTexRestore = new HashSet<ulong>(_effectTexRestore.Concat(targets.Select(t => t.Hash))); }
        }
        return (restoredColors, restoredTextures, failure);
    }
}
