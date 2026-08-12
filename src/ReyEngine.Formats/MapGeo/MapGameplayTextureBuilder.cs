using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M439: author <c>MapGameplayTexture</c>'s two sub-structures — the thing the field editor could not do,
/// because one is an embedded list and the other four are object links.
///
/// <para><b>The shape, measured from the meta dump:</b></para>
/// <code>
/// MapGameplayTexture
///   0xa5aaf88e            List2&lt;Embedded GameplayTextureSampler&gt;  { name, texturePath }   &lt;- the TEXTURE
///   RedChannel            Link -&gt; GameplayTextureChannel
///   GreenChannel          Link -&gt; GameplayTextureChannel
///   BlueChannel           Link -&gt; GameplayTextureChannel
///   AlphaChannel          Link -&gt; GameplayTextureChannel
///   AdditionalChannels    List2&lt;Link -&gt; GameplayTextureChannel&gt;
///   ShaderParameters      Map&lt;String,F32&gt;
///   TextureResolution     U8
/// </code>
///
/// <para><b>The texture needs no link.</b> GameplayTextureChannel declares ChannelName, MinimapColor,
/// SoundName, ShaderParameterOverrides and three unnamed fields — and NO texture field. The texture lives
/// in the sampler list, which is an embedded struct of two strings and therefore perfectly safe to write.
/// A channel is a semantic slot (what this colour channel MEANS), not a texture reference.</para>
///
/// <para><b>Stated plainly: this is unshipped territory.</b> Neither GameplayTextureChannel nor
/// GameplayTextureSampler has a single instance in 50,107 shipped bins, and MapGameplayTexture appears in
/// 0 of 206 shipped map bins. The STRUCTURE here is measured from the schema; the SEMANTICS — what the
/// engine expects a channel to be called, which resolution is valid — are not, because nothing
/// demonstrates them. Adding this component as a bare marker is already confirmed to crash the client
/// with <c>Missing sampler "AlphaMask"</c>, so completing it is a bet, not a fix.</para>
/// </summary>
public static class MapGameplayTextureBuilder
{
    public static readonly uint ChannelClass = 0xff5060b7;    // GameplayTextureChannel
    public static readonly uint SamplerClass = 0x8d65d994;    // GameplayTextureSampler

    public static readonly uint SamplersField = 0xa5aaf88e;   // unnamed List2<Embedded GameplayTextureSampler>
    public static readonly uint FieldSamplerName = HashAlgorithms.Fnv1a("name");
    public static readonly uint FieldTexturePath = HashAlgorithms.Fnv1a("texturePath");
    public static readonly uint FieldChannelName = HashAlgorithms.Fnv1a("ChannelName");
    public static readonly uint FieldMinimapColor = HashAlgorithms.Fnv1a("MinimapColor");
    public static readonly uint FieldSoundName = HashAlgorithms.Fnv1a("SoundName");

    private static readonly uint ContainerCls = HashAlgorithms.Fnv1a("MapContainer");
    private static readonly uint ComponentsField = HashAlgorithms.Fnv1a("components");

    /// <summary>The four fixed colour slots, in the order the engine names them.</summary>
    public enum Slot { Red, Green, Blue, Alpha }

    public static uint SlotField(Slot slot) => slot switch
    {
        Slot.Red => HashAlgorithms.Fnv1a("RedChannel"),
        Slot.Green => HashAlgorithms.Fnv1a("GreenChannel"),
        Slot.Blue => HashAlgorithms.Fnv1a("BlueChannel"),
        Slot.Alpha => HashAlgorithms.Fnv1a("AlphaChannel"),
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public sealed record Result(bool Changed, string Detail);

    /// <summary>
    /// Give the component a texture. Appends an embedded <c>GameplayTextureSampler</c>, replacing any
    /// entry that already carries the same <paramref name="name"/>.
    /// </summary>
    public static byte[]? SetSampler(byte[] materialsBin, string name, string texturePath, out Result result)
    {
        ArgumentNullException.ThrowIfNull(materialsBin);
        result = new Result(false, "");
        if (string.IsNullOrWhiteSpace(texturePath))
        { result = new Result(false, "a sampler needs a texture path"); return null; }

        if (!TryOpen(materialsBin, out var tree, out var component, out string error))
        { result = new Result(false, error); return null; }

        var samplers = component!.Properties.TryGetValue(SamplersField, out var existing)
                       && existing is BinTreeContainer c
            ? c.Elements.ToList()
            : new List<BinTreeProperty>();

        samplers.RemoveAll(e => e is BinTreeStruct s
            && s.Properties.TryGetValue(FieldSamplerName, out var n)
            && n is BinTreeString ns
            && string.Equals(ns.Value, name, StringComparison.OrdinalIgnoreCase));

        // Container elements carry no name hash on the wire, hence NameHash 0.
        samplers.Add(new BinTreeEmbedded(0, SamplerClass, new BinTreeProperty[]
        {
            new BinTreeString(FieldSamplerName, name ?? ""),
            new BinTreeString(FieldTexturePath, texturePath.Trim()),
        }));

        component.Properties[SamplersField] =
            new BinTreeContainer(SamplersField, BinPropertyType.Embedded, samplers);

        result = new Result(true, $"sampler '{name}' -> {texturePath.Trim()} ({samplers.Count} sampler(s) total)");
        return Write(tree!);
    }

    /// <summary>
    /// Create a <c>GameplayTextureChannel</c> as a top-level bin object and link the given slot to it.
    ///
    /// <para>A link stores the target's PATH HASH, so the channel has to be a real object in this bin —
    /// which is exactly why copying a component between bins drops links, and why this cannot be done by
    /// setting a field to a number.</para>
    /// </summary>
    public static byte[]? SetChannel(byte[] materialsBin, Slot slot, string channelName,
        string? soundName, out Result result)
    {
        ArgumentNullException.ThrowIfNull(materialsBin);
        result = new Result(false, "");
        if (string.IsNullOrWhiteSpace(channelName))
        { result = new Result(false, "a channel needs a name"); return null; }

        if (!TryOpen(materialsBin, out var tree, out var component, out string error))
        { result = new Result(false, error); return null; }

        // A stable, collision-checked id derived from the name, so re-running is idempotent rather than
        // minting a fresh orphan every time.
        uint pathHash = HashAlgorithms.Fnv1a($"MapGameplayTextureChannel/{slot}/{channelName.Trim()}");
        while (tree!.Objects.ContainsKey(pathHash) && tree.Objects[pathHash].ClassHash != ChannelClass)
            pathHash++;

        tree.Objects[pathHash] = new BinTreeObject(pathHash, ChannelClass, new BinTreeProperty[]
        {
            new BinTreeString(FieldChannelName, channelName.Trim()),
            new BinTreeString(FieldSoundName, soundName?.Trim() ?? ""),
        });
        component!.Properties[SlotField(slot)] = new BinTreeObjectLink(SlotField(slot), pathHash);

        result = new Result(true, $"{slot}Channel -> GameplayTextureChannel '{channelName.Trim()}' (0x{pathHash:x8})");
        return Write(tree);
    }

    /// <summary>Which of the four slots still point at nothing. A null link is what makes the client fail
    /// with <c>Missing sampler "AlphaMask"</c>.</summary>
    public static IReadOnlyList<Slot> UnlinkedSlots(byte[] materialsBin)
    {
        var missing = new List<Slot>();
        if (!TryOpen(materialsBin, out _, out var component, out _)) return missing;

        foreach (Slot slot in Enum.GetValues<Slot>())
        {
            if (!component!.Properties.TryGetValue(SlotField(slot), out var p)
                || p is not BinTreeObjectLink link || link.Value == 0)
                missing.Add(slot);
        }
        return missing;
    }

    /// <summary>The texture paths the component currently declares.</summary>
    public static IReadOnlyList<(string Name, string TexturePath)> Samplers(byte[] materialsBin)
    {
        var result = new List<(string, string)>();
        if (!TryOpen(materialsBin, out _, out var component, out _)) return result;
        if (!component!.Properties.TryGetValue(SamplersField, out var p) || p is not BinTreeContainer c) return result;

        foreach (var el in c.Elements.OfType<BinTreeStruct>())
            result.Add((
                el.Properties.TryGetValue(FieldSamplerName, out var n) && n is BinTreeString ns ? ns.Value : "",
                el.Properties.TryGetValue(FieldTexturePath, out var t) && t is BinTreeString ts ? ts.Value : ""));
        return result;
    }

    private static bool TryOpen(byte[] bin, out BinTree? tree, out BinTreeStruct? component, out string error)
    {
        tree = null; component = null; error = "";
        try { tree = new BinTree(new MemoryStream(bin, false)); }
        catch (Exception ex) { error = $"bin did not parse: {ex.Message}"; return false; }

        foreach (var (_, obj) in tree.Objects)
        {
            if (obj.ClassHash != ContainerCls) continue;
            if (!obj.Properties.TryGetValue(ComponentsField, out var prop) || prop is not BinTreeContainer comps) continue;
            component = comps.Elements.OfType<BinTreeStruct>()
                .FirstOrDefault(s => s.ClassHash == MapGraphicsFeatures.GameplayTexture.Hash);
            if (component is not null) return true;
        }
        error = "this bin declares no MapGameplayTexture — add the feature first";
        return false;
    }

    private static byte[] Write(BinTree tree)
    {
        var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }
}
