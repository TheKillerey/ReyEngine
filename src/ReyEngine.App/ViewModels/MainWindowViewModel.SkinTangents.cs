using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M758: bake tangents into a skin's .skn, for PBR skins with normal maps and RMA textures - LTK Manager
/// 1.21's "Bake tangents", and byte for byte the same file (see <see cref="SknTangentBaker"/>). From the
/// asset tree's context menu on any .skn, or from the character window for the skin it shows. A Riot
/// file is written into the project, as every other edit is; a project file is rewritten in place.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private bool CanBakeSkinTangents(AssetNodeViewModel? node) =>
        ProjectMode && node?.Entry is { } e && e.DisplayName.EndsWith(".skn", StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanBakeSkinTangents))]
    private async Task BakeSkinTangents(AssetNodeViewModel? node)
    {
        if (node?.Entry is { } entry) await BakeSkinTangentsAsync(entry);
    }

    /// <summary>The character window's button: the skin it is showing, then the skin reloaded.</summary>
    private async Task BakePreviewSkinTangentsAsync()
    {
        if (_previewSkn is not { } entry) { _log.Warn("Tangents", "Open a skin in the character window first."); return; }
        if (!ProjectMode) { _log.Warn("Tangents", "Open a project first - the baked mesh is written into it."); return; }
        string? bin = _previewSkinBin;
        if (await BakeSkinTangentsAsync(entry)) await LoadMeshPreviewAsync(entry, bin);
    }

    private async Task<bool> BakeSkinTangentsAsync(WadAssetEntry entry)
    {
        if (!ProjectMode || Project.RootPath is null) { _log.Warn("Tangents", "Open a project first - the baked mesh is written into it."); return false; }
        string name = entry.DisplayName;
        try
        {
            byte[] source = GetAssetBytes(entry);
            var (bytes, result) = await Task.Run(() =>
            {
                var baked = SknTangentBaker.Bake(source, out var r);
                // it must still read where the rest of the editor reads it
                SkinnedMeshDecoder.Decode(baked);
                return (baked, r);
            });

            string savedTo;
            if (TryWriteToProjectFile(entry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".skn");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = entry.PathHash,
                    ResolvedPath = entry.IsResolved ? entry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            int split = result.VerticesAfter - result.VerticesBefore;
            _log.Success("Tangents", $"{name}: MikkTSpace tangents baked ({result.From} -> {result.To} vertices, "
                + $"{result.VerticesAfter:n0} vertices{(split > 0 ? $", {split:n0} split where corners needed different tangents" : "")}, "
                + $"{result.Ranges} submesh(es)) -> {Path.GetFileName(savedTo)}.");
            _log.Info("Tangents", "Bake normal maps in MikkTSpace (Substance and Blender do by default) against this same mesh; "
                + "the tangent w is stored for League's flipped V.");
            return true;
        }
        catch (SknTangentBaker.BakeException ex)
        {
            // measured: about 9% of Riot's own skins carry a vertex with a zero normal, and LTK Manager
            // refuses exactly those, at the same vertex
            _log.Error("Tangents", $"{name}: {ex.Message}. LTK Manager's bake refuses this mesh too; fix the mesh and try again.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("Tangents", $"{name}: {ex.Message}");
            return false;
        }
    }
}
