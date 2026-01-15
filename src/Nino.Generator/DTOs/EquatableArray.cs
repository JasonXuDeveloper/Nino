using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Nino.Generator.DTOs;

/// <summary>
/// Immutable array wrapper with value-based equality for use in source generator DTOs.
/// Enables proper incremental caching by implementing structural equality.
/// </summary>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly T[]? _array;

    public EquatableArray(IEnumerable<T>? items)
    {
        _array = items?.ToArray();
    }

    public EquatableArray(params T[] items)
    {
        _array = items;
    }

    public static EquatableArray<T> Empty => new(Array.Empty<T>());

    public int Length => _array?.Length ?? 0;

    public T this[int index] => _array![index];

    public bool Equals(EquatableArray<T> other)
    {
        if (_array is null) return other._array is null;
        if (other._array is null) return false;
        if (_array.Length != other._array.Length) return false;

        for (int i = 0; i < _array.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(_array[i], other._array[i]))
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
        if (_array is null) return 0;

        // Simple hash code calculation compatible with .NET Standard 2.0
        unchecked
        {
            int hash = 17;
            foreach (var item in _array)
            {
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            }
            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        return (_array ?? Array.Empty<T>()).AsEnumerable().GetEnumerator();
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
        return _array is null ? ImmutableArray<T>.Empty : ImmutableArray.Create(_array);
    }

    public T[] ToArray()
    {
        return _array is null ? Array.Empty<T>() : _array.ToArray();
    }
}
