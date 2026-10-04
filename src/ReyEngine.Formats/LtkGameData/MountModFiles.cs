using ReyEngine.Core.Assets;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M819: the mod's own copies of chunks, read from the project's mounts - what a target's base is when the project ships the chunk itself (<see cref="IGameDataModFiles"/>).
///
/// <para><b>Raw by construction.</b> It asks the mounts of the project - a folder, an override, a project WAD - and nothing else: not <see cref="AssetMountService.Read"/>, which answers what the overlay
/// serves, and not the game. A base that was an overlaid result would have the declarations applied to their own output, which is what an edit written back would do too. The mounts a layer
/// holds are asked in the order they rank in.</para>
///
/// <para>A layer's WAD folders are one namespace: a chunk path hash names one file in them, whatever the folder. <c>RAW</c> is its own: it wins over every layer, and only the base layer has one.</para>
/// </summary>
public sealed class MountModFiles : IGameDataModFiles
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IAssetMount>> _layers;
    private readonly IReadOnlyList<IAssetMount> _raw;

    /// <param name="layers">The mounts each layer holds, by the layer's name, in the order they win in (the first that holds a chunk answers). A layer that is not listed holds none.</param>
    /// <param name="raw">The mounts of the <c>RAW</c> folder, in the same order; none for a project without one.</param>
    public MountModFiles(IReadOnlyDictionary<string, IReadOnlyList<IAssetMount>> layers, IReadOnlyList<IAssetMount>? raw = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        _layers = layers;
        _raw = raw ?? Array.Empty<IAssetMount>();
    }

    public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes) =>
        _layers.TryGetValue(layer, out var mounts) ? Read(mounts, chunk, maxBytes) : null;

    public byte[]? ReadRawFile(ulong chunk, long maxBytes) => Read(_raw, chunk, maxBytes);

    private static byte[]? Read(IReadOnlyList<IAssetMount> mounts, ulong chunk, long maxBytes)
    {
        foreach (var mount in mounts)
        {
            if (!mount.Contains(chunk)) continue;
            // the size is known before a byte is read: a copy larger than the overlay reads is refused on that
            if (mount.Get(chunk) is { } asset && asset.Size > maxBytes)
                throw new IOException($"the file is {asset.Size:N0} bytes, more than the {maxBytes:N0} the overlay reads");
            return mount.Read(chunk);
        }
        return null;
    }
}
