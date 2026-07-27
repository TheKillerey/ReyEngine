using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.ViewModels;

/// <summary>M211: a shader, with a name short enough to actually read.
///
/// <para>Every path in the cache starts <c>assets/shaders/generated/shaders/</c>, which is 33 characters of
/// pure noise repeated on all 462 entries and pushed the part that identifies the shader off the edge of the
/// list. The full path stays on the row for the tooltip and for the debug panel.</para></summary>
public sealed class ShaderRow
{
    public required string Full { get; init; }

    /// <summary>The path with the common generated-shader prefix removed, e.g.
    /// <c>staticmesh/defaultenv_flat</c>.</summary>
    public string Display
    {
        get
        {
            var s = Full;
            foreach (var prefix in new[] { "assets/shaders/generated/shaders/", "assets/shaders/generated/" })
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return s[prefix.Length..];
            return s;
        }
    }
}

/// <summary>A .skn or .mapgeo asset that can be loaded as a scene.</summary>
public sealed class SceneAssetRow
{
    public required string Path { get; init; }
    public required ulong Hash { get; init; }
    public bool IsMap => Path.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase);
    public string Display => System.IO.Path.GetFileName(Path);
    public string Kind => IsMap ? "map" : "character";
}

/// <summary>One submesh of the loaded scene and how its material resolved.</summary>
public sealed partial class SceneSubmeshRow : ObservableObject
{
    public required string Material { get; init; }
    public required int Triangles { get; init; }
    public required string Status { get; init; }
    public required bool Ok { get; init; }
    public string? Shader { get; init; }
    public bool UsedFallbackShader { get; init; }
    public PreviewMaterial? Pipeline { get; init; }

    [ObservableProperty] private bool _visible = true;
    partial void OnVisibleChanged(bool v) { if (Pipeline is not null) Pipeline.Visible = v; }

    public string Label => $"{Material}   ({Triangles:n0} tris)";
    public string Detail => Ok
        ? (UsedFallbackShader ? $"⚠ picked shader · {Shader}" : Shader ?? "")
        : Status;
}

/// <summary>A .bin asset that might hold materials.</summary>
public sealed class MaterialBinRow
{
    public required string Path { get; init; }
    public required ulong Hash { get; init; }
    public string Display => System.IO.Path.GetFileName(Path);
    public override string ToString() => Path;
}

/// <summary>One material inside the selected bin.</summary>
public sealed class MaterialRow
{
    public required MaterialBinding Binding { get; init; }
    /// <summary>Which bin it actually came from - often a long-named dependency, not the one picked.</summary>
    public string SourceBin { get; init; } = "";
    public string Name => Binding.Name;
    public string Shader => Binding.RenderShader ?? Binding.ShaderName ?? "(no shader)";
    public bool HasShader => !string.IsNullOrWhiteSpace(Binding.RenderShader);
    public string Label => $"{Name}";
    public string Detail => $"{Shader}   ·   {Binding.Slots.Count} tex, {Binding.Parameters.Count} param"
                            + (SourceBin.Length > 0 ? $"   ·   {System.IO.Path.GetFileName(SourceBin)}" : "");
}

/// <summary>One cooked permutation, as a row.</summary>
public sealed class PermutationRow
{
    public required ShaderPermutation Perm { get; init; }
    public required int Ordinal { get; init; }
    public string Label => $"#{Ordinal}  blob {Perm.BlobIndex}  {Perm.DefineSummary}";
}

/// <summary>A reflected texture slot the user can point at an image.</summary>
public sealed partial class TextureSlotRow : ObservableObject
{
    public required string Name { get; init; }
    public required uint Slot { get; init; }
    public required string Dimension { get; init; }
    [ObservableProperty] private string _source = "(white 1x1)";
    public string Label => $"t{Slot}  {Name}";
}

/// <summary>A reflected constant the user can override.</summary>
public sealed partial class ConstantRow : ObservableObject
{
    public required string Buffer { get; init; }
    public required string Name { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
    public required string TypeName { get; init; }
    public required bool IsUsed { get; init; }
    /// <summary>Space-separated floats. Empty means "leave the renderer's own value alone".</summary>
    [ObservableProperty] private string _value = "";
    public string Label => $"{Name}  ({TypeName} @+{Offset})";
    public string Detail => $"{Buffer}  {(IsUsed ? "read by this permutation" : "declared but unused here")}";
}

/// <summary>M210: the experimental DirectX 11 shader preview.
///
/// <para>Deliberately isolated from the Material Editor and from the OpenGL viewport. The point is to find
/// out whether League's own compiled shaders can be loaded, bound and rendered correctly — everything here
/// is diagnostics-first, and nothing it learns is applied to a map.</para>
/// </summary>
public sealed partial class ShaderPreviewViewModel : ObservableObject, IDisposable
{
    private readonly ShaderCacheReader? _cache;
    private readonly ShaderPreviewRenderer _renderer = new();
    private readonly PreviewSettings _settings = new();
    private readonly DispatcherTimer _timer;
    private readonly List<string> _allShaderNames = new();
    private readonly Func<ulong, byte[]?>? _readAsset;
    private readonly Func<uint, string?> _resolveBinName;
    private readonly ShaderPermutationIndex? _perms;
    private readonly List<MaterialBinRow> _allBins = new();
    private readonly List<SceneAssetRow> _allScenes = new();
    private SkinMeshProperties? _skinMesh;
    private DxbcShader? _vs, _ps;
    // double-buffered: an Image only repaints when its Source REFERENCE changes, so writing into the
    // same WriteableBitmap every frame shows nothing. Alternating two avoids the null-then-set flicker.
    private WriteableBitmap? _bitmapA, _bitmapB;
    private bool _useB;
    private int _bmpW, _bmpH;
    private readonly DateTime _start = DateTime.UtcNow;
    private int _framesThisSecond;
    private DateTime _fpsMark = DateTime.UtcNow;
    private DateTime _lastTick = DateTime.UtcNow;

    /// <summary>M215: the SAME camera class the map viewport flies, so the controls feel identical -
    /// WASD/QE to fly, drag to look, middle-drag to pan, wheel to zoom, F to reframe.</summary>
    public OrbitCamera Camera { get; } = new();

    /// <summary>M216: the size to render at, in real device pixels.
    ///
    /// <para>This was a hardcoded 640x480 stretched to fill a panel more than twice that wide, which is
    /// where "everything is blurry" came from - it was not the shaders and not a LOW_QUALITY_MODE
    /// permutation (measured: that define is never selected). Rendering at the surface's own pixel size
    /// makes it sharp. Capped, because the cost is per pixel and a maximised 4K window would quadruple the
    /// frame time for no visible gain at preview distances.</para></summary>
    private int _renderWidth = 960, _renderHeight = 640;

    public void SetSurfaceSize(double widthDip, double heightDip, double scaling)
    {
        int w = (int)Math.Round(widthDip * scaling);
        int h = (int)Math.Round(heightDip * scaling);
        _renderWidth = Math.Clamp(w, 160, 2560);
        _renderHeight = Math.Clamp(h, 120, 1600);
    }

    private readonly HashSet<Avalonia.Input.Key> _heldKeys = new();

    public void KeyDown(Avalonia.Input.Key k) => _heldKeys.Add(k);
    public void KeyUp(Avalonia.Input.Key k) => _heldKeys.Remove(k);
    public void ClearKeys() => _heldKeys.Clear();

    public void LookBy(float dx, float dy) => Camera.Look(-dx * 0.005f, dy * 0.005f);
    public void OrbitBy(float dx, float dy) => Camera.Orbit(dx * 0.01f, dy * 0.01f);
    public void PanBy(float dx, float dy) => Camera.Pan(-dx, dy);
    public void ZoomBy(float wheel) => Camera.Zoom(wheel > 0 ? 0.9f : 1.111f);
    public void AdjustFlySpeed(float wheel) => Camera.AdjustFlySpeed(wheel > 0 ? 1.15f : 0.87f);

    /// <summary>Frame whatever is loaded. The scene is recentred on its own bounds at load, so the target
    /// is always the origin and only the distance depends on how big the thing is.</summary>
    public void FocusCamera()
    {
        float radius = _renderer.Mesh?.Radius ?? 1f;
        Camera.FocusOn(System.Numerics.Vector3.Zero, radius);
        Camera.Near = MathF.Max(0.01f, radius * 0.002f);
        Camera.Far = MathF.Max(100f, radius * 60f);
        Camera.FlySpeed = MathF.Max(20f, radius * 0.9f);
    }

    private void ApplyCameraInput(float dt)
    {
        float f = 0, r = 0, u = 0;
        if (_heldKeys.Contains(Avalonia.Input.Key.W)) f += 1;
        if (_heldKeys.Contains(Avalonia.Input.Key.S)) f -= 1;
        if (_heldKeys.Contains(Avalonia.Input.Key.D)) r += 1;
        if (_heldKeys.Contains(Avalonia.Input.Key.A)) r -= 1;
        if (_heldKeys.Contains(Avalonia.Input.Key.E)) u += 1;
        if (_heldKeys.Contains(Avalonia.Input.Key.Q)) u -= 1;
        if (_heldKeys.Contains(Avalonia.Input.Key.F)) FocusCamera();
        if (f != 0 || r != 0 || u != 0) Camera.MoveLocal(f, r, u, dt);
    }

    public ObservableCollection<ShaderRow> ShaderNames { get; } = new();
    public ObservableCollection<PermutationRow> VertexPermutations { get; } = new();
    public ObservableCollection<PermutationRow> PixelPermutations { get; } = new();
    public ObservableCollection<TextureSlotRow> TextureSlots { get; } = new();
    public ObservableCollection<ConstantRow> Constants { get; } = new();
    public ObservableCollection<string> MeshNames { get; } = new(PreviewGeometry.BuiltInNames);
    public ObservableCollection<MaterialBinRow> MaterialBins { get; } = new();
    public ObservableCollection<MaterialRow> Materials { get; } = new();
    public ObservableCollection<SceneAssetRow> SceneAssets { get; } = new();
    public ObservableCollection<SceneSubmeshRow> SceneSubmeshes { get; } = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private ShaderRow? _selectedShader;
    [ObservableProperty] private PermutationRow? _selectedVertexPerm;
    [ObservableProperty] private PermutationRow? _selectedPixelPerm;
    [ObservableProperty] private string _selectedMesh = "Sphere";
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _metadata = "";
    [ObservableProperty] private string _bindings = "";
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string _perf = "";
    [ObservableProperty] private string _comparisonSource = "";
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _binFilter = "";
    [ObservableProperty] private MaterialBinRow? _selectedBin;
    [ObservableProperty] private MaterialRow? _selectedMaterial;
    [ObservableProperty] private string _materialReport = "";
    [ObservableProperty] private string _sceneFilter = "";
    [ObservableProperty] private SceneAssetRow? _selectedSceneAsset;
    [ObservableProperty] private string _sceneReport = "";
    public bool CanBrowseMaterials => _readAsset is not null && _allBins.Count > 0;

    // render settings, mirrored onto PreviewSettings
    [ObservableProperty] private double _yaw = 0.6, _pitch = 0.4, _distance = 3.2;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private bool _cullBackFaces = true;
    [ObservableProperty] private bool _depthTest = true;
    [ObservableProperty] private bool _alphaBlend;
    [ObservableProperty] private bool _transposeMatrices = true;
    [ObservableProperty] private bool _useComparisonShader;
    [ObservableProperty] private bool _animateTime = true;

    /// <summary>M223: mirror world X, matching the map viewport. On by default.</summary>
    [ObservableProperty] private bool _mirrorX = true;

    [ObservableProperty] private double _sunAzimuth = 2.2, _sunElevation = 0.9;

    public bool CacheAvailable => _cache is not null;

    public ShaderPreviewViewModel(string? gameDataFinalDir, IHashResolver? resolver,
        Func<ulong, byte[]?>? readAsset = null,
        IEnumerable<(string Path, ulong Hash)>? binAssets = null,
        Func<uint, string?>? resolveBinName = null,
        IEnumerable<(string Path, ulong Hash)>? sceneAssets = null)
    {
        _readAsset = readAsset;
        _resolveBinName = resolveBinName ?? (_ => null);
        if (binAssets is not null)
            foreach (var (path, hash) in binAssets)
                _allBins.Add(new MaterialBinRow { Path = path, Hash = hash });
        if (sceneAssets is not null)
            foreach (var (path, hash) in sceneAssets)
                _allScenes.Add(new SceneAssetRow { Path = path, Hash = hash });
        ApplyBinFilter();
        ApplySceneFilter();

        if (string.IsNullOrWhiteSpace(gameDataFinalDir) || !Directory.Exists(gameDataFinalDir))
        {
            Status = "No game directory is configured, so there is no shader cache to read. "
                     + "Set the game folder in project settings (it should contain DATA/FINAL).";
            HasError = true;
        }
        else
        {
            try { _perms = new ShaderPermutationIndex(gameDataFinalDir); }
            catch { _perms = null; }   // only affects define-set completeness, not loading

            _cache = ShaderCacheReader.Open(gameDataFinalDir, resolver, out var err);
            if (_cache is null) { Status = err ?? "the shader cache could not be opened"; HasError = true; }
            else
            {
                _allShaderNames.AddRange(_cache.ShaderNames());
                ApplyFilter();
                Status = $"{_allShaderNames.Count:n0} shaders in ShaderCache.dx11.wad.client. Pick one and press Load.";
            }
        }

        if (!_renderer.Initialize(out var derr))
        {
            Status = derr ?? "D3D11 device creation failed";
            HasError = true;
        }
        AppendLog();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        ShaderNames.Clear();
        foreach (var n in _allShaderNames)
            if (Filter.Length == 0 || n.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                ShaderNames.Add(new ShaderRow { Full = n });
    }

    partial void OnSelectedShaderChanged(ShaderRow? value)
    {
        VertexPermutations.Clear();
        PixelPermutations.Clear();
        if (value is null || _cache is null) return;

        Fill(VertexPermutations, ShaderCacheReader.TocPathFor(value.Full, DxbcStage.Vertex));
        Fill(PixelPermutations, ShaderCacheReader.TocPathFor(value.Full, DxbcStage.Pixel));
        SelectedVertexPerm = VertexPermutations.FirstOrDefault();
        SelectedPixelPerm = PixelPermutations.FirstOrDefault();

        Status = $"{VertexPermutations.Count:n0} vertex / {PixelPermutations.Count:n0} pixel permutations cooked.";
        HasError = false;
    }

    private void Fill(ObservableCollection<PermutationRow> into, string tocPath)
    {
        var toc = _cache!.ReadToc(tocPath);
        if (toc is null) return;
        var described = ShaderCacheReader.DescribePermutations(toc, out bool truncated, 200_000);
        int i = 0;
        foreach (var p in described) into.Add(new PermutationRow { Perm = p, Ordinal = i++ });
        if (truncated)
            Status = "Permutation define sets could not be recovered (the define pool is too large to enumerate); "
                     + "blob indices are still exact.";
    }

    partial void OnBinFilterChanged(string value) => ApplyBinFilter();

    private void ApplyBinFilter()
    {
        MaterialBins.Clear();
        foreach (var b in _allBins)
            if (BinFilter.Length == 0 || b.Path.Contains(BinFilter, StringComparison.OrdinalIgnoreCase))
                MaterialBins.Add(b);
    }

    partial void OnSelectedBinChanged(MaterialBinRow? value)
    {
        Materials.Clear();
        SelectedMaterial = null;
        if (value is null || _readAsset is null) return;

        try
        {
            // M217: follow the DEPENDENCY CLOSURE, not just the bin that was picked.
            //
            // A champion's skin bin barely holds any materials - it lists long-named dependency bins that
            // do, and the same BFS is how the app already finds champion VFX (M84-M86). Reading only the
            // selected bin found nothing for Kayn but a "(skin default texture)" placeholder with no
            // renderShader, so every submesh fell through to it and nothing drew.
            _skinMesh = null;
            var seen = new HashSet<ulong> { value.Hash };
            var queue = new Queue<(string Path, ulong Hash)>();
            queue.Enqueue((value.Path, value.Hash));

            var byName = new Dictionary<string, MaterialRow>(StringComparer.OrdinalIgnoreCase);
            int binsRead = 0, guard = 0;

            while (queue.Count > 0 && guard++ < 64)
            {
                var (path, hash) = queue.Dequeue();
                byte[]? bytes;
                try { bytes = _readAsset(hash); } catch { continue; }
                if (bytes is null || bytes.Length == 0) continue;
                binsRead++;

                try
                {
                    var doc = MaterialDocument.Parse(bytes, _resolveBinName);
                    _skinMesh ??= doc.SkinMesh;
                    foreach (var m in doc.Materials)
                    {
                        var row = new MaterialRow { Binding = m, SourceBin = path };
                        // a definition that names a shader beats a placeholder of the same name
                        if (!byName.TryGetValue(m.Name, out var existing) || (row.HasShader && !existing.HasShader))
                            byName[m.Name] = row;
                    }
                }
                catch { /* a bin that will not parse as materials is normal in the closure */ }

                foreach (var dep in ReyEngine.Formats.Vfx.VfxSystemResolver.ExtractDependencies(bytes))
                {
                    ulong h = HashAlgorithms.WadPath(dep);
                    if (seen.Add(h)) queue.Enqueue((dep, h));
                }
            }

            foreach (var row in byName.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                Materials.Add(row);

            int withShader = Materials.Count(r => r.HasShader);
            if (_skinMesh is not null)
                MaterialReport = DescribeSkinMesh(_skinMesh);
            Status = Materials.Count == 0
                ? $"{value.Display} and its {binsRead - 1} dependency bin(s) hold no materials."
                : $"{value.Display}: {Materials.Count} material(s) ({withShader} with a shader) "
                  + $"across {binsRead} bin(s) including dependencies.";
            HasError = Materials.Count == 0;
        }
        catch (Exception ex) { Fail($"{value.Display}: {ex.Message}"); }
    }

    /// <summary>M213: bring a real material up end to end - its shader, the permutation the engine would
    /// actually pick for its define set, its textures decoded from the game's own .tex files, and its
    /// parameters written into the constants the shader declares.</summary>
    [RelayCommand]
    private void ApplyMaterial()
    {
        if (SelectedMaterial is null || _cache is null) return;
        var b = SelectedMaterial.Binding;
        var sb = new StringBuilder();

        string? shader = b.RenderShader;
        if (string.IsNullOrWhiteSpace(shader))
        {
            Fail($"'{b.Name}' names no renderShader, so there is no shader to load.");
            MaterialReport = $"{b.Name}\\n\\nThis material has no renderShader. Its class was "
                             + $"'{b.ShaderName ?? "unknown"}'.";
            return;
        }

        string full = "assets/shaders/generated/" + shader.Trim('/');
        var match = _allShaderNames.FirstOrDefault(n => n.Equals(full, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Fail($"'{shader}' is not in the shader cache.");
            MaterialReport = $"{b.Name}\\n\\nrenderShader: {shader}\\nexpected at: {full}\\n\\nNot present in "
                             + "ShaderCache.dx11.wad.client.";
            return;
        }

        sb.AppendLine($"MATERIAL   {b.Name}");
        sb.AppendLine($"shader     {shader}");
        sb.AppendLine($"class      {b.ShaderName}");
        sb.AppendLine();

        // the define set the engine would resolve
        _perms?.TryGetShaderDefs(shader, out var features, out var switchDefaults);
        IReadOnlyDictionary<string, string>? feat = null;
        IReadOnlyDictionary<string, bool>? swDef = null;
        if (_perms is not null && _perms.TryGetShaderDefs(shader, out var f2, out var s2)) { feat = f2; swDef = s2; }

        sb.AppendLine("DEFINE SET");
        sb.AppendLine($"   material switches  {(b.Switches.Count == 0 ? "(none)" : string.Join(", ", b.Switches.Select(kv => $"{kv.Key}={(kv.Value ? 1 : 0)}")))}");
        sb.AppendLine($"   material macros    {(b.Macros.Count == 0 ? "(none)" : string.Join(", ", b.Macros.Select(kv => $"{kv.Key}={kv.Value}")))}");
        sb.AppendLine($"   shader defaults    {(swDef is null || swDef.Count == 0 ? "(none / shaders.bin unavailable)" : string.Join(", ", swDef.Select(kv => $"{kv.Key}={(kv.Value ? 1 : 0)}")))}");
        sb.AppendLine();

        // resolve BOTH stages against that set
        string vsPath = ShaderCacheReader.TocPathFor(match, DxbcStage.Vertex);
        string psPath = ShaderCacheReader.TocPathFor(match, DxbcStage.Pixel);
        var vsToc = _cache.ReadToc(vsPath);
        var psToc = _cache.ReadToc(psPath);
        if (vsToc is null || psToc is null) { Fail($"'{shader}' does not ship both stages."); MaterialReport = sb.ToString(); return; }

        var vsPerm = ShaderCacheReader.ResolvePermutation(vsToc, b.Macros, b.Switches, feat, swDef, out var vsWhy);
        var psPerm = ShaderCacheReader.ResolvePermutation(psToc, b.Macros, b.Switches, feat, swDef, out var psWhy);

        sb.AppendLine("PERMUTATION RESOLUTION");
        sb.AppendLine($"   vertex  {(vsPerm is null ? "NOT FOUND" : $"blob #{vsPerm.BlobIndex}, key 0x{vsPerm.Key:x16}")}");
        sb.AppendLine($"           {vsWhy}");
        sb.AppendLine($"   pixel   {(psPerm is null ? "NOT FOUND" : $"blob #{psPerm.BlobIndex}, key 0x{psPerm.Key:x16}")}");
        sb.AppendLine($"           {psWhy}");
        sb.AppendLine();

        if (vsPerm is null || psPerm is null)
        {
            MaterialReport = sb.ToString();
            Fail("No cooked permutation matches this material's define set - the live client would fail the same way.");
            return;
        }

        // select them in the UI, then load through the normal path
        SelectedShader = ShaderNames.FirstOrDefault(r => r.Full.Equals(match, StringComparison.OrdinalIgnoreCase))
                         ?? new ShaderRow { Full = match };
        SelectedVertexPerm = VertexPermutations.FirstOrDefault(r => r.Perm.Key == vsPerm.Key)
                             ?? new PermutationRow { Perm = vsPerm, Ordinal = -1 };
        SelectedPixelPerm = PixelPermutations.FirstOrDefault(r => r.Perm.Key == psPerm.Key)
                            ?? new PermutationRow { Perm = psPerm, Ordinal = -1 };
        Load();
        if (!IsLoaded) { MaterialReport = sb.ToString() + "\\nShader creation failed - see the Shader tab."; return; }

        // ---- textures, from the game's own .tex files
        sb.AppendLine("TEXTURES");
        int bound = 0, missing = 0;
        var declared = TextureSlots.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in b.Slots)
        {
            string target = ResolveTextureTarget(slot.SamplerName, _ps!, _vs!) ?? (slot.SamplerName + "__TX");
            bool wanted = declared.Contains(target);
            if (string.IsNullOrWhiteSpace(slot.Path))
            { sb.AppendLine($"   -  {slot.SamplerName,-28} (no path authored)"); continue; }

            if (!wanted)
            {
                sb.AppendLine($"   ·  {slot.SamplerName,-28} -> {target} not declared by this permutation");
                continue;
            }
            if (_readAsset is null) { sb.AppendLine($"   ?  {slot.SamplerName,-28} (no asset mounts)"); missing++; continue; }

            try
            {
                var data = _readAsset(HashAlgorithms.WadPath(slot.Path.ToLowerInvariant()));
                if (data is null || data.Length == 0)
                { sb.AppendLine($"   !  {slot.SamplerName,-28} {slot.Path}  NOT FOUND"); missing++; continue; }

                var img = TextureDecoder.Decode(data);
                _renderer.SetTexture(target, img.Rgba, img.Width, img.Height);
                var row = TextureSlots.FirstOrDefault(t => t.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (row is not null) row.Source = $"{System.IO.Path.GetFileName(slot.Path)} ({img.Width}x{img.Height})";
                sb.AppendLine($"   +  {slot.SamplerName,-28} {img.Width}x{img.Height}  {slot.Path}");
                bound++;
            }
            catch (Exception ex)
            { sb.AppendLine($"   !  {slot.SamplerName,-28} {slot.Path}  {ex.Message}"); missing++; }
        }
        sb.AppendLine();

        // ---- parameters into the reflected constants
        sb.AppendLine("PARAMETERS");
        int applied = 0;
        foreach (var prm in b.Parameters)
        {
            var target = Constants.FirstOrDefault(c => c.Name.Equals(prm.Name, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            { sb.AppendLine($"   ·  {prm.Name,-28} not a constant this permutation declares"); continue; }
            if (!prm.TryGetVector4(out var v))
            { sb.AppendLine($"   ·  {prm.Name,-28} {prm.TypeName} is not numeric"); continue; }

            target.Value = string.Join(" ", new[] { v.X, v.Y, v.Z, v.W }
                .Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sb.AppendLine($"   +  {prm.Name,-28} {target.Value}");
            applied++;
        }

        MaterialReport = sb.ToString();
        RebuildBindings();
        Status = $"'{b.Name}' loaded: {bound} texture(s) bound, {applied} parameter(s) applied"
                 + (missing > 0 ? $", {missing} texture(s) missing." : ".");
        HasError = missing > 0;
    }

    partial void OnSceneFilterChanged(string value) => ApplySceneFilter();

    private void ApplySceneFilter()
    {
        SceneAssets.Clear();
        foreach (var a in _allScenes)
            if (SceneFilter.Length == 0 || a.Path.Contains(SceneFilter, StringComparison.OrdinalIgnoreCase))
                SceneAssets.Add(a);
    }

    /// <summary>M214: load a whole character or map and draw every submesh with its OWN material.
    ///
    /// <para>This is the step from "does this shader run" to "does this content render". One vertex and
    /// index buffer, then one pipeline per submesh: its material's shader, the permutation its define set
    /// resolves to, its textures, and its parameters. A submesh whose material cannot be resolved is
    /// reported and skipped rather than drawn with someone else's shader.</para></summary>
    [RelayCommand]
    private void LoadScene()
    {
        if (SelectedSceneAsset is null || _cache is null || _readAsset is null) return;
        if (Materials.Count == 0)
        {
            Fail("Pick a materials .bin first - the Material tab's bin supplies the material definitions.");
            return;
        }

        var asset = SelectedSceneAsset;
        var sb = new StringBuilder();
        SceneSubmeshes.Clear();

        PreviewMesh mesh;
        List<(string Material, int Start, int Count)> parts;
        try
        {
            var bytes = _readAsset(asset.Hash);
            if (bytes is null || bytes.Length == 0) { Fail($"{asset.Display}: not readable"); return; }

            if (asset.IsMap)
            {
                var map = MapGeoDecoder.Decode(bytes);
                mesh = PreviewGeometry.FromLeagueArrays(asset.Display, map.Positions.Length / 3,
                    map.Positions, map.Normals, map.Uvs, map.Colors, map.LightmapUvs, map.Indices);
                parts = map.Groups.Select(g => (g.Material, g.StartIndex, g.IndexCount)).ToList();
            }
            else
            {
                var m = SkinnedMeshDecoder.Decode(bytes);
                mesh = PreviewGeometry.FromLeagueArrays(asset.Display, m.VertexCount,
                    m.Positions, m.Normals, m.Uvs, m.Colors, m.LightmapUvs, m.Indices,
                    m.BlendIndices, m.BlendWeights);
                parts = m.SubMeshes.Select(x => (x.Material, x.StartIndex, x.IndexCount)).ToList();
            }
        }
        catch (Exception ex) { Fail($"{asset.Display}: {ex.Message}"); return; }

        sb.AppendLine($"SCENE   {asset.Display}   ({asset.Kind})");
        sb.AppendLine($"        {mesh.Vertices.Length:n0} vertices, {mesh.TriangleCount:n0} triangles, "
                      + $"{parts.Count} submesh(es)");
        sb.AppendLine($"        recentred on its own bounds, radius {mesh.Radius:n0}");
        sb.AppendLine();

        _renderer.ClearMaterials();
        _renderer.SetMesh(mesh);

        int ok = 0, failed = 0, texBound = 0, texMissing = 0;
        DxbcShader? firstVs = null;

        // group submeshes by material: several submeshes commonly share one
        foreach (var group in parts.GroupBy(x => x.Material, StringComparer.OrdinalIgnoreCase))
        {
            string matName = group.Key;
            int tris = group.Sum(x => x.Count) / 3;

            var binding = MaterialFor(matName);

            if (binding is null)
            {
                sb.AppendLine($"   !  {matName,-34} no material of that name in the selected bin");
                SceneSubmeshes.Add(new SceneSubmeshRow
                { Material = matName, Triangles = tris, Ok = false, Status = "not in the selected bin" });
                failed++;
                continue;
            }

            if (!binding.Name.Equals(matName, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"      submesh '{matName}' -> material '{binding.Name}'");

            var built = BuildSceneMaterial(binding, group.ToList(), sb, ref texBound, ref texMissing,
                out string why, out bool usedFallback);
            if (built is null)
            {
                SceneSubmeshes.Add(new SceneSubmeshRow
                { Material = matName, Triangles = tris, Ok = false, Status = why });
                failed++;
                continue;
            }

            firstVs ??= built.VsRefl;

            // M222: the skin hides some submeshes by default - Kalista's Altar_Spear draws through her
            // otherwise. Hidden rather than skipped, so it can be switched back on from the list.
            bool hidden = _skinMesh?.InitialSubmeshesToHide
                .Any(h => h.Equals(matName, StringComparison.OrdinalIgnoreCase)) == true;
            if (hidden)
            {
                built.Visible = false;
                sb.AppendLine($"      '{matName}' hidden by initialSubmeshToHide");
            }

            SceneSubmeshes.Add(new SceneSubmeshRow
            {
                Material = matName, Triangles = tris, Ok = true, Status = "ok",
                Shader = binding.Name.Equals(matName, StringComparison.OrdinalIgnoreCase)
                    ? (binding.RenderShader ?? SelectedShader?.Display)
                    : $"{binding.Name} · {binding.RenderShader ?? SelectedShader?.Display}",
                UsedFallbackShader = usedFallback,
                Pipeline = built,
                Visible = !hidden,
            });
            ok++;
        }

        if (_skinMesh is not null)
        {
            sb.AppendLine();
            sb.Append(DescribeSkinMesh(_skinMesh));
        }
        sb.AppendLine();
        sb.AppendLine($"{ok} material(s) live, {failed} unresolved, {texBound} texture(s) bound"
                      + (texMissing > 0 ? $", {texMissing} missing" : ""));

        SceneReport = sb.ToString();
        BuildSlots();
        RebuildBindings();

        if (ok == 0)
        {
            IsLoaded = false;
            Fail(SelectedShader is null
                ? $"{asset.Display}: nothing resolved. These materials author no shader of their own - "
                  + "pick one in the Shader list on the left to stand in, then load the scene again."
                : $"{asset.Display}: not one material resolved, so there is nothing to draw.");
            return;
        }

        string? cerr = "no vertex shader";
        if (firstVs is not null && _renderer.BuildComparisonShader(firstVs, out cerr))
            ComparisonSource = _renderer.ComparisonShaderSource ?? "";
        else
            ComparisonSource = "// comparison unavailable for this scene: " + (cerr ?? "unknown");

        FocusCamera();
        IsLoaded = true;
        HasError = failed > 0;
        int fellBack = SceneSubmeshes.Count(r => r.UsedFallbackShader);
        Status = $"{asset.Display}: {ok} material(s) drawing, {failed} unresolved"
                 + (fellBack > 0 ? $", {fellBack} using the picked shader (they author none)." : ".");
        AppendLog();
    }

    /// <summary>M214: which material draws this submesh?
    ///
    /// <para>For map geometry the group carries the material's own NAME, so a name match is the link. For a
    /// champion it is the other way round: submeshes are called <c>Body</c>, <c>Wings</c>, <c>Sword</c>, and
    /// the material declares which submeshes it covers. Matching on the name alone finds nothing at all on a
    /// champion - the first attempt at this resolved 0 of 5 on Aatrox.</para>
    ///
    /// <para>Order matters: the submesh assignment is checked first because it is the explicit statement, and
    /// the name is the fallback. A material flagged as the default covers anything left over.</para></summary>
    private MaterialBinding? MaterialFor(string submeshOrName)
    {
        // Each rule is tried for a material that actually names a shader before settling for one that
        // does not - otherwise Kayn's "(skin default texture)" placeholder swallowed every submesh.
        foreach (bool needShader in new[] { true, false })
        {
            // 1. a material that explicitly lists this submesh
            foreach (var r in Materials)
            {
                if (needShader && !r.HasShader) continue;
                foreach (var sub in r.Binding.Submeshes)
                    if (sub.Equals(submeshOrName, StringComparison.OrdinalIgnoreCase)) return r.Binding;
            }

            // 2. a material named after it (this is how mapgeo groups reference materials)
            foreach (var r in Materials)
            {
                if (needShader && !r.HasShader) continue;
                if (r.Binding.Name.Equals(submeshOrName, StringComparison.OrdinalIgnoreCase)) return r.Binding;
            }

            // 3. whatever the bin marks as the default
            foreach (var r in Materials)
            {
                if (needShader && !r.HasShader) continue;
                if (r.Binding.IsDefault) return r.Binding;
            }
        }

        return null;
    }

    /// <summary>M218: which declared texture should this material's sampler feed?
    ///
    /// <para>Normally it is <c>samplerName + "__TX"</c>, measured over 16,279 pairs. Champions break that,
    /// and it is not a corner case: a skin's default diffuse and every inline per-submesh override are
    /// parsed as a sampler literally named <c>texture</c>, because that is the field name in
    /// <c>skinMeshProperties</c>. There is no <c>texture__TX</c> in any shader, so Kayn bound zero textures
    /// and every slot sampled the white stand-in.</para>
    ///
    /// <para>For that generic name only, the diffuse slot is picked by inspecting what the shader declares:
    /// the first non-shared texture, preferring one whose name contains "Diffuse". Names ending
    /// <c>_SharedTexture</c> are engine-supplied (FOW, the colour-remap ramp) and are never a material's
    /// diffuse. This is a preview affordance and it is reported on the row, not a claim about how the engine
    /// binds.</para></summary>
    private static string? ResolveTextureTarget(string samplerName, DxbcShader ps, DxbcShader vs)
    {
        string exact = samplerName + "__TX";
        foreach (var refl in new[] { ps, vs })
            foreach (var t in refl.Textures)
                if (t.Name.Equals(exact, StringComparison.OrdinalIgnoreCase)) return t.Name;

        // the champion convention: an unqualified "texture" means the diffuse
        if (!samplerName.Equals("texture", StringComparison.OrdinalIgnoreCase)) return null;

        var candidates = ps.Textures.Concat(vs.Textures)
            .Where(t => !t.Name.Contains("_Shared", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return null;

        foreach (var t in candidates)
            if (t.Name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)) return t.Name;
        foreach (var t in candidates)
            if (t.Name.Contains("Main", StringComparison.OrdinalIgnoreCase)) return t.Name;
        return candidates[0].Name;
    }

    /// <summary>M222: what the skin's own material layer holds, and what of it the preview can use.</summary>
    private static string DescribeSkinMesh(SkinMeshProperties p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SKIN MESH PROPERTIES  (the skin's own material layer, beside its StaticMaterialDefs)");
        sb.AppendLine();
        void Row(string name, string? value, string note)
            => sb.AppendLine($"   {name,-26} {value ?? "(not authored)",-34} {note}");

        Row("selfIllumination", p.SelfIllumination?.ToString("0.###"),
            "-> SELF_ILLUMINATION, declared by all 90 skinnedmesh shaders sampled");
        Row("fresnel", p.Fresnel?.ToString("0.###"), "-> Fresnel_Size where declared (name match, UNVERIFIED)");
        Row("fresnelColor", p.FresnelColor?.ToString(), "-> Fresnel_Color where declared (name match, UNVERIFIED)");
        Row("initialSubmeshToHide", p.InitialSubmeshesToHide.Count > 0
            ? string.Join(", ", p.InitialSubmeshesToHide) : null, "-> hidden on load");
        sb.AppendLine();
        Row("skinScale", p.SkinScale?.ToString("0.###"),
            "NOT applied: the preview recentres and auto-frames, so a uniform scale is invisible");
        Row("glossTexture", p.GlossTexture is null ? null : System.IO.Path.GetFileName(p.GlossTexture),
            "NOT applied: no matching texture slot found in the sampled shaders");
        Row("reflectionMap", p.ReflectionMap is null ? null : System.IO.Path.GetFileName(p.ReflectionMap),
            "NOT applied: a cubemap; the renderer creates 2D textures only");
        Row("reflectionOpacityDirect", p.ReflectionOpacityDirect?.ToString("0.###"), "NOT applied: no matching constant");
        Row("reflectionOpacityGlancing", p.ReflectionOpacityGlancing?.ToString("0.###"), "NOT applied: no matching constant");
        Row("brushAlphaOverride", p.BrushAlphaOverride?.ToString("0.###"), "NOT applied: brush/grass fade is not simulated");
        return sb.ToString();
    }

    /// <summary>Write the skin-level values a shader can actually read into this material's parameters.
    /// A material's OWN authored parameter still wins - these only fill what it leaves unset.</summary>
    private void ApplySkinMeshParams(PreviewMaterial mat)
    {
        if (_skinMesh is null) return;

        void Put(string name, params float[] v)
        { if (!mat.Params.ContainsKey(name)) mat.Params[name] = v; }

        if (_skinMesh.SelfIllumination is { } si) Put("SELF_ILLUMINATION", si, si, si, si);
        if (_skinMesh.Fresnel is { } fr) Put("Fresnel_Size", fr, fr, fr, fr);
        if (_skinMesh.FresnelColor is { } fc) Put("Fresnel_Color", fc.X, fc.Y, fc.Z, fc.W);
    }

    /// <summary>Resolve one material to a live pipeline covering its submesh slices.</summary>
    private PreviewMaterial? BuildSceneMaterial(MaterialBinding b,
        List<(string Material, int Start, int Count)> slices, StringBuilder sb,
        ref int texBound, ref int texMissing, out string why, out bool usedFallback)
    {
        why = "";
        string? shader = b.RenderShader;

        // M217: a champion's skin bin frequently authors NO shader at all - Kayn's base skin resolves four
        // pseudo-bindings, "(skin default texture)" and three "(inline override: ...)", which carry the
        // TEXTURES but never a renderShader. The engine supplies a default there and what that default is
        // has not been established, so rather than invent one the preview falls back to whatever shader is
        // selected in the list on the left, and says so on the row. That keeps the window useful for
        // exactly the thing it is for - putting a real material's textures through a real shader - without
        // asserting something about the game that has not been measured. 42% of the material corpus is in
        // the same position, so this is the common case, not an edge one.
        usedFallback = false;
        if (string.IsNullOrWhiteSpace(shader))
        {
            shader = SelectedShader?.Full is { Length: > 0 } picked
                ? ShaderCacheReader.StripStage(picked).Replace("assets/shaders/generated/", "", StringComparison.OrdinalIgnoreCase)
                : null;
            if (string.IsNullOrWhiteSpace(shader))
            {
                why = "no renderShader, and no shader picked to stand in";
                sb.AppendLine($"   !  {b.Name,-34} no renderShader - pick one in the Shader list to stand in");
                return null;
            }
            usedFallback = true;
        }

        string full = "assets/shaders/generated/" + shader.Trim('/');
        string vsPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex);
        string psPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel);
        var vsToc = _cache!.ReadToc(vsPath);
        var psToc = _cache.ReadToc(psPath);
        if (vsToc is null || psToc is null)
        { why = "shader not in the cache"; sb.AppendLine($"   !  {b.Name,-34} '{shader}' missing a stage"); return null; }

        IReadOnlyDictionary<string, string>? feat = null;
        IReadOnlyDictionary<string, bool>? swDef = null;
        if (_perms is not null && _perms.TryGetShaderDefs(shader, out var f, out var sd)) { feat = f; swDef = sd; }

        var vsPerm = ShaderCacheReader.ResolvePermutation(vsToc, b.Macros, b.Switches, feat, swDef, out _);
        var psPerm = ShaderCacheReader.ResolvePermutation(psToc, b.Macros, b.Switches, feat, swDef, out var pw);
        if (vsPerm is null || psPerm is null)
        { why = "no cooked permutation"; sb.AppendLine($"   !  {b.Name,-34} {pw}"); return null; }

        // M220: do NOT substitute a different permutation here.
        //
        // M219 tried swapping to a cooked sibling that omits the engine colour-remap stage, to get colour
        // back. It is not safe and was removed after being wrong twice: for skinnedmesh/diffuse_alpha the
        // only remap-free permutations are GENERATE_SHADOW_MAP depth passes, and for Xayah's wing
        // (Outline_Iridescent_Add_Scroll) the swap replaced blob 172 - the material's real permutation,
        // binding four textures - with blob 1, an outline pass that drew a white streak across the model.
        // Both passed a "does it still declare a diffuse" guard, which is precisely why that guard was not
        // enough. The permutation the define set resolves to is the one to draw.
        var vs = _cache.LoadShader(vsPath, vsPerm.BlobIndex, out _);
        var ps = _cache.LoadShader(psPath, psPerm.BlobIndex, out _);
        if (vs is null || ps is null) { why = "bytecode would not load"; sb.AppendLine($"   !  {b.Name,-34} bytecode load failed"); return null; }

        // One pipeline per material; the FIRST slice is its draw range. Extra slices get their own
        // pipeline object sharing the same shaders, because a draw call covers one contiguous range.
        PreviewMaterial? first = null;
        foreach (var slice in slices)
        {
            var mat = _renderer.BuildMaterial(b.Name, vs, ps, slice.Start, slice.Count, out var rep);
            if (mat is null) { why = rep.Error ?? "pipeline creation failed"; sb.AppendLine($"   !  {b.Name,-34} {why}"); return null; }

            foreach (var slot in b.Slots)
            {
                if (string.IsNullOrWhiteSpace(slot.Path)) continue;
                string? target = ResolveTextureTarget(slot.SamplerName, ps, vs);
                if (target is null) continue;
                try
                {
                    var data = _readAsset!(HashAlgorithms.WadPath(slot.Path.ToLowerInvariant()));
                    if (data is null || data.Length == 0)
                    { sb.AppendLine($"      texture NOT FOUND  {slot.Path}"); texMissing++; continue; }
                    var img = TextureDecoder.Decode(data);
                    _renderer.SetTexture(mat, target, img.Rgba, img.Width, img.Height);
                    if (!target.Equals(slot.SamplerName + "__TX", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"      '{slot.SamplerName}' -> {target}  ({img.Width}x{img.Height})");
                    texBound++;
                }
                catch (Exception ex) { sb.AppendLine($"      texture FAILED  {slot.Path}: {ex.Message}"); texMissing++; }
            }

            foreach (var prm in b.Parameters)
                if (prm.TryGetVector4(out var v))
                    mat.Params[prm.Name] = new[] { v.X, v.Y, v.Z, v.W };
            ApplySkinMeshParams(mat);

            _renderer.AddMaterial(mat);
            first ??= mat;
        }

        sb.AppendLine($"   {(usedFallback ? "~" : "+")}  {b.Name,-34} {shader}"
                      + $"  (vs blob {vsPerm.BlobIndex}, ps blob {psPerm.BlobIndex}, {slices.Count} slice(s))"
                      + (usedFallback ? "   [picked shader, not authored]" : ""));
        return first;
    }

    [RelayCommand]
    private void Load()
    {
        if (_cache is null || SelectedShader is null) return;
        if (SelectedVertexPerm is null || SelectedPixelPerm is null)
        {
            Status = "This shader does not ship both a vertex and a pixel stage, so it cannot be previewed.";
            HasError = true;
            return;
        }

        string vsPath = ShaderCacheReader.TocPathFor(SelectedShader.Full, DxbcStage.Vertex);
        string psPath = ShaderCacheReader.TocPathFor(SelectedShader.Full, DxbcStage.Pixel);

        _vs = _cache.LoadShader(vsPath, SelectedVertexPerm.Perm.BlobIndex, out var e1);
        if (_vs is null) { Fail($"vertex stage: {e1}"); return; }
        _ps = _cache.LoadShader(psPath, SelectedPixelPerm.Perm.BlobIndex, out var e2);
        if (_ps is null) { Fail($"pixel stage: {e2}"); return; }

        var report = _renderer.LoadShaders(_vs, _ps);
        if (!report.Success) { Fail(report.Error ?? "shader creation failed"); BuildMetadata(vsPath, psPath, report); return; }

        _renderer.SetMesh(PreviewGeometry.CreateBuiltIn(SelectedMesh));
        FocusCamera();
        BuildSlots();
        BuildMetadata(vsPath, psPath, report);

        if (!_renderer.BuildComparisonShader(_vs, out var cerr))
            ComparisonSource = "// the comparison shader could not be built for this vertex shader:\n// " + cerr;
        else
            ComparisonSource = _renderer.ComparisonShaderSource ?? "";

        IsLoaded = true;
        HasError = false;
        Status = $"Loaded. {report.Steps.Count} pipeline objects created"
                 + (report.Warnings.Count > 0 ? $", {report.Warnings.Count} warning(s)." : ".");
        AppendLog();
    }

    private void Fail(string msg)
    {
        IsLoaded = false;
        HasError = true;
        Status = msg;
        AppendLog();
    }

    [RelayCommand] private void Reload() => Load();

    partial void OnSelectedMeshChanged(string value)
    {
        if (IsLoaded) _renderer.SetMesh(PreviewGeometry.CreateBuiltIn(value));
    }

    private void BuildSlots()
    {
        TextureSlots.Clear();
        Constants.Clear();
        foreach (var refl in new[] { _vs, _ps })
        {
            if (refl is null) continue;
            foreach (var t in refl.Textures)
                if (TextureSlots.All(r => !r.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
                    TextureSlots.Add(new TextureSlotRow { Name = t.Name, Slot = t.BindPoint, Dimension = t.DimensionName });
            foreach (var cb in refl.ConstantBuffers)
                foreach (var v in cb.Variables)
                    if (Constants.All(r => !r.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase)))
                        Constants.Add(new ConstantRow
                        {
                            Buffer = cb.Name, Name = v.Name, Offset = v.Offset,
                            Size = v.Size, TypeName = v.TypeName, IsUsed = v.IsUsed,
                        });
        }
    }

    private void BuildMetadata(string vsPath, string psPath, ShaderLoadReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"device      {_renderer.DeviceDescription}");
        sb.AppendLine();
        Describe(sb, "VERTEX", vsPath, SelectedVertexPerm, _vs);
        Describe(sb, "PIXEL", psPath, SelectedPixelPerm, _ps);

        if (report.Steps.Count > 0)
        {
            sb.AppendLine("pipeline objects created");
            foreach (var s in report.Steps) sb.AppendLine("   + " + s);
            sb.AppendLine();
        }
        if (report.Error is not null) sb.AppendLine("ERROR  " + report.Error).AppendLine();
        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("warnings");
            foreach (var w in report.Warnings) sb.AppendLine("   ! " + w);
            sb.AppendLine();
        }
        if (report.UnmatchedInputs.Count > 0)
        {
            sb.AppendLine("MISSING VERTEX DATA - these read as zero, the preview mesh has no such attribute");
            foreach (var u in report.UnmatchedInputs) sb.AppendLine("   ? " + u);
        }
        Metadata = sb.ToString();
        RebuildBindings();
    }

    private static void Describe(StringBuilder sb, string label, string path, PermutationRow? perm, DxbcShader? sh)
    {
        sb.AppendLine($"{label} STAGE");
        sb.AppendLine($"   path        {path}");
        if (perm is not null)
        {
            sb.AppendLine($"   permutation blob #{perm.Perm.BlobIndex}, key 0x{perm.Perm.Key:x16}");
            sb.AppendLine($"   defines     {perm.Perm.DefineSummary}");
        }
        if (sh is null) { sb.AppendLine("   NOT LOADED").AppendLine(); return; }
        sb.AppendLine($"   bytecode    {sh.ByteSize:n0} bytes  ({sh.ShaderModel}, reported stage {sh.Stage})");
        sb.AppendLine($"   trimmed     {(sh.WasTrimmed ? "yes - the container over-reported its length, as expected" : "no")}");
        sb.AppendLine($"   chunks      {string.Join(" ", sh.ChunkTags)}");
        sb.AppendLine($"   compiler    {sh.Creator}");
        sb.AppendLine($"   inputs      {(sh.Inputs.Count == 0 ? "(none)" : "")}");
        foreach (var e in sh.Inputs)
            sb.AppendLine($"      v{e.Register} {e.FullSemantic,-16} {e.ComponentTypeName,-5} mask={e.MaskString,-4}"
                          + (e.IsRead ? "" : "  (declared but unread)"));
        sb.AppendLine();
    }

    private void RebuildBindings()
    {
        var sb = new StringBuilder();
        foreach (var (label, refl) in new[] { ("VERTEX", _vs), ("PIXEL", _ps) })
        {
            if (refl is null) continue;
            sb.AppendLine($"{label} STAGE");
            foreach (var cb in refl.ConstantBuffers)
            {
                sb.AppendLine($"   cbuffer b{cb.BindPoint}  {cb.Name}  ({cb.Size} bytes -> {cb.AllocationSize} allocated)");
                foreach (var v in cb.Variables)
                    sb.AppendLine($"      +{v.Offset,-5} {v.Size,-4} {v.TypeName,-14} {(v.IsUsed ? "USED" : "    ")}  {v.Name}");
            }
            foreach (var t in refl.Textures) sb.AppendLine($"   texture t{t.BindPoint}  {t.Name}  [{t.DimensionName}]");
            foreach (var s in refl.Samplers) sb.AppendLine($"   sampler s{s.BindPoint}  {s.Name}");
            sb.AppendLine();
        }

        var unbound = _renderer.UnboundTextureNames().ToList();
        if (unbound.Count > 0)
        {
            sb.AppendLine("UNBOUND TEXTURES - these sample as opaque white");
            foreach (var u in unbound) sb.AppendLine("   ? " + u);
            sb.AppendLine();
        }

        sb.AppendLine("RENDER STATE");
        sb.AppendLine($"   fill        {(Wireframe ? "wireframe" : "solid")}");
        sb.AppendLine($"   cull        {(CullBackFaces ? "back faces (front = clockwise)" : "none")}");
        sb.AppendLine($"   depth       {(DepthTest ? "test on, write on, LESS" : "off")}");
        sb.AppendLine($"   blend       {(AlphaBlend ? "src-alpha / inv-src-alpha" : "opaque")}");
        sb.AppendLine($"   stencil     off");
        sb.AppendLine($"   target      B8G8R8A8_UNORM offscreen + D32_FLOAT depth, read back to a bitmap");
        sb.AppendLine($"   matrices    {(TransposeMatrices ? "transposed before upload" : "uploaded row-major as-is")}");
        Bindings = sb.ToString();
    }

    private void AppendLog() => Log = string.Join("\n", _renderer.Diagnostics);

    // ---------------------------------------------------------------- frame loop

    private void Tick()
    {
        if (!IsLoaded || !_renderer.IsReady) return;

        var now0 = DateTime.UtcNow;
        float dt = (float)Math.Clamp((now0 - _lastTick).TotalSeconds, 0.0, 0.25);
        _lastTick = now0;
        ApplyCameraInput(dt);

        _settings.MirrorX = MirrorX;
        _settings.SuppliedView = Camera.View;
        _settings.SuppliedProjection = Camera.Projection((float)_renderWidth / _renderHeight);
        _settings.SuppliedCameraPosition = Camera.Position;
        _settings.Wireframe = Wireframe;
        _settings.CullBackFaces = CullBackFaces;
        _settings.DepthTest = DepthTest;
        _settings.AlphaBlend = AlphaBlend;
        _settings.TransposeMatrices = TransposeMatrices;
        _settings.UseComparisonShader = UseComparisonShader;
        _settings.TimeSeconds = AnimateTime ? (float)(DateTime.UtcNow - _start).TotalSeconds : 0f;
        _settings.SunDirection = Vector3.Normalize(new Vector3(
            MathF.Cos((float)SunElevation) * MathF.Sin((float)SunAzimuth),
            -MathF.Sin((float)SunElevation),
            MathF.Cos((float)SunElevation) * MathF.Cos((float)SunAzimuth)));

        ApplyOverrides();

        int w = _renderWidth, h = _renderHeight;
        var unbound = new List<string>();
        var pixels = _renderer.RenderFrame(w, h, _settings, out var err, unbound);
        if (pixels is null)
        {
            if (err is not null && Status != err) { Status = err; HasError = true; AppendLog(); }
            return;
        }

        if (_bitmapA is null || _bmpW != w || _bmpH != h)
        {
            _bitmapA?.Dispose();
            _bitmapB?.Dispose();
            var size = new Avalonia.PixelSize(w, h);
            var dpi = new Avalonia.Vector(96, 96);
            _bitmapA = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            _bitmapB = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            _bmpW = w; _bmpH = h;
        }

        var target = _useB ? _bitmapB! : _bitmapA!;
        _useB = !_useB;
        using (var fb = target.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        Preview = target;

        _framesThisSecond++;
        var now = DateTime.UtcNow;
        if ((now - _fpsMark).TotalSeconds >= 1.0)
        {
            Perf = $"{_framesThisSecond} fps · {_renderer.LastFrameMs:F2} ms/frame · "
                   + $"{_renderer.DrawCalls} draw call{(_renderer.DrawCalls == 1 ? "" : "s")} · {w}x{h}"
                   + (UseComparisonShader ? "  ·  showing ReyEngine's model, not Riot's shader" : "");
            _framesThisSecond = 0;
            _fpsMark = now;
            int distinctUnbound = unbound.Distinct(StringComparer.Ordinal).Count();
            if (distinctUnbound > 0 && !UseComparisonShader)
                Perf += $"  ·  {distinctUnbound} unbound constant(s)";
        }
    }

    private void ApplyOverrides()
    {
        foreach (var c in Constants)
        {
            if (string.IsNullOrWhiteSpace(c.Value)) { _renderer.Overrides.Remove(c.Name); continue; }
            var parts = c.Value.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var vals = new List<float>(parts.Length);
            foreach (var p in parts)
                if (float.TryParse(p, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float f)) vals.Add(f);
            if (vals.Count > 0) _renderer.Overrides[c.Name] = vals.ToArray();
        }
    }

    /// <summary>Point a reflected texture slot at an image file.
    ///
    /// <para>Avalonia hands pixels back in its own layout, which on Windows is BGRA; the D3D textures are
    /// created as R8G8B8A8_UNORM, so the two channels are swapped on the way in. Getting this backwards is
    /// invisible on a greyscale test image and obvious on anything else.</para></summary>
    public void BindTextureFile(TextureSlotRow row, string path)
    {
        try
        {
            using var src = new Bitmap(path);
            int w = src.PixelSize.Width, h = src.PixelSize.Height;
            var buf = new byte[w * h * 4];

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                buf, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                src.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, w * 4);
            }
            finally { handle.Free(); }

            for (int i = 0; i < buf.Length; i += 4) (buf[i], buf[i + 2]) = (buf[i + 2], buf[i]);

            _renderer.SetTexture(row.Name, buf, w, h);
            row.Source = $"{Path.GetFileName(path)}  ({w}x{h})";
            RebuildBindings();
            AppendLog();
            Status = $"bound {Path.GetFileName(path)} to {row.Name}";
            HasError = false;
        }
        catch (Exception ex)
        {
            Status = $"could not bind '{Path.GetFileName(path)}': {ex.Message}";
            HasError = true;
        }
    }

    /// <summary>A procedural checker, so a texture slot can be exercised without hunting for a file.
    ///
    /// <para>M212: the levels are deliberately kept UNDER half scale. Environment shaders double the
    /// diffuse and clamp it (measured: <c>saturate(2 x texture)</c>), so League's env textures are authored
    /// at roughly half scale and anything brighter than 0.5 flattens to a solid block. The first checker
    /// used 235/130/70 and rendered as a white-and-lavender sphere, which looked like a renderer bug and
    /// was really a test texture outside the range the shader is built for.</para></summary>
    [RelayCommand]
    private void BindCheckerToAll()
    {
        const int T = 64;
        var tex = new byte[T * T * 4];
        for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                bool on = ((x / 8) + (y / 8)) % 2 == 0;
                int i = (y * T + x) * 4;
                tex[i] = (byte)(on ? 112 : 22);
                tex[i + 1] = (byte)(on ? 62 : 22);
                tex[i + 2] = (byte)(on ? 34 : 46);
                tex[i + 3] = 255;
            }
        foreach (var slot in TextureSlots)
        {
            _renderer.SetTexture(slot.Name, tex, T, T);
            slot.Source = "checker 64x64 (half-scale)";
        }
        RebuildBindings();
        AppendLog();
    }

    partial void OnWireframeChanged(bool v) => RebuildBindings();
    partial void OnCullBackFacesChanged(bool v) => RebuildBindings();
    partial void OnDepthTestChanged(bool v) => RebuildBindings();
    partial void OnAlphaBlendChanged(bool v) => RebuildBindings();
    partial void OnTransposeMatricesChanged(bool v) => RebuildBindings();

    public void Dispose()
    {
        _timer.Stop();
        _renderer.Dispose();
        _cache?.Dispose();
        _bitmapA?.Dispose();
        _bitmapB?.Dispose();
    }
}
