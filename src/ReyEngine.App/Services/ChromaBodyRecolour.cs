using ReyEngine.Core.Decoding;

namespace ReyEngine.App.Services;

/// <summary>M824: one body texture the Chroma Studio recolours - the chunk hash is the identity (an unnamed
/// <c>0x...</c> link has no path to hash), the path is what the project folder file is named.</summary>
public sealed record ChromaTarget(ulong Hash, string Path);

/// <summary>M824: what project.json holds for one skin's body recolour - the transform and the textures it was applied to.</summary>
public sealed record ChromaSavedRecipe(ColorTransform Transform, IReadOnlyList<ChromaTarget> Targets);

/// <summary>M824: one recoloured texture on its way to a renderer: the chunk, its RGBA8 texels and their size.</summary>
public sealed record ChromaPushItem(ulong Hash, byte[] Rgba, int Width, int Height);

/// <summary>M824: what a save of the body recolour did, for the card's status line and the log.</summary>
/// <param name="Written">Textures written into the project.</param>
/// <param name="Skipped">Textures the transform left exactly as they were (nothing selected in them) or the writer cannot write.</param>
/// <param name="Failed">Textures whose original could not be read or whose write failed.</param>
/// <param name="Reverted">Textures of this skin's earlier recipe that are no longer part of it and were put back to Riot's.</param>
/// <param name="WrittenHashes">The chunk hashes now recoloured by this skin's recipe (each has a record and a file).</param>
/// <param name="Settled">The textures that are as the recipe says: written, or left unchanged by it. A failed one is not.</param>
public sealed record ChromaSaveResult(int Written, int Skipped, int Failed, int Reverted,
    IReadOnlyList<ulong> WrittenHashes, IReadOnlyList<ulong> Settled, IReadOnlyList<string> Notes)
{
    public string Summary =>
        $"{Written} texture(s) written" + (Skipped > 0 ? $", {Skipped} unchanged or unsupported" : "")
        + (Failed > 0 ? $", {Failed} failed" : "") + (Reverted > 0 ? $", {Reverted} put back to the original" : "");
}

/// <summary>
/// M824: the BODY RECOLOUR sliders as plain numbers, and the <see cref="ColorTransform"/> they stand for.
///
/// <para>The view model binds to these through its own properties; this type is the one place the mapping lives, so the
/// save, the restore from project.json and the live preview cannot disagree about what a slider means. Defaults are a
/// transform that changes nothing (<see cref="ColorTransform.IsIdentity"/>), with grey protection ON at the threshold the
/// M813 review asked for: BC1/BC3 decode a neutral texel to something like (132,130,132), about 1.5% saturation, so an
/// exact-grey protection (threshold 0) would almost never fire on a real texture - 8% with a 6% feather leaves steel,
/// bone and cloth greys alone and recolours everything that has a colour.</para>
/// </summary>
public sealed record ChromaRecolourSettings
{
    public const float DefaultGreyThreshold = 0.08f;
    public const float DefaultGreyFeather = 0.06f;
    public const float DefaultRangeWidth = 60f;
    public const float DefaultRangeFeather = 30f;

    public float HueShift { get; init; }
    public float Saturation { get; init; } = 1f;
    public float Brightness { get; init; } = 1f;
    public bool Colorize { get; init; }
    public float ColorizeHue { get; init; }
    public bool RangeOn { get; init; }
    public float RangeCenter { get; init; }
    public float RangeWidth { get; init; } = DefaultRangeWidth;
    public float RangeFeather { get; init; } = DefaultRangeFeather;
    public float GreyThreshold { get; init; } = DefaultGreyThreshold;
    public float GreyFeather { get; init; } = DefaultGreyFeather;
    public float Strength { get; init; } = 1f;

    public static ChromaRecolourSettings Default { get; } = new();

    /// <summary>The transform these sliders make. A grey protection of 0 threshold and 0 feather is NO protection (null),
    /// not "protect exact greys" - the slider's far end must mean off.</summary>
    public ColorTransform ToTransform() => new()
    {
        HueShiftDegrees = HueShift,
        Saturation = Saturation,
        Brightness = Brightness,
        ColorizeHueDegrees = Colorize ? ColorizeHue : null,
        HueSelection = RangeOn ? new HueRange(RangeCenter, RangeWidth, RangeFeather) : null,
        GreyProtection = GreyThreshold > 0f || GreyFeather > 0f ? new GreyProtection(GreyThreshold, GreyFeather) : null,
        Strength = Strength,
    };

    /// <summary>The sliders for a saved transform (a record read back from project.json). A selection the settings never
    /// made keeps the slider defaults, so switching it on afterwards starts from a sensible range.</summary>
    public static ChromaRecolourSettings From(ColorTransform t) => new()
    {
        HueShift = t.HueShiftDegrees,
        Saturation = t.Saturation,
        Brightness = t.Brightness,
        Colorize = t.ColorizeHueDegrees.HasValue,
        ColorizeHue = t.ColorizeHueDegrees ?? 0f,
        RangeOn = t.HueSelection.HasValue,
        RangeCenter = t.HueSelection?.CenterDegrees ?? 0f,
        RangeWidth = t.HueSelection?.WidthDegrees ?? DefaultRangeWidth,
        RangeFeather = t.HueSelection?.FeatherDegrees ?? DefaultRangeFeather,
        GreyThreshold = t.GreyProtection?.Threshold ?? 0f,
        GreyFeather = t.GreyProtection?.Feather ?? 0f,
        Strength = t.Strength,
    };
}

/// <summary>
/// M824: the originals the live preview recolours, and the recoloured copies it hands to the renderers.
///
/// <para><b>Never compounding.</b> Every <see cref="Render"/> starts from the ORIGINAL texels (the decoded pristine file,
/// kept as it was decoded) and applies the transform once into a separate buffer, exactly as the save re-derives from the
/// pristine bytes - so dragging a slider back and forth ends where it started, and what the viewport shows is what the save
/// writes up to the BC re-encode.</para>
///
/// <para><b>Cost.</b> One pass over a 2048 x 2048 texture is about 130 ms in a Release build (M813 measured it, a texture with
/// flat areas is 3-4x cheaper) and the pass is per texel with no neighbours, so it is cut into bands and the bands run in
/// parallel. The preview loop still renders on a worker and keeps only the NEWEST request, so a slow texture lowers the update
/// rate rather than queueing work.</para>
///
/// <para>Not thread-safe: one render at a time, which the view model's loop guarantees. The buffers a render returns are
/// reused by the next one.</para>
/// </summary>
public sealed class ChromaPreviewBuffers
{
    private sealed class Entry
    {
        public required int Width, Height;
        public required byte[] Original;
        public byte[]? Result;
    }

    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly object _gate = new();

    /// <summary>Bands a texture is cut into for the parallel pass. A band is whole texels (a multiple of 4 bytes).</summary>
    private const int Bands = 16;

    public int Count { get { lock (_gate) return _entries.Count; } }
    public bool Has(ulong hash) { lock (_gate) return _entries.ContainsKey(hash); }

    private Entry? Find(ulong hash) { lock (_gate) return _entries.GetValueOrDefault(hash); }

    /// <summary>Keep this decoded texture as the original for <paramref name="hash"/>. The image is not copied and is never
    /// written to.</summary>
    public void Set(ulong hash, TextureImage original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var entry = new Entry { Width = original.Width, Height = original.Height, Original = original.Rgba };
        lock (_gate) _entries[hash] = entry;
    }

    public bool Remove(ulong hash) { lock (_gate) return _entries.Remove(hash); }
    public void Clear() { lock (_gate) _entries.Clear(); }

    public bool TrySize(ulong hash, out int width, out int height)
    {
        if (Find(hash) is { } e) { width = e.Width; height = e.Height; return true; }
        width = height = 0;
        return false;
    }

    /// <summary>The original texels, as decoded. Read-only by contract.</summary>
    public byte[]? OriginalOf(ulong hash) => Find(hash)?.Original;

    /// <summary>The texture with <paramref name="transform"/> applied to its original texels, in a buffer this object owns
    /// (valid until the next render of the same hash). The identity returns a copy of the original. Null when the hash is not
    /// held. Only one render at a time may be running (the lookup is locked, the pass is not).</summary>
    public byte[]? Render(ulong hash, ColorTransform transform, CancellationToken cancellationToken = default)
    {
        if (Find(hash) is not { } e) return null;
        var result = e.Result is { } r && r.Length == e.Original.Length ? r : (e.Result = new byte[e.Original.Length]);
        Buffer.BlockCopy(e.Original, 0, result, 0, e.Original.Length);
        if (transform.IsIdentity) return result;

        int texels = result.Length / 4;
        if (texels < 4096)
        {
            transform.ApplyInPlace(result);
            return result;
        }

        int perBand = (texels + Bands - 1) / Bands;
        Parallel.For(0, Bands, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)),
        }, band =>
        {
            int start = band * perBand;
            if (start >= texels) return;
            int count = Math.Min(perBand, texels - start);
            transform.ApplyInPlace(result.AsSpan(start * 4, count * 4));
        });
        return result;
    }
}
