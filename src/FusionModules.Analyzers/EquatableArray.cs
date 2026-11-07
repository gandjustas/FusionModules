using System.Collections;
using System.Collections.Immutable;

namespace FusionModules.Analyzers;

/// <summary>
/// An immutable array that compares by its elements, for a generator pipeline's values: an
/// <see cref="ImmutableArray{T}"/> compares by reference, so a step that rebuilds an equal one is
/// taken for a change and everything after it runs again.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(IEnumerable<T> items) => _items = items.ToImmutableArray();

    /// <summary>The elements; empty for the default value.</summary>
    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public bool IsEmpty => Items.IsEmpty;

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in Items)
        {
            hash = unchecked((hash * 31) + item.GetHashCode());
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
