using ReyEngine.Core.Assets;
using ReyEngine.Formats.Materials;

namespace ReyEngine.App.ViewModels;

/// <summary>A material object surfaced as a virtual Content Browser asset (M33): metadata projected from a
/// <see cref="MaterialSummary"/> plus its source .bin and read-only/project state. Clicking one opens the
/// Material Editor on the source bin.</summary>
public sealed class MaterialAssetViewModel : ViewModelBase
{
    public MaterialSummary Summary { get; }
    public WadAssetEntry SourceEntry { get; }
    public bool ReadOnly { get; }

    public MaterialAssetViewModel(MaterialSummary summary, WadAssetEntry source, bool readOnly)
    {
        Summary = summary;
        SourceEntry = source;
        ReadOnly = readOnly;
    }

    public string Name => Summary.ShortName;
    public string FullName => Summary.Name;
    public string Shader => Summary.Shader;
    public string Profile => Summary.ProfileLabel;
    public string Features => Summary.FeatureSummary;
    public int SamplerCount => Summary.SamplerCount;
    public int ParameterCount => Summary.ParameterCount;
    public string? DiffusePath => Summary.DiffusePath;
    public string SourceBin => System.IO.Path.GetFileName(SourceEntry.Path);

    /// <summary>PRJ (editable project material) vs RIOT (read-only reference — Copy To Project to edit).</summary>
    public string Badge => ReadOnly ? "RIOT" : "PRJ";

    public string Tooltip =>
        $"{FullName}\nshader: {Shader}\nprofile: {Profile} ({Features})\n" +
        $"samplers: {SamplerCount} · params: {ParameterCount}\n" +
        $"source: {SourceBin}{(ReadOnly ? "  (read-only reference)" : "")}";
}
