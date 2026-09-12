using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Characters;

/// <summary>What a file found in a character folder is for.</summary>
public enum CharacterImportRole { Ignored, Mesh, Skeleton, Clip, Texture }

/// <summary>One file the scan found, what it is, and what will happen to it.</summary>
/// <param name="Path">Full path on disk.</param>
/// <param name="Format">Header verdict; a texture's is <see cref="CharacterFileKind.Unknown"/> with its extension as the label.</param>
/// <param name="Note">Why it is ignored, or what the upgrade will do.</param>
public sealed record CharacterImportFile(string Path, string FileName, CharacterFileFormat Format, long Size, CharacterImportRole Role, string? Note)
{
    public string Extension => System.IO.Path.GetExtension(FileName).ToLowerInvariant();
    public bool WillUpgrade => Role is CharacterImportRole.Mesh or CharacterImportRole.Skeleton or CharacterImportRole.Clip && Format.CanUpgrade;
}

/// <summary>A folder read for a character: which files play which role, the name to suggest, and what
/// stands in the way.</summary>
public sealed record CharacterFolderScan(string Folder, string SuggestedName, IReadOnlyList<CharacterImportFile> Files,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public CharacterImportFile? Mesh => Files.FirstOrDefault(f => f.Role == CharacterImportRole.Mesh);
    public CharacterImportFile? Skeleton => Files.FirstOrDefault(f => f.Role == CharacterImportRole.Skeleton);
    public IReadOnlyList<CharacterImportFile> Clips => Files.Where(f => f.Role == CharacterImportRole.Clip).ToList();
    public IReadOnlyList<CharacterImportFile> Textures => Files.Where(f => f.Role == CharacterImportRole.Texture).ToList();
    public bool CanImport => Problems.Count == 0 && Mesh is not null && Skeleton is not null;
}

/// <summary>One file's upgrade, for the log: "golem_idle1.anm: ANM v3 -> ANM v5".</summary>
public sealed record CharacterImportUpgrade(string FileName, string Before, string After);

/// <summary>Everything to stage, and what was done to get there.</summary>
public sealed record CharacterImportResult(CharacterPackage Package, IReadOnlyList<CharacterPackageFile> Files,
    IReadOnlyList<CharacterImportUpgrade> Upgrades, IReadOnlyList<string> Warnings);

/// <summary>
/// M697: an old character folder - skn, skl, anm, a texture - read, checked, upgraded and packaged as
/// a prop the map can place.
///
/// <para>The folder is whatever the user has: a champion folder extracted years ago, a mod's
/// <c>assets/characters/x/skins/base</c>, a loose export. The scan takes every file under it by
/// extension and header, picks the mesh (the one named like the folder, else the largest), the
/// skeleton (the one named like the mesh, else the first), every readable clip, and the texture (a
/// <c>_tx_cm</c> beside the mesh, else the largest), and says why every other file is left out.</para>
///
/// <para>The checks are the ones that decide whether the prop can draw: the mesh's bone indices
/// must exist in the skeleton's influence list (a mismatched pair is a crash or a folded mesh), and a
/// clip must animate at least one joint of THIS skeleton (a clip for another rig plays nothing).
/// The first is a problem; the second only a warning, and the clip is dropped.</para>
/// </summary>
public static class CharacterFolderImporter
{
    public static readonly string[] TextureExtensions = { ".tex", ".dds", ".tga", ".png" };
    private static readonly string[] GenericFolderNames = { "base", "skins", "skin", "animations", "characters", "assets", "data", "particles", "skin0", "skin00", "skin01", "skin02" };

    /// <summary>Read the folder. Never throws on a bad file; a file that cannot be opened is listed as ignored.</summary>
    public static CharacterFolderScan Scan(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder)) return new(folder, "", Array.Empty<CharacterImportFile>(), new[] { "The folder does not exist." }, Array.Empty<string>());

        var found = new List<Found>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool character = ext is ".skn" or ".skl" or ".anm";
            if (!character && !TextureExtensions.Contains(ext)) continue;
            long size;
            CharacterFileFormat format;
            try
            {
                size = new FileInfo(path).Length;
                if (character)
                {
                    var head = new byte[16];
                    using var fs = File.OpenRead(path);
                    int read = fs.Read(head, 0, head.Length);
                    format = CharacterFileFormats.Inspect(head.AsSpan(0, read));
                }
                else format = new CharacterFileFormat(CharacterFileKind.Unknown, ext.TrimStart('.').ToUpperInvariant(), true, ext == ".tex", null);
            }
            catch (Exception ex) { found.Add(new(path, Path.GetFileName(path), ext, 0, new(CharacterFileKind.Unknown, "unreadable", false, false, ex.Message))); continue; }
            found.Add(new(path, Path.GetFileName(path), ext, size, format));
        }

        string suggested = SuggestName(folder, found.Where(f => f.Ext == ".skn").Select(f => f.Name).ToList());
        string stem(string name) => Path.GetFileNameWithoutExtension(name).ToLowerInvariant();

        // the mesh: named like the folder / suggestion, else the largest readable skn
        var meshes = found.Where(f => f.Ext == ".skn" && f.Format.IsReadable).ToList();
        var mesh = meshes.FirstOrDefault(m => stem(m.Name).Equals(suggested, StringComparison.OrdinalIgnoreCase))
                   ?? meshes.OrderByDescending(m => m.Size).FirstOrDefault();
        // the skeleton: named like the mesh, else the first readable skl
        var skeletons = found.Where(f => f.Ext == ".skl" && f.Format.IsReadable).ToList();
        var skeleton = mesh is not null ? skeletons.FirstOrDefault(s => stem(s.Name) == stem(mesh.Name)) : null;
        skeleton ??= skeletons.OrderByDescending(s => s.Size).FirstOrDefault();
        // the texture: a _tx_cm named like the mesh, else any _tx_cm, else the largest
        var textures = found.Where(f => TextureExtensions.Contains(f.Ext)).ToList();
        var texture = textures.FirstOrDefault(t => mesh is not null && stem(t.Name).StartsWith(stem(mesh.Name), StringComparison.Ordinal) && stem(t.Name).Contains("_tx_cm"))
                      ?? textures.FirstOrDefault(t => stem(t.Name).Contains("_tx_cm"))
                      ?? textures.OrderByDescending(t => t.Size).FirstOrDefault();

        var files = new List<CharacterImportFile>();
        foreach (var f in found.OrderBy(f => f.Ext).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            CharacterImportRole role;
            string? note = f.Format.Note;
            if (f.Path == mesh?.Path) { role = CharacterImportRole.Mesh; note = UpgradeNote(f.Format, CharacterFileFormats.CurrentSkinLabel); }
            else if (f.Path == skeleton?.Path) { role = CharacterImportRole.Skeleton; note = UpgradeNote(f.Format, CharacterFileFormats.CurrentSkeletonLabel); }
            else if (f.Ext == ".anm" && f.Format.IsReadable) { role = CharacterImportRole.Clip; note = UpgradeNote(f.Format, CharacterFileFormats.CurrentAnimationLabel); }
            else if (TextureExtensions.Contains(f.Ext)) { role = CharacterImportRole.Texture; note = f.Path == texture?.Path ? (f.Ext == ".tex" ? "the diffuse texture" : "the diffuse texture; converted to .tex") : "another texture; pick it to use it instead"; }
            else if (f.Ext == ".skn") { role = CharacterImportRole.Ignored; note = f.Format.IsReadable ? "another mesh; the one named like the character (else the largest) is used" : note; }
            else if (f.Ext == ".skl") { role = CharacterImportRole.Ignored; note = f.Format.IsReadable ? "another skeleton; the one named like the mesh is used" : note; }
            else { role = CharacterImportRole.Ignored; }
            files.Add(new(f.Path, f.Name, f.Format, f.Size, role, note));
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        if (mesh is null) problems.Add(meshes.Count == 0 && found.Any(f => f.Ext == ".skn") ? "The .skn mesh here cannot be read." : "No .skn mesh in this folder.");
        if (skeleton is null) problems.Add(skeletons.Count == 0 && found.Any(f => f.Ext == ".skl") ? "The .skl skeleton here cannot be read." : "No .skl skeleton in this folder.");
        if (texture is null) warnings.Add("No texture found - the prop will draw white until one is assigned.");
        if (!files.Any(f => f.Role == CharacterImportRole.Clip)) warnings.Add("No readable animation - the prop will stand in its bind pose.");
        return new(folder, suggested, files, problems, warnings);
    }

    private sealed record Found(string Path, string Name, string Ext, long Size, CharacterFileFormat Format);

    private static string? UpgradeNote(CharacterFileFormat format, string current) =>
        !format.IsReadable ? format.Note
        : format.IsCurrent ? "already current"
        : $"upgraded to {current}" + (format.Note is { } n ? $" ({n})" : "");

    /// <summary>A character name from the folder: its own name unless that is a generic one (base,
    /// skins, animations...), then the mesh's file stem, then the nearest non-generic parent. Made valid
    /// for <see cref="CharacterPackageBuilder.ValidateName"/>.</summary>
    public static string SuggestName(string folder, IReadOnlyList<string> meshFileNames)
    {
        var candidates = new List<string>();
        string? dir = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(dir);
        if (!GenericFolderNames.Contains(leaf.ToLowerInvariant())) candidates.Add(leaf);
        else
        {
            // Riot's layout: <character>/skins/base - the first ancestor that is not one of the generic
            // segments is the character
            for (var parent = Path.GetDirectoryName(dir); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                string name = Path.GetFileName(parent);
                if (name.Length == 0) break;
                if (GenericFolderNames.Contains(name.ToLowerInvariant())) continue;
                candidates.Add(name);
                break;
            }
        }
        candidates.AddRange(meshFileNames.Select(Path.GetFileNameWithoutExtension)!);
        foreach (var c in candidates)
            if (Sanitize(c) is { Length: > 0 } s) return s;
        return "NewCharacter";
    }

    /// <summary>Letters, digits and underscores, starting with a letter and an upper-case first letter -
    /// the shape of Riot's own character keys.</summary>
    public static string Sanitize(string raw)
    {
        var chars = raw.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').SkipWhile(c => !char.IsAsciiLetter(c)).Take(64).ToArray();
        if (chars.Length == 0) return "";
        chars[0] = char.ToUpperInvariant(chars[0]);
        return new string(chars);
    }

    /// <summary>The clip name for an .anm file: the character's own prefix stripped ("golem_idle1" ->
    /// "Idle1"), first letter upper-case, the rest as the file spells it.</summary>
    public static string ClipNameFor(string anmFileName, string characterName)
    {
        string stem = Path.GetFileNameWithoutExtension(anmFileName);
        foreach (var prefix in new[] { characterName + "_", characterName.ToLowerInvariant() + "_" })
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && stem.Length > prefix.Length) { stem = stem[prefix.Length..]; break; }
        stem = Sanitize(stem);
        return stem.Length == 0 ? "Clip" : stem;
    }

    /// <summary>Idles, runs, walks, channels and flights loop; attacks, deaths and spells do not.</summary>
    public static bool LoopsByName(string clipName) =>
        new[] { "idle", "run", "walk", "move", "channel", "fly", "float", "swim", "turn" }.Any(k => clipName.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>The clips a scan proposes: one per readable .anm, named from the file, looping by name,
    /// de-duplicated by name (the second "Idle1" becomes "Idle1_2").</summary>
    public static IReadOnlyList<CharacterClipSpec> ProposeClips(CharacterFolderScan scan, string characterName)
    {
        var clips = new List<CharacterClipSpec>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in scan.Clips)
        {
            string name = ClipNameFor(file.FileName, characterName);
            string unique = name;
            for (int i = 2; !used.Add(unique); i++) unique = $"{name}_{i}";
            clips.Add(new CharacterClipSpec(unique, file.FileName, LoopsByName(unique)));
        }
        return clips;
    }

    /// <summary>The spec a scan proposes under <paramref name="name"/>: its mesh, skeleton, the chosen
    /// texture (as a .tex) and <see cref="ProposeClips"/>.</summary>
    public static CharacterPackageSpec Propose(CharacterFolderScan scan, string name, CharacterImportFile? texture = null, float skinScale = 1f)
    {
        if (!scan.CanImport) throw new InvalidOperationException(string.Join(" ", scan.Problems));
        texture ??= scan.Textures.FirstOrDefault(t => t.Note is { } n && n.StartsWith("the diffuse texture", StringComparison.Ordinal)) ?? scan.Textures.FirstOrDefault();
        string textureName = texture is null ? name.ToLowerInvariant() + "_tx_cm.tex" : Path.ChangeExtension(texture.FileName, ".tex");
        return new CharacterPackageSpec(name, scan.Mesh!.FileName, scan.Skeleton!.FileName, textureName, ProposeClips(scan, name), skinScale);
    }

    /// <summary>
    /// Read, check, upgrade and package. <paramref name="decodeImage"/> decodes a format Core cannot
    /// (.png) into RGBA; without it a .png texture is refused. Throws on a problem the scan could not
    /// see (an unreadable file after all, a mesh whose bones the skeleton lacks); a clip that animates
    /// none of the skeleton's joints is dropped with a warning rather than shipped silent.
    /// </summary>
    public static CharacterImportResult Execute(CharacterFolderScan scan, CharacterPackageSpec spec, CharacterImportFile? texture = null,
        Func<byte[], TextureImage?>? decodeImage = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(spec);
        if (!scan.CanImport) throw new InvalidOperationException(string.Join(" ", scan.Problems));
        var upgrades = new List<CharacterImportUpgrade>();
        var warnings = new List<string>(scan.Warnings);

        // the mesh and the skeleton, upgraded, and checked against each other
        var mesh = Upgrade(File.ReadAllBytes(scan.Mesh!.Path), scan.Mesh.FileName, upgrades);
        var skeleton = Upgrade(File.ReadAllBytes(scan.Skeleton!.Path), scan.Skeleton.FileName, upgrades);
        var meshAsset = SkinnedMeshDecoder.Decode(mesh);
        var rig = SkeletonDecoder.Decode(skeleton);
        if (meshAsset.BlendIndices is { } indices && indices.Length > 0)
        {
            int max = indices.Max();
            if (max >= rig.Influences.Count)
                throw new InvalidOperationException($"{scan.Mesh.FileName} skins to bone slot {max} but {scan.Skeleton.FileName} has only {rig.Influences.Count} influence(s) - these two files are not a pair.");
        }

        // the clips, upgraded, each checked to move at least one joint of THIS rig
        var jointHashes = rig.Joints.Select(j => j.AnimHash).ToHashSet();
        var keptClips = new List<CharacterClipSpec>();
        var clipBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in spec.Clips)
        {
            var file = scan.Clips.FirstOrDefault(f => f.FileName.Equals(clip.AnmFileName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"The clip '{clip.Name}' names {clip.AnmFileName}, which the folder does not hold.");
            var bytes = Upgrade(File.ReadAllBytes(file.Path), file.FileName, upgrades);
            var pose = new Dictionary<uint, (System.Numerics.Quaternion, System.Numerics.Vector3, System.Numerics.Vector3)>();
            AnimationDecoder.Decode(bytes, clip.Name).Evaluate(0f, pose);
            int matched = pose.Keys.Count(jointHashes.Contains);
            if (matched == 0)
            {
                warnings.Add($"{file.FileName} animates none of {scan.Skeleton.FileName}'s {rig.Joints.Count} joints - it is for another rig and was left out.");
                continue;
            }
            if (matched < pose.Count / 2)
                warnings.Add($"{file.FileName} animates only {matched} of its {pose.Count} tracks on this skeleton.");
            keptClips.Add(clip);
            clipBytes[clip.Name] = bytes;
        }

        // the texture, as a .tex
        texture ??= scan.Textures.FirstOrDefault(t => Path.ChangeExtension(t.FileName, ".tex").Equals(spec.TextureFileName, StringComparison.OrdinalIgnoreCase))
                    ?? scan.Textures.FirstOrDefault();
        byte[]? tex = null;
        if (texture is not null)
        {
            tex = ToTex(File.ReadAllBytes(texture.Path), texture.Extension, decodeImage);
            if (texture.Extension != ".tex") upgrades.Add(new(texture.FileName, texture.Extension.TrimStart('.').ToUpperInvariant(), "TEX"));
        }
        else warnings.Add("No texture was staged - assign one to the skin bin later.");

        var package = CharacterPackageBuilder.Build(spec with { Clips = keptClips });
        var files = new List<CharacterPackageFile>(package.Bins)
        {
            new(package.SkinPath, mesh),
            new(package.SkeletonPath, skeleton),
        };
        if (tex is not null) files.Add(new(package.TexturePath, tex));
        foreach (var clip in keptClips) files.Add(new(package.ClipPaths[clip.Name], clipBytes[clip.Name]));
        return new(package, files, upgrades, warnings);
    }

    private static byte[] Upgrade(byte[] bytes, string fileName, List<CharacterImportUpgrade> log)
    {
        var upgrade = CharacterFormatUpgrader.Upgrade(bytes);
        if (!upgrade.Before.IsReadable) throw new InvalidOperationException($"{fileName}: {upgrade.Before.Note ?? "cannot be read"}.");
        if (upgrade.Changed) log.Add(new(fileName, upgrade.Before.Label, upgrade.After.Label));
        return upgrade.Bytes;
    }

    /// <summary>A .tex as it is; a block-compressed .dds wrapped without re-encoding when its mips are
    /// whole, else decoded and encoded; a .tga decoded and encoded; anything else through
    /// <paramref name="decodeImage"/>.</summary>
    public static byte[] ToTex(byte[] bytes, string extension, Func<byte[], TextureImage?>? decodeImage)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".tex": return bytes;
            case ".dds":
                if (TexWriter.TryWrapDds(bytes, out var wrapped)) return wrapped;
                return TexWriter.Write(TextureDecoder.Decode(bytes), TexFormat.Bc3, mipmaps: true);
            case ".tga":
                return TexWriter.Write(TextureDecoder.Decode(bytes), TexFormat.Bc3, mipmaps: true);
            default:
                var image = decodeImage?.Invoke(bytes) ?? throw new NotSupportedException($"A {extension} texture cannot be decoded here.");
                return TexWriter.Write(image, TexFormat.Bc3, mipmaps: true);
        }
    }
}
