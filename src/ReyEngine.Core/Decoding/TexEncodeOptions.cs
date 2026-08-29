namespace ReyEngine.Core.Decoding;

/// <summary>What the user picks in the import dialog. <see cref="Auto"/> chooses BC1 or BC3 from the
/// image's own alpha, which is the right default because the two differ mainly in whether alpha survives.</summary>
public enum TexFormatChoice
{
    Auto,
    Bc1,
    Bc3,
}

/// <summary>
/// M391: the settings an image import offers, and the arithmetic behind them.
///
/// <para>The option SET is deliberately small, and that is a measured result rather than a simplification.
/// A survey of 613,542 shipped .tex files across 455 WADs found:</para>
/// <list type="bullet">
///   <item>BC3 (format byte 12) 404,254 and BC1 (10) 208,979 — together 99.95% of everything Riot ships.
///     Three other bytes exist (13, 14, 20) totalling 309 files, all in UI/chibi assets. They are not
///     offered: an encoder for a byte the game may not accept produces files that load in the editor
///     and fail in game.</item>
///   <item>94% of BC3 and 87% of BC1 ship WITH mipmaps, so mipmaps default on.</item>
///   <item>7,620 shipped BC1 textures are NOT power-of-two. There is deliberately no "resize to power of
///     two" option — it would corrupt legitimate sizes. BC needs 4x4 BLOCKS, not powers of two, and
///     <see cref="TexWriter.Write"/> already stores whole blocks at every level, so any dimension works.</item>
/// </list>
/// </summary>
public sealed record TexEncodeOptions
{
    public TexFormatChoice Format { get; init; } = TexFormatChoice.Auto;

    /// <summary>Write the full chain down to 1x1. On by default; see the survey note above.</summary>
    public bool Mipmaps { get; init; } = true;

    public static TexEncodeOptions Default { get; } = new();

    /// <summary>
    /// The concrete format this image will be written as. <see cref="TexFormatChoice.Auto"/> picks BC3
    /// when any pixel is meaningfully transparent and BC1 otherwise — BC1 carries at most 1 bit of alpha,
    /// so choosing it for a graded alpha silently hard-edges the texture.
    /// </summary>
    public TexFormat Resolve(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Format switch
        {
            TexFormatChoice.Bc1 => TexFormat.Bc1,
            TexFormatChoice.Bc3 => TexFormat.Bc3,
            _ => TexWriter.HasPunchThroughAlpha(image) ? TexFormat.Bc3 : TexFormat.Bc1,
        };
    }

    /// <summary>Bytes the encoded .tex will occupy, for the dialog's size preview. Exact, not an estimate:
    /// it is the same block arithmetic the writer uses (12-byte header + whole 4x4 blocks per level).</summary>
    public long PredictBytes(int width, int height, TexFormat format)
    {
        if (width <= 0 || height <= 0) return 0;
        // M594: the writer grows a block-compressed image onto the 4x4 grid before encoding, because D3D
        // refuses to create one that is off it. Predict the size of what will actually be WRITTEN, or the
        // import dialog under-reports every off-grid texture.
        if (TexWriter.IsBlockCompressed(format) && !TexWriter.FitsBlockGrid(width, height))
        {
            width = Math.Max(4, (width + 3) / 4 * 4);
            height = Math.Max(4, (height + 3) / 4 * 4);
        }
        int perBlock = format == TexFormat.Bc1 ? 8 : 16;
        long total = 12;
        int w = width, h = height;
        while (true)
        {
            total += (long)((w + 3) / 4) * ((h + 3) / 4) * perBlock;
            if (!Mipmaps || (w == 1 && h == 1)) break;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        return total;
    }

    public long PredictBytes(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return PredictBytes(image.Width, image.Height, Resolve(image));
    }

    /// <summary>Encode with these settings.</summary>
    public byte[] Encode(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return TexWriter.Write(image, Resolve(image), Mipmaps);
    }

    /// <summary>Why an image cannot be written, or null when it can. The .tex header stores width and
    /// height as u16, so anything past 65535 would silently wrap to a wrong size.</summary>
    public static string? Validate(TextureImage? image)
    {
        if (image is null) return "no image";
        if (image.Width <= 0 || image.Height <= 0) return "image has no pixels";
        if (image.Width > ushort.MaxValue || image.Height > ushort.MaxValue)
            return $"{image.Width}x{image.Height} exceeds the .tex header's 16-bit size field";
        long need = (long)image.Width * image.Height * 4;
        if (image.Rgba is null || image.Rgba.LongLength < need)
            return $"pixel buffer holds {image.Rgba?.LongLength ?? 0} bytes, needs {need}";
        return null;
    }
}
