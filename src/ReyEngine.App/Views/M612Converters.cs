using Avalonia.Data.Converters;

namespace ReyEngine.App.Views;

public static class M612Converters
{
    /// <summary>An action with no animation in this skin is dimmed rather than hidden. 15 of 174
    /// champions have no Q clip at all and 48 have no Recall, so a row that vanished would read as
    /// "this champion has no Q" — which is the opposite of what the data says.</summary>
    public static readonly IValueConverter ClipOpacity =
        new FuncValueConverter<bool, double>(hasClip => hasClip ? 1.0 : 0.4);
}
