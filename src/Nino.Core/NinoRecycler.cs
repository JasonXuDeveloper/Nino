using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Per-type hook invoked when ref deserialization discards an existing instance, so pooled objects
    /// (e.g. a pooled <c>List&lt;T&gt;</c> assigned in a <see cref="NinoRefDeserializationAttribute"/> factory)
    /// can be returned to their pool instead of being leaked.
    /// <para>
    /// An instance is discarded when the serialized data contains <c>null</c> for a member that currently holds
    /// a value, when a collection shrinks, when an array has to be reallocated, or when a polymorphic member is
    /// replaced by an instance of a different runtime type.
    /// </para>
    /// <para>
    /// Handlers are looked up by the declared (static) type of the member. Without one, a
    /// <see cref="NinoPooledAttribute"/> instance goes back to its pool, and anything else to
    /// <see cref="NinoRecycler.Fallback"/>. Only register a handler for objects that are exclusively owned by
    /// the deserialized object graph.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The declared type of the discarded value</typeparam>
    public static class NinoRecycler<T>
    {
        private static Action<T> _handler;

        // Whether a T can be a [NinoPooled] instance: sealed types (string...) that are not pooled never are.
        private static readonly bool MayBePooled = !typeof(T).IsValueType &&
                                                   (typeof(INinoPooled).IsAssignableFrom(typeof(T)) || !typeof(T).IsSealed);

        /// <summary>
        /// Whether a handler is registered for <typeparamref name="T"/> (or a fallback handler exists).
        /// </summary>
        public static bool IsRegistered
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _handler != null || (!typeof(T).IsValueType && NinoRecycler.Fallback != null) ||
                   (MayBePooled && NinoRecycler.PoolsInUse);
        }

        /// <summary>
        /// Registers the handler invoked with discarded instances of <typeparamref name="T"/>.
        /// </summary>
        public static void Register(Action<T> handler) => _handler = handler;

        /// <summary>
        /// Removes the handler registered for <typeparamref name="T"/>.
        /// </summary>
        public static void Unregister() => _handler = null;

        /// <summary>
        /// Passes <paramref name="value"/> to the registered handler (if any). Null values are ignored.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Recycle(T value)
        {
            if (value == null)
                return;

            var handler = _handler;
            if (handler != null)
            {
                handler(value);
                return;
            }

            if (typeof(T).IsValueType)
                return; // the fallback takes objects; boxing a struct to recycle it would only allocate

            if (MayBePooled && value is INinoPooled pooled)
            {
                pooled.ReturnToPool();
                return;
            }

            var fallback = NinoRecycler.Fallback;
            if (fallback != null)
                fallback(value);
        }
    }

    /// <summary>
    /// The fallback handler, and helpers used by formatters to discard values during ref deserialization.
    /// </summary>
    public static class NinoRecycler
    {
        /// <summary>
        /// Handler invoked for discarded instances whose declared type has no handler registered via
        /// <see cref="NinoRecycler{T}.Register"/>. Not called for value types.
        /// </summary>
        public static Action<object> Fallback { get; set; }

        /// <summary>Set once a <see cref="NinoPool{T}"/> holds an instance: dropped objects may then be pooled.</summary>
        internal static bool PoolsInUse;

        /// <summary>
        /// Recycles the current value (if any) and resets it to default.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Discard<T>(ref T value)
        {
            if (value == null)
                return;

            var old = value;
            value = default;
            NinoRecycler<T>.Recycle(old);
        }

        /// <summary>
        /// Recycles every element of <paramref name="values"/> if a handler for <typeparamref name="T"/> exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void RecycleRange<T>(Span<T> values)
        {
            if (!NinoRecycler<T>.IsRegistered)
                return;

            foreach (var item in values)
                NinoRecycler<T>.Recycle(item);
        }

        /// <summary>Recycles the items of a collection that is about to be cleared, if a handler exists.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void RecycleItems<T>(IEnumerable<T> items)
        {
            if (!NinoRecycler<T>.IsRegistered)
                return;

            foreach (var item in items)
                NinoRecycler<T>.Recycle(item);
        }

        /// <summary>Recycles the keys and values of a dictionary that is about to be cleared, if handlers exist.</summary>
        internal static void RecycleItems<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> pairs)
        {
            bool keys = NinoRecycler<TKey>.IsRegistered;
            bool values = NinoRecycler<TValue>.IsRegistered;
            if (!keys && !values)
                return;

            foreach (var pair in pairs)
            {
                if (keys)
                    NinoRecycler<TKey>.Recycle(pair.Key);
                if (values)
                    NinoRecycler<TValue>.Recycle(pair.Value);
            }
        }
    }
}
