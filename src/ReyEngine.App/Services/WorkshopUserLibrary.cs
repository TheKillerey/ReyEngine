using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.Services;

/// <summary>One effect the user brought into the Workshop themselves, from a file of their own.</summary>
/// <param name="Id">Stable across sessions, so deleting one entry survives a restart and never removes
/// a different one. Derived from the file plus the system, not from the position in the list.</param>
/// <param name="FilePath">The <c>.troybin</c> or <c>.bin</c> on disk. The user's file, read where it is
/// - nothing is copied into a cache, so an entry whose file has moved reports that rather than
/// pretending.</param>
public sealed record WorkshopUserEntry(
    string Id,
    string FilePath,
    string DisplayName,
    string ParticlePath,
    uint SystemHash,
    int Emitters,
    int VisualEmitters,
    string? PreviewTexturePath,
    bool IsLegacy,
    bool IsDecoded,
    DateTime AddedUtc)
{
    public bool FileExists => File.Exists(FilePath);
}

/// <summary>
/// M656: one mesh the user put on the shelf, inside one of their own files.
///
/// <para>A pointer, exactly like <see cref="WorkshopUserEntry"/>: <paramref name="MeshName"/> is looked
/// up in the file again at add time, so editing the source is picked up and a mesh that has been renamed
/// away says so instead of adding the wrong geometry.</para>
/// </summary>
/// <param name="SourceMaterialsBin">For a mapgeo, the bin its ORIGINAL material can be copied out of -
/// remembered here so the shelf keeps what M654 made findable rather than asking again every time.</param>
public sealed record WorkshopUserMesh(
    string Id,
    string FilePath,
    string MeshName,
    string MaterialName,
    int VertexCount,
    int TriangleCount,
    string? SourceMaterialsBin,
    DateTime AddedUtc)
{
    public bool FileExists => File.Exists(FilePath);
    public string Detail => $"{VertexCount:n0} verts  |  {TriangleCount:n0} tris  |  {MaterialName}";
}

/// <summary>
/// M655: the Workshop's own shelf, beside the catalogue harvested out of the installed game.
///
/// <para>The catalogue is a read-only census of what Riot ships, rebuilt from scratch whenever a patch
/// changes the wads' fingerprint — so nothing of the user's can live in it without being thrown away on
/// the next patch day. This is where a <c>.troybin</c> the user found, or the particles inside a custom
/// <c>materials.bin</c> / skin <c>.bin</c> they are working on, are kept instead: a small index that
/// points at their files, is loaded and saved alongside the catalogue cache, and is theirs to delete.</para>
///
/// <para>The files themselves are never copied. An entry is a pointer, so editing the source .bin and
/// re-opening the Workshop shows the edited effect, and a file that has moved says so rather than
/// silently importing something stale.</para>
/// </summary>
public sealed class WorkshopUserLibrary
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    private readonly List<WorkshopUserEntry> _entries = new();
    private readonly List<WorkshopUserMesh> _meshes = new();

    /// <summary>M656: the file grew a second list. Written as an object from now on; an M655 shelf is a
    /// bare array of particles and is still read, so nobody's shelf is lost by upgrading.</summary>
    private sealed record Shelf(List<WorkshopUserEntry>? Particles, List<WorkshopUserMesh>? Meshes);

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReyEngine", "Workshop", "user-library.json");

    /// <summary>Where this shelf is kept. Injectable so a test never touches the real one.</summary>
    public string StorePath { get; }

    public IReadOnlyList<WorkshopUserEntry> Entries => _entries;
    public IReadOnlyList<WorkshopUserMesh> Meshes => _meshes;

    public WorkshopUserLibrary(string? storePath = null)
    {
        StorePath = storePath ?? DefaultStorePath;
        Load();
    }

    private void Load()
    {
        _entries.Clear();
        _meshes.Clear();
        try
        {
            if (!File.Exists(StorePath)) return;
            string text = File.ReadAllText(StorePath);
            if (text.TrimStart().StartsWith('['))
            {
                // the M655 shape: a bare array of particles
                var legacy = JsonSerializer.Deserialize<List<WorkshopUserEntry>>(text, Json);
                if (legacy is not null) _entries.AddRange(legacy.Where(e => e is not null));
                return;
            }
            var read = JsonSerializer.Deserialize<Shelf>(text, Json);
            if (read?.Particles is not null) _entries.AddRange(read.Particles.Where(e => e is not null));
            if (read?.Meshes is not null) _meshes.AddRange(read.Meshes.Where(e => e is not null));
        }
        catch { /* a damaged shelf must not stop the Workshop opening */ }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(new Shelf(_entries, _meshes), Json));
        }
        catch { /* the shelf is a convenience; failing to persist it is not fatal this session */ }
    }

    /// <summary>Read one or more <c>.troybin</c> files. Returns what was added; anything unreadable is
    /// reported in <paramref name="failures"/> by name and reason rather than silently skipped.</summary>
    public IReadOnlyList<WorkshopUserEntry> ScanTroyBins(IEnumerable<string> paths,
        out IReadOnlyList<string> failures)
    {
        var found = new List<WorkshopUserEntry>();
        var bad = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                if (!TroyBinFile.TryParse(File.ReadAllBytes(path), out var troy, out var error))
                { bad.Add($"{Path.GetFileName(path)}: {error}"); continue; }

                // Same emitter count the catalogue uses: the DECODED list when there is one, because the
                // string heuristic over-counts badly (M425 - firetorch_purple has 4 and it reported 11).
                int emitters = troy!.HasDecodedBody ? troy.Emitters.Count : Math.Max(1, troy.EmitterNames.Count);
                string? preview = troy.TexturePaths.FirstOrDefault();
                string name = Path.GetFileNameWithoutExtension(path);
                found.Add(new WorkshopUserEntry(
                    Id: Identity(path, name),
                    FilePath: path,
                    DisplayName: name,
                    ParticlePath: path.Replace('\\', '/'),
                    SystemHash: HashAlgorithms.Fnv1a(path.Replace('\\', '/')),
                    Emitters: emitters,
                    VisualEmitters: troy.TexturePaths.Any() ? emitters : 0,
                    PreviewTexturePath: preview is null ? null : Normalize(preview),
                    IsLegacy: true,
                    IsDecoded: troy.HasDecodedBody,
                    AddedUtc: DateTime.UtcNow));
            }
            catch (Exception ex) { bad.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        failures = bad;
        return found;
    }

    /// <summary>Every VFX system inside a custom <c>.bin</c> — a map's <c>materials.bin</c>, a champion
    /// skin bin, or anything else built the same way. Nothing is added yet: the caller picks.</summary>
    public IReadOnlyList<WorkshopUserEntry> ScanBin(string path, out string? failure)
    {
        failure = null;
        try
        {
            var systems = VfxSystemResolver.ExtractAll(File.ReadAllBytes(path));
            if (systems.Count == 0) { failure = "no VFX systems in this .bin."; return Array.Empty<WorkshopUserEntry>(); }
            return systems.Values.Select(system =>
            {
                string? preview = system.Emitters.SelectMany(e => new[]
                    { e.TexturePath, e.ParticleColorTexturePath, e.TextureMultPath,
                      e.Distortion?.NormalMapTexturePath }).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                string label = string.IsNullOrWhiteSpace(system.Name)
                    ? (string.IsNullOrWhiteSpace(system.ParticlePath) ? $"0x{system.PathHash:x8}" : system.ParticlePath)
                    : system.Name;
                return new WorkshopUserEntry(
                    Id: Identity(path, $"{system.PathHash:x8}"),
                    FilePath: path,
                    DisplayName: label,
                    ParticlePath: system.ParticlePath,
                    SystemHash: system.PathHash,
                    Emitters: system.Emitters.Count,
                    VisualEmitters: system.Emitters.Count(e => e.IsVisual),
                    PreviewTexturePath: preview is null ? null : Normalize(preview),
                    IsLegacy: false,
                    IsDecoded: true,
                    AddedUtc: DateTime.UtcNow);
            }).OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) { failure = ex.Message; return Array.Empty<WorkshopUserEntry>(); }
    }

    /// <summary>
    /// M656: every mesh inside one of the user's mesh files, as shelf candidates. Nothing is added yet.
    ///
    /// <para>Read through <see cref="SceneFileLoader"/>, the same code Add Mesh uses, so a format works
    /// in both windows or in neither. That matters most for a <c>.mapgeo</c>, which is a library of
    /// hundreds - shelving one rock out of Summoner's Rift is the whole point.</para>
    /// </summary>
    public IReadOnlyList<WorkshopUserMesh> ScanMeshFile(string path, out string? failure)
    {
        var loaded = SceneFileLoader.Load(path, out failure);
        if (loaded is null) return Array.Empty<WorkshopUserMesh>();
        if (loaded.Scene.Meshes.Count == 0) { failure = "no drawable mesh in this file."; return Array.Empty<WorkshopUserMesh>(); }

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WorkshopUserMesh>(loaded.Scene.Meshes.Count);
        foreach (var mesh in loaded.Scene.Meshes)
        {
            // A file may name two meshes the same; the shelf must still be able to tell them apart.
            byName.TryGetValue(mesh.Name, out int seen);
            byName[mesh.Name] = seen + 1;
            result.Add(new WorkshopUserMesh(
                Id: Identity(path, seen == 0 ? mesh.Name : $"{mesh.Name}#{seen}"),
                FilePath: path,
                MeshName: mesh.Name,
                MaterialName: mesh.MaterialName,
                VertexCount: mesh.Positions.Length / 3,
                TriangleCount: mesh.Indices.Length / 3,
                SourceMaterialsBin: loaded.SourceMaterialsBin,
                AddedUtc: DateTime.UtcNow));
        }
        return result;
    }

    public (int Added, int Replaced) AddMeshes(IEnumerable<WorkshopUserMesh> meshes)
    {
        int added = 0, replaced = 0;
        foreach (var mesh in meshes)
        {
            int at = _meshes.FindIndex(x => x.Id == mesh.Id);
            if (at >= 0) { _meshes[at] = mesh; replaced++; }
            else { _meshes.Add(mesh); added++; }
        }
        if (added > 0 || replaced > 0) Save();
        return (added, replaced);
    }

    public bool RemoveMesh(string id)
    {
        int removed = _meshes.RemoveAll(x => x.Id == id);
        if (removed > 0) Save();
        return removed > 0;
    }

    /// <summary>Add entries, replacing any that describe the same file and system. Returns how many rows
    /// are new, so "imported 12, replaced 3" can be said honestly.</summary>
    public (int Added, int Replaced) Add(IEnumerable<WorkshopUserEntry> entries)
    {
        int added = 0, replaced = 0;
        foreach (var entry in entries)
        {
            int at = _entries.FindIndex(x => x.Id == entry.Id);
            if (at >= 0) { _entries[at] = entry; replaced++; }
            else { _entries.Add(entry); added++; }
        }
        if (added > 0 || replaced > 0) Save();
        return (added, replaced);
    }

    public bool Remove(string id)
    {
        int removed = _entries.RemoveAll(x => x.Id == id);
        if (removed > 0) Save();
        return removed > 0;
    }

    /// <summary>The shelf as Workshop templates, so the list, the preview and the import path all treat a
    /// user's effect exactly like a shipped one. <c>SourceWad</c> carries the file on disk — see
    /// <see cref="WorkshopCatalogService.ReadBinClosure"/>, which reads a plain file as itself.</summary>
    public IReadOnlyList<WorkshopParticleTemplate> ToTemplates() => _entries
        .Select(e => new WorkshopParticleTemplate(
            e.SystemHash, e.DisplayName, e.ParticlePath,
            SourceBinHash: HashAlgorithms.WadPath(e.FilePath.Replace('\\', '/')),
            SourceBinPath: e.FilePath,
            SourceWad: e.FilePath,
            Emitters: e.Emitters, VisualEmitters: e.VisualEmitters,
            PreviewTexturePath: e.PreviewTexturePath,
            IsLegacy: e.IsLegacy, IsDecoded: e.IsDecoded,
            IsUser: true, UserEntryId: e.Id, UserFileExists: e.FileExists))
        .ToArray();

    private static string Identity(string path, string discriminator) =>
        $"{HashAlgorithms.Fnv1a(path.Replace('\\', '/').ToLowerInvariant()):x8}:{discriminator}";

    private static string Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : path.Trim().Replace('\\', '/').TrimStart('/');
}
