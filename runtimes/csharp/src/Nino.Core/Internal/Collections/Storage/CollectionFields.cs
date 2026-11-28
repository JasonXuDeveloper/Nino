using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The fields of the runtime's collections at the offsets <see cref="CollectionViews"/> found; use each only when
    /// its flag there is set.
    /// </summary>
    internal static class CollectionFields
    {
        /// <summary>An entry of <see cref="HashSet{T}"/> (HashCode, Next, Value; hashCode, next, value
        /// elsewhere).</summary>
        internal struct SetEntry<T>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public int HashCode;
            public int Next;
            public T Value;
#pragma warning restore CS0649

            /// <summary>Where an entry keeps its value, in bytes from its start: a JIT-time constant, also in code
            /// shared over a class (its fields' offsets are known there).</summary>
            public static int ValueOffset
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    SetEntry<T> entry = default;
                    return Distance(ref Unsafe.As<SetEntry<T>, byte>(ref entry), ref Unsafe.As<T, byte>(ref entry.Value));
                }
            }
        }

        /// <summary>
        /// An entry of <see cref="Dictionary{TKey,TValue}"/> as the runtime lays it out (hashCode, next, key, value; the
        /// startup checks of <see cref="CollectionViews"/> compare it with the runtime's).
        /// </summary>
        internal struct MapEntry<TKey, TValue>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public int HashCode;
            public int Next;
            public TKey Key;
            public TValue Value;
#pragma warning restore CS0649

            /// <summary>Where an entry keeps its key, as <see cref="SetEntry{T}.ValueOffset"/>.</summary>
            public static int KeyOffset
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    MapEntry<TKey, TValue> entry = default;
                    return Distance(ref Unsafe.As<MapEntry<TKey, TValue>, byte>(ref entry),
                        ref Unsafe.As<TKey, byte>(ref entry.Key));
                }
            }

            /// <summary>Where an entry keeps its value, as <see cref="SetEntry{T}.ValueOffset"/>.</summary>
            public static int ValueOffset
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    MapEntry<TKey, TValue> entry = default;
                    return Distance(ref Unsafe.As<MapEntry<TKey, TValue>, byte>(ref entry),
                        ref Unsafe.As<TValue, byte>(ref entry.Value));
                }
            }
        }

        /// <summary>The byte distance from <paramref name="from"/> to <paramref name="to"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Distance(ref byte from, ref byte to) => (int)Unsafe.ByteOffset(ref from, ref to);

        /// <summary>
        /// The fields of <see cref="KeyValuePair{TKey,TValue}"/> (private: <c>key</c>, then <c>value</c>) as a struct of
        /// the same shape lays them out, which every runtime lays out by the same rules (<see cref="PairLayout.Mirrors"/>
        /// proves it on pairs of references and values).
        /// </summary>
        internal struct PairMirror<TKey, TValue>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public TKey Key;
            public TValue Value;
#pragma warning restore CS0649
        }

        /// <summary>
        /// Whether <see cref="PairMirror{TKey,TValue}"/> reads a <see cref="KeyValuePair{TKey,TValue}"/> (these pairs are
        /// the startup proof RuntimeTests reads).
        /// </summary>
        internal static class PairLayout
        {
            public static readonly bool Mirrors = ObjectTypes.Passes(() =>
                Mirror("k", 5L) && Mirror(5L, "v") && Mirror("k", "v") && Mirror((byte)3, new object()) &&
                Mirror(7, (short)-2));

            private static bool Mirror<TKey, TValue>(TKey key, TValue value)
            {
                var pair = new KeyValuePair<TKey, TValue>(key, value);
                ref var mirror = ref Unsafe.As<KeyValuePair<TKey, TValue>, PairMirror<TKey, TValue>>(ref pair);
                return Unsafe.SizeOf<PairMirror<TKey, TValue>>() == Unsafe.SizeOf<KeyValuePair<TKey, TValue>>() &&
                       EqualityComparer<TKey>.Default.Equals(mirror.Key, key) &&
                       EqualityComparer<TValue>.Default.Equals(mirror.Value, value);
            }
        }

        /// <summary>Where a <see cref="KeyValuePair{TKey,TValue}"/> keeps its key and value (-1: not known).</summary>
        internal static class PairLayout<TKey, TValue>
        {
            private static PairMirror<TKey, TValue> s_pair;

            public static readonly int Key = !RuntimeHelpers.IsReferenceOrContainsReferences<KeyValuePair<TKey, TValue>>()
                ? NinoBlocks.Find(Unsafe.SizeOf<TKey>(),
                    () => new KeyValuePair<TKey, TValue>(NinoBlocks.Marked<TKey>(), default))
                : PairLayout.Mirrors ? NinoBlocks.Offset(ref s_pair, ref s_pair.Key) : -1;

            public static readonly int Value = !RuntimeHelpers.IsReferenceOrContainsReferences<KeyValuePair<TKey, TValue>>()
                ? NinoBlocks.Find(Unsafe.SizeOf<TValue>(),
                    () => new KeyValuePair<TKey, TValue>(default, NinoBlocks.Marked<TValue>()))
                : PairLayout.Mirrors ? NinoBlocks.Offset(ref s_pair, ref s_pair.Value) : -1;
        }

        /// <summary>
        /// Whether the entries of sets and dictionaries of values are blocks on this runtime: their items mapped at the
        /// offsets measured in the entries (<see cref="NinoSetEntryFormatter{T,TFormatter}"/>'s and
        /// <see cref="NinoMapEntryFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>'s block maps), so the block kernels move a run of them. The startup proof RuntimeTests reads: a runtime that lays an
        /// entry out so that an item overlaps another field, or is not found, moves its entries one at a time.
        /// </summary>
        internal static class EntryBlocks
        {
            public static readonly bool Mapped = ObjectTypes.Passes(() =>
                Set<int>() && Set<long>() && Set<Guid>() && Map<int, int>() && Map<long, short>() &&
                Map<short, long>() && Map<byte, long>() && Map<int, Guid>());

            private static bool Set<T>() where T : unmanaged =>
                NinoBlockPlan<SetEntry<T>, NinoSetEntryFormatter<T, UnmanagedFormatter<T>>>.Mapped;

            private static bool Map<TKey, TValue>() where TKey : unmanaged where TValue : unmanaged =>
                NinoBlockPlan<MapEntry<TKey, TValue>, NinoMapEntryFormatter<TKey, TValue, UnmanagedFormatter<TKey>,
                    UnmanagedFormatter<TValue>>>.Mapped;
        }

        /// <summary>
        /// The <see cref="NinoRun.InUse"/> of a run of a set's or a dictionary's entries with free entries, the one rule
        /// for both: by how the runtime marks a free entry (<see cref="CollectionViews.SetFree"/>, <see cref="CollectionViews.MapFree"/>) and where the entry
        /// at <paramref name="first"/> has its hash code and link. The offsets are the distances from the entry to its
        /// fields, which the JIT folds to constants, written by each run with the entry's own fields: a field of a class
        /// of its own, which a run never initialized (sets whose entries were all in use), was a helper call when Tier1
        /// compiled the write and kept the run and the writer's cursor on the stack (asm.py: <c>HashSet&lt;int&gt;</c>'s
        /// write 40 to 57 stack accesses), and an entry's fields reached through a struct of their own were calls in code shared over a class
        /// (its <c>Run</c> 247 to 378 bytes).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int InUse(CollectionViews.FreeEntries free, ref byte first, ref int hashCode, ref int next) =>
            InUseAt(free, Distance(ref first, ref Unsafe.As<int, byte>(ref hashCode)),
                Distance(ref first, ref Unsafe.As<int, byte>(ref next)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int InUseAt(CollectionViews.FreeEntries free, int hashCode, int next) => free switch
        {
            CollectionViews.FreeEntries.Next => NinoRun.InUseAt(next, -1),
            CollectionViews.FreeEntries.HashCode => NinoRun.InUseAt(hashCode, 0),
            _ => NinoRun.AllInUse,
        };

        /// <summary>
        /// The run of <paramref name="set"/>'s entries (<paramref name="count"/> of them in use, not 0), each the element
        /// (<see cref="NinoSetEntryFormatter{T,TFormatter}"/>), in enumeration order, the free ones skipped; false where the runtime's layout,
        /// or how it frees entries, is not known (enumerate it then). A loop over an array, where the enumerator checks
        /// the version per element.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Run<T>(HashSet<T> set, int count, out NinoRun run)
        {
            run = default;
            if (!CollectionViews.HashSet)
                return false;
            int bound = ObjectFields.At<int>(set, CollectionViews.HashSetBound);
            var entries = Unsafe.As<SetEntry<T>[]>(ObjectFields.At<object>(set, CollectionViews.HashSetEntries));
            if (entries == null || bound > entries.Length || bound < count ||
                bound != count && CollectionViews.SetFree == CollectionViews.FreeEntries.Unknown)
                return false;
            ref var first = ref entries[0];
            run = new NinoRun(ref Unsafe.As<SetEntry<T>, byte>(ref first), bound, Unsafe.SizeOf<SetEntry<T>>(),
                NinoRun.NoKey, bound == count ? NinoRun.AllInUse : InUse(CollectionViews.SetFree,
                    ref Unsafe.As<SetEntry<T>, byte>(ref first), ref first.HashCode, ref first.Next));
            return true;
        }

        /// <summary>
        /// As <see cref="Run{T}(HashSet{T}, int, out NinoRun)"/>, for a dictionary: the run of its entries, each the
        /// element (<see cref="NinoMapEntryFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>). The check is the set's, written out again: a helper of both,
        /// generic over the entry, was not inlined in code shared over a class, and its out values kept a dictionary's
        /// write in memory (asm.py: a generated body holding a Dictionary&lt;string, string&gt; 26 to 56 stack
        /// accesses).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Run<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count, out NinoRun run)
        {
            run = default;
            if (!CollectionViews.Dictionary)
                return false;
            int bound = ObjectFields.At<int>(dictionary, CollectionViews.DictionaryBound);
            var entries = Unsafe.As<MapEntry<TKey, TValue>[]>(
                ObjectFields.At<object>(dictionary, CollectionViews.DictionaryEntries));
            if (entries == null || bound > entries.Length || bound < count ||
                bound != count && CollectionViews.MapFree == CollectionViews.FreeEntries.Unknown)
                return false;
            ref var first = ref entries[0];
            run = new NinoRun(ref Unsafe.As<MapEntry<TKey, TValue>, byte>(ref first), bound,
                Unsafe.SizeOf<MapEntry<TKey, TValue>>(), NinoRun.NoKey,
                bound == count ? NinoRun.AllInUse : InUse(CollectionViews.MapFree,
                    ref Unsafe.As<MapEntry<TKey, TValue>, byte>(ref first), ref first.HashCode, ref first.Next));
            return true;
        }

        /// <summary>
        /// The run of the first <paramref name="count"/> (not 0) of a new set's or dictionary's
        /// <paramref name="entries"/>, which a read fills, every one in use.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoRun Run<TEntry>(TEntry[] entries, int count) =>
            new(ref Unsafe.As<TEntry, byte>(ref entries[0]), count, Unsafe.SizeOf<TEntry>(), NinoRun.NoKey,
                NinoRun.AllInUse);

        /// <summary>
        /// The <see cref="List{T}"/> that <paramref name="collection"/> wraps (what its constructors without a list
        /// make, and <c>ObservableCollection</c>'s), or null when it wraps another <see cref="IList{T}"/>. Call where
        /// <see cref="CollectionViews.Collection"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static List<T> List<T>(System.Collections.ObjectModel.Collection<T> collection)
        {
            object items = ObjectFields.At<object>(collection, CollectionViews.CollectionItems);
            return items != null && items.GetType() == typeof(List<T>) ? Unsafe.As<List<T>>(items) : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Items<T>(List<T> list) => ref ObjectFields.At<T[]>(list, CollectionViews.ListItems);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(List<T> list) => ref ObjectFields.At<int>(list, CollectionViews.ListSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Array<T>(Stack<T> stack) =>
            ref ObjectFields.At<T[]>(stack, CollectionViews.StackArray);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(Stack<T> stack) => ref ObjectFields.At<int>(stack, CollectionViews.StackSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(Stack<T> stack) =>
            ref ObjectFields.At<int>(stack, CollectionViews.StackVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Array<T>(Queue<T> queue) =>
            ref ObjectFields.At<T[]>(queue, CollectionViews.QueueArray);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Head<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueHead);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Tail<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueTail);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(Queue<T> queue) =>
            ref ObjectFields.At<int>(queue, CollectionViews.QueueVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TKey[] Keys<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<TKey[]>(list, CollectionViews.SortedListKeys);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TValue[] Values<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<TValue[]>(list, CollectionViews.SortedListValues);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.SortedListSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.SortedListVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref IComparer<TKey> Comparer<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<IComparer<TKey>>(list, CollectionViews.SortedListComparer);

        /// <summary>The root node of the set's red-black tree (an internal type).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref object Root<T>(SortedSet<T> set) => ref ObjectFields.At<object>(set, CollectionViews.SortedSetRoot);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Count<T>(SortedSet<T> set) => ref ObjectFields.At<int>(set, CollectionViews.SortedSetCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(SortedSet<T> set) =>
            ref ObjectFields.At<int>(set, CollectionViews.SortedSetVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref LinkedListNode<T> Head<T>(LinkedList<T> list) =>
            ref ObjectFields.At<LinkedListNode<T>>(list, CollectionViews.LinkedListHead);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Count<T>(LinkedList<T> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.LinkedListCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(LinkedList<T> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.LinkedListVersion);

        /// <summary>
        /// <see cref="LinkedListNode{T}"/>'s fields, in its order on every known implementation (checked by
        /// <see cref="CollectionViews"/>): the list, then the next and previous nodes (a ring: the last node's next is
        /// the first), then the item.
        /// </summary>
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        internal sealed class LinkedNode<T>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public LinkedList<T> list;
            public LinkedListNode<T> next;
            public LinkedListNode<T> prev;
            public T item;
#pragma warning restore CS0649
        }

        /// <summary>
        /// Links <paramref name="node"/> (new) into <paramref name="list"/> after <paramref name="last"/> (null: as the
        /// first). The list's own fields are set at the end, by the caller and <see cref="Close{T}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Append<T>(LinkedList<T> list, LinkedListNode<T> node, LinkedListNode<T> last)
        {
            var m = Unsafe.As<LinkedNode<T>>(node);
            m.list = list;
            if (last != null)
            {
                m.prev = last;
                Unsafe.As<LinkedNode<T>>(last).next = node;
            }
        }

        /// <summary>Closes the ring of nodes <see cref="Append{T}"/> linked.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Close<T>(LinkedListNode<T> first, LinkedListNode<T> last)
        {
            Unsafe.As<LinkedNode<T>>(first).prev = last;
            Unsafe.As<LinkedNode<T>>(last).next = first;
        }

        /// <summary>The set of the dictionary's pairs, ordered by key.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SortedSet<KeyValuePair<TKey, TValue>> Set<TKey, TValue>(
            SortedDictionary<TKey, TValue> dictionary) =>
            ObjectFields.At<SortedSet<KeyValuePair<TKey, TValue>>>(dictionary, CollectionViews.SortedDictionarySet);
    }
}
