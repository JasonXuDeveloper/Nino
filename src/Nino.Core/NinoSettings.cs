using System;
using System.Collections.Generic;

namespace Nino.Core
{
    /// <summary>
    /// Every process-wide setting of the Nino runtime.
    /// <para>
    /// <b>Set before first use.</b> Settings are plain fields read without synchronization, so configure them at
    /// startup, before the first serialization call on any thread. A change made while other threads serialize
    /// takes effect at some point, not atomically with respect to calls in flight; pool sizes are applied when a
    /// pool is created.
    /// </para>
    /// <para>
    /// Some values are stored next to the code that reads them on hot paths (a recycler handler in
    /// <see cref="NinoRecycler{T}"/>, one static field per closed type, so recycling needs no lookup); this class is
    /// still the only place that writes them.
    /// </para>
    /// <para>
    /// Not settings: formatter, type and polymorphic registrations (made by generated code, see
    /// <see cref="NinoFormatterAttribute"/>), and <c>WEAK_VERSION_TOLERANCE</c>, a compile-time symbol of the
    /// project that defines the types.
    /// </para>
    /// </summary>
    public static class NinoSettings
    {
        internal const int DefaultPoolMaxRetainedValue = 256;

        /// <summary>Default of <see cref="MaxDepth"/>.</summary>
        public const int DefaultMaxDepth = 512;

        /// <summary>Default of <see cref="MaxHashCollisions"/>.</summary>
        public const int DefaultMaxHashCollisions = 100;

        private static readonly object Lock = new object();
        private static bool _frozen;
        private static int _maxDepth = DefaultMaxDepth;
        private static int _maxCollectionLength = int.MaxValue;
        private static int _maxHashCollisions = DefaultMaxHashCollisions;

        /// <summary>
        /// How deep objects may nest in data being read or written before Nino throws (a
        /// <see cref="NinoDataException"/> when reading, an <see cref="InvalidOperationException"/> when writing).
        /// Counted are class bodies, struct bodies that can hold themselves and collections that hold themselves.
        /// Class bodies past 128 levels that Nino can create before reading their members are read and written later,
        /// from a shallow point (they use no stack, and need no limit); this bounds everything else. Default
        /// <see cref="DefaultMaxDepth"/> (512); at least 128. The thread's stack is checked as well: a small stack
        /// fails earlier.
        /// <para>
        /// <b>Set before first use</b>, like every limit here: read and write paths take it as a constant, so it is
        /// frozen the first time one of them looks at it, and setting it afterwards throws
        /// <see cref="InvalidOperationException"/>.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is below 128.</exception>
        /// <exception cref="InvalidOperationException">Nino has already used the setting.</exception>
        public static int MaxDepth
        {
            get => _maxDepth;
            set
            {
                if (value < NinoDepth.Inline)
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        $"Nino: the depth limit must be at least {NinoDepth.Inline}.");
                lock (Lock)
                {
                    CheckNotFrozen(nameof(MaxDepth));
                    _maxDepth = value;
                }
            }
        }

        /// <summary>
        /// The most elements (or key/value pairs) one collection or array in the data may claim;
        /// a longer one throws <see cref="NinoDataException"/> before anything is allocated for it. For a
        /// multi-dimensional array it is the total element count. Default <see cref="int.MaxValue"/>: no limit
        /// besides the data itself (every length is already checked against the bytes left, so a few bytes cannot
        /// claim a large collection). Lower it to bound the memory a valid but large payload may take.
        /// <b>Set before first use</b> (see <see cref="MaxDepth"/>).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        /// <exception cref="InvalidOperationException">Nino has already used the setting.</exception>
        public static int MaxCollectionLength
        {
            get => _maxCollectionLength;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "Nino: the collection length limit cannot be negative.");
                lock (Lock)
                {
                    CheckNotFrozen(nameof(MaxCollectionLength));
                    _maxCollectionLength = value;
                }
            }
        }

        /// <summary>
        /// The most keys a key read from the data may find in its hash bucket when Nino adds it to a hash table it
        /// fills: <see cref="Dictionary{TKey,TValue}"/>, <see cref="HashSet{T}"/>, <c>ILookup</c>, and (with
        /// Nino.Immutable) <c>ImmutableDictionary</c> and <c>ImmutableHashSet</c>, where it is the most keys with one
        /// hash code. More throws <see cref="NinoDataException"/>: data built to make every key collide (hash
        /// flooding) would otherwise take time quadratic in its size to read. Default
        /// <see cref="DefaultMaxHashCollisions"/> (100), far above what keys with a reasonable hash code ever reach
        /// (a bucket holds about one key); raise it for keys whose hash codes are poor on purpose or by accident (a
        /// struct key without its own <c>GetHashCode</c>, where the runtime may combine its fields so that many keys
        /// share one). At least 1. The counting is free: Nino walks the bucket anyway. Where the runtime fills the
        /// table itself (a collection read into an existing one), Nino walks the new key's bucket after adding it.
        /// String keys are left to the tables that switch to a randomized comparer after 100 collisions on their own
        /// (.NET's <c>Dictionary</c> and <c>HashSet</c>, .NET Framework's <c>Dictionary</c>).
        /// <c>ConcurrentDictionary</c> is not covered: it keeps its buckets to itself.
        /// <b>Set before first use</b> (see <see cref="MaxDepth"/>).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
        /// <exception cref="InvalidOperationException">Nino has already used the setting.</exception>
        public static int MaxHashCollisions
        {
            get => _maxHashCollisions;
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "Nino: the hash collision limit must be at least 1.");
                lock (Lock)
                {
                    CheckNotFrozen(nameof(MaxHashCollisions));
                    _maxHashCollisions = value;
                }
            }
        }

        private static void CheckNotFrozen(string name)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    $"Nino: NinoSettings.{name} must be set before the first serialization or deserialization: " +
                    "read and write paths have already taken it as a constant.");
        }

        /// <summary>The limits as they are now, from now on fixed (<see cref="NinoLimits"/>).</summary>
        internal static Snapshot Freeze()
        {
            lock (Lock)
            {
                _frozen = true;
                return new Snapshot
                {
                    MaxDepth = _maxDepth,
                    MaxCollectionLength = _maxCollectionLength,
                    MaxHashCollisions = _maxHashCollisions,
                };
            }
        }

        internal sealed class Snapshot
        {
            public int MaxDepth;
            public int MaxCollectionLength;
            public int MaxHashCollisions;
        }

        private static int _defaultPoolMaxRetained = DefaultPoolMaxRetainedValue;

        /// <summary>
        /// The <see cref="NinoPool{T}.MaxRetained"/> of pools created from now on (a pool is created when its type is
        /// first deserialized or returned). Default 256. Use <see cref="SetPoolMaxRetained{T}"/> for a pool that may
        /// already exist.
        /// </summary>
        public static int DefaultPoolMaxRetained
        {
            get => _defaultPoolMaxRetained;
            set => _defaultPoolMaxRetained = CheckMaxRetained(value);
        }

        /// <summary>
        /// Most instances the shared stack of <typeparamref name="T"/>'s pool keeps; more are left to the garbage
        /// collector. Each thread additionally keeps up to 64 of its own.
        /// </summary>
        public static void SetPoolMaxRetained<T>(int maxRetained) where T : class, INinoPooled =>
            NinoPool<T>.Shared.MaxRetained = CheckMaxRetained(maxRetained);

        /// <summary>
        /// Registers the handler that reuse deserialization calls with each instance it drops from a slot (member,
        /// element, key or value) declared as <typeparamref name="T"/> (see <see cref="NinoRecycler"/> for when that
        /// happens). It replaces returning <see cref="NinoPooledAttribute"/> instances to their pool for that
        /// declared type. Pass null to remove it. Only register a handler for objects that are exclusively owned by
        /// the deserialized object graph.
        /// </summary>
        public static void SetRecycler<T>(Action<T> handler) => NinoRecycler<T>.Handler = handler;

        private static int CheckMaxRetained(int value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            return value;
        }
    }
}
