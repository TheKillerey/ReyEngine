using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M834: the Material Editor side of a Shader Graph's custom parameters. A rename in the graph reaches every material that names this graph
/// (REY_GRAPH = the graph's number) in the document the Material Editor holds, through the editor's own
/// <see cref="ParameterRenameCommand"/>s pushed as ONE composite undo step on the editor's stack. All the renames are applied first and the command is
/// pushed afterwards, inside a try: a failure puts every renamed parameter back. The graph's undo is the graph's: undoing the rename there renames the
/// parameters back (a further editor step), so the two never disagree about the name.
/// </summary>
public sealed partial class MaterialGraphViewModel
{
    /// <summary>The non-linked materials of the Material Editor's document that name the graph saved at <paramref name="graphPath"/>.</summary>
    private List<MaterialBindingViewModel> MaterialsOfGraph(string? graphPath, out string? problem)
    {
        problem = null;
        var list = new List<MaterialBindingViewModel>();
        var editor = _editor;
        if (graphPath is null) { problem = "the graph is not saved yet, so no material can name it"; return list; }
        if (editor is null) { problem = "no Material Editor is attached, so materials were not renamed"; return list; }
        string? root = editor.ProjectRoot?.Invoke();
        if (root is null) { problem = "no project is open, so materials were not renamed"; return list; }
        SgNumberEntry? entry;
        try { entry = ShaderGraphRegistry.FindByFile(root, ShaderGraphRegistry.StemOf(graphPath)); }
        catch (Exception ex) { problem = "the graph registry cannot be read (" + ex.Message + ")"; return list; }
        if (entry is null) { problem = "no material was assigned to this graph yet"; return list; }
        foreach (var mb in editor.Materials)
        {
            if (mb.IsLinked) continue;
            var macro = mb.Model.AllMacros.FirstOrDefault(m => SgShip.IsGraphMacro(m.Name));
            if (macro is null || !SgShipValidator.TryParseNumber(macro.Value, out int n) || n != entry.Number) continue;
            if (SgBase.Normalize(mb.Model.RenderShader ?? "") != SgBase.Normalize(entry.Base)) continue;
            list.Add(mb);
        }
        return list;
    }

    public IReadOnlyList<string> MaterialsCarryingCustomParameter(string? graphPath, string name) =>
        MaterialsOfGraph(graphPath, out _).Where(mb => mb.Model.Parameters.Any(p => p.Name == name)).Select(mb => mb.Name).ToList();

    internal (int Renamed, string Note) RenameCustomParameterInMaterials(string? graphPath, string from, string to)
    {
        var editor = _editor;
        var mats = MaterialsOfGraph(graphPath, out string? problem);
        if (editor is null || mats.Count == 0) return (0, problem is null ? "No material of the open Material Editor document is assigned to this graph." : "(" + problem + ")");

        var applied = new List<(MaterialBindingViewModel Mb, MaterialParameter P)>();
        var skipped = new List<string>();
        var commands = new List<IEditorCommand>();
        try
        {
            foreach (var mb in mats)
            {
                var p = mb.Model.Parameters.FirstOrDefault(x => x.Name == from);
                if (p is null) continue;                                                       // this material never carried it
                if (mb.Model.Parameters.Any(x => x.Name == to)) { skipped.Add($"'{mb.Name}' (it already has a '{to}')"); continue; }
                if (!mb.Model.RenameParameter(p, to)) { skipped.Add($"'{mb.Name}' (the rename was refused)"); continue; }
                applied.Add((mb, p));
                var binding = mb;
                commands.Add(new ParameterRenameCommand(editor.DocContext, mb.Model, p, from, to, () => SyncRows(editor, binding)));
            }
            if (commands.Count > 0)
                editor.UndoService?.PushApplied(commands.Count == 1 ? commands[0] : new CompositeCommand($"Rename parameter {from} to {to}", commands, editor.DocContext));
        }
        catch
        {
            // nothing half-renamed: every parameter that was renamed goes back
            foreach (var (mb, p) in applied) { try { mb.Model.RenameParameter(p, from); } catch { /* the parameter is gone: nothing to put back */ } }
            foreach (var (mb, _) in applied) SyncRows(editor, mb);
            throw;
        }
        foreach (var (mb, _) in applied) SyncRows(editor, mb);
        string note = skipped.Count > 0 ? $"Not renamed: {string.Join(", ", skipped)}: those still carry '{from}', which no shaders.bin declares after this rename, so the export is blocked until you fix them." : "";
        if (applied.Count == 0 && skipped.Count == 0) note = $"No assigned material carries '{from}'.";
        return (applied.Count, note);
    }
}
