using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Hud;

namespace ReyEngine.App.ViewModels;

/// <summary>One row in the element tree.</summary>
public sealed partial class HudElementNodeViewModel : ObservableObject
{
    public required HudElement Element { get; init; }
    public string Name => Element.ShortName;
    public string ClassShort => Element.ClassName.Replace("UiElement", "").Replace("Data", "");
    public bool IsScene => Element.IsScene;
    public ObservableCollection<HudElementNodeViewModel> Children { get; } = new();
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;
    public bool Dimmed => !Element.Enabled;
}

/// <summary>A renderable element: its rectangle in canvas space + the atlas bitmap and source crop.</summary>
public sealed record HudDrawItem(
    HudElement Element, Bitmap? Atlas,
    double X, double Y, double W, double H,
    double SrcX, double SrcY, double SrcW, double SrcH,
    Avalonia.Media.Color? Tint);

/// <summary>
/// M140: the HUD Editor — loads a ClientStates/…/UIBase layout bin, resolves its texture atlases, and
/// exposes the element tree plus a flat, layer-sorted draw list for the 2D canvas. Read-only for now:
/// select an element in the tree or on the canvas and the inspector shows its rect / layer / texture.
/// </summary>
public sealed partial class HudEditorViewModel : ObservableObject
{
    public HudDocument? Document { get; private set; }
    public WadAssetEntry? Entry { get; private set; }

    [ObservableProperty] private string _title = "No HUD open";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private HudElementNodeViewModel? _selectedNode;

    public ObservableCollection<HudElementNodeViewModel> Tree { get; } = new();
    public ObservableCollection<HudDrawItem> DrawItems { get; } = new();
    /// <summary>Canvas space = the HUD reference resolution (1600×1200 for League).</summary>
    public double CanvasWidth => Document?.ReferenceWidth ?? 1600;
    public double CanvasHeight => Document?.ReferenceHeight ?? 1200;

    // host hooks
    public Func<string, Bitmap?>? ResolveAtlas;
    public Action<string>? Info;

    // ---- selection / inspector ----
    public HudElement? Selected => SelectedNode?.Element;
    public bool HasSelection => Selected is not null;
    [ObservableProperty] private string _inspName = "";
    [ObservableProperty] private string _inspClass = "";
    [ObservableProperty] private string _inspRect = "";
    [ObservableProperty] private string _inspLayer = "";
    [ObservableProperty] private string _inspAnchor = "";
    [ObservableProperty] private string _inspTexture = "";
    [ObservableProperty] private string _inspColor = "";
    [ObservableProperty] private bool _inspEnabled;
    [ObservableProperty] private Bitmap? _inspTexturePreview;
    [ObservableProperty] private Avalonia.Rect _selectionRect;
    [ObservableProperty] private bool _hasSelectionRect;

    private readonly Dictionary<uint, HudElementNodeViewModel> _nodesByHash = new();
    private readonly Dictionary<string, Bitmap?> _atlasCache = new(StringComparer.OrdinalIgnoreCase);

    public bool Load(WadAssetEntry entry, HudDocument doc)
    {
        Document = doc; Entry = entry;
        Title = entry.DisplayName;
        Subtitle = $"{doc.AllElements.Count} elements · {doc.AtlasPaths.Count} atlases · reference {doc.ReferenceWidth}×{doc.ReferenceHeight}";
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));

        BuildTree();
        BuildDrawList();
        int drawn = DrawItems.Count(d => d.Atlas is not null);
        Status = $"{DrawItems.Count} rectangles, {drawn} with a resolved atlas texture."
            + (drawn < DrawItems.Count(d => d.Element.Texture is not null) ? " Some atlases didn't resolve — open the mod's project so its textures mount." : "");
        return true;
    }

    private void BuildTree()
    {
        Tree.Clear();
        _nodesByHash.Clear();
        foreach (var root in Document!.Roots) Tree.Add(BuildNode(root));
    }

    private HudElementNodeViewModel BuildNode(HudElement e)
    {
        var node = new HudElementNodeViewModel { Element = e };
        _nodesByHash[e.PathHash] = node;
        foreach (var c in e.Children) node.Children.Add(BuildNode(c));
        return node;
    }

    private Bitmap? Atlas(string path)
    {
        if (_atlasCache.TryGetValue(path, out var cached)) return cached;
        Bitmap? bmp = null;
        try { bmp = ResolveAtlas?.Invoke(path); } catch { }
        _atlasCache[path] = bmp;
        return bmp;
    }

    private void BuildDrawList()
    {
        DrawItems.Clear();
        // draw painter's order: by layer, then tree order (already layer-sorted within parents)
        foreach (var e in Document!.AllElements
                     .Where(e => e is { HasRect: true, IsScene: false } && e.Size.X > 0 && e.Size.Y > 0)
                     .OrderBy(e => e.Layer))
        {
            if (!e.Enabled) continue;   // hidden elements aren't drawn (still selectable in the tree)
            Bitmap? atlas = e.Texture is { } t ? Atlas(t.AtlasPath) : null;
            var tint = e.Color is { } c
                ? Avalonia.Media.Color.FromArgb((byte)(Math.Clamp(c.W, 0, 1) * 255), (byte)(Math.Clamp(c.X, 0, 1) * 255),
                    (byte)(Math.Clamp(c.Y, 0, 1) * 255), (byte)(Math.Clamp(c.Z, 0, 1) * 255))
                : (Avalonia.Media.Color?)null;
            DrawItems.Add(new HudDrawItem(e, atlas,
                e.Position.X, e.Position.Y, e.Size.X, e.Size.Y,
                e.Texture?.SrcX ?? 0, e.Texture?.SrcY ?? 0, e.Texture?.SrcW ?? 0, e.Texture?.SrcH ?? 0, tint));
        }
    }

    partial void OnSelectedNodeChanged(HudElementNodeViewModel? value)
    {
        foreach (var n in _nodesByHash.Values) n.IsSelected = ReferenceEquals(n, value);
        RefreshInspector();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Selected));   // the canvas selection outline binds Selected.PathHash
    }

    /// <summary>Select by path hash (canvas click → tree).</summary>
    public void SelectByHash(uint hash)
    {
        if (_nodesByHash.TryGetValue(hash, out var node))
        {
            ExpandTo(node);
            SelectedNode = node;
        }
    }

    private void ExpandTo(HudElementNodeViewModel target)
    {
        // walk parents via the element ParentHash chain and expand them
        var e = target.Element;
        while (e.ParentHash != 0 && _nodesByHash.TryGetValue(e.ParentHash, out var pn))
        {
            pn.IsExpanded = true;
            e = pn.Element;
        }
    }

    private void RefreshInspector()
    {
        var e = Selected;
        HasSelectionRect = false;
        if (e is null)
        {
            InspName = InspClass = InspRect = InspLayer = InspAnchor = InspTexture = InspColor = "";
            InspTexturePreview = null;
            return;
        }
        InspName = e.FullName;
        InspClass = e.ClassName;
        InspLayer = e.Layer.ToString();
        InspEnabled = e.Enabled;
        InspRect = e.HasRect ? $"Position ({e.Position.X:0}, {e.Position.Y:0})   Size {e.Size.X:0} × {e.Size.Y:0}" : "no rectangle";
        InspAnchor = $"({e.Anchor.X:0.###}, {e.Anchor.Y:0.###})";
        if (e.Texture is { } t)
        {
            InspTexture = $"{t.AtlasPath}\nUV ({t.SrcX:0}, {t.SrcY:0}) → {t.SrcW:0} × {t.SrcH:0} of {t.AtlasWidth}×{t.AtlasHeight}";
            InspTexturePreview = Atlas(t.AtlasPath);
        }
        else { InspTexture = "(no texture)"; InspTexturePreview = null; }
        InspColor = e.Color is { } c ? $"rgba({c.X:0.##}, {c.Y:0.##}, {c.Z:0.##}, {c.W:0.##})" : "(none)";
        if (e.HasRect)
        {
            SelectionRect = new Avalonia.Rect(e.Position.X, e.Position.Y, e.Size.X, e.Size.Y);
            HasSelectionRect = true;
        }
    }

    [RelayCommand] private void ClearSelection() => SelectedNode = null;
}
