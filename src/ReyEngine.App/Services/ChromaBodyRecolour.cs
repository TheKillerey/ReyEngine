using System.Numerics;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;

namespace ReyEngine.App.Services;

/// <summary>M824: one body texture the Chroma Studio recolours - the chunk hash is the identity (an unnamed
/// <c>0x...</c> link has no path to hash), the path is what the project folder file is named.</summary>
public sealed record ChromaTarget(ulong Hash, string Path);

/// <summary>M824: what project.json holds for one skin's body recolour - the transform and the textures it was applied to.
/// M825 adds, optionally, the colour parameters the recolour owns and the transform they were written with (null and empty for a
/// recipe saved before, which therefore loads exactly as it did).</summary>
public sealed record ChromaSavedRecipe(ColorTransform Transform, IReadOnlyList<ChromaTarget> Targets,
    IReadOnlyList<ChromaParameterRef>? Parameters = null, ColorTransform? ParameterTransform = null)
{
    public IReadOnlyList<ChromaParameterRef> SavedParameters => Parameters ?? Array.Empty<ChromaParameterRef>();
}

/// <summary>M825: one colour parameter of the skin bin as the card lists it - Riot's value (what every recolour is derived from), the
/// value the project's bin holds now, and whether a recolour may act on it.</summary>
/// <param name="EditedOutside">The project's value differs from Riot's and no recipe accounts for it: somebody edited it in the Material tab
/// (or a mod's GameData did). Left off by default; switching it on means saving replaces that edit.</param>
/// <param name="DrivenBy">A material driver sets this parameter at run time (see <see cref="SkinColorParam.DrivenBy"/>); the preview leaves it alone.</param>
/// <param name="OwnedEdited">The recipe owns this parameter but the project's value is no longer what the recipe wrote: it was edited (in the Material tab) since.
/// Left off, and kept as it is, unless the person switches it on again.</param>
/// <param name="DefaultOffReason">Why the row starts switched off although a recolour may act on it (a save of it is likely to be refused), or null.</param>
public sealed record ChromaParameterInfo(SkinColorParamKey Key, string MaterialName, string TypeName, Vector4 Riot, Vector4 Current,
    bool Recolourable, string Reason, bool EditedOutside, string? DrivenBy = null, bool OwnedEdited = false, string? DefaultOffReason = null);

/// <summary>M825: what the host read for the card's colour parameter list. <see cref="Problem"/> says why the list is empty or short
/// ("Riot's original skin bin cannot be read..."), or is empty.</summary>
public sealed record ChromaParameterSnapshot(IReadOnlyList<ChromaParameterInfo> Parameters, string Problem = "");

/// <summary>M825: one recoloured colour parameter on its way to the D3D11 scene: which parameter, the material's name (what the renderer's
/// materials are called) and the four floats the shader reads.</summary>
public sealed record ChromaParamPush(SkinColorParamKey Key, string MaterialName, float[] Value);

/// <summary>M825: what a save of the colour parameters did, for the card's status line and the log.</summary>
/// <param name="Settled">The parameters the bin now holds as the recipe says (written, or already so): these are the saved recipe.</param>
public sealed record ChromaParamSaveResult(int Written, int Unchanged, int Skipped, int Reverted,
    IReadOnlyList<SkinColorParamKey> Settled, IReadOnlyList<string> Notes)
{
    public string Summary =>
        $"{Written} colour parameter(s) written" + (Unchanged > 0 ? $", {Unchanged} already as wanted" : "")
        + (Skipped > 0 ? $", {Skipped} left alone" : "") + (Reverted > 0 ? $", {Reverted} put back to Riot's" : "");
}

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

/// <summary>
/// M825: where a recoloured colour parameter goes in the D3D11 character scene. The renderer fills each material's constant buffers on every
/// draw from <c>PreviewMaterial.Params</c>, so a parameter's new colour is written into the material that holds it and the next frame shows it:
/// no scene rebuild, no texture work. The window calls this with the view model's batch; a test calls the same code against a real device.
/// </summary>
public static class ChromaDx11Parameters
{
    /// <summary>Write the colours into the committed character materials (the ones whose <c>CharacterSubmeshIndex</c> says they are the
    /// character's, not a particle's or a prop's) and into the arrays the skin block's own colour is carried in. Returns how many places were
    /// written - 0 means nothing on screen holds any of these parameters.</summary>
    public static int Apply(ReyEngine.Rendering.D3D11.ShaderPreviewRenderer renderer, PreparedCharacterScene? scene, IReadOnlyList<ChromaParamPush> items)
    {
        int touched = 0;
        foreach (var item in items)
        {
            if (item.Value.Length < 4) continue;
            if (item.Key.Material == 0)
            {
                // the skin block's own colour: the scene hands it to every slice that does not author the parameter itself, one array each
                if (scene is null || !item.Key.Name.Equals("fresnelColor", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var (name, value) in scene.SkinBoundParameters)
                    if (name.Equals("Fresnel_Color", StringComparison.OrdinalIgnoreCase) && value.Length >= 4) { Array.Copy(item.Value, value, 4); touched++; }
                continue;
            }
            foreach (var material in renderer.Materials)
            {
                if (material.CharacterSubmeshIndex < 0 || !string.Equals(material.Name, item.MaterialName, StringComparison.OrdinalIgnoreCase)) continue;
                // the array the scene's slice and this material share is rewritten in place. A material that does not hold the parameter is left
                // alone: the scene feeds the shader what it was built with, and a value it never had is not what the game draws.
                if (!material.Params.TryGetValue(item.Key.Name, out var held) || held.Length < 4) continue;
                Array.Copy(item.Value, held, 4);
                touched++;
            }
        }
        return touched;
    }
}
