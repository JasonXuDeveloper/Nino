using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>
    /// A nullable value whose memory is always what Nino writes: its presence is a whole <typeparamref name="T"/>
    /// (1 in its first byte when it has a value, zero otherwise) and its value is zero when it has none, so it holds no
    /// padding and no stale bytes. Nino writes it exactly as <c>T?</c> (either reads the other's data). An array or a
    /// <see cref="List{T}"/> of it is written and read as one block copy where <typeparamref name="T"/> is as large as
    /// its alignment (the primitives, enums, <see cref="DateTime"/>, <see cref="TimeSpan"/>), where <c>T?[]</c> clears the
    /// padding and the absent values of each element on the way. Use it for collections of nullable values written
    /// often; <c>T?</c> writes the same bytes everywhere else.
    /// </summary>
    /// <remarks>
    /// A value is clean when it is made by this type's constructors, conversions or <c>default</c>. Memory it was never
    /// assigned in (<see cref="GC.AllocateUninitializedArray{T}"/>, a struct built through <c>Unsafe</c>) is written as
    /// it is. <typeparamref name="T"/> is written as its bytes (a primitive, an enum or a raw struct).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct NinoNullable<T> : IEquatable<NinoNullable<T>> where T : unmanaged
    {
        // A whole T, so the value starts at T's size, a multiple of its alignment: no padding between them.
        private readonly T _hasValue;
        private readonly T _value;

        /// <summary>A value that has <paramref name="value"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoNullable(T value)
        {
            T present = default;
            Unsafe.As<T, byte>(ref present) = 1;
            _hasValue = present;
            _value = value;
        }

        /// <summary>Whether it has a value.</summary>
        public bool HasValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.As<T, byte>(ref Unsafe.AsRef(in _hasValue)) != 0;
        }

        /// <summary>The value; throws <see cref="InvalidOperationException"/> when there is none.</summary>
        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (!HasValue)
                    ThrowNoValue();
                return _value;
            }
        }

        /// <summary>The value, or the default of <typeparamref name="T"/> when there is none.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetValueOrDefault() => _value;

        /// <summary>The value, or <paramref name="defaultValue"/> when there is none.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetValueOrDefault(T defaultValue) => HasValue ? _value : defaultValue;

        /// <summary>The same value as a <c>T?</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T? ToNullable() => HasValue ? _value : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator NinoNullable<T>(T value) => new(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator NinoNullable<T>(T? value) =>
            value.HasValue ? new NinoNullable<T>(value.GetValueOrDefault()) : default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator T?(NinoNullable<T> value) => value.ToNullable();

        /// <summary>The value; throws <see cref="InvalidOperationException"/> when there is none.</summary>
        public static explicit operator T(NinoNullable<T> value) => value.Value;

        public bool Equals(NinoNullable<T> other) => HasValue
            ? other.HasValue && EqualityComparer<T>.Default.Equals(_value, other._value)
            : !other.HasValue;

        public override bool Equals(object obj) => obj switch
        {
            NinoNullable<T> other => Equals(other),
            T value => HasValue && EqualityComparer<T>.Default.Equals(_value, value),
            null => !HasValue,
            _ => false,
        };

        public override int GetHashCode() => HasValue ? _value.GetHashCode() : 0;

        public override string ToString() => HasValue ? _value.ToString() : "";

        public static bool operator ==(NinoNullable<T> left, NinoNullable<T> right) => left.Equals(right);

        public static bool operator !=(NinoNullable<T> left, NinoNullable<T> right) => !left.Equals(right);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNoValue() =>
            throw new InvalidOperationException("Nino: the NinoNullable has no value.");
    }
}
