using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.App.Services;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M825: one colour parameter in the BODY RECOLOUR's PARAMETERS list - an include switch, what the parameter is, Riot's colour and the
/// colour the sliders make of it. A parameter a recolour must not act on (a size that is named after a colour, a negative colour, one
/// that lives in a linked bin) is listed with the reason and cannot be switched on: nothing is skipped silently.
/// </summary>
public sealed partial class ChromaParameterRowViewModel : ObservableObject
{
    private readonly MeshPreviewViewModel _owner;

    public ChromaParameterRowViewModel(MeshPreviewViewModel owner, ChromaParameterInfo info, bool include)
    {
        _owner = owner;
        Info = info;
        Name = (ShortName(info.MaterialName) + " . " + info.Key.Name) + (info.Key.Occurrence > 0 ? $" (#{info.Key.Occurrence + 1})" : "");
        string? why = MeshPreviewViewModel.WhyNotPreviewed(info);
        CanPreview = why is null;
        Detail = SkinColorInventory.Format(info.Riot) + " · " + info.TypeName + (why is null ? "" : " · not drawn in the preview: " + why);
        Tip = $"{info.MaterialName}\n{info.Key.Name}  ({info.TypeName})\nRiot's value {SkinColorInventory.Format(info.Riot)}";
        CurrentValue = info.Current;
        OriginalBrush = Swatch(info.Riot);
        _resultBrush = OriginalBrush;
        CanInclude = info.Recolourable;
        _isIncluded = include && info.Recolourable;
        if (!info.Recolourable) _note = info.Reason;
        IsEditedOutside = info.EditedOutside || info.OwnedEdited;
        OwnedEditedNow = info.OwnedEdited;
    }

    /// <summary>The recipe owns this parameter but its value was edited since (see <see cref="ChromaParameterInfo.OwnedEdited"/>). Cleared when a save writes it.</summary>
    public bool OwnedEditedNow { get; set; }

    /// <summary>An edit of the parameter that is kept: the recipe owns it, it was edited since, and the switch is off - so no save touches it.</summary>
    public bool KeepsEdit => OwnedEditedNow && !IsIncluded;

    public ChromaParameterInfo Info { get; }
    public SkinColorParamKey Key => Info.Key;
    public string Name { get; }
    public string Detail { get; }
    public string Tip { get; }
    /// <summary>The preview can draw this parameter's recolour (see <see cref="MeshPreviewViewModel.WhyNotPreviewed"/>).</summary>
    public bool CanPreview { get; }

    /// <summary>What the project's bin holds for this parameter now. Moves when a save or a revert changes it.</summary>
    public Vector4 CurrentValue { get; set; }

    /// <summary>Riot's colour as a swatch: left of the arrow.</summary>
    public IBrush OriginalBrush { get; }

    /// <summary>The colour the sliders make of it, as a swatch: right of the arrow.</summary>
    [ObservableProperty] private IBrush _resultBrush;

    [ObservableProperty] private bool _isIncluded;

    /// <summary>False when the parameter must not be recoloured; <see cref="Note"/> says why.</summary>
    [ObservableProperty] private bool _canInclude = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNote))] private string _note = "";
    public bool HasNote => Note.Length > 0;

    /// <summary>The project's value is neither Riot's nor a recolour's: somebody edited it. Left off by default.</summary>
    [ObservableProperty] private bool _isEditedOutside;

    partial void OnIsIncludedChanged(bool value) => _owner.OnChromaParameterIncludeChanged(this);

    public ChromaParameterRef ToRef() => new()
    {
        Material = Info.Key.Material, MaterialName = Info.MaterialName, Name = Info.Key.Name, Occurrence = Info.Key.Occurrence,
    };

    internal static string ShortName(string material)
    {
        int cut = material.LastIndexOf('/');
        return cut >= 0 && cut + 1 < material.Length ? material[(cut + 1)..] : material;
    }

    /// <summary>A colour as an opaque swatch. Above-1 (HDR) and negative values clamp - the swatch is a picture of the colour, not the number.</summary>
    internal static IBrush Swatch(Vector4 c)
    {
        static byte B(float v) => float.IsNaN(v) ? (byte)0 : (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
        return new ImmutableSolidColorBrush(Color.FromRgb(B(c.X), B(c.Y), B(c.Z)));
    }
}

/// <summary>
/// M825: Chroma Studio C3 - the colour PARAMETERS half of the Character window's BODY RECOLOUR.
///
/// <para>The same sliders that recolour the body textures recolour the skin bin's colour parameters (<c>TintColor</c>, <c>OutlineColor</c>,
/// the skin block's <c>fresnelColor</c>...), with the same <see cref="ColorTransform"/>. They are not pool textures, so there is nothing to
/// decode and nothing to re-encode: a parameter is four floats, and the D3D11 scene reads them from its materials on every draw, so the
/// preview is an in-place write of the recoloured value into the material that holds it - no scene rebuild, no texture work, instant.
/// What the preview shows is derived from RIOT's value of the parameter (never from what is on screen), so dragging back and forth never
/// compounds, exactly as for the textures.</para>
///
/// <para>Everything that touches the project is the host's (<c>MainWindowViewModel.ChromaParameters.cs</c>): reading Riot's and the
/// project's skin bin, writing the changed values through the bin save path, the recipe in project.json. This half holds the list, the
/// pending state, the preview and the commands' parameter step.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    // ---- hooks (the host's and the window's) -------------------------------------------------------------

    /// <summary>The colour parameters of a skin bin, with Riot's value and the project's: (skin bin, the parameters the saved recipe owns, the transform
    /// it was saved with) -> list. The owned set is the card's own copy, taken on the UI thread: the call is on a worker, under the readers' lease, and
    /// must not read the project's lists. Null: this host lists none.</summary>
    public Func<string, IReadOnlySet<SkinColorParamKey>, ColorTransform, ChromaParameterSnapshot?>? ReadChromaParameters { get; set; }

    /// <summary>Writes the parameters: (skin bin, transform, parameters to recolour, parameters of the earlier recipe that are no longer part
    /// of it) -> what happened. Throws when it cannot write at all (the edit then stays pending).</summary>
    public Func<string, ColorTransform, IReadOnlyList<ChromaParameterRef>, IReadOnlyList<ChromaParameterRef>, Task<ChromaParamSaveResult>>? SaveChromaParameters { get; set; }

    /// <summary>Puts these parameters back to Riot's value (bin and record), returning how many it restored.</summary>
    public Func<string, IReadOnlyList<ChromaParameterRef>, Task<int>>? RevertChromaParameters { get; set; }

    /// <summary>The window's: write these colours into the D3D11 scene's materials (and draw a frame).</summary>
    public Action<IReadOnlyList<ChromaParamPush>>? PushChromaParamsDx11 { get; set; }

    /// <summary>The host's: the GL viewport's counterpart, for what the GL preview can draw of a parameter.</summary>
    public Action<IReadOnlyList<ChromaParamPush>>? PushChromaParamsGl { get; set; }

    // ---- the list and the state --------------------------------------------------------------------------

    /// <summary>The colour parameters the recolour can act on, and the ones it must leave alone (with the reason).</summary>
    public ObservableCollection<ChromaParameterRowViewModel> ChromaParameters { get; } = new();

    [ObservableProperty] private bool _hasChromaParameters;

    /// <summary>The sliders and the buttons are usable: the skin has a body texture or a colour parameter the recolour can act on.</summary>
    public bool HasChromaRecolourControls => HasChromaTextures || HasChromaParameters;
    partial void OnHasChromaParametersChanged(bool value) => OnPropertyChanged(nameof(HasChromaRecolourControls));
    partial void OnHasChromaTexturesChanged(bool value) => OnPropertyChanged(nameof(HasChromaRecolourControls));

    /// <summary>Why the parameter list is empty or short, when the host said so ("Riot's original skin bin cannot be read..."). Empty otherwise.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChromaParameterProblem))] private string _chromaParameterProblem = "";
    public bool HasChromaParameterProblem => ChromaParameterProblem.Length > 0;

    private readonly List<ChromaParameterRowViewModel> _chromaParamRows = new();
    private ColorTransform _chromaSavedParamTransform = ColorTransform.Identity;
    private IReadOnlyList<ChromaParameterRef> _chromaSavedParamRefs = Array.Empty<ChromaParameterRef>();
    private HashSet<SkinColorParamKey> _chromaSavedParamKeys = new();
    private HashSet<SkinColorParamKey> _chromaParamShown = new();     // parameters the renderers draw as a recolour right now (under _chromaGate)
    private HashSet<SkinColorParamKey> _chromaParamRestore = new();   // parameters to draw as they were once (a revert; under _chromaGate)
    private Task _chromaParamPrepare = Task.CompletedTask;

    private static SkinColorParamKey KeyOf(ChromaParameterRef r) => new(r.Material, r.Name, r.Occurrence);

    private IEnumerable<ChromaParameterRowViewModel> IncludedChromaParamRows => _chromaParamRows.Where(r => r.IsIncluded && r.CanInclude);

    /// <summary>
    /// The parameters of the saved recipe that this card has no switch for: the scan did not list them, or the host says they cannot be
    /// recoloured now. CARRIED, like the textures a scan omits (<see cref="CarriedChromaTargets"/>): part of the saved state, never a target
    /// and never stale, because an absent switch is not a switched-off one.
    /// </summary>
    private List<ChromaParameterRef> CarriedChromaParams() =>
        _chromaSavedParamRefs.Where(p => !_chromaParamRows.Any(r => r.Key == KeyOf(p) && r.CanInclude && !r.KeepsEdit)).ToList();

    /// <summary>Forget the previous skin's parameters and take the saved recipe's (called by <see cref="ResetChromaRecolour"/>).</summary>
    private void ResetChromaParameters(ChromaSavedRecipe? saved)
    {
        _chromaParamRows.Clear();
        ChromaParameters.Clear();
        HasChromaParameters = false;
        ChromaParameterProblem = "";
        _chromaParamPrepare = Task.CompletedTask;
        lock (_chromaGate) { _chromaParamShown = new HashSet<SkinColorParamKey>(); _chromaParamRestore = new HashSet<SkinColorParamKey>(); }
        _chromaSavedParamTransform = ColorTransform.Identity;
        _chromaSavedParamRefs = Array.Empty<ChromaParameterRef>();
        _chromaSavedParamKeys = new HashSet<SkinColorParamKey>();
        if (saved is null || saved.SavedParameters.Count == 0) return;
        _chromaSavedParamTransform = saved.ParameterTransform ?? saved.Transform;
        _chromaSavedParamRefs = saved.SavedParameters;
        _chromaSavedParamKeys = saved.SavedParameters.Select(KeyOf).ToHashSet();
    }

    /// <summary>The scan arrived: list the skin bin's colour parameters, as the host reads them (Riot's value beside the project's), on a worker.</summary>
    private Task PrepareChromaParametersAsync(int generation)
    {
        _chromaParamRows.Clear();
        ChromaParameters.Clear();
        HasChromaParameters = false;
        ChromaParameterProblem = "";
        lock (_chromaGate) { _chromaParamShown = new HashSet<SkinColorParamKey>(); _chromaParamRestore = new HashSet<SkinColorParamKey>(); }
        var read = ReadChromaParameters;
        if (read is null || _chromaSkinBin is not { } skinBin) return Task.CompletedTask;
        return LoadAsync();

        async Task LoadAsync()
        {
            using var readers = AcquireChromaReaders?.Invoke();   // on this thread, before the worker starts
            var token = _chromaCts.Token;
            ChromaParameterSnapshot? snapshot;
            var ownedNow = _chromaSavedParamKeys.ToHashSet();   // copies, taken here on the UI thread
            var recipeTransform = _chromaSavedParamTransform;
            try { snapshot = await Task.Run(() => read(skinBin, ownedNow, recipeTransform), token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { snapshot = new ChromaParameterSnapshot(Array.Empty<ChromaParameterInfo>(), "The colour parameters could not be read: " + ex.Message); }
            if (generation != Volatile.Read(ref _chromaGeneration) || token.IsCancellationRequested) return;
            if (snapshot is null) return;

            // a recipe is saved for this skin (textures or parameters): its parameters are the ones on; otherwise every colour the recolour may act on
            bool recipe = _chromaSavedHashes.Count > 0 || _chromaSavedParamKeys.Count > 0;
            // a row is made with its switch already set (no change event), so the textures' restoring guard is not needed - and it is shared with the
            // texture classification, which may be finishing on another thread when nothing marshals the continuations (a test)
            foreach (var info in snapshot.Parameters)
            {
                bool owned = _chromaSavedParamKeys.Contains(info.Key);
                // a recipe's parameters are on - except one edited since (kept as it is until the person switches it on); with no recipe, every colour the
                // recolour may act on - except one somebody edited, and one whose save is likely to be refused
                bool include = recipe ? owned && !info.OwnedEdited : info.Recolourable && !info.EditedOutside && info.DefaultOffReason is null;
                var row = new ChromaParameterRowViewModel(this, info, include);
                if (owned && info.OwnedEdited && info.Recolourable)
                    row.Note = "Edited in the Material tab since the recolour: kept as it is. Switch it on to recolour it again (that replaces the edit).";
                else if (info.Recolourable && info.EditedOutside && !owned)
                    row.Note = "The project's value was edited outside the recolour. Switch it on only to replace that edit.";
                else if (info.Recolourable && !owned && info.DefaultOffReason is not null) row.Note = info.DefaultOffReason;
                else if (owned) row.Note = "recoloured in the project";
                _chromaParamRows.Add(row);
            }
            foreach (var row in _chromaParamRows) ChromaParameters.Add(row);
            HasChromaParameters = _chromaParamRows.Count > 0;
            ChromaParameterProblem = snapshot.Problem;
            UpdateChromaDirty();
            RequestChromaPreview();   // sliders restored from the project, or moved before the list was in
        }
    }

    /// <summary>
    /// Why the preview cannot draw this parameter's recolour, or null when it can. The D3D11 scene reads a material's parameter by name, FIRST
    /// copy only (M790), and a skin block's one colour it feeds the shaders (<c>fresnelColor</c> -> <c>Fresnel_Color</c>); so a repeated name's
    /// later copy is never drawn, nor is the skin block's <c>reflectionFresnelColor</c> (no shader of the scene reads it). A parameter a material
    /// DRIVER sets (Lillia's gear tints) is drawn at the driver's value, which the recolour does not change. All of them are still written to the bin.
    /// </summary>
    internal static string? WhyNotPreviewed(ChromaParameterInfo info) =>
        info.DrivenBy is not null ? "the game overrides it with a material driver's value"
        : info.Key.Occurrence > 0 ? "the scene reads the first copy of a repeated name"
        : info.Key.Material == 0 && !info.Key.Name.Equals("fresnelColor", StringComparison.OrdinalIgnoreCase) ? "no preview draws it"
        : null;

    internal void OnChromaParameterIncludeChanged(ChromaParameterRowViewModel row)
    {
        if (_chromaRestoring) return;
        UpdateChromaDirty();
        RequestChromaPreview();
    }

    // ---- the live preview --------------------------------------------------------------------------------

    private readonly record struct ChromaParamItem(SkinColorParamKey Key, string MaterialName, Vector4 Original, Vector4 Current, bool IsColorType);

    private sealed record ChromaParamPreview(ChromaParamItem[] All, HashSet<SkinColorParamKey> Included, HashSet<SkinColorParamKey> Saved)
    {
        public static readonly ChromaParamPreview None = new(Array.Empty<ChromaParamItem>(), new HashSet<SkinColorParamKey>(), new HashSet<SkinColorParamKey>());
    }

    /// <summary>The parameters the preview draws: the ones whose recolour the scene can show (<see cref="WhyNotPreviewed"/>). The rest are written
    /// on save all the same, and their swatches follow the sliders.</summary>
    private ChromaParamPreview CurrentChromaParamPreview() =>
        _chromaParamRows.Count == 0
            ? ChromaParamPreview.None
            : new ChromaParamPreview(
                _chromaParamRows.Where(r => r.CanPreview).Select(r => new ChromaParamItem(r.Key, r.Info.MaterialName, r.Info.Riot, r.CurrentValue, r.Info.TypeName == "Color")).ToArray(),
                IncludedChromaParamRows.Where(r => r.CanPreview).Select(r => r.Key).ToHashSet(),
                _chromaSavedParamKeys);

    /// <summary>The swatch beside each parameter: what the sliders make of Riot's colour (Riot's own when the parameter is off).</summary>
    private void RefreshChromaParamSwatches(ColorTransform transform)
    {
        foreach (var row in _chromaParamRows)
        {
            var colour = row.IsIncluded && row.CanInclude && !transform.IsIdentity ? transform.Apply(row.Info.Riot) : row.Info.Riot;
            row.ResultBrush = ChromaParameterRowViewModel.Swatch(colour);
        }
    }

    /// <summary>The colours to hand the renderer: the included parameters recoloured from Riot's value, plus the ones that just left the set
    /// (or are being reverted) drawn as they were. Nothing under no change unless a recolour is on screen to take off.</summary>
    private List<ChromaParamPush> RenderChromaParams(ColorTransform transform, ChromaParamPreview preview, HashSet<SkinColorParamKey> shown,
        HashSet<SkinColorParamKey> restore, out HashSet<SkinColorParamKey> nowShown)
    {
        var batch = new List<ChromaParamPush>();
        var byKey = preview.All.ToDictionary(i => i.Key);
        foreach (var key in preview.Included)
        {
            if (!byKey.TryGetValue(key, out var item)) continue;
            if (transform.IsIdentity && !shown.Contains(key) && !restore.Contains(key) && !preview.Saved.Contains(key)) continue;
            batch.Add(Make(item, transform.IsIdentity ? item.Original : transform.Apply(item.Original), item.Current.W));
        }
        foreach (var key in shown.Concat(restore).Distinct())
        {
            if (preview.Included.Contains(key) || !byKey.TryGetValue(key, out var item)) continue;
            // what the project will hold: Riot's where the saved recipe owns it, else what it holds now
            batch.Add(Make(item, preview.Saved.Contains(key) ? item.Original : item.Current, item.Current.W));
        }
        nowShown = new HashSet<SkinColorParamKey>();
        if (!transform.IsIdentity) foreach (var key in preview.Included) nowShown.Add(key);
        return batch;

        // a Color-typed value is stored in bytes: what the bin will hold is the clamped value, and so is what the preview draws
        static ChromaParamPush Make(ChromaParamItem item, Vector4 colour, float alpha)
        {
            static float Unit(float f) => float.IsNaN(f) ? 0f : Math.Clamp(f, 0f, 1f);
            return item.IsColorType
                ? new(item.Key, item.MaterialName, new[] { Unit(colour.X), Unit(colour.Y), Unit(colour.Z), Unit(alpha) })
                : new(item.Key, item.MaterialName, new[] { colour.X, colour.Y, colour.Z, alpha });
        }
    }

    private void PushChromaParamBatch(List<ChromaParamPush> batch, int generation)
    {
        if (batch.Count == 0 || generation != Volatile.Read(ref _chromaGeneration)) return;
        if (UseDx11Preview && Dx11Scene is not null) PushChromaParamsDx11?.Invoke(batch);
        else PushChromaParamsGl?.Invoke(batch);
    }
}
