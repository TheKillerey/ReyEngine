using System.Collections.ObjectModel;
using ReyEngine.Core.Build;

namespace ReyEngine.App.ViewModels;

public sealed record FootprintWadRowViewModel(string WadName, int Files, string Size);

public sealed record FootprintSourceRowViewModel(string Folder, int Files, int WadsTouched, string Size);

/// <summary>
/// M134: the Overlay Footprint window — how many game WADs this mod forces loaders to patch,
/// which they are, and which project folders cause the fan-out. Born from the LTK crash: an
/// overlay spanning ~208 WADs killed the game at load; trimming shared-path textures fixed it.
/// </summary>
public sealed class OverlayFootprintWindowViewModel
{
    public required string Summary { get; init; }
    public required string WarningText { get; init; }
    public bool HasWarning => WarningText.Length > 0;
    public ObservableCollection<FootprintWadRowViewModel> Wads { get; } = new();
    public ObservableCollection<FootprintSourceRowViewModel> Sources { get; } = new();

    public static OverlayFootprintWindowViewModel From(OverlayFootprint fp)
    {
        static string Mb(long b) => $"{b / 1048576.0:0.0} MB";
        var vm = new OverlayFootprintWindowViewModel
        {
            Summary = $"{fp.ProjectFiles:n0} packable file(s) ({Mb(fp.ProjectBytes)}) — loaders will patch "
                    + $"{fp.TouchedWads} of {fp.GameWadsScanned} game WADs"
                    + (fp.UnmatchedFiles > 0 ? $" · {fp.UnmatchedFiles:n0} file(s) match no game WAD (new content)" : "") + ".",
            WarningText = fp.TouchedWads > 100
                ? $"⚠ {fp.TouchedWads} WADs is a very wide overlay — this scale has crashed the game via LTK Manager "
                  + "(E_INVALIDARG at load). Trim the biggest fan-out sources below (shared character/item textures "
                  + "drag in every WAD that contains them)."
                : fp.TouchedWads > 40
                    ? $"⚠ {fp.TouchedWads} WADs is a wide overlay — consider trimming the fan-out sources below if the game misbehaves."
                    : "",
        };
        foreach (var w in fp.Wads) vm.Wads.Add(new FootprintWadRowViewModel(w.WadName, w.Files, Mb(w.Bytes)));
        foreach (var s in fp.TopSources.Take(30))
            vm.Sources.Add(new FootprintSourceRowViewModel(s.Folder, s.Files, s.WadsTouched, Mb(s.Bytes)));
        return vm;
    }
}
