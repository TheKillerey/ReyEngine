using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReyEngine.App.Views;

/// <summary>M74: tiny modal prompt — <see cref="ConfirmAsync"/> for yes/no, <see cref="InputAsync"/> for text.</summary>
public partial class PromptWindow : Window
{
    private bool _ok;

    public PromptWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { OnOk(null, null!); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { OnCancel(null, null!); e.Handled = true; }
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e) { _ok = true; Close(); }
    private void OnCancel(object? sender, RoutedEventArgs e) { _ok = false; Close(); }

    /// <summary>Yes/no confirmation. Returns true when the user confirmed.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string okLabel = "OK")
    {
        var win = new PromptWindow { Title = title };
        win.MessageText.Text = message;
        win.OkButton.Content = okLabel;
        await win.ShowDialog(owner);
        return win._ok;
    }

    /// <summary>Single-line text input. Returns the entered text, or null when cancelled.</summary>
    public static async Task<string?> InputAsync(Window owner, string title, string message, string initial = "", string okLabel = "OK")
    {
        var win = new PromptWindow { Title = title };
        win.MessageText.Text = message;
        win.OkButton.Content = okLabel;
        win.InputBox.IsVisible = true;
        win.InputBox.Text = initial;
        win.Opened += (_, _) => { win.InputBox.Focus(); win.InputBox.SelectAll(); };
        await win.ShowDialog(owner);
        return win._ok ? win.InputBox.Text : null;
    }
}
