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
    /// Fills a new <see cref="HashSet{T}"/> or <see cref="Dictionary{TKey,TValue}"/> of raw elements by writing its
    /// entries and buckets directly, as its Add would, without the checks Add repeats per element (the comparer, the
    /// free list, the capacity, the version). The collection is created and sized through its public API first (its
    /// constructor or EnsureCapacity allocate the buckets and entries and set the fast-mod multiplier); only the
    /// entries, the buckets and the count are written here, at the offsets <see cref="CollectionViews"/> found. The
    /// hashing differs between implementations, and not along the lines of their fields (Mono's dictionary has .NET
    /// Core's fields and buckets but masks hash codes to 31 bits as .NET Framework's does): the bucket is .NET's fast
    /// modulo where the table keeps a multiplier (64-bit .NET 5+), else the remainder; whether hash codes are masked
    /// and what a bucket holds for its first entry (its index + 1, empty 0; or its index, empty -1) are found at
    /// startup, as the first of the four that a collection filled here passes the public API's checks with. None:
    /// the formatters add element by element.
    /// <para>
    /// Hash flooding: every key added here walks its bucket, as Add would, and more than
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
        private static readonly CollectionViews.HashOffsets? SetAt = CollectionViews.HashSetOffsets;
        private static readonly int SetEntries = SetAt?.Entries ?? 0;
        private static readonly int SetBound = SetAt?.Bound ?? 0;
        private static readonly int SetBuckets = SetAt?.Buckets ?? 0;
        private static readonly int SetFastMod = SetAt?.FastMod ?? -1;
        private static readonly int SetCount = SetAt?.Count ?? 0;

        private static readonly CollectionViews.HashOffsets? MapAt = CollectionViews.DictionaryOffsets;
        private static readonly int MapEntries = MapAt?.Entries ?? 0;
        private static readonly int MapBound = MapAt?.Bound ?? 0;
        private static readonly int MapBuckets = MapAt?.Buckets ?? 0;
        private static readonly int MapFastMod = MapAt?.FastMod ?? -1;
        private static readonly int MapCount = MapAt?.Count ?? 0;

        // The hashing (mask and bias) each passed its check with, -1 for none.
        private static readonly int SetHashing = SetAt == null ? -1 : FindHashing(CheckSet);
        private static readonly int SetMask = Mask(SetHashing), SetBias = Bias(SetHashing);
        private static readonly int MapHashing = MapAt == null ? -1 : FindHashing(CheckMap);
        private static readonly int MapMask = Mask(MapHashing), MapBias = Bias(MapHashing);

        /// <summary>Whether <see cref="TryFill{T}"/> fills a new <see cref="HashSet{T}"/> on this runtime.</summary>
        public static readonly bool Set = SetHashing >= 0;

        /// <summary>Whether <see cref="TryFill{TKey,TValue}"/> fills a new dictionary on this runtime.</summary>
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
        /// Adds the <paramref name="count"/> raw values at <paramref name="source"/> to <paramref name="set"/>, new
        /// (default comparer) and sized for them; false, with the set untouched, when it is not as expected. A value
        /// the data holds twice throws, as the element by element read does.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryFill<T>(HashSet<T> set, ref byte source, int count, int size)
        {
            // A set with its own comparer (Untrusted mode's) is not hashed here.
            if (!ReferenceEquals(set.Comparer, EqualityComparer<T>.Default))
                return false;
            // A bool other than 0 or 1, and in Untrusted mode other values no constructor makes, are invalid data.
            if (!NinoLayout<T>.CopiesAsIs)
                NinoChecks.Values<T>(ref source, count, size);
            return Fill(set, ref source, count, size, SetMask, SetBias);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Fill<T>(HashSet<T> set, ref byte source, int count, int size, int mask, int bias)
        {
            var buckets = ObjectFields.At<int[]>(set, SetBuckets);
            var entries = Unsafe.As<CollectionFields.SetEntry<T>[]>(ObjectFields.At<object>(set, SetEntries));
            if (buckets == null || buckets.Length == 0 || entries == null || entries.Length < count ||
                ObjectFields.At<int>(set, SetBound) != 0)
                return false;

            ulong multiplier = SetFastMod >= 0 ? ObjectFields.At<ulong>(set, SetFastMod) : 0;
            uint length = (uint)buckets.Length;
            ref int firstBucket = ref buckets[0];
            // Entry at is reached from the entry being written (entry, i), not from the first: one register fewer in
            // the loop, which the collision count takes (with it, the first entry's address was kept on the stack).
            ref var entry = ref entries[0];
            for (int i = 0; i < count; i++)
            {
                T value = NinoUnsafe.Read<T>(ref source);
                source = ref Unsafe.Add(ref source, size);
                int hash = EqualityComparer<T>.Default.GetHashCode(value) & mask;
                ref int bucket = ref Unsafe.Add(ref firstBucket, (IntPtr)Bucket((uint)hash, length, multiplier));
                // Only a key whose bucket is not empty counts (the common case, an empty bucket, does nothing more).
                int at = bucket - bias;
                if (at >= 0)
                {
                    int left = NinoLimits.MaxHashCollisions;
                    do
                    {
                        if (--left < 0)
                            NinoThrow.HashFlood(typeof(HashSet<T>));
                        ref var other = ref Unsafe.Add(ref entry, (IntPtr)(at - i));
                        if (other.HashCode == hash && EqualityComparer<T>.Default.Equals(other.Value, value))
                            NinoThrow.DuplicateKey(typeof(HashSet<T>));
                        at = other.Next;
                    } while (at >= 0);
                }

                entry.HashCode = hash;
                entry.Next = bucket - bias;
                entry.Value = value;
                bucket = i + bias;
                entry = ref Unsafe.Add(ref entry, 1);
            }

            ObjectFields.At<int>(set, SetBound) = count;
            ObjectFields.At<int>(set, SetCount) = count;
            return true;
        }

        /// <summary>
        /// As <see cref="TryFill{T}"/>, for the raw pairs of an empty dictionary with the default comparer: a key, then
        /// its value, each pair <paramref name="pairSize"/> bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryFill<TKey, TValue>(Dictionary<TKey, TValue> dictionary, ref byte source, int count,
            int keySize, int pairSize) =>
            // An existing dictionary may have been refilled (cleared) with another comparer, which this hashing is
            // not.
            ReferenceEquals(dictionary.Comparer, EqualityComparer<TKey>.Default) &&
            Fill(dictionary, ref source, count, keySize, pairSize, MapMask, MapBias);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Fill<TKey, TValue>(Dictionary<TKey, TValue> dictionary, ref byte source, int count,
            int keySize, int pairSize, int mask, int bias)
        {
            var buckets = ObjectFields.At<int[]>(dictionary, MapBuckets);
            var entries = Unsafe.As<CollectionFields.MapEntry<TKey, TValue>[]>(
                ObjectFields.At<object>(dictionary, MapEntries));
            if (buckets == null || buckets.Length == 0 || entries == null || entries.Length < count ||
                ObjectFields.At<int>(dictionary, MapBound) != 0)
                return false;

            ulong multiplier = MapFastMod >= 0 ? ObjectFields.At<ulong>(dictionary, MapFastMod) : 0;
            uint length = (uint)buckets.Length;
            ref int firstBucket = ref buckets[0];
            // Entry at is reached from the entry being written (entry, i), not from the first: one register fewer in
            // the loop, which the collision count takes (with it, the first entry's address was kept on the stack).
            ref var entry = ref entries[0];
            for (int i = 0; i < count; i++)
            {
                TKey key = NinoUnsafe.Read<TKey>(ref source);
                TValue value = NinoUnsafe.Read<TValue>(ref source, keySize);
                source = ref Unsafe.Add(ref source, pairSize);
                int hash = EqualityComparer<TKey>.Default.GetHashCode(key) & mask;
                ref int bucket = ref Unsafe.Add(ref firstBucket, (IntPtr)Bucket((uint)hash, length, multiplier));
                // Only a key whose bucket is not empty counts (the common case, an empty bucket, does nothing more).
                int at = bucket - bias;
                if (at >= 0)
                {
                    int left = NinoLimits.MaxHashCollisions;
                    do
                    {
                        if (--left < 0)
                            NinoThrow.HashFlood(typeof(Dictionary<TKey, TValue>));
                        ref var other = ref Unsafe.Add(ref entry, (IntPtr)(at - i));
                        if (other.HashCode == hash && EqualityComparer<TKey>.Default.Equals(other.Key, key))
                            NinoThrow.DuplicateKey(typeof(Dictionary<TKey, TValue>));
                        at = other.Next;
                    } while (at >= 0);
                }

                entry.HashCode = hash;
                entry.Next = bucket - bias;
                entry.Key = key;
                entry.Value = value;
                bucket = i + bias;
                entry = ref Unsafe.Add(ref entry, 1);
            }

            ObjectFields.At<int>(dictionary, MapBound) = count;
            ObjectFields.At<int>(dictionary, MapCount) = count;
            return true;
        }

        /// <summary>
        /// Whether <paramref name="dictionary"/> takes keys one at a time through <see cref="Insert{TKey,TValue}"/>: an
        /// empty dictionary with the default comparer, sized for <paramref name="count"/> pairs, on a runtime whose
        /// layout and hashing (for these key and value types) were found. Otherwise the caller adds through the
        /// dictionary and then calls <see cref="Added{TKey,TValue}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CanInsert<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count)
        {
            if (!Plan<TKey, TValue>.Inserts)
                return false;
            return Empty(dictionary, count);
        }

        /// <summary>
        /// What <see cref="CanInsert{TKey,TValue}"/> and <see cref="Added{TKey,TValue}"/> decide from the key and value
        /// types alone, once per pair of types. A reading loop copies them into locals: in the code shared by
        /// reference types, each call to the generic methods costs a runtime lookup, and the calls kept the loop's
        /// state on the stack (a <c>Dictionary&lt;string, string&gt;</c> read: 146 to 173 stack accesses).
        /// </summary>
        public static class Plan<TKey, TValue>
        {
            // String keys of a table that protects itself (GuardsStringMaps) are left to it: it hashes them faster
            // (non-randomized until it sees collisions) than the default comparer here would, and walks no bucket.
            /// <summary>Whether keys may be added by <see cref="Insert{TKey,TValue}"/>.</summary>
            public static readonly bool Inserts = typeof(TKey) == typeof(string)
                ? MapStrings && !GuardsStringMaps
                : Map && (MapReferences || !NinoUnsafe.IsReferenceOrContainsReferences<TKey>() &&
                    !NinoUnsafe.IsReferenceOrContainsReferences<TValue>());

            /// <summary>Whether a key the dictionary added has its bucket walked (<see cref="Walk{TKey,TValue}"/>).</summary>
            public static readonly bool Walks = !(typeof(TKey) == typeof(string) && GuardsStringMaps) &&
                (NinoUnsafe.IsReferenceOrContainsReferences<TKey>() || NinoUnsafe.IsReferenceOrContainsReferences<TValue>()
                    ? MapReferenceChains
                    : MapChains);
        }

        /// <summary>As <see cref="Plan{TKey,TValue}"/>, for a set.</summary>
        public static class Plan<T>
        {
            /// <summary>Whether elements may be added by <see cref="Insert{T}"/>.</summary>
            public static readonly bool Inserts = typeof(T) == typeof(string)
                ? SetStrings && !GuardsStringSets
                : Set && (SetReferences || !NinoUnsafe.IsReferenceOrContainsReferences<T>());

            /// <summary>Whether an element the set added has its bucket walked (<see cref="Walk{T}"/>).</summary>
            public static readonly bool Walks = !(typeof(T) == typeof(string) && GuardsStringSets) &&
                (NinoUnsafe.IsReferenceOrContainsReferences<T>() ? SetReferenceChains : SetChains);
        }

        // Empty, never filled, with the default comparer and sized for count (the flags are checked by the caller:
        // the startup checks use this before the flags are set).
        private static bool Empty<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count)
        {
            var buckets = ObjectFields.At<int[]>(dictionary, MapBuckets);
            return buckets != null && buckets.Length != 0 &&
                   ObjectFields.At<object>(dictionary, MapEntries) is Array entries && entries.Length >= count &&
                   ObjectFields.At<int>(dictionary, MapBound) == 0 &&
                   ReferenceEquals(dictionary.Comparer, EqualityComparer<TKey>.Default);
        }

        /// <summary>
        /// Adds a pair read from the data to a dictionary <see cref="CanInsert{TKey,TValue}"/> accepted (and that only
        /// this has added to since), as its Add would: a null key, a key it holds already, or more than
        /// <see cref="NinoSettings.MaxHashCollisions"/> keys in the key's bucket are invalid data.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Insert<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value,
            Type owner = null)
        {
            if (key == null)
                NinoThrow.NullKey(dictionary);
            var buckets = ObjectFields.At<int[]>(dictionary, MapBuckets);
            var entries = Unsafe.As<CollectionFields.MapEntry<TKey, TValue>[]>(
                ObjectFields.At<object>(dictionary, MapEntries));
            ulong multiplier = MapFastMod >= 0 ? ObjectFields.At<ulong>(dictionary, MapFastMod) : 0;
            ref int bound = ref ObjectFields.At<int>(dictionary, MapBound);
            int index = bound;
            int hash = EqualityComparer<TKey>.Default.GetHashCode(key) & MapMask;
            ref int bucket = ref buckets[(int)Bucket((uint)hash, (uint)buckets.Length, multiplier)];
            // Only a key whose bucket is not empty counts (the common case, an empty bucket, does nothing more).
            int at = bucket - MapBias;
            if (at >= 0)
            {
                int left = NinoLimits.MaxHashCollisions;
                do
                {
                    if (--left < 0)
                        NinoThrow.HashFlood(owner ?? dictionary.GetType());
                    ref var other = ref entries[at];
                    if (other.HashCode == hash && EqualityComparer<TKey>.Default.Equals(other.Key, key))
                        NinoThrow.DuplicateKey(owner ?? dictionary.GetType());
                    at = other.Next;
                } while (at >= 0);
            }

            ref var entry = ref entries[index];
            entry.HashCode = hash;
            entry.Next = bucket - MapBias;
            entry.Key = key;
            entry.Value = value;
            bucket = index + MapBias;
            bound = index + 1;
            ObjectFields.At<int>(dictionary, MapCount) = index + 1;
        }

        /// <summary>As <see cref="CanInsert{TKey,TValue}"/>, for a set.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CanInsert<T>(HashSet<T> set, int count)
        {
            if (!Plan<T>.Inserts)
                return false;
            return Empty(set, count);
        }

        private static bool Empty<T>(HashSet<T> set, int count)
        {
            var buckets = ObjectFields.At<int[]>(set, SetBuckets);
            return buckets != null && buckets.Length != 0 &&
                   ObjectFields.At<object>(set, SetEntries) is Array entries && entries.Length >= count &&
                   ObjectFields.At<int>(set, SetBound) == 0 &&
                   ReferenceEquals(set.Comparer, EqualityComparer<T>.Default);
        }

        /// <summary>As <see cref="Insert{TKey,TValue}"/>, for an element of a set (null is one).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Insert<T>(HashSet<T> set, T value)
        {
            var buckets = ObjectFields.At<int[]>(set, SetBuckets);
            var entries = Unsafe.As<CollectionFields.SetEntry<T>[]>(ObjectFields.At<object>(set, SetEntries));
            ulong multiplier = SetFastMod >= 0 ? ObjectFields.At<ulong>(set, SetFastMod) : 0;
            ref int bound = ref ObjectFields.At<int>(set, SetBound);
            int index = bound;
            int hash = (value == null ? 0 : EqualityComparer<T>.Default.GetHashCode(value)) & SetMask;
            ref int bucket = ref buckets[(int)Bucket((uint)hash, (uint)buckets.Length, multiplier)];
            // Only a key whose bucket is not empty counts (the common case, an empty bucket, does nothing more).
            int at = bucket - SetBias;
            if (at >= 0)
            {
                int left = NinoLimits.MaxHashCollisions;
                do
                {
                    if (--left < 0)
                        NinoThrow.HashFlood(set.GetType());
                    ref var other = ref entries[at];
                    if (other.HashCode == hash && EqualityComparer<T>.Default.Equals(other.Value, value))
                        NinoThrow.DuplicateKey(set.GetType());
                    at = other.Next;
                } while (at >= 0);
            }

            ref var entry = ref entries[index];
            entry.HashCode = hash;
            entry.Next = bucket - SetBias;
            entry.Value = value;
            bucket = index + SetBias;
            bound = index + 1;
            ObjectFields.At<int>(set, SetCount) = index + 1;
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

        /// <summary>Walks the bucket of <paramref name="key"/>, which the dictionary added (<see cref="Plan{TKey,TValue}.Walks"/>).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Walk<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key)
        {
            var entries = Unsafe.As<CollectionFields.MapEntry<TKey, TValue>[]>(
                ObjectFields.At<object>(dictionary, MapEntries));
            int bound = ObjectFields.At<int>(dictionary, MapBound);
            int at, left = NinoLimits.MaxHashCollisions;
            if (Map)
            {
                // The whole chain, the key's own entry included.
                var buckets = ObjectFields.At<int[]>(dictionary, MapBuckets);
                ulong multiplier = MapFastMod >= 0 ? ObjectFields.At<ulong>(dictionary, MapFastMod) : 0;
                int hash = dictionary.Comparer.GetHashCode(key) & MapMask;
                at = buckets[(int)Bucket((uint)hash, (uint)buckets.Length, multiplier)] - MapBias;
                left++;
            }
            else if (dictionary.Count == bound)
                at = entries[bound - 1].Next;
            else
                return;

            for (; at >= 0; at = entries[at].Next)
                if (--left < 0)
                    NinoThrow.HashFlood(dictionary.GetType());
        }

        /// <summary>As <see cref="Added{TKey,TValue}"/>, for an element of a set.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Added<T>(HashSet<T> set, T value)
        {
            if (Plan<T>.Walks)
                Walk(set, value);
        }

        /// <summary>Walks the bucket of <paramref name="value"/>, which the set added (<see cref="Plan{T}.Walks"/>).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Walk<T>(HashSet<T> set, T value)
        {
            var entries = Unsafe.As<CollectionFields.SetEntry<T>[]>(ObjectFields.At<object>(set, SetEntries));
            int bound = ObjectFields.At<int>(set, SetBound);
            int at, left = NinoLimits.MaxHashCollisions;
            if (Set)
            {
                var buckets = ObjectFields.At<int[]>(set, SetBuckets);
                ulong multiplier = SetFastMod >= 0 ? ObjectFields.At<ulong>(set, SetFastMod) : 0;
                int hash = (value == null ? 0 : set.Comparer.GetHashCode(value)) & SetMask;
                at = buckets[(int)Bucket((uint)hash, (uint)buckets.Length, multiplier)] - SetBias;
                left++;
            }
            else if (set.Count == bound)
                at = entries[bound - 1].Next;
            else
                return;

            for (; at >= 0; at = entries[at].Next)
                if (--left < 0)
                    NinoThrow.HashFlood(set.GetType());
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
