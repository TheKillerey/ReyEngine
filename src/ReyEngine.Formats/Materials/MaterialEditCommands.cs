using ReyEngine.Core.Undo;

namespace ReyEngine.Formats.Materials;

/// <summary>Reversible texture-path edit on a material sampler slot. Same-slot edits merge.</summary>
public sealed class TexturePathEditCommand : IEditorCommand
{
    private readonly TextureSlot _slot;
    private readonly string _oldPath;
    private string _newPath;
    private readonly Action<string>? _onApplied;

    public TexturePathEditCommand(object? context, TextureSlot slot, string oldPath, string newPath, Action<string>? onApplied)
    {
        Context = context;
        _slot = slot;
        _oldPath = oldPath;
        _newPath = newPath;
        _onApplied = onApplied;
    }

    public string Name => $"Edit {_slot.SamplerName}";
    public object? Context { get; }

    public void Execute() { _slot.SetPath(_newPath); _onApplied?.Invoke(_newPath); }
    public void Undo() { _slot.SetPath(_oldPath); _onApplied?.Invoke(_oldPath); }

    public bool CanMergeWith(IEditorCommand next) => next is TexturePathEditCommand t && ReferenceEquals(t._slot, _slot);
    public void MergeWith(IEditorCommand next) => _newPath = ((TexturePathEditCommand)next)._newPath;
}

/// <summary>
/// M493: reversible sampler ADDRESS-MODE edit. Same-axis edits on the same slot merge.
///
/// <para>The value is nullable because ABSENT is a distinct, common state rather than a synonym for 0:
/// censused over the nine shipped map WADs, 4,726 samplers author no address field at all while 8,667
/// author addressW=1 alone (M490). Undo therefore has to be able to put the field back to "not present",
/// which a plain int could not express.</para>
/// </summary>
public sealed class SamplerAddressEditCommand : IEditorCommand
{
    private readonly TextureSlot _slot;
    private readonly TextureSlot.AddressAxis _axis;
    private readonly int? _oldValue;
    private int? _newValue;
    private readonly Action? _onApplied;

    public SamplerAddressEditCommand(object? context, TextureSlot slot, TextureSlot.AddressAxis axis,
        int? oldValue, int? newValue, Action? onApplied)
    {
        Context = context;
        _slot = slot;
        _axis = axis;
        _oldValue = oldValue;
        _newValue = newValue;
        _onApplied = onApplied;
    }

    public string Name => $"Edit {_slot.SamplerName} address{_axis}";
    public object? Context { get; }

    public void Execute() { _slot.SetAddress(_axis, _newValue); _onApplied?.Invoke(); }
    public void Undo() { _slot.SetAddress(_axis, _oldValue); _onApplied?.Invoke(); }

    public bool CanMergeWith(IEditorCommand next) =>
        next is SamplerAddressEditCommand s && ReferenceEquals(s._slot, _slot) && s._axis == _axis;
    public void MergeWith(IEditorCommand next) => _newValue = ((SamplerAddressEditCommand)next)._newValue;
}

/// <summary>Reversible material parameter (vec4 tint etc.) edit. Same-parameter edits merge.</summary>
public sealed class MaterialParamEditCommand : IEditorCommand
{
    private readonly MaterialParameter _param;
    private readonly string _oldText;
    private string _newText;
    private readonly Action<string>? _onApplied;

    public MaterialParamEditCommand(object? context, MaterialParameter param, string oldText, string newText, Action<string>? onApplied)
    {
        Context = context;
        _param = param;
        _oldText = oldText;
        _newText = newText;
        _onApplied = onApplied;
    }

    public string Name => $"Edit {_param.Name}";
    public object? Context { get; }

    public void Execute() { _param.Apply(_newText); _onApplied?.Invoke(_newText); }
    public void Undo() { _param.Apply(_oldText); _onApplied?.Invoke(_oldText); }

    public bool CanMergeWith(IEditorCommand next) => next is MaterialParamEditCommand p && ReferenceEquals(p._param, _param);
    public void MergeWith(IEditorCommand next) => _newText = ((MaterialParamEditCommand)next)._newText;
}

/// <summary>
/// Reversible sampler add/remove. The removed slot keeps its underlying bin element alive, so undo
/// re-inserts the EXACT original element (not a re-clone) — values and schema survive the round trip.
/// </summary>
public sealed class SamplerAddRemoveCommand : IEditorCommand
{
    private readonly MaterialBinding _binding;
    private readonly TextureSlot _slot;
    private readonly bool _isAdd;
    private readonly Action<TextureSlot, bool>? _onApplied;   // (slot, nowPresent) — VM adds/removes its slot row

    public SamplerAddRemoveCommand(object? context, MaterialBinding binding, TextureSlot slot, bool isAdd,
        Action<TextureSlot, bool>? onApplied)
    {
        Context = context;
        _binding = binding;
        _slot = slot;
        _isAdd = isAdd;
        _onApplied = onApplied;
    }

    public string Name => _isAdd ? $"Add Sampler {_slot.SamplerName}" : $"Remove Sampler {_slot.SamplerName}";
    public object? Context { get; }

    public void Execute() => SetPresent(_isAdd);
    public void Undo() => SetPresent(!_isAdd);

    private void SetPresent(bool present)
    {
        bool ok = present ? _binding.ReinsertSampler(_slot) : _binding.RemoveSampler(_slot);
        if (!ok) throw new InvalidOperationException($"Sampler '{_slot.SamplerName}' could not be {(present ? "re-added" : "removed")}.");
        _onApplied?.Invoke(_slot, present);
    }

    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
