using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M830: the editable section of the Material Graph's Details panel. Every row WRAPS the Material Editor's own
/// row view model (<see cref="MaterialParameterViewModel"/>, <see cref="TextureSlotViewModel"/>,
/// <see cref="MaterialSwitchViewModel"/>, <see cref="MaterialBindingViewModel"/>): a value typed here is applied
/// by the very same Apply that the Material tab's box runs, so it lands in the same document, records the same
/// undo step, marks the same dirty flag and reaches the viewport through the same live-preview path. A row owns no
/// copy of a value - it only formats and parses.
/// </summary>
public abstract class GraphEditRow : ObservableObject
{
    public abstract string Heading { get; }

    /// <summary>The Material Editor model object this row edits (null for a note). A row is only reused while the node still resolves to the same one.</summary>
    public virtual object? Wrapped => null;

    /// <summary>Read the value again from the editor's row (after an edit, an undo or a reload).</summary>
    public virtual void Refresh() { }

    /// <summary>Apply whatever was typed (Enter, or the box losing focus).</summary>
    public virtual void Commit() { }
}

/// <summary>A reason, shown where an editor would be: a node the graph cannot edit, and why.</summary>
public sealed class GraphNoteRow : GraphEditRow
{
    public GraphNoteRow(string heading, string text) { Heading = heading; Text = text; }
    public override object? Wrapped => Text;
    public override string Heading { get; }
    public string Text { get; }
}

/// <summary>One number of a vector parameter.</summary>
public sealed partial class GraphComponentField : ObservableObject
{
    private readonly GraphParamEditRow _owner;
    public GraphComponentField(string label, string text, GraphParamEditRow owner) { Label = label; _text = text; _owner = owner; }
    public string Label { get; }
    [ObservableProperty] private string _text;

    // a box that is being removed (the row refreshed under it) loses focus on its way out: that is no edit
    [RelayCommand] private void Commit() { if (_owner.Components.Contains(this)) _owner.Commit(); }
}

/// <summary>A scalar / vector / colour parameter: one box per component, plus a colour picker for a colour.</summary>
public sealed partial class GraphParamEditRow : GraphEditRow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private bool _syncing;

    public GraphParamEditRow(MaterialParameterViewModel parameter, bool colour)
    {
        Parameter = parameter;
        IsColour = colour;
        Refresh();
    }

    public MaterialParameterViewModel Parameter { get; }
    public override object? Wrapped => Parameter;
    public override string Heading => $"{Parameter.Name}  ({Parameter.TypeName})";
    public bool IsEditable => Parameter.IsEditable;
    public string? DrivenNote => Parameter.DrivenNote;
    public bool IsDriven => Parameter.IsDriven;

    public ObservableCollection<GraphComponentField> Components { get; } = new();

    /// <summary>The value does not read as 1 to 4 numbers (a string, a hash, a matrix): one text box instead.</summary>
    [ObservableProperty] private bool _useRaw;
    [ObservableProperty] private string _rawText = "";
    public bool UseComponents => !UseRaw;
    partial void OnUseRawChanged(bool value) => OnPropertyChanged(nameof(UseComponents));

    public bool IsColour { get; }
    [ObservableProperty] private bool _hasPicker;
    [ObservableProperty] private Color _pickerColor;
    [ObservableProperty] private string _hex = "";
    [ObservableProperty] private string _error = "";
    public bool HasError => Error.Length > 0;
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public override void Refresh()
    {
        _syncing = true;
        try
        {
            string text = Parameter.Model.CurrentText;
            var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var values = new float[parts.Length];
            bool numeric = parts.Length is >= 1 and <= 4;
            for (int i = 0; numeric && i < parts.Length; i++)
                numeric = float.TryParse(parts[i], NumberStyles.Float, Inv, out values[i]) && float.IsFinite(values[i]);

            UseRaw = !numeric;
            RawText = text;
            if (!numeric) Components.Clear();
            else
            {
                string[] labels = IsColour && parts.Length >= 3
                    ? new[] { "R", "G", "B", "A" }
                    : parts.Length == 1 ? new[] { "" } : new[] { "X", "Y", "Z", "W" };
                if (Components.Count == parts.Length)
                    for (int i = 0; i < parts.Length; i++) Components[i].Text = parts[i];   // in place: the box being typed in keeps its focus
                else
                {
                    Components.Clear();
                    for (int i = 0; i < parts.Length; i++) Components.Add(new GraphComponentField(labels[i], parts[i], this));
                }
            }

            HasPicker = IsColour && numeric && parts.Length >= 3;
            if (HasPicker)
            {
                PickerColor = ToColor(values[0], values[1], values[2]);
                Hex = ToHex(PickerColor);
            }
            Error = Parameter.HasError ? Parameter.ErrorText : "";
        }
        finally { _syncing = false; }
    }

    private static byte B(float f) => (byte)Math.Clamp((int)Math.Round((float.IsNaN(f) ? 0 : f) * 255f), 0, 255);
    private static Color ToColor(float r, float g, float b) => Color.FromRgb(B(r), B(g), B(b));
    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public override void Commit()
    {
        if (!IsEditable || _syncing) return;
        if (HasPicker && Hex.Trim() != ToHex(PickerColor)) { CommitHex(); return; }   // a typed colour not yet applied
        if (UseRaw) { ApplyText(RawText.Trim()); return; }
        var values = new float[Components.Count];
        for (int i = 0; i < values.Length; i++)
        {
            string t = Components[i].Text.Trim();
            if (!float.TryParse(t, NumberStyles.Float, Inv, out values[i]) || !float.IsFinite(values[i]))
            {
                Error = $"'{t}' is not a number.";
                return;
            }
        }
        ApplyText(string.Join(", ", values.Select(v => v.ToString("R", Inv))));
    }

    [RelayCommand] private void CommitText() => Commit();

    /// <summary>The picker moved: write the colour's three channels, keep the alpha and any channel the picker cannot show.</summary>
    partial void OnPickerColorChanged(Color value)
    {
        if (_syncing || !HasPicker) return;
        SetRgb(value);
    }

    [RelayCommand]
    private void CommitHex()
    {
        string t = Hex.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, Inv, out int rgb))
        {
            Error = $"'{Hex}' is not a colour: write it as #RRGGBB.";
            return;
        }
        SetRgb(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    private void SetRgb(Color c)
    {
        var current = Parameter.Model.CurrentText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (current.Length < 3) return;
        // a channel the picker leaves as it was keeps its exact authored text: only a changed 8-bit value is written
        // back, so a brightness boost above 1 survives a click that did not touch that channel
        byte[] now = new byte[3];
        for (int i = 0; i < 3; i++)
            now[i] = float.TryParse(current[i], NumberStyles.Float, Inv, out float v) ? B(v) : (byte)0;
        byte[] want = { c.R, c.G, c.B };
        if (now.SequenceEqual(want)) { Refresh(); return; }
        for (int i = 0; i < 3; i++)
            if (now[i] != want[i]) current[i] = (want[i] / 255f).ToString("R", Inv);
        ApplyText(string.Join(", ", current));
    }

    private void ApplyText(string text)
    {
        if (string.Equals(text, Parameter.Model.CurrentText, StringComparison.Ordinal)) { Error = ""; Refresh(); return; }
        Parameter.EditedText = text;
        Parameter.ApplyCommand.Execute(null);   // the Material tab's own Apply: validates, records undo, marks dirty, notifies
        Refresh();
    }
}

/// <summary>A texture sample: the path (the Material tab's box), Replace / Open, and the sampler address modes.</summary>
public sealed partial class GraphTextureEditRow : GraphEditRow
{
    public GraphTextureEditRow(TextureSlotViewModel slot) { Slot = slot; _pending = slot.EditedPath; }

    public TextureSlotViewModel Slot { get; }
    public override object? Wrapped => Slot;
    public override string Heading => $"{Slot.SamplerName}  (texture)";

    [ObservableProperty] private string _pending;

    public override void Refresh() => Pending = Slot.EditedPath;

    public override void Commit()
    {
        string path = (Pending ?? "").Trim();
        if (string.Equals(path, Slot.Model.Path, StringComparison.Ordinal)) return;
        Slot.EditedPath = path;
        Slot.ApplyCommand.Execute(null);   // the Material tab's own Apply: SetPath, undo step, thumbnail, notify
        Refresh();
    }

    [RelayCommand] private void CommitPath() => Commit();
}

/// <summary>A switch: one toggle, bound straight to the Material tab's row, so both always show one state.</summary>
public sealed class GraphSwitchEditRow : GraphEditRow
{
    public GraphSwitchEditRow(MaterialSwitchViewModel sw) => Switch = sw;
    public MaterialSwitchViewModel Switch { get; }
    public override object? Wrapped => Switch;
    public override string Heading => $"{Switch.Name}  (switch)";
    public string Hint => "A switch without an explicit 'on' is enabled (M103); Riot writes 'on' only to turn one off. "
                          + "Switching it can select another compiled shader permutation: the graph is rebuilt for it.";
}

/// <summary>The material output: the render-state fields the Material tab edits (cull, blend, blend factors).</summary>
public sealed class GraphRenderStateEditRow : GraphEditRow
{
    public GraphRenderStateEditRow(MaterialBindingViewModel binding) => Binding = binding;
    public MaterialBindingViewModel Binding { get; }
    public override object? Wrapped => Binding;
    public override string Heading => "Render state";
    public bool CanEdit => Binding.CanEditRenderState;
    public string Note => Binding.CanEditRenderState
        ? "Depth test and the write mask are shown but not edited: the Material tab edits neither. AlphaTestValue is a parameter - edit its node."
        : "This material has no technique pass, so it has no render state to edit.";
}
