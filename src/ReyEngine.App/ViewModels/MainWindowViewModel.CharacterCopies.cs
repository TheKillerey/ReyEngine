using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool CopyCharacterReferencesForEdit(WadAssetEntry entry)
    {
        if (Project.ProjectFilePath is null) return false;
        try
        {
            var pending = new Queue<WadAssetEntry>();
            var seen = new HashSet<ulong>();
            pending.Enqueue(entry);
            // Start from the open skin too when the edited asset is a mesh/texture rather than its bin.
            if (MeshPreview.MaterialEditor.BinEntry is { } skin) pending.Enqueue(skin);
            while (pending.TryDequeue(out var asset))
            {
                if (!seen.Add(asset.PathHash)) continue;
                if (RefusesGameDataWrite(asset)) return false;
                var bytes = GetAssetBytes(asset);
                if (asset.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string path in BinValidator.Validate(asset.Path, bytes, Array.Empty<byte[]>(), _ => true).ReferencedPaths)
                        if (TryResolveEntry(HashAlgorithms.WadPath(path), out var dependency)) pending.Enqueue(dependency);
                    // Current-patch texture/animation references are 64-bit links, not path strings.
                    var fields = new Stack<EditableBinField>(BinEditorDocument.Parse(bytes, ResolveBinName, ResolveWadPath).Roots);
                    while (fields.TryPop(out var field))
                    {
                        foreach (var child in field.Children) fields.Push(child);
                        if (field.Kind != BinValueKind.WadPath) continue;
                        ulong hash = field.OriginalText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            && ulong.TryParse(field.OriginalText.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var rawHash)
                            ? rawHash : HashAlgorithms.WadPath(field.OriginalText);
                        if (TryResolveEntry(hash, out var linked)) pending.Enqueue(linked);
                    }
                }
                if (_mounts is { } mounts && mounts.TryGet(asset.PathHash, out var mounted)
                    && mounted.Source.Kind != AssetSourceKind.RiotReference) continue;
                if (_overrides.Has(asset.PathHash)) continue;
                // Some Riot dependency names exceed Windows' per-component filename limit.
                // The existing hash-named override store can carry those assets without changing their WAD path.
                if (asset.Path.Split('/', '\\').Any(part => part.Length > 255)
                    || !TryPlaceInProjectFolder(asset, bytes, out _))
                {
                    var file = ProjectWorkspace.StoreOverrideBytes(Project, asset.PathHash, bytes, Path.GetExtension(asset.Path));
                    _overrides.Set(new ProjectAssetOverride
                    {
                        PathHash = asset.PathHash, ResolvedPath = asset.Path, OverrideFile = file,
                        AddedUtc = DateTime.UtcNow.ToString("o"),
                    });
                }
            }
            Project.IsDirty = true;
            RefreshOverrideMount();
            BuildProjectTree();
            _overrides.SaveTo(Project);
            ReyProjectService.Save(Project, Project.ProjectFilePath);
            _log.Success("Character", "Copied reference assets into the project before saving character edits.");
            return true;
        }
        catch (Exception ex) { _log.Error("Character", ex.Message); return false; }
    }
}
