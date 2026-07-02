namespace ReyEngine.Core.Selection;

/// <summary>
/// A generic multi-selection set with a primary item (the most-recently-added, used as the anchor for
/// single-item UI). Reference-equality based; raises <see cref="Changed"/> whenever the membership or
/// primary changes. Generic so it lives in Core with no dependency on the mesh types it selects.
/// </summary>
public sealed class SelectionSet<T> where T : class
{
    private readonly List<T> _items = new();

    public IReadOnlyList<T> Items => _items;
    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;
    public bool IsMulti => _items.Count > 1;

    /// <summary>The anchor item (last added / last set). Null when the selection is empty.</summary>
    public T? Primary { get; private set; }

    /// <summary>Raised after any membership or primary change.</summary>
    public event Action? Changed;

    public bool Contains(T item) => _items.Contains(item);

    /// <summary>Replace the whole selection with a single item (normal click).</summary>
    public void SetSingle(T? item)
    {
        if (_items.Count == (item is null ? 0 : 1) && ReferenceEquals(Primary, item)) return;
        _items.Clear();
        if (item is not null) _items.Add(item);
        Primary = item;
        Changed?.Invoke();
    }

    /// <summary>Toggle an item's membership (Ctrl+click). Removing the primary re-anchors to the last remaining.</summary>
    public void Toggle(T item)
    {
        if (_items.Remove(item))
        {
            if (ReferenceEquals(Primary, item)) Primary = _items.Count > 0 ? _items[^1] : null;
        }
        else
        {
            _items.Add(item);
            Primary = item;
        }
        Changed?.Invoke();
    }

    /// <summary>Add an item without removing others (Shift/range add); becomes the primary.</summary>
    public void Add(T item)
    {
        if (_items.Contains(item)) { Primary = item; Changed?.Invoke(); return; }
        _items.Add(item);
        Primary = item;
        Changed?.Invoke();
    }

    /// <summary>Replace the whole selection with the given items (tree multi-select mirror).</summary>
    public void SetMany(IEnumerable<T> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Primary = _items.Count > 0 ? _items[^1] : null;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0 && Primary is null) return;
        _items.Clear();
        Primary = null;
        Changed?.Invoke();
    }
}
