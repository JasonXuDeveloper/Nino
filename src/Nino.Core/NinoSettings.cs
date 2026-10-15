using System;

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
        /// Registers the handler that ref deserialization calls with each instance it drops from a slot (member,
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
