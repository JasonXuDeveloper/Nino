using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>
    /// A nullable value whose memory is always what Nino writes: its presence is a whole <typeparamref name="T"/>
    /// (1 in its first byte when it has a value, zero otherwise) and its value is zero when it has none, so it holds no
    /// padding and no stale bytes. Nino writes that memory everywhere, as it writes any raw struct: a member, an
    /// element of any collection and a field of a raw struct are one copy, with nothing to clear on the way (a
    /// <c>T?</c> run clears the padding and the absent value of each element). It takes two <typeparamref name="T"/>
    /// a value: the bytes of a <c>T?</c> run where <typeparamref name="T"/> is as large as its alignment (the
    /// primitives, enums, <see cref="DateTime"/>, <see cref="TimeSpan"/>), more where it is wider (a
    /// <see cref="decimal"/> takes 32 bytes, a <c>decimal?</c> element 24), and never the bytes of a single
    /// <c>T?</c> (a presence byte, then the value when present): convert between the two with
    /// <see cref="ToNullable"/> and the constructor.
    /// </summary>
    /// <remarks>
    /// A value is clean when it is made by this type's constructors, conversions or <c>default</c>. Memory it was never
    /// assigned in (<see cref="GC.AllocateUninitializedArray{T}"/>, a struct built through <c>Unsafe</c>) is written as
    /// it is. <typeparamref name="T"/> is written as its bytes (a primitive, an enum or a raw struct). Data read in
    /// <see cref="NinoSettings.Untrusted"/> mode is checked: a presence of 0 or 1, an absent value of zeros.
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
            _hasValue = Present;
            _value = value;
        }

        // The presence of a value: a T whose first byte is 1. Made once per T: written per value (a zero, then the
        // byte, then the whole T read back) it stalled every construction on a store the load does not cover.
        private static readonly T Present = MakePresent();

        private static T MakePresent()
        {
            T present = default;
            Unsafe.As<T, byte>(ref present) = 1;
            return present;
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
