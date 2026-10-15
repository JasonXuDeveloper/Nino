using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Nino.Generator.Common;

/// <summary>
/// Immutable array wrapper with value-based equality for use in source generator DTOs.
/// Enables proper incremental caching by implementing structural equality.
/// <para>
/// The items are always copied in, so no caller can mutate them afterwards. <c>default</c> is the same value as
/// <see cref="Empty"/>.
/// </para>
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly T[]? _array;

    public EquatableArray(IEnumerable<T>? items)
    {
        _array = items?.ToArray();
    }

    public static EquatableArray<T> Empty => default;

    private T[] Items => _array ?? Array.Empty<T>();

    public int Length => _array?.Length ?? 0;

    public T this[int index] => Items[index];

    public bool Equals(EquatableArray<T> other)
    {
        var items = Items;
        var otherItems = other.Items;
        if (items.Length != otherItems.Length) return false;

        for (int i = 0; i < items.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(items[i], otherItems[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is EquatableArray<T> other && Equals(other);
    }

    public override int GetHashCode()
    {
        // Simple hash code calculation compatible with .NET Standard 2.0; empty and default hash alike.
        unchecked
        {
            int hash = 17;
            foreach (var item in Items)
            {
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            }
            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        return Items.AsEnumerable().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right)
    {
        return !left.Equals(right);
    }

    public static implicit operator EquatableArray<T>(T[] array)
    {
        return new EquatableArray<T>(array);
    }

    public static implicit operator EquatableArray<T>(ImmutableArray<T> array)
    {
        return new EquatableArray<T>(array);
    }

    public ImmutableArray<T> ToImmutableArray()
    {
        return ImmutableArray.Create(Items);
    }

    public T[] ToArray()
    {
        return Items.ToArray();
    }
}
