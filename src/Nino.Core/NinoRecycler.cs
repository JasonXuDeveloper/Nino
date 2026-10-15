using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Where ref deserialization sends an instance it drops: the handler registered for the declared type
    /// <typeparamref name="T"/> (<see cref="NinoSettings.SetRecycler{T}"/>), else <c>ReturnToPool()</c> for a
    /// <see cref="NinoPooledAttribute"/> instance, else nowhere (the garbage collector takes it).
    /// <para>
    /// Formatters do not call <see cref="Recycle"/> to decide ownership themselves: they go through the primitives
    /// of <see cref="NinoRecycler"/>, which state the ownership rule once.
    /// </para>
    /// <para>
    /// The handler lives in this generic class rather than in <see cref="NinoSettings"/> because it is read on every
    /// recycle: a static field of a generic class is one load per closed type, with no dictionary lookup. Only
    /// register a handler for objects that are exclusively owned by the deserialized object graph.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The declared type of the discarded value</typeparam>
    public static class NinoRecycler<T>
    {
        /// <summary>Set through <see cref="NinoSettings.SetRecycler{T}"/>.</summary>
        internal static Action<T> Handler;

        // Whether a T can be a [NinoPooled] instance: sealed types (string...) that are not pooled never are.
        private static readonly bool MayBePooled = !typeof(T).IsValueType &&
                                                   (typeof(INinoPooled).IsAssignableFrom(typeof(T)) || !typeof(T).IsSealed);

        /// <summary>
        /// Whether <see cref="Recycle"/> may do anything for <typeparamref name="T"/>: a handler is registered, or the
        /// value may be a pooled instance and a pool exists. Collections skip walking dropped elements otherwise.
        /// </summary>
        public static bool IsRegistered
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Handler != null || (MayBePooled && NinoRecycler.PoolsInUse);
        }

        /// <summary>
        /// Sends a dropped <paramref name="value"/> to the registered handler, or back to its pool. Null values are
        /// ignored. Call it once per dropped instance, and only for an instance nothing else took over (see
        /// <see cref="NinoRecycler"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Recycle(T value)
        {
            if (value == null)
                return;

            var handler = Handler;
            if (handler != null)
            {
                handler(value);
                return;
            }

            if (MayBePooled && value is INinoPooled pooled)
                pooled.ReturnToPool();
        }
    }

    /// <summary>
    /// The ownership rules of ref deserialization, in one place. A formatter's <c>Deserialize(ref T value, ...)</c>
    /// owns the instance in <c>value</c> and must end in exactly one of these states for it:
    /// <list type="number">
    /// <item><b>Reused</b>: populated in place and left in the slot. Nothing to recycle.</item>
    /// <item><b>Handed on</b> (<see cref="Reuse{TDeclared,TConcrete}"/>): an interface or base-typed slot is read by
    /// the formatter of a concrete type. If the instance is of that type it is passed on and that formatter owns it
    /// (it reuses or drops it, by these same rules); otherwise it is recycled here, once, before reading.</item>
    /// <item><b>Replaced</b> (<see cref="Replace{T}"/>): a new instance takes the slot (re-created types, tuples,
    /// reallocated arrays); the old one is recycled once, after the new one exists.</item>
    /// <item><b>Discarded</b> (<see cref="Discard{T}"/>): the data holds null; the slot is cleared and the old
    /// instance recycled once.</item>
    /// </list>
    /// Elements a collection drops (it shrinks, it is cleared and refilled, a dictionary key disappears) are each
    /// recycled once as the element type, by the collection formatter, through the <c>DiscardAll</c>,
    /// <c>DiscardPairs</c> and <c>DrainAndDiscard</c> helpers.
    /// An instance read as a back reference (<see cref="Reader.ReadReference{T}"/>) replaces the slot without
    /// recycling: it may still be referenced elsewhere in the graph. Recycling means
    /// <see cref="NinoRecycler{T}.Recycle"/> with the declared type of the slot (or element).
    /// </summary>
    public static class NinoRecycler
    {
        /// <summary>
        /// Set once any <see cref="NinoPool{T}"/> exists: from then on a dropped object of a non-sealed declared type
        /// may be a pooled instance, so collections walk the elements they drop. Not a setting: it only lets
        /// processes that pool nothing skip that walk.
        /// </summary>
        internal static bool PoolsInUse;

        /// <summary>
        /// Discard: the data holds null. Clears <paramref name="value"/> and recycles the instance it held (if any).
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
        /// Replace: stores <paramref name="replacement"/> in <paramref name="value"/> and recycles the instance it
        /// held, unless it is the same one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Replace<T>(ref T value, T replacement) where T : class
        {
            var old = value;
            value = replacement;
            if (old != null && !ReferenceEquals(old, replacement))
                NinoRecycler<T>.Recycle(old);
        }

        /// <summary>
        /// Reuse or recycle: takes the instance out of a <typeparamref name="TDeclared"/> slot for a formatter that
        /// reads a <typeparamref name="TConcrete"/>. Returns it when it is a <typeparamref name="TConcrete"/> (pass it
        /// to that formatter, which now owns it); otherwise recycles it as <typeparamref name="TDeclared"/>, clears
        /// the slot and returns null. Either way the caller must not recycle the old instance again.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TConcrete Reuse<TDeclared, TConcrete>(ref TDeclared value)
            where TDeclared : class
            where TConcrete : class
        {
            var old = value;
            if (old == null)
                return null;
            if (old is TConcrete concrete)
                return concrete;
            value = null;
            NinoRecycler<TDeclared>.Recycle(old);
            return null;
        }

        /// <summary>
        /// Reuse or recycle by exact runtime type, for polymorphic slots: keeps the instance when its type is
        /// <paramref name="type"/> (the body formatter that reads it then owns it), otherwise recycles it and clears
        /// the slot.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ReuseExact<T>(ref T value, Type type) where T : class
        {
            var old = value;
            if (old == null || old.GetType() == type)
                return;
            value = null;
            NinoRecycler<T>.Recycle(old);
        }

        /// <summary>Discards elements a collection drops (the tail it shrinks by): each is recycled once.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void DiscardAll<T>(Span<T> values)
        {
            if (!NinoRecycler<T>.IsRegistered)
                return;

            foreach (var item in values)
                NinoRecycler<T>.Recycle(item);
        }

        /// <summary>
        /// Discards the elements of a collection that is about to be cleared and refilled: each is recycled once. The
        /// caller clears the collection.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void DiscardAll<T>(IEnumerable<T> items)
        {
            if (!NinoRecycler<T>.IsRegistered)
                return;

            foreach (var item in items)
                NinoRecycler<T>.Recycle(item);
        }

        /// <summary>Empties a producer/consumer collection, recycling each element once.</summary>
        internal static void DrainAndDiscard<T>(IProducerConsumerCollection<T> items)
        {
            bool recycle = NinoRecycler<T>.IsRegistered;
            while (items.TryTake(out var item))
            {
                if (recycle)
                    NinoRecycler<T>.Recycle(item);
            }
        }

        /// <summary>
        /// Discards the pairs a dictionary drops (all of them before it is cleared, or the stale ones): each key
        /// and value is recycled once. The caller removes them.
        /// </summary>
        internal static void DiscardPairs<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> pairs)
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
