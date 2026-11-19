using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The cursor form of a value's write and read (#347): written at a reference into free space after its room is
    /// checked (<see cref="MaxSize{T,TFormatter}"/>, <see cref="WriteAt{T,TFormatter}"/>), read from a reference and the
    /// bytes left (<see cref="ReadAt{T,TFormatter}"/>), each returning the bytes it took. A loop over many values
    /// (<see cref="NinoRuns.WriteCursors{TKey,TValue,TKeyFormatter,TValueFormatter}"/>,
    /// <see cref="NinoRuns.ReadCursors{TKey,TValue,TKeyFormatter,TValueFormatter,TStore}"/>) then holds three values,
    /// its data, position and end, in registers, where through the writer or the reader every value would load and
    /// store their fields. One decision per item, folded by the JIT: raw values and blocks read as one
    /// (<see cref="NinoRuns.ScattersItems{T,TFormatter}"/>) through their kind, with the checks a read of them makes;
    /// formatters with <see cref="INinoFormatter.HasCursor"/> (strings in the default encoding and UTF-8, and pairs of
    /// items that have it) through their members, a constrained call; <see cref="None"/>, the missing key of a run
    /// of values in a row, as nothing. Other values have no cursor form: their loops go through the writer and the
    /// reader, value by value.
    /// </summary>
    internal static class NinoCursor
    {
        /// <summary>The key of a run without keys: the element loop is the pair loop with no key.</summary>
        internal struct None
        {
        }

        /// <summary>The formatter of <see cref="None"/>: nothing on the wire.</summary>
        internal struct NoneFormatter : INinoFormatter<None>
        {
            public void Serialize(None value, ref Writer writer)
            {
            }

            public void Deserialize(ref Reader reader, ref None value)
            {
            }
        }

        /// <summary>Whether <typeparamref name="T"/>'s values have the cursor form: a JIT-time constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Has<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            typeof(T) == typeof(None) || NinoRuns.ScattersItems<T, TFormatter>() || Own<T, TFormatter>();

        /// <summary>
        /// Whether <typeparamref name="TFormatter"/> has the cursor form itself (<see cref="INinoFormatter.HasCursor"/>,
        /// <see cref="NinoKind{TFormatter}.Cursor"/>): a JIT-time constant everywhere. The member is internal, so of
        /// the formatters of classes only Nino.Core's two string formatters have it, and those are told by their types:
        /// in code shared over a class, a field of <see cref="NinoKind{TFormatter}"/> is read at run time, even of a
        /// formatter that is not shared, and every collection of strings then compiled the cursor loop, the per-value
        /// loop and its local writer's loop side by side (asm.py: <c>ArrayFormatter</c> of strings 914 to 1,439 bytes,
        /// a generic class's 849 to 1,470). Value types (pairs) read the field only where their kind is per value: a
        /// raw, slot or block collection returns before any read of it, so the field was not initialized when Tier1
        /// compiled the method and stayed a helper call there, with the cursor loop compiled in (asm.py:
        /// <c>HashSet&lt;NinoNullable&lt;int&gt;&gt;</c> 811 to 1,174 bytes).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Own<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            typeof(T).IsValueType
                ? NinoKind.Of<T, TFormatter>() == NinoElementKind.PerValue && NinoKind<TFormatter>.Cursor
                : typeof(TFormatter) == typeof(StringFormatter) || typeof(TFormatter) == typeof(Utf8StringFormatter);

        /// <summary>The most bytes <see cref="WriteAt{T,TFormatter}"/> writes for <paramref name="value"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MaxSize<T, TFormatter>(in T value) where TFormatter : struct, INinoFormatter<T>
        {
            if (typeof(T) == typeof(None))
                return 0;
            if (NinoRuns.ScattersItems<T, TFormatter>())
                return NinoKind.Stride<T, TFormatter>();
            return default(TFormatter).MaxSize(value);
        }

        /// <summary>
        /// Writes <paramref name="value"/> at <paramref name="destination"/> (room for
        /// <see cref="MaxSize{T,TFormatter}"/> bytes) as its formatter's <c>Serialize</c> writes it. Returns the bytes
        /// written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WriteAt<T, TFormatter>(ref byte destination, in T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (typeof(T) == typeof(None))
                return 0;
            if (NinoRuns.ScattersItems<T, TFormatter>())
            {
                NinoBlocks.WriteItem<T, TFormatter>(in value, ref destination);
                return NinoKind.Stride<T, TFormatter>();
            }

            return default(TFormatter).WriteAt(ref destination, value);
        }

        /// <summary>
        /// Reads into <paramref name="value"/> the value at <paramref name="source"/> (<paramref name="available"/>
        /// bytes), as its formatter's <c>Deserialize</c> reads it, with its checks: too few bytes and values no write
        /// makes are invalid data. Returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadAt<T, TFormatter>(ref byte source, int available, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (typeof(T) == typeof(None))
                return 0;
            if (NinoRuns.ScattersItems<T, TFormatter>())
            {
                int size = NinoKind.Stride<T, TFormatter>();
                if ((uint)available < (uint)size)
                    NinoThrow.EndOfData();
                NinoBlocks.ReadItem<T, TFormatter>(ref value, ref source);
                return size;
            }

            return default(TFormatter).ReadAt(ref source, available, ref value);
        }

        /// <summary>
        /// Where <see cref="NinoRuns.ReadCursors{TKey,TValue,TKeyFormatter,TValueFormatter,TStore}"/> reads each pair
        /// and what it does with it, the one thing its collections do differently: read in place
        /// (<see cref="Entries"/>), added one by one to a dictionary (<see cref="Adds"/>), or merged into the pairs of
        /// one read again (<see cref="Merges"/>). The stores are plain structs, not generic, told apart by
        /// <c>typeof</c> in <see cref="KeyAt{TStore}"/>, <see cref="ValueAt{TKey,TValue,TStore}"/> and
        /// <see cref="Stored{TKey,TValue,TStore}"/>: an instance member of a store generic over the key and value was a
        /// call through a run-time method handle per pair in code shared over a class (asm.py, three <c>call r8</c> in
        /// the loop), and no generic interface member is needed (IL2CPP).
        /// </summary>
        internal struct Entries
        {
            /// <summary>The offset of each key from its value (0 without keys).</summary>
            public nint Key;
        }

        /// <summary>
        /// A dictionary that takes its pairs one at a time, each read into one pair of locals (<see cref="Pair"/>, the
        /// run of the loop): inserted where the dictionary is as expected (<see cref="Insert"/>), else added by it and
        /// its key's bucket walked after (<see cref="Walk"/>: hash flooding); a null or repeated key is invalid data.
        /// </summary>
        internal struct Adds
        {
            public nint Key;
            public object Dictionary;
            public bool Insert;
            public bool Walk;
        }

        /// <summary>
        /// A dictionary read again: each pair read into one pair of locals, each key kept in <see cref="Seen"/> (an
        /// array of the keys), each value read over the one the key maps to (kept when the data holds the same, a
        /// string's chars), a key not there before added with its bucket walked. Counts the keys that were there.
        /// </summary>
        internal struct Merges
        {
            public nint Key;
            public object Dictionary;
            public object Seen;
            public int Matched;
            public int Read;
            public bool Exists;
        }

        /// <summary>
        /// One key and value read one pair at a time by <see cref="Adds"/> and <see cref="Merges"/>: <see cref="Run"/>
        /// is their run, the one pair <c>count</c> times over.
        /// </summary>
        internal struct Pair<TKey, TValue>
        {
            public TKey Key;
            public TValue Value;

            /// <summary>
            /// The run of <paramref name="count"/> pairs at <paramref name="pair"/> (0 bytes apart), the value its
            /// entry and the key <see cref="KeyOf"/> bytes from it.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static NinoRun Run(ref Pair<TKey, TValue> pair, int count) =>
                new(ref Unsafe.As<TValue, byte>(ref pair.Value), count, 0, NinoRun.NoKey, NinoRun.AllInUse);

            /// <summary>The offset of the key from the value.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static nint KeyOf(ref Pair<TKey, TValue> pair) => Unsafe.ByteOffset(ref
                Unsafe.As<TValue, byte>(ref pair.Value), ref Unsafe.As<TKey, byte>(ref pair.Key));
        }

        /// <summary>The offset of each key from its value in the store's run.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint KeyAt<TStore>(ref TStore store) where TStore : struct =>
            typeof(TStore) == typeof(Adds) ? Unsafe.As<TStore, Adds>(ref store).Key
            : typeof(TStore) == typeof(Merges) ? Unsafe.As<TStore, Merges>(ref store).Key
            : Unsafe.As<TStore, Entries>(ref store).Key;

        /// <summary>Where the value of the entry at <paramref name="entry"/> is read into, once its key is.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TValue ValueAt<TKey, TValue, TStore>(ref TStore store, ref byte entry, ref TKey key)
            where TStore : struct
        {
#if NET8_0_OR_GREATER
            if (typeof(TStore) == typeof(Merges))
                return ref Merge<TKey, TValue>(ref Unsafe.As<TStore, Merges>(ref store), key);
#else
            if (typeof(TStore) == typeof(Merges))
                Merge(ref Unsafe.As<TStore, Merges>(ref store), key, out Unsafe.As<byte, TValue>(ref entry));
#endif
            return ref Unsafe.As<byte, TValue>(ref entry);
        }

        /// <summary>What the store does with a pair read at <paramref name="key"/> and <paramref name="value"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Stored<TKey, TValue, TStore>(ref TStore store, ref TKey key, ref TValue value)
            where TStore : struct
        {
            if (typeof(TStore) == typeof(Adds))
            {
                Add(ref Unsafe.As<TStore, Adds>(ref store), key, value);
                // The next pair is read into empty places, not compared with this one's strings.
                key = default;
                value = default;
            }
            else if (typeof(TStore) == typeof(Merges))
            {
                ref var merges = ref Unsafe.As<TStore, Merges>(ref store);
                Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(Unsafe.As<TKey[]>(merges.Seen)), merges.Read++) = key;
#if !NET8_0_OR_GREATER
                var dictionary = Unsafe.As<System.Collections.Generic.Dictionary<TKey, TValue>>(merges.Dictionary);
                dictionary[key] = value;
                if (!merges.Exists && HashTables.Plan<TKey, TValue>.Walks)
                    HashTables.Walk(dictionary, key);
                value = default;
#endif
                // The next key is read as new.
                key = default;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Add<TKey, TValue>(ref Adds adds, TKey key, TValue value)
        {
            var dictionary = Unsafe.As<System.Collections.Generic.Dictionary<TKey, TValue>>(adds.Dictionary);
            if (adds.Insert)
            {
                HashTables.Insert(dictionary, key, value);
                return;
            }

            if (key == null)
                NinoThrow.NullKey(dictionary);
            NinoCollections.AddUnique(dictionary, key, value);
            if (adds.Walk)
                HashTables.Walk(dictionary, key);
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref TValue Merge<TKey, TValue>(ref Merges merges, TKey key)
        {
            var dictionary = Unsafe.As<System.Collections.Generic.Dictionary<TKey, TValue>>(merges.Dictionary);
            if (key == null)
                NinoThrow.NullKey(dictionary);
            // Reading the value never touches this dictionary, so the reference stays valid.
            ref TValue slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary,
                key, out bool exists);
            if (exists)
                merges.Matched++;
            else
                Added(dictionary, key);
            return ref slot;
        }
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Merge<TKey, TValue>(ref Merges merges, TKey key, out TValue value)
        {
            var dictionary = Unsafe.As<System.Collections.Generic.Dictionary<TKey, TValue>>(merges.Dictionary);
            if (key == null)
                NinoThrow.NullKey(dictionary);
            merges.Exists = dictionary.TryGetValue(key, out value);
            if (merges.Exists)
                merges.Matched++;
        }
#endif

        // A key a merge added (not there before): its bucket walked (HashTables.Plan), out of line with the plan's
        // lookup, which keys rarely need.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Added<TKey, TValue>(System.Collections.Generic.Dictionary<TKey, TValue> dictionary, TKey key)
        {
            if (HashTables.Plan<TKey, TValue>.Walks)
                HashTables.Walk(dictionary, key);
        }
    }
}
