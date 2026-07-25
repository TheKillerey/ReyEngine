using Avalonia.Controls;

namespace ReyEngine.App.Views;

/// <summary>M171: recolour the open map's textures. Non-modal — a whole-map run takes a while and the
/// user should be able to look at the viewport while it happens.</summary>
public partial class TextureRecolorWindow : Window
{
    public TextureRecolorWindow()
    {
        InitializeComponent();
    }
}
