using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if !NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// Fills a new <see cref="HashSet{T}"/> or <see cref="Dictionary{TKey,TValue}"/> by writing its entries and buckets
    /// directly, as its Add would, without the checks Add repeats per element (the comparer, the free list, the
    /// capacity, the version): a read fills the entries through their run (<see cref="NinoRuns.ReadSet{T,TFormatter}"/>,
    /// a set's values or a dictionary's pairs: raw values and blocks scattered by the block kernels, others value by
    /// value), then <see cref="Link{T}(HashSet{T}, int)"/> hashes them and links the buckets in one pass (#320, #355).
    /// The collection is created and sized through its public API first (its constructor or EnsureCapacity allocate the
    /// buckets and entries and set the fast-mod multiplier); only the entries, the buckets and the count are written
    /// here, at the offsets <see cref="CollectionViews"/> found. The hashing differs between implementations, and not
    /// along the lines of their fields (Mono's dictionary has .NET Core's fields and buckets but masks hash codes to 31
    /// bits as .NET Framework's does): the bucket is .NET's fast modulo where the table keeps a multiplier (64-bit .NET
    /// 5+), else the remainder; whether hash codes are masked and what a bucket holds for its first entry (its index +
    /// 1, empty 0; or its index, empty -1) are found at startup, as the first of the four that a collection filled here
    /// passes the public API's checks with. None: the formatters add element by element.
    /// <para>
    /// One layer for both (#355): a set and a dictionary are the same table, their entries told apart by where the key
    /// is (the set's value, the dictionary's key) and by the null policy (a set holds null, hashed as 0; a dictionary's
    /// null key is invalid data). The members generic over the collection are thin forwards to it.
    /// </para>
    /// <para>
    /// Hash flooding: every key linked or added here walks its bucket, as Add would, and more than
    /// <see cref="NinoSettings.MaxHashCollisions"/> keys in it is invalid data (keys built to collide would make
    /// reading quadratic). Keys that are not raw are added here one at a time (<see cref="Insert{TKey,TValue}"/>)
    /// where entries with references and string keys pass the same startup checks; where the runtime adds them (a
    /// collection that is not new, or not sized for the data, a runtime whose layout or hashing was not found), the
    /// bucket of each key it added is walked afterwards (<see cref="Added{TKey,TValue}"/>), except for string keys of
    /// the runtimes that protect those tables themselves (randomized comparer after 100 collisions).
    /// </para>
    /// </summary>
    internal static partial class HashTables
    {
        /// <summary>The fields of a table at the offsets <see cref="CollectionViews"/> found (0, and no multiplier,
        /// where none were).</summary>
        private readonly struct Fields
        {
            public readonly int Entries, Bound, Buckets, FastMod, Count;

            public Fields(CollectionViews.HashOffsets? at)
            {
                Entries = at?.Entries ?? 0;
                Bound = at?.Bound ?? 0;
                Buckets = at?.Buckets ?? 0;
                FastMod = at?.FastMod ?? -1;
                Count = at?.Count ?? 0;
            }
        }

        private static readonly CollectionViews.HashOffsets? SetAt = CollectionViews.HashSetOffsets;
        private static readonly CollectionViews.HashOffsets? MapAt = CollectionViews.DictionaryOffsets;
        private static readonly Fields SetFields = new(SetAt);
        private static readonly Fields MapFields = new(MapAt);

        // The hashing (mask and bias) each passed its check with, -1 for none.
        private static readonly int SetHashing = SetAt == null ? -1 : FindHashing(CheckSet);
        private static readonly int SetMask = Mask(SetHashing), SetBias = Bias(SetHashing);
        private static readonly int MapHashing = MapAt == null ? -1 : FindHashing(CheckMap);
        private static readonly int MapMask = Mask(MapHashing), MapBias = Bias(MapHashing);

        /// <summary>Whether a new <see cref="HashSet{T}"/> is filled and linked here on this runtime (its hashing
        /// found).</summary>
        public static readonly bool Set = SetHashing >= 0;

        /// <summary>As <see cref="Set"/>, for a dictionary.</summary>
        public static readonly bool Map = MapHashing >= 0;

        // Whether the chain of an entry the runtime added can be walked: the layout is known, and for entries with
        // references (laid out by the same rules as the mirrors, as each instantiation is), read back right from a
        // collection the runtime filled, comparing references only: nothing is written through a layout not seen.
        private static readonly bool MapChains = MapAt != null;
        private static readonly bool MapReferenceChains = MapChains && ObjectTypes.Passes(CheckMapChains);
        private static readonly bool SetChains = SetAt != null;
        private static readonly bool SetReferenceChains = SetChains && ObjectTypes.Passes(CheckSetChains);

        // Entries with references, then string keys (hashed by EqualityComparer<string>.Default, which .NET's tables
        // do not use for them), filled here one key at a time: each checked as the hashing is, through the public API.
        private static readonly bool MapReferences =
            Map && MapReferenceChains && ObjectTypes.Passes(CheckMapReferences);
        private static readonly bool MapStrings = MapReferences && ObjectTypes.Passes(CheckMapStrings);
        private static readonly bool SetReferences =
            Set && SetReferenceChains && ObjectTypes.Passes(CheckSetReferences);
        private static readonly bool SetStrings = SetReferences && ObjectTypes.Passes(CheckSetStrings);

        // String keys of tables the runtime protects itself: .NET's (Core) Dictionary and HashSet, and .NET Framework's
        // Dictionary, switch to a randomized comparer after 100 collisions. Mono and IL2CPP are not counted on.
#if NET8_0_OR_GREATER
        private const bool GuardsStringMaps = true, GuardsStringSets = true;
#else
        private static readonly bool GuardsStringMaps =
            RuntimeInformation.FrameworkDescription.StartsWith(".NET", StringComparison.Ordinal);
        private static readonly bool GuardsStringSets = GuardsStringMaps &&
            !RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal);
#endif

        // Hashing 0 to 3: hash codes as they are or masked to 31 bits; a bucket holds its first entry's index + 1 or
        // its index.
        private static int Mask(int hashing) => (hashing & 1) == 0 ? -1 : int.MaxValue;
        private static int Bias(int hashing) => hashing < 2 ? 1 : 0;

        private static int FindHashing(Func<int, int, bool> check)
        {
            for (int hashing = 0; hashing < 4; hashing++)
                if (ObjectTypes.Passes(() => check(Mask(hashing), Bias(hashing))))
                    return hashing;
            return -1;
        }

        /// <summary>
        /// The fewest values a read of values one by one (<see cref="NinoRuns"/>' per-value loops) fills into the
        /// entries of a new collection and links after (<see cref="Link{TKey,TValue}(Dictionary{TKey,TValue}, int)"/>)
        /// rather than inserting each: below it the two calls it adds (the read loop and the link) cost more than the
        /// inserts they save. Runners, dictionaries (#320, run 44243147222): one int-keyed pair 4 to 9 % slower than one
        /// insert on Xeon 6975P-C and Neoverse-N2, four on par, 12 and more up to 13 % faster. Values a set or a
        /// dictionary takes as one block (<see cref="NinoRuns.ScattersSet{T,TFormatter}"/>) are filled from one on, as
        /// the raw fill they generalize was.
        /// </summary>
        public const int FillFrom = 4;

        // One table for a set and a dictionary: a set's entry is a SetEntry<TKey> (its value the key; null a value,
        // hashed as 0), a dictionary's a MapEntry<TKey, TValue> (a null key is invalid data). The core below is generic
        // over the key and value (a set's value its key again) and told which by a constant (set), its entries reached
        // through Unsafe.As on the mirror: fields at offsets the JIT knows in code shared over a class, where members of
        // a struct telling them apart were not inlined (asm.py: Link of a Dictionary<int, string> 253 to 999 bytes,
        // 1 to 51 stack accesses).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref int HashCode<TKey, TValue>(ref byte entry, bool set) => ref set
            ? ref Unsafe.As<byte, CollectionFields.SetEntry<TKey>>(ref entry).HashCode
            : ref Unsafe.As<byte, CollectionFields.MapEntry<TKey, TValue>>(ref entry).HashCode;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref int Next<TKey, TValue>(ref byte entry, bool set) => ref set
            ? ref Unsafe.As<byte, CollectionFields.SetEntry<TKey>>(ref entry).Next
            : ref Unsafe.As<byte, CollectionFields.MapEntry<TKey, TValue>>(ref entry).Next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref TKey Key<TKey, TValue>(ref byte entry, bool set) => ref set
            ? ref Unsafe.As<byte, CollectionFields.SetEntry<TKey>>(ref entry).Value
            : ref Unsafe.As<byte, CollectionFields.MapEntry<TKey, TValue>>(ref entry).Key;

        // The first entry of a table's entries array (not empty).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte First<TKey, TValue>(object entries, bool set) => ref set
            ? ref Unsafe.As<CollectionFields.SetEntry<TKey>, byte>(
                ref Unsafe.As<CollectionFields.SetEntry<TKey>[]>(entries)[0])
            : ref Unsafe.As<CollectionFields.MapEntry<TKey, TValue>, byte>(
                ref Unsafe.As<CollectionFields.MapEntry<TKey, TValue>[]>(entries)[0]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Stride<TKey, TValue>(bool set) => set
            ? Unsafe.SizeOf<CollectionFields.SetEntry<TKey>>()
            : Unsafe.SizeOf<CollectionFields.MapEntry<TKey, TValue>>();

        // The hash code of a key read from the data under the table's mask: a set's null is 0, a dictionary's invalid.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Hash<TKey>(object table, TKey key, int mask, bool set)
        {
            if (key == null)
            {
                if (!set)
                    NinoThrow.NullKey(table);
                return 0;
            }

            return EqualityComparer<TKey>.Default.GetHashCode(key) & mask;
        }

        /// <summary>
        /// What <see cref="CanInsert{TKey,TValue}"/> and <see cref="Added{TKey,TValue}"/> decide from the key and value
        /// types alone, once per pair of types. A reading loop copies them into locals: in the code shared by
        /// reference types, each call to the generic methods costs a runtime lookup, and the calls kept the loop's
        /// state on the stack (a <c>Dictionary&lt;string, string&gt;</c> read: 146 to 173 stack accesses).
        /// </summary>
        public static class Plan<TKey, TValue>
        {
            /// <summary>Whether keys may be added by <see cref="Insert{TKey,TValue}"/>.</summary>
            public static readonly bool Inserts = Decides(false, typeof(TKey) == typeof(string),
                NinoUnsafe.IsReferenceOrContainsReferences<TKey>() ||
                NinoUnsafe.IsReferenceOrContainsReferences<TValue>(), walks: false);

            /// <summary>Whether a key the dictionary added has its bucket walked (<see cref="Walk{TKey,TValue}"/>).</summary>
            public static readonly bool Walks = Decides(false, typeof(TKey) == typeof(string),
                NinoUnsafe.IsReferenceOrContainsReferences<TKey>() ||
                NinoUnsafe.IsReferenceOrContainsReferences<TValue>(), walks: true);
        }

        /// <summary>As <see cref="Plan{TKey,TValue}"/>, for a set.</summary>
        public static class Plan<T>
        {
            /// <summary>Whether elements may be added by <see cref="Insert{T}"/>.</summary>
            public static readonly bool Inserts = Decides(true, typeof(T) == typeof(string),
                NinoUnsafe.IsReferenceOrContainsReferences<T>(), walks: false);

            /// <summary>Whether an element the set added has its bucket walked (<see cref="Walk{T}"/>).</summary>
            public static readonly bool Walks = Decides(true, typeof(T) == typeof(string),
                NinoUnsafe.IsReferenceOrContainsReferences<T>(), walks: true);
        }

        // The plan of a set's or a dictionary's entries: string keys of a table that protects itself (GuardsString*) are
        // left to it (it hashes them faster, non-randomized until it sees collisions, than the default comparer here
        // would, and walks no bucket); others inserted where the hashing (and for references the reference checks)
        // passed, and walked where the chains can be read.
        private static bool Decides(bool set, bool strings, bool references, bool walks)
        {
            bool guards = strings && (set ? GuardsStringSets : GuardsStringMaps);
            if (walks)
                return !guards && (references
                    ? set ? SetReferenceChains : MapReferenceChains
                    : set ? SetChains : MapChains);
            if (strings)
                return (set ? SetStrings : MapStrings) && !guards;
            return (set ? Set : Map) && ((set ? SetReferences : MapReferences) || !references);
        }

        /// <summary>
        /// Whether <paramref name="dictionary"/> takes keys one at a time through <see cref="Insert{TKey,TValue}"/>: an
        /// empty dictionary with the default comparer, sized for <paramref name="count"/> pairs, on a runtime whose
        /// layout and hashing (for these key and value types) were found. Otherwise the caller adds through the
        /// dictionary and then calls <see cref="Added{TKey,TValue}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CanInsert<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count) =>
            Plan<TKey, TValue>.Inserts && Empty(dictionary, count);

        /// <summary>As <see cref="CanInsert{TKey,TValue}"/>, for a set.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CanInsert<T>(HashSet<T> set, int count) => Plan<T>.Inserts && Empty(set, count);

        // Empty, never filled, with the default comparer and sized for count (the flags are checked by the caller:
        // the startup checks use this before the flags are set).
        private static bool Empty<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count) =>
            ReferenceEquals(dictionary.Comparer, EqualityComparer<TKey>.Default) && Empty(dictionary, count, MapFields);

        private static bool Empty<T>(HashSet<T> set, int count) =>
            ReferenceEquals(set.Comparer, EqualityComparer<T>.Default) && Empty(set, count, SetFields);

        private static bool Empty(object table, int count, in Fields at)
        {
            var buckets = ObjectFields.At<int[]>(table, at.Buckets);
            return buckets != null && buckets.Length != 0 &&
                   ObjectFields.At<object>(table, at.Entries) is Array entries && entries.Length >= count &&
                   ObjectFields.At<int>(table, at.Bound) == 0;
        }

        /// <summary>
        /// The entries of a dictionary <see cref="CanInsert{TKey,TValue}"/> accepted, for a read that stores its
        /// <c>count</c> pairs into them in order and then calls <see cref="Link{TKey,TValue}"/>: the pairs read into
        /// their places, with no call per pair, and the buckets built in one pass afterwards.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CollectionFields.MapEntry<TKey, TValue>[] Entries<TKey, TValue>(Dictionary<TKey, TValue> dictionary) =>
            Unsafe.As<CollectionFields.MapEntry<TKey, TValue>[]>(ObjectFields.At<object>(dictionary, MapFields.Entries));

        /// <summary>As <see cref="Entries{TKey,TValue}"/>, for a set <see cref="CanInsert{T}"/> accepted.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CollectionFields.SetEntry<T>[] Entries<T>(HashSet<T> set) =>
            Unsafe.As<CollectionFields.SetEntry<T>[]>(ObjectFields.At<object>(set, SetFields.Entries));

        /// <summary>
        /// Adds a pair read from the data to a dictionary <see cref="CanInsert{TKey,TValue}"/> accepted (and that only
        /// this has added to since), as its Add would: a null key, a key it holds already, or more than
        /// <see cref="NinoSettings.MaxHashCollisions"/> keys in the key's bucket are invalid data.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Insert<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value,
            Type owner = null)
        {
            ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<TKey, TValue>>(
                ref Insert<TKey, TValue>(dictionary, MapFields, false, key, MapMask, MapBias, owner));
            entry.Key = key;
            entry.Value = value;
        }

        /// <summary>As <see cref="Insert{TKey,TValue}"/>, for an element of a set (null is one).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Insert<T>(HashSet<T> set, T value) =>
            Unsafe.As<byte, CollectionFields.SetEntry<T>>(
                ref Insert<T, T>(set, SetFields, true, value, SetMask, SetBias, null)).Value = value;

        // Links the table's next entry for key into its bucket (the collisions counted, a repeated key rejected) and
        // counts it in; the caller stores the key and value into it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte Insert<TKey, TValue>(object table, in Fields at, bool set, TKey key, int mask, int bias,
            Type owner)
        {
            int hash = Hash(table, key, mask, set);
            var buckets = ObjectFields.At<int[]>(table, at.Buckets);
            ref byte first = ref First<TKey, TValue>(ObjectFields.At<object>(table, at.Entries), set);
            int stride = Stride<TKey, TValue>(set);
            ulong multiplier = at.FastMod >= 0 ? ObjectFields.At<ulong>(table, at.FastMod) : 0;
            ref int bound = ref ObjectFields.At<int>(table, at.Bound);
            int index = bound;
            ref int bucket = ref buckets[(int)Bucket((uint)hash, (uint)buckets.Length, multiplier)];
            // Only a key whose bucket is not empty counts (the common case, an empty bucket, does nothing more).
            int other = bucket - bias;
            if (other >= 0)
            {
                int left = NinoLimits.MaxHashCollisions;
                do
                {
                    if (--left < 0)
                        NinoThrow.HashFlood(owner ?? table.GetType());
                    ref byte entry = ref Unsafe.Add(ref first, (nint)other * stride);
                    if (HashCode<TKey, TValue>(ref entry, set) == hash &&
                        EqualityComparer<TKey>.Default.Equals(Key<TKey, TValue>(ref entry, set), key))
                        NinoThrow.DuplicateKey(owner ?? table.GetType());
                    other = Next<TKey, TValue>(ref entry, set);
                } while (other >= 0);
            }

            ref byte added = ref Unsafe.Add(ref first, (nint)index * stride);
            HashCode<TKey, TValue>(ref added, set) = hash;
            Next<TKey, TValue>(ref added, set) = bucket - bias;
            bucket = index + bias;
            bound = index + 1;
            ObjectFields.At<int>(table, at.Count) = index + 1;
            return ref added;
        }

        /// <summary>
        /// Builds the buckets of a dictionary whose first <paramref name="count"/> entries a read filled
        /// (<see cref="Entries{TKey,TValue}"/>), as <see cref="Insert{TKey,TValue}"/> would have one key at a time: a
        /// null key, a key twice, or more than <see cref="NinoSettings.MaxHashCollisions"/> keys in a bucket are
        /// invalid data (the caller then empties the dictionary: <see cref="Unlink{TKey,TValue}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Link<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count) =>
            Link<TKey, TValue>(dictionary, MapFields, false, count, MapMask, MapBias);

        /// <summary>
        /// As <see cref="Link{TKey,TValue}(Dictionary{TKey,TValue}, int)"/>, for a set whose first
        /// <paramref name="count"/> entries a read filled (<see cref="Entries{T}"/>): a value twice, or more than
        /// <see cref="NinoSettings.MaxHashCollisions"/> values in a bucket, are invalid data (the caller then empties
        /// the set: <see cref="Unlink{T}"/>); null is a value.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Link<T>(HashSet<T> set, int count) => Link<T, T>(set, SetFields, true, count, SetMask, SetBias);

        // Link with the hashing given: the startup checks try each (CheckSet, CheckMap).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Link<TKey, TValue>(object table, in Fields at, bool set, int count, int mask, int bias)
        {
            var buckets = ObjectFields.At<int[]>(table, at.Buckets);
            ulong multiplier = at.FastMod >= 0 ? ObjectFields.At<ulong>(table, at.FastMod) : 0;
            uint length = (uint)buckets.Length;
            int stride = Stride<TKey, TValue>(set);
            ref int firstBucket = ref buckets[0];
            ref byte entry = ref First<TKey, TValue>(ObjectFields.At<object>(table, at.Entries), set);
            // Entry at is reached from the entry being linked (entry, i), not from the first: one register fewer in
            // the loop, which the collision count takes (with it, the first entry's address was kept on the stack).
            for (int i = 0; i < count; i++)
            {
                TKey key = Key<TKey, TValue>(ref entry, set);
                int hash = Hash(table, key, mask, set);
                ref int bucket = ref Unsafe.Add(ref firstBucket, (IntPtr)Bucket((uint)hash, length, multiplier));
                // Only a key whose bucket is not empty counts (the common case, an empty bucket, does nothing more).
                int other = bucket - bias;
                if (other >= 0)
                {
                    int left = NinoLimits.MaxHashCollisions;
                    do
                    {
                        if (--left < 0)
                            NinoThrow.HashFlood(table.GetType());
                        ref byte linked = ref Unsafe.AddByteOffset(ref entry, (nint)(other - i) * stride);
                        if (HashCode<TKey, TValue>(ref linked, set) == hash &&
                            EqualityComparer<TKey>.Default.Equals(Key<TKey, TValue>(ref linked, set), key))
                            NinoThrow.DuplicateKey(table.GetType());
                        other = Next<TKey, TValue>(ref linked, set);
                    } while (other >= 0);
                }

                HashCode<TKey, TValue>(ref entry, set) = hash;
                Next<TKey, TValue>(ref entry, set) = bucket - bias;
                bucket = i + bias;
                entry = ref Unsafe.Add(ref entry, stride);
            }

            ObjectFields.At<int>(table, at.Bound) = count;
            ObjectFields.At<int>(table, at.Count) = count;
        }

        /// <summary>
        /// Leaves a dictionary whose entries a read filled (<see cref="Entries{TKey,TValue}"/>) empty, as it was before:
        /// its entries and buckets cleared, so no reference the read stored stays reachable from it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unlink<TKey, TValue>(Dictionary<TKey, TValue> dictionary) => Unlink(dictionary, MapFields);

        /// <summary>As <see cref="Unlink{TKey,TValue}"/>, for a set.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unlink<T>(HashSet<T> set) => Unlink(set, SetFields);

        private static void Unlink(object table, in Fields at)
        {
            var buckets = ObjectFields.At<int[]>(table, at.Buckets);
            var entries = Unsafe.As<Array>(ObjectFields.At<object>(table, at.Entries));
            Array.Clear(buckets, 0, buckets.Length);
            Array.Clear(entries, 0, entries.Length);
        }

        /// <summary>
        /// After the dictionary itself added <paramref name="key"/>, read from the data: throws when more than
        /// <see cref="NinoSettings.MaxHashCollisions"/> other keys share its bucket. The bucket is found by the
        /// dictionary's comparer where the hashing is known (<see cref="Map"/>), else the key is taken to be the last
        /// entry, which it is when no entry is free (none removed since the dictionary was filled from empty). Nothing
        /// where the chain cannot be walked, and for string keys of a runtime that protects the table itself.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Added<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key)
        {
            if (Plan<TKey, TValue>.Walks)
                Walk(dictionary, key);
        }

        /// <summary>As <see cref="Added{TKey,TValue}"/>, for an element of a set.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Added<T>(HashSet<T> set, T value)
        {
            if (Plan<T>.Walks)
                Walk(set, value);
        }

        /// <summary>Walks the bucket of <paramref name="key"/>, which the dictionary added (<see cref="Plan{TKey,TValue}.Walks"/>).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Walk<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key) =>
            Walk<TKey, TValue>(dictionary, MapFields, false, key, dictionary.Comparer, dictionary.Count, MapMask, MapBias,
                Map);

        /// <summary>Walks the bucket of <paramref name="value"/>, which the set added (<see cref="Plan{T}.Walks"/>).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Walk<T>(HashSet<T> set, T value) =>
            Walk<T, T>(set, SetFields, true, value, set.Comparer, set.Count, SetMask, SetBias, Set);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Walk<TKey, TValue>(object table, in Fields at, bool set, TKey key,
            IEqualityComparer<TKey> comparer, int count, int mask, int bias, bool hashed)
        {
            ref byte first = ref First<TKey, TValue>(ObjectFields.At<object>(table, at.Entries), set);
            int stride = Stride<TKey, TValue>(set);
            int bound = ObjectFields.At<int>(table, at.Bound);
            int other, left = NinoLimits.MaxHashCollisions;
            if (hashed)
            {
                // The whole chain, the key's own entry included.
                var buckets = ObjectFields.At<int[]>(table, at.Buckets);
                ulong multiplier = at.FastMod >= 0 ? ObjectFields.At<ulong>(table, at.FastMod) : 0;
                int hash = (key == null ? 0 : comparer.GetHashCode(key)) & mask;
                other = buckets[(int)Bucket((uint)hash, (uint)buckets.Length, multiplier)] - bias;
                left++;
            }
            else if (count == bound)
                other = Next<TKey, TValue>(ref Unsafe.Add(ref first, (nint)(bound - 1) * stride), set);
            else
                return;

            for (; other >= 0; other = Next<TKey, TValue>(ref Unsafe.Add(ref first, (nint)other * stride), set))
                if (--left < 0)
                    NinoThrow.HashFlood(table.GetType());
        }

        /// <summary>
        /// The bucket of a hash code: .NET's fast modulo where the table keeps a multiplier (64-bit .NET 5+), or one
        /// from <see cref="FastModMultiplier"/>, else the remainder. Exact for every hash and a length up to
        /// int.MaxValue.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Bucket(uint hash, uint length, ulong multiplier) =>
            multiplier != 0 ? (uint)(((((multiplier * hash) >> 32) + 1) * length) >> 32) : hash % length;

        /// <summary>
        /// The multiplier of <see cref="Bucket"/> for <paramref name="length"/> (.NET's HashHelpers.GetFastModMultiplier)
        /// in a 64-bit process, where the 64-bit multiplications are cheaper than a division; 0 (the remainder) in a
        /// 32-bit one.
        /// </summary>
        internal static ulong FastModMultiplier(uint length) => NinoUnsafe.Is64Bit ? ulong.MaxValue / length + 1 : 0;
    }
}
