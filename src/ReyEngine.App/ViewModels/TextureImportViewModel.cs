using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M392: the image-import dialog. Decodes each picked image once, then lets the user choose how it is
/// written as a Riot .tex and shows what that costs before committing.
///
/// <para>The option set comes from M391, which sized it against 613,542 shipped .tex files: BC1 or BC3,
/// mipmaps on or off, and deliberately no resize-to-power-of-two (7,620 shipped textures are NPOT).</para>
///
/// <para>Files that fail to decode are kept in the list with their reason rather than dropped, so an
/// import of twenty images does not silently become an import of nineteen.</para>
/// </summary>
public sealed partial class TextureImportViewModel : ObservableObject
{
    /// <summary>Extensions worth offering to convert. A .tex input is excluded on purpose: it is already
    /// in the target format, and re-encoding it would be a lossy round trip for no reason.</summary>
    public static readonly string[] ConvertibleExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds" };

    public static bool IsConvertibleImage(string path) =>
        ConvertibleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public sealed partial class Item : ObservableObject
    {
        public string SourcePath { get; }
        public string Name => Path.GetFileName(SourcePath);
        public TextureImage? Image { get; }

        /// <summary>Why this file cannot be converted, or null. Shown next to the row.</summary>
        public string? Error { get; }
        public bool CanConvert => Image is not null && Error is null;

        [ObservableProperty] private string _detail = "";

        public Item(string path, TextureImage? image, string? error)
        { SourcePath = path; Image = image; Error = error; }

        /// <summary>The name this becomes in the project: same stem, .tex extension.</summary>
        public string TargetName => Path.GetFileNameWithoutExtension(SourcePath) + ".tex";
    }

    public ObservableCollection<Item> Items { get; } = new();

    [ObservableProperty] private TexFormatChoice _format = TexFormatChoice.Auto;
    [ObservableProperty] private bool _mipmaps = true;

    /// <summary>False when nothing in the list can actually be converted — the dialog's OK is disabled
    /// rather than silently writing nothing.</summary>
    public bool HasConvertible => Items.Any(i => i.CanConvert);

    public string Title => Items.Count == 1
        ? $"Import '{Items[0].Name}'"
        : $"Import {Items.Count} image(s)";

    /// <param name="readBytes">Injected so this is testable without touching disk.</param>
    public TextureImportViewModel(IEnumerable<string> paths, Func<string, byte[]>? readBytes = null)
    {
        readBytes ??= File.ReadAllBytes;
        foreach (var p in paths)
        {
            TextureImage? img = null;
            string? err = null;
            try
            {
                img = TextureDecoder.Decode(readBytes(p));
                err = TexEncodeOptions.Validate(img);
                if (err is not null) img = null;
            }
            catch (Exception ex) { err = ex.Message; }
            Items.Add(new Item(p, img, err));
        }
        Refresh();
    }

    public TexEncodeOptions Options => new() { Format = Format, Mipmaps = Mipmaps };

    partial void OnFormatChanged(TexFormatChoice value) => Refresh();
    partial void OnMipmapsChanged(bool value) => Refresh();

    /// <summary>Per-row summary: source size, chosen format, and the EXACT output size (PredictBytes is
    /// the writer's own arithmetic, not an estimate — see M391).</summary>
    private void Refresh()
    {
        var o = Options;
        foreach (var it in Items)
        {
            if (it.Image is not { } img) { it.Detail = it.Error ?? "could not be read"; continue; }
            var fmt = o.Resolve(img);
            long bytes = o.PredictBytes(img);
            it.Detail = $"{img.Width}x{img.Height}  ->  {it.TargetName}  "
                      + $"[{(fmt == TexFormat.Bc1 ? "BC1" : "BC3")}"
                      + (Format == TexFormatChoice.Auto ? ", auto" : "")
                      + $"{(Mipmaps ? ", mips" : ", no mips")}]  {Human(bytes)}";
        }
        OnPropertyChanged(nameof(TotalSummary));
        OnPropertyChanged(nameof(HasConvertible));
    }

    public string TotalSummary
    {
        get
        {
            var o = Options;
            int n = 0; long total = 0;
            foreach (var it in Items)
                if (it.Image is { } img) { n++; total += o.PredictBytes(img); }
            int failed = Items.Count - n;
            return n == 0
                ? "Nothing here can be converted."
                : $"{n} file(s) -> {Human(total)}" + (failed > 0 ? $"   ({failed} unreadable)" : "");
        }
    }

    private static string Human(long b) =>
        b >= 1024 * 1024 ? $"{b / 1024.0 / 1024.0:0.##} MB" : $"{b / 1024.0:0.#} KB";

    /// <summary>Encode everything convertible. Returns (targetFileName, bytes) pairs; the caller decides
    /// where they land so the project's own write path and mount priority still apply.</summary>
    public List<(string Name, byte[] Bytes)> Encode()
    {
        var o = Options;
        var result = new List<(string, byte[])>();
        foreach (var it in Items)
            if (it.Image is { } img)
                result.Add((it.TargetName, o.Encode(img)));
        return result;
    }
}
