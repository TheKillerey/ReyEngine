using Avalonia.Controls;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.Views;

public partial class LegacyMapPortWindow : Window
{
    public LegacyMapPortWindow() => InitializeComponent();

    /// <param name="remembered">M600: the project's last completed port, or null to open on defaults.</param>
    public static Task<LegacyMapPortShaderSelection?> ShowAsync(Window owner, LegacyMapPortResult result,
        IReadOnlyList<string> shaderChoices, LegacyDestinationContentSummary destination,
        ReyEngine.Core.Projects.LegacyPortSettings? remembered = null)
    {
        var completion = new TaskCompletionSource<LegacyMapPortShaderSelection?>();
        var viewModel = new LegacyMapPortWindowViewModel(result, shaderChoices, destination, remembered);
        var window = new LegacyMapPortWindow { DataContext = viewModel };
        viewModel.Confirmed = selection => { completion.TrySetResult(selection); window.Close(); };
        viewModel.Cancelled = () => { completion.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => completion.TrySetResult(null);
        window.ShowDialog(owner);
        return completion.Task;
    }
}
