using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Shaders;
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
    private DxbcShader? _vs, _ps;
    // double-buffered: an Image only repaints when its Source REFERENCE changes, so writing into the
    // same WriteableBitmap every frame shows nothing. Alternating two avoids the null-then-set flicker.
    private WriteableBitmap? _bitmapA, _bitmapB;
    private bool _useB;
    private int _bmpW, _bmpH;
    private readonly DateTime _start = DateTime.UtcNow;
    private int _framesThisSecond;
    private DateTime _fpsMark = DateTime.UtcNow;

    public ObservableCollection<ShaderRow> ShaderNames { get; } = new();
    public ObservableCollection<PermutationRow> VertexPermutations { get; } = new();
    public ObservableCollection<PermutationRow> PixelPermutations { get; } = new();
    public ObservableCollection<TextureSlotRow> TextureSlots { get; } = new();
    public ObservableCollection<ConstantRow> Constants { get; } = new();
    public ObservableCollection<string> MeshNames { get; } = new(PreviewGeometry.BuiltInNames);

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

    // render settings, mirrored onto PreviewSettings
    [ObservableProperty] private double _yaw = 0.6, _pitch = 0.4, _distance = 3.2;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private bool _cullBackFaces = true;
    [ObservableProperty] private bool _depthTest = true;
    [ObservableProperty] private bool _alphaBlend;
    [ObservableProperty] private bool _transposeMatrices = true;
    [ObservableProperty] private bool _useComparisonShader;
    [ObservableProperty] private bool _animateTime = true;
    [ObservableProperty] private double _sunAzimuth = 2.2, _sunElevation = 0.9;

    public bool CacheAvailable => _cache is not null;

    public ShaderPreviewViewModel(string? gameDataFinalDir, IHashResolver? resolver)
    {
        if (string.IsNullOrWhiteSpace(gameDataFinalDir) || !Directory.Exists(gameDataFinalDir))
        {
            Status = "No game directory is configured, so there is no shader cache to read. "
                     + "Set the game folder in project settings (it should contain DATA/FINAL).";
            HasError = true;
        }
        else
        {
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

        _settings.Yaw = (float)Yaw;
        _settings.Pitch = (float)Pitch;
        _settings.Distance = (float)Distance;
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

        const int w = 640, h = 480;
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
            if (unbound.Count > 0 && !UseComparisonShader)
                Perf += $"  ·  {unbound.Count} unbound constant(s)";
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

    /// <summary>A procedural checker, so a texture slot can be exercised without hunting for a file.</summary>
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
                tex[i] = (byte)(on ? 235 : 45);
                tex[i + 1] = (byte)(on ? 130 : 45);
                tex[i + 2] = (byte)(on ? 70 : 95);
                tex[i + 3] = 255;
            }
        foreach (var slot in TextureSlots)
        {
            _renderer.SetTexture(slot.Name, tex, T, T);
            slot.Source = "checker 64x64";
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
