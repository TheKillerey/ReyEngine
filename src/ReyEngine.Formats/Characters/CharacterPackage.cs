using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Characters;

/// <summary>One clip of the new character's animation graph. <paramref name="AnmFileName"/> is the file's
/// name only; it lives in the character's animations folder.</summary>
public sealed record CharacterClipSpec(string Name, string AnmFileName, bool Loop);

/// <summary>
/// What a character is made of, by file NAME - the builder decides where each file lives and writes
/// those paths into the bins, so the caller stages every file at <see cref="CharacterPackage"/>'s paths.
/// </summary>
/// <param name="Name">The character key: folder, object paths and the record's name. Letters, digits
/// and underscores, starting with a letter - see <see cref="CharacterPackageBuilder.ValidateName"/>.</param>
/// <param name="SkinScale">The game scales the whole model by it; 1 is written as absent, like Riot.</param>
/// <param name="SelfIllumination">Riot writes it on every decorative prop (1 on the camps, 0.7 on the
/// waterwheel, 1.5 on the Yonkey), so it is always written.</param>
public sealed record CharacterPackageSpec(
    string Name,
    string SkinFileName,
    string SkeletonFileName,
    string TextureFileName,
    IReadOnlyList<CharacterClipSpec> Clips,
    float SkinScale = 1f,
    float SelfIllumination = 1f);

public sealed record CharacterPackageFile(string Path, byte[] Bytes);

/// <summary>The three bins and the paths every asset must be staged at.</summary>
/// <param name="CharacterRecord">The record's object path, what a placement's <c>CharacterRecord</c> names.</param>
/// <param name="Skin">The skin's object path, what a placement's <c>Skin</c> names.</param>
/// <param name="IdleClip">The clip a placement should idle with, or null when the graph has none.</param>
/// <param name="Bins">Record, skin and graph bins with their wad paths.</param>
/// <param name="SkinPath">Where the .skn must be staged (a lower-case wad path).</param>
/// <param name="ClipPaths">Where each clip's .anm must be staged, by clip name.</param>
public sealed record CharacterPackage(
    string Name,
    string CharacterRecord,
    string Skin,
    string? IdleClip,
    IReadOnlyList<CharacterPackageFile> Bins,
    string SkinPath,
    string SkeletonPath,
    string TexturePath,
    IReadOnlyDictionary<string, string> ClipPaths)
{
    /// <summary>Every path the package expects to exist in the wad once staged, bins included.</summary>
    public IEnumerable<string> AllPaths => Bins.Select(b => b.Path).Append(SkinPath).Append(SkeletonPath).Append(TexturePath).Concat(ClipPaths.Values);
}

/// <summary>
/// M696: the bins a placed character needs, in the shape Riot's own decorative props have.
///
/// <para><b>Measured, not designed.</b> Every character placed as scenery on Map11 / Map12 / Map453
/// (the poros, the gromp prop, the train, the bloom statue, the waterwheel, the Yonkey) resolves through
/// the same three bins, and the smallest of them - S3Yonkey on the jade map - is the template here:
/// a <c>CharacterRecord</c> with its name and an empty <c>useableData</c>, a
/// <c>SkinCharacterDataProperties</c> whose <c>skinMeshProperties</c> names the skeleton, the mesh, the
/// texture, the scale and the self-illumination, and an <c>animationGraphData</c> whose clips are
/// <c>AtomicClipData</c> on the one "Default" track. The record and the graph are the skin bin's
/// <c>linked</c> dependencies, and the skin's object path is what a placement names.</para>
///
/// <para>Field forms follow the shipped bins to the byte: the mesh and skeleton are <c>string</c>
/// paths in Riot's mixed case, the texture and every clip file are <c>file</c> (wad chunk) links,
/// clips are <c>pointer</c> map values and the track an <c>embed</c>, and a looping clip carries
/// <c>mFlags = 2</c> exactly as the idles and runs of the camps do. The client silently drops a
/// property whose wire form disagrees with its schema, so none of this is negotiable.</para>
/// </summary>
public static class CharacterPackageBuilder
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>The looping flag the shipped idles and runs carry.</summary>
    public const uint LoopFlag = 2;

    /// <summary>Null when the name can be a character key; otherwise why not.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the character a name.";
        if (name.Length > 64) return "The name is longer than 64 characters.";
        if (!char.IsAsciiLetter(name[0])) return "The name must start with a letter.";
        foreach (char c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return "Only letters, digits and underscores - the name becomes a folder and an object path.";
        return null;
    }

    public static string AssetFolder(string name) => $"assets/characters/{name.ToLowerInvariant()}/skins/base";
    public static string RecordBinPath(string name) => $"data/characters/{name.ToLowerInvariant()}/{name.ToLowerInvariant()}.bin";
    public static string SkinBinPath(string name) => $"data/characters/{name.ToLowerInvariant()}/skins/skin0.bin";
    public static string GraphBinPath(string name) => $"data/characters/{name.ToLowerInvariant()}/animations/skin0.bin";
    public static string CharacterRecordPath(string name) => $"Characters/{name}/CharacterRecords/Root";
    public static string SkinObjectPath(string name) => $"Characters/{name}/Skins/Skin0";
    public static string GraphObjectPath(string name) => $"Characters/{name}/Animations/Skin0";

    /// <summary>The clip a placement idles with: Idle1 by name, else the first clip with "idle" in its
    /// name, else the first clip at all.</summary>
    public static string? PickIdle(IReadOnlyList<CharacterClipSpec> clips) =>
        clips.FirstOrDefault(c => c.Name.Equals("Idle1", StringComparison.OrdinalIgnoreCase))?.Name
        ?? clips.FirstOrDefault(c => c.Name.Contains("idle", StringComparison.OrdinalIgnoreCase))?.Name
        ?? clips.FirstOrDefault()?.Name;

    public static CharacterPackage Build(CharacterPackageSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (ValidateName(spec.Name) is { } problem) throw new ArgumentException(problem, nameof(spec));
        foreach (var file in new[] { spec.SkinFileName, spec.SkeletonFileName, spec.TextureFileName }.Concat(spec.Clips.Select(c => c.AnmFileName)))
            if (string.IsNullOrWhiteSpace(file) || file.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new ArgumentException($"'{file}' is not a file name.", nameof(spec));
        if (spec.Clips.Select(c => c.Name.ToLowerInvariant()).Distinct().Count() != spec.Clips.Count)
            throw new ArgumentException("Two clips share a name.", nameof(spec));

        string name = spec.Name;
        string folder = AssetFolder(name);                                  // lower-case, the wad's own form
        string riotFolder = $"ASSETS/Characters/{name}/Skins/Base";          // the mixed case Riot writes in string fields
        string skinPath = $"{folder}/{spec.SkinFileName.ToLowerInvariant()}";
        string skeletonPath = $"{folder}/{spec.SkeletonFileName.ToLowerInvariant()}";
        string texturePath = $"{folder}/{spec.TextureFileName.ToLowerInvariant()}";
        var clipPaths = spec.Clips.ToDictionary(c => c.Name, c => $"{folder}/animations/{c.AnmFileName.ToLowerInvariant()}", StringComparer.OrdinalIgnoreCase);

        string recordPath = CharacterRecordPath(name);
        string skinObject = SkinObjectPath(name);
        string graphObject = GraphObjectPath(name);

        // 1) the record: the Yonkey's - a name and an empty useableData, plus the Meta object every
        //    character bin carries
        var record = new BinTree(new[]
        {
            new BinTreeObject(H(recordPath), H("CharacterRecord"), new BinTreeProperty[]
            {
                new BinTreeString(H("mCharacterName"), name),
                new BinTreeEmbedded(H("useableData"), H("useableData"), new BinTreeProperty[] { new BinTreeU32(H("flags"), 0) }),
            }),
            new BinTreeObject(H($"Characters/{name}/Skins/Meta"), H("SkinCharacterMetaDataProperties"), Array.Empty<BinTreeProperty>()),
        }, Array.Empty<string>());

        // 2) the skin: mesh properties in Riot's field order, linked to the record and the graph
        var meshProps = new List<BinTreeProperty>
        {
            new BinTreeString(H("skeleton"), $"{riotFolder}/{spec.SkeletonFileName}"),
            new BinTreeString(H("simpleSkin"), $"{riotFolder}/{spec.SkinFileName}"),
            new BinTreeWadChunkLink(H("texture"), HashAlgorithms.WadPath(texturePath)),
        };
        if (spec.SkinScale > 0f && Math.Abs(spec.SkinScale - 1f) > 1e-6f) meshProps.Add(new BinTreeF32(H("skinScale"), spec.SkinScale));
        meshProps.Add(new BinTreeF32(H("selfIllumination"), spec.SelfIllumination));
        var skin = new BinTree(new[]
        {
            new BinTreeObject(H(skinObject), H("SkinCharacterDataProperties"), new BinTreeProperty[]
            {
                new BinTreeString(H("championSkinName"), name),
                new BinTreeEmbedded(H("skinAnimationProperties"), H("skinAnimationProperties"), new BinTreeProperty[]
                {
                    new BinTreeObjectLink(H("animationGraphData"), H(graphObject)),
                }),
                new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"), meshProps),
                new BinTreeString(H("emoteBuffbone"), ""),
                new BinTreeString(H("godrayFXbone"), ""),
                new BinTreeHash(H("objectPath"), H(skinObject)),
            }),
        }, new[] { $"DATA/Characters/{name}/{name}.bin", $"DATA/Characters/{name}/Animations/Skin0.bin" });

        // 3) the graph: every clip atomic on the Default track; looping ones flagged like the camps' idles
        var clips = spec.Clips.Select(c =>
        {
            var props = new List<BinTreeProperty>();
            if (c.Loop) props.Add(new BinTreeU32(H("mFlags"), LoopFlag));
            props.Add(new BinTreeHash(H("mTrackDataName"), H("Default")));
            props.Add(new BinTreeEmbedded(H("mAnimationResourceData"), H("AnimationResourceData"), new BinTreeProperty[]
            {
                new BinTreeWadChunkLink(H("mAnimationFilePath"), HashAlgorithms.WadPath(clipPaths[c.Name])),
            }));
            return new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, H(c.Name)), new BinTreeStruct(0, H("AtomicClipData"), props));
        }).ToList();
        var graph = new BinTree(new[]
        {
            new BinTreeObject(H(graphObject), H("animationGraphData"), new BinTreeProperty[]
            {
                new BinTreeMap(H("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct, clips),
                new BinTreeMap(H("mTrackDataMap"), BinPropertyType.Hash, BinPropertyType.Embedded, new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, H("Default")),
                        new BinTreeEmbedded(0, H("TrackData"), Array.Empty<BinTreeProperty>())),
                }),
                new BinTreeHash(H("objectPath"), H(graphObject)),
            }),
        }, Array.Empty<string>());

        return new CharacterPackage(name, recordPath, skinObject, PickIdle(spec.Clips),
            new[]
            {
                new CharacterPackageFile(RecordBinPath(name), Bytes(record)),
                new CharacterPackageFile(SkinBinPath(name), Bytes(skin)),
                new CharacterPackageFile(GraphBinPath(name), Bytes(graph)),
            },
            skinPath, skeletonPath, texturePath, clipPaths);
    }

    private static byte[] Bytes(BinTree tree)
    {
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }
}
