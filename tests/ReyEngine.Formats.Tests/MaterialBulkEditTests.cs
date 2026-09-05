using System.Globalization;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Undo;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M645: bulk editing of materials. The shared inspector's arithmetic is pure over the material bindings,
/// so it is asserted against a real skin bin: what a pick has in common, that an edit writes every
/// material that has the field as ONE undo step, that a value one material rejects rolls the whole batch
/// back, and that the editor's rows drive it.
/// </summary>
public sealed class MaterialBulkEditTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private sealed record Fixture(MaterialDocument Doc, byte[] Bin, Core.Assets.WadAssetEntry Entry, WadArchive Archive) : IDisposable
    {
        public IReadOnlyList<MaterialBinding> Statics => Doc.Materials.Where(m => m.IsStaticMaterialDef).ToList();
        public void Dispose() => Archive.Dispose();
    }

    /// <summary>Ahri's base skin bin: several StaticMaterialDefs sharing sampler and parameter names.</summary>
    private static Fixture? Ahri()
    {
        string wad = Path.Combine(Final, "Champions", "Ahri.wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return null;
        var archive = WadArchive.Open(wad, new WadPathResolver(database));
        ulong binHash = HashAlgorithms.WadPath("data/characters/ahri/skins/skin0.bin");
        if (!archive.TryGetEntry(binHash, out var entry)) { archive.Dispose(); return null; }
        byte[] bin = archive.Extract(binHash);
        var doc = MaterialDocument.Parse(bin,
            h => database.TryGetBinName(h, out var n) ? n : null,
            h => database.TryGetPath(h, out var p) ? p : null);
        if (doc.Materials.Count(m => m.IsStaticMaterialDef) < 2) { archive.Dispose(); return null; }
        return new Fixture(doc, bin, entry, archive);
    }

    /// <summary>A texture the hash database resolves, other than <paramref name="not"/>. M590: skin samplers are
    /// 64-bit chunk links, so a made-up path writes fine but reads back as hex - the round trip needs a
    /// path the database knows.</summary>
    private static string Replacement(Fixture f, IEnumerable<string> not)
    {
        var avoid = not.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return f.Archive.Entries
            .Where(e => e.IsResolved && e.Path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                        && e.Path.Contains("/skins/skin", StringComparison.OrdinalIgnoreCase) && !avoid.Contains(e.Path))
            .Select(e => e.Path).OrderBy(x => x, StringComparer.Ordinal).First();
    }

    /// <summary>A sampler name at least two of the materials have.</summary>
    private static string? SharedSampler(IReadOnlyList<MaterialBinding> materials) =>
        materials.SelectMany(m => m.Slots.Select(s => s.SamplerName))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2).Select(g => g.Key).FirstOrDefault();

    // ===================================================== shared vs mixed

    [Fact]
    public void APickKnowsWhatItsMaterialsHaveInCommon()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var materials = f.Statics;
            var summary = MaterialBulkEdit.Summarize(materials);
            Assert.Equal(materials.Count, summary.Count);

            // The shader is shared or mixed exactly as the distinct count says.
            int shaders = materials.Select(m => m.RenderShader ?? m.ShaderName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Assert.Equal(shaders > 1, summary.ShaderMixed);
            if (shaders == 1) Assert.NotNull(summary.Shader);

            // Every sampler name any material has is a row, with the count of materials that have it.
            foreach (var field in summary.Samplers)
            {
                int present = materials.Count(m => m.Slots.Any(s => s.SamplerName.Equals(field.Name, StringComparison.OrdinalIgnoreCase)));
                Assert.Equal(present, field.PresentOn);
                var paths = materials.SelectMany(m => m.Slots.Where(s => s.SamplerName.Equals(field.Name, StringComparison.OrdinalIgnoreCase)).Select(s => s.Path)).Distinct().ToList();
                Assert.Equal(paths.Count > 1, field.Mixed);
                if (!field.Mixed) Assert.Equal(paths[0], field.Shared);
            }
            Assert.Equal(materials.Count(m => m.CanEditRenderState), summary.CanEditRenderState);
        }
    }

    // ===================================================== one undo step

    [Fact]
    public void ASamplerPathWritesEveryMaterialThatHasItAsOneStep()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var materials = f.Statics;
            string? sampler = SharedSampler(materials);
            if (sampler is null) return;
            var having = materials.Where(m => m.Slots.Any(s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase))).ToList();
            var originals = having.ToDictionary(m => m, m => m.Slots.First(s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase)).Path);
            int applied = 0;
            var undo = new UndoRedoService();
            string replacement = Replacement(f, originals.Values);

            var command = MaterialBulkEdit.SetSamplerPath(materials, sampler, replacement, context: f.Doc, onApplied: _ => applied++);
            Assert.Equal(having.Count, command.Count);
            foreach (var m in having)
                Assert.Equal(replacement, m.Slots.First(s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase)).Path, ignoreCase: true);
            // Materials without the sampler were not given one.
            foreach (var m in materials.Except(having))
                Assert.DoesNotContain(m.Slots, s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase));
            Assert.True(f.Doc.IsDirty);

            undo.PushApplied(command);
            Assert.True(undo.Undo());
            foreach (var (m, path) in originals)
                Assert.Equal(path, m.Slots.First(s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase)).Path);
            Assert.False(f.Doc.IsDirty);
            Assert.True(applied >= having.Count * 2);   // the rows were told on the way out and on the way back

            // Writing the value they already have is not a step: only the materials whose path differs move.
            var same = MaterialBulkEdit.SetSamplerPath(having, sampler, originals[having[0]], f.Doc, null);
            Assert.Equal(having.Count(m => !string.Equals(originals[m], originals[having[0]], StringComparison.Ordinal)), same.Count);
        }
    }

    [Fact]
    public void AParameterOneMaterialRejectsRollsTheWholeBatchBack()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var materials = f.Statics;
            // A numeric parameter at least two materials share.
            string? name = materials.SelectMany(m => m.Parameters.Where(p => p.IsEditable))
                .Where(p => float.TryParse(p.CurrentText.Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2).Select(g => g.Key).FirstOrDefault();
            if (name is null) return;
            var before = materials.ToDictionary(m => m, m => m.Parameters.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.CurrentText).ToList());

            Assert.Throws<InvalidOperationException>(() => MaterialBulkEdit.SetParameter(materials, name, "not a number", f.Doc, null));
            foreach (var (m, texts) in before)
                Assert.Equal(texts, m.Parameters.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.CurrentText).ToList());
            Assert.False(f.Doc.IsDirty);
        }
    }

    [Fact]
    public void RenderStateAndTogglesWriteEveryMaterialAndUndoRestores()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var materials = f.Statics;
            var stateful = materials.Where(m => m.CanEditRenderState).ToList();
            if (stateful.Count >= 2)
            {
                var cullBefore = stateful.ToDictionary(m => m, m => m.GetPassBool("cullEnable", whenAbsent: true));
                var cull = MaterialBulkEdit.SetPassBool(materials, "cullEnable", false, whenAbsent: true, f.Doc, null);
                Assert.All(stateful, m => Assert.False(m.GetPassBool("cullEnable", whenAbsent: true)));
                Assert.Equal(cullBefore.Count(kv => kv.Value), cull.Count);   // only the ones that were culling changed
                cull.Undo();
                foreach (var (m, was) in cullBefore) Assert.Equal(was, m.GetPassBool("cullEnable", whenAbsent: true));

                var srcBefore = stateful.ToDictionary(m => m, m => m.GetPassU32("srcColorBlendFactor"));
                var src = MaterialBulkEdit.SetBlendFactor(materials, "src", 6, f.Doc, null);
                Assert.All(stateful, m => Assert.Equal(6, m.GetPassU32("srcColorBlendFactor")));
                Assert.All(stateful, m => Assert.Equal(6, m.GetPassU32("srcAlphaBlendFactor")));   // colour and alpha together (M415)
                src.Undo();
                foreach (var (m, was) in srcBefore) Assert.Equal(was, m.GetPassU32("srcColorBlendFactor"));
            }

            string? sw = materials.SelectMany(m => m.AllSwitches.Select(s => s.Name))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2).Select(g => g.Key).FirstOrDefault();
            if (sw is not null)
            {
                var having = materials.Where(m => m.AllSwitches.Any(s => s.Name.Equals(sw, StringComparison.OrdinalIgnoreCase))).ToList();
                var wasOn = having.ToDictionary(m => m, m => m.AllSwitches.First(s => s.Name.Equals(sw, StringComparison.OrdinalIgnoreCase)).On);
                var on = MaterialBulkEdit.SetSwitch(materials, sw, true, f.Doc, null);
                Assert.All(having, m => Assert.True(m.AllSwitches.First(s => s.Name.Equals(sw, StringComparison.OrdinalIgnoreCase)).On));
                Assert.Equal(wasOn.Count(kv => !kv.Value), on.Count);
                on.Undo();
                foreach (var (m, was) in wasOn) Assert.Equal(was, m.AllSwitches.First(s => s.Name.Equals(sw, StringComparison.OrdinalIgnoreCase)).On);
            }
        }
    }

    // ===================================================== the editor's rows

    [Fact]
    public void TheEditorsRowsDriveTheBatchAndUndoIsOneStep()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService() };
            editor.Load(f.Doc, f.Entry, f.Bin);
            var statics = editor.Materials.Where(m => m.Model.IsStaticMaterialDef).ToList();
            string? sampler = SharedSampler(statics.Select(m => m.Model).ToList());
            if (sampler is null) return;
            var two = statics.Where(m => m.Model.Slots.Any(s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase))).Take(2).ToList();

            Assert.False(editor.IsBulk);
            editor.BulkSelection.Add(two[0]);
            Assert.False(editor.IsBulk);                     // one is the single editor's job
            editor.BulkSelection.Add(two[1]);
            Assert.True(editor.IsBulk);
            Assert.Contains("2 materials", editor.BulkStatus);

            var row = editor.BulkSamplers.First(r => r.Name.Equals(sampler, StringComparison.OrdinalIgnoreCase));
            string replacement = Replacement(f, two.Select(m => m.Slots.First(s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase)).EditedPath));
            row.EditedText = replacement;
            row.ApplyCommand.Execute(null);

            foreach (var m in two)
            {
                var slot = m.Slots.First(s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(replacement, slot.Model.Path, ignoreCase: true);
                Assert.Equal(replacement, slot.EditedPath, ignoreCase: true);   // the single rows followed
                Assert.True(m.IsDirty);
            }
            Assert.True(editor.IsDirty);
            Assert.Equal(replacement, row.ValueLabel, ignoreCase: true);   // shared again, on the new value
            Assert.True(editor.UndoService!.CanUndo);

            Assert.True(editor.UndoService.Undo());
            Assert.False(editor.UndoService.CanUndo);        // ONE step held both materials
            foreach (var m in two)
                Assert.NotEqual(replacement, m.Slots.First(s => s.SamplerName.Equals(sampler, StringComparison.OrdinalIgnoreCase)).EditedPath);
            Assert.False(editor.IsDirty);

            // A parameter that will not parse leaves the row an error instead of a half-written batch.
            var numeric = editor.BulkParameters.FirstOrDefault(r => !r.Mixed && float.TryParse(r.ValueLabel.Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            if (numeric is not null)
            {
                numeric.EditedText = "nonsense";
                numeric.ApplyCommand.Execute(null);
                Assert.True(numeric.HasError);
                Assert.False(editor.IsDirty);
            }

            editor.BulkSelection.Clear();
            Assert.False(editor.IsBulk);
        }
    }

    // ===================================================== the wiring is where it says it is

    [Fact]
    public void TheSharedControlHostsThePickerAndTheBulkInspector()
    {
        var view = Source("src", "ReyEngine.App", "Views", "MaterialEditorView.axaml");
        if (view is null) return;
        Assert.Contains("SelectedItems=\"{Binding BulkSelection}\"", view);
        Assert.Contains("IsVisible=\"{Binding IsBulk}\"", view);
        Assert.Contains("<ContentControl Content=\"{Binding SelectedMaterial}\" IsVisible=\"{Binding !IsBulk}\">", view);
        Assert.Contains("ItemsSource=\"{Binding BulkSamplers}\"", view);
        Assert.Contains("ItemsSource=\"{Binding BulkParameters}\"", view);
        Assert.Contains("ItemsSource=\"{Binding BulkSwitches}\"", view);
        Assert.Contains("ItemsSource=\"{Binding BulkMacros}\"", view);
        Assert.Contains("Command=\"{Binding ApplyBulkShaderCommand}\"", view);
    }
}
