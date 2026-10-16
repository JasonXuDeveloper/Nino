using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// The formatter used for <typeparamref name="T"/> by the top-level <see cref="NinoSerializer"/> /
    /// <see cref="NinoDeserializer"/> APIs and by <see cref="CachedFormatter{T}"/>.
    /// <para>
    /// Generated code registers the formatters of <c>[NinoType]</c>s. Anything else (collections, nullables,
    /// tuples, generic <c>[NinoType]</c>s...) is built on first use. Reading the instance is a static field load.
    /// </para>
    /// </summary>
    public static class NinoFormatterCache<T>
    {
        private static NinoFormatter<T> _instance;

        // Written under NinoFormatterCache.Gate only (registration and the first use), read by NinoTopFormatter<T>
        // once: a formatter registered with Register, and whether T has been serialized or deserialized yet.
        private static bool _replaced;
        private static bool _used;

        /// <summary>The formatter for <typeparamref name="T"/>; resolved on first access.</summary>
        internal static NinoFormatter<T> Instance
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _instance ?? Resolve();
        }

        /// <summary>Whether a formatter has been registered or resolved.</summary>
        public static bool IsRegistered => Volatile.Read(ref _instance) != null;

        /// <summary>
        /// Registers (or replaces) the formatter for <typeparamref name="T"/>, used by the top-level APIs and by
        /// <see cref="CachedFormatter{T}"/>. Register at startup: once <typeparamref name="T"/> has been serialized
        /// or deserialized, the formatter in use is fixed, and registering one throws.
        /// </summary>
        /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has already been serialized or
        /// deserialized.</exception>
        public static void Register<TFormatter>() where TFormatter : struct, INinoFormatter<T>
        {
            lock (NinoFormatterCache.Gate)
            {
                if (_used)
                    NinoThrow.RegisteredAfterUse(typeof(T));
                Volatile.Write(ref _instance, new StructFormatter<T, TFormatter>());
                _replaced = true;
            }

            NinoFormatterCache.MarkRegistered(typeof(T));
        }

        /// <summary>
        /// The first use of <typeparamref name="T"/> by the top-level APIs or <see cref="CachedFormatter{T}"/>: from
        /// now on <see cref="Register{TFormatter}"/> throws, so the formatter and the flag returned stay valid.
        /// Null when <typeparamref name="T"/> has no formatter (its callers then report it through
        /// <see cref="Instance"/>).
        /// </summary>
        internal static NinoFormatter<T> TakeSnapshot(out bool replaced)
        {
            try
            {
                _ = Instance;
            }
            catch (Exception)
            {
                // No formatter: the regular path reports it on every call.
            }

            lock (NinoFormatterCache.Gate)
            {
                _used = true;
                replaced = _replaced;
                return Volatile.Read(ref _instance);
            }
        }

        /// <summary>Registers the formatter unless one is already registered. Used by generated code.</summary>
        public static void TryRegister<TFormatter>() where TFormatter : struct, INinoFormatter<T>
        {
            if (Volatile.Read(ref _instance) != null)
                return;
            Interlocked.CompareExchange(ref _instance, new StructFormatter<T, TFormatter>(), null);
            NinoFormatterCache.MarkRegistered(typeof(T));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoFormatter<T> Resolve()
        {
            NinoRegistry.RegisterType(typeof(T));
            var instance = Volatile.Read(ref _instance);
            if (instance != null)
                return instance;

            var formatterType = NinoFormatterResolver.GetFormatterType(typeof(T), topLevel: true);
            if (formatterType == null)
                NinoThrow.NoFormatter(typeof(T));

            var created = (NinoFormatter<T>)Activator.CreateInstance(
                typeof(StructFormatter<,>).MakeGenericType(typeof(T), formatterType));
            Interlocked.CompareExchange(ref _instance, created, null);
            NinoFormatterCache.MarkRegistered(typeof(T));
            return _instance;
        }
    }

    /// <summary>
    /// The types whose <see cref="NinoFormatterCache{T}"/> holds a formatter (registered or resolved), for the
    /// resolver, which only has a <see cref="Type"/> and would otherwise need reflection to ask.
    /// </summary>
    internal static class NinoFormatterCache
    {
        /// <summary>Orders registrations with the first use of a type (see <see cref="NinoTopFormatter{T}"/>).</summary>
        internal static readonly object Gate = new object();

        private static readonly ConcurrentDictionary<Type, bool> Registered = new ConcurrentDictionary<Type, bool>();

        /// <summary>Called after the formatter of <paramref name="type"/> has been stored.</summary>
        public static void MarkRegistered(Type type) => Registered.TryAdd(type, true);

        /// <summary>Same as <see cref="NinoFormatterCache{T}.IsRegistered"/>.</summary>
        public static bool IsRegistered(Type type) => Registered.ContainsKey(type);

        /// <summary>Every type with a formatter so far (a snapshot), for the cold path of polymorphic reads.</summary>
        public static ICollection<Type> RegisteredTypes => Registered.Keys;
    }

    /// <summary>
    /// Whether the registered formatter of <typeparamref name="T"/> writes its raw memory. Evaluated once, on the first
    /// top-level call for a value type; as a <c>static readonly</c> field it is a JIT-time constant afterwards, so the
    /// top-level APIs copy raw values directly instead of calling the formatter. Register custom formatters for
    /// such types before first use.
    /// </summary>
    internal static class NinoRaw<T>
    {
        public static readonly bool IsRaw = Compute();

        private static bool Compute()
        {
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
                return false;
            // Through the snapshot: copying raw values is a use of T's formatter, after which it cannot be replaced.
            var formatter = NinoTopFormatter<T>.Formatter;
            return formatter != null && formatter.IsRaw;
        }
    }

    /// <summary>
    /// The formatter of <typeparamref name="T"/> as the top-level APIs and <see cref="CachedFormatter{T}"/> use it, taken
    /// on the first such use (<see cref="NinoFormatterCache{T}.TakeSnapshot"/>), after which registering another
    /// throws. Both fields are <c>static readonly</c>: Tier1 code folds <see cref="Replaced"/> to a constant (the
    /// intercepted calls of <see cref="NinoDirect"/> lose their check) and knows the exact class of
    /// <see cref="Formatter"/>, so it calls the struct formatter directly (and inlines it) instead of virtually.
    /// </summary>
    internal static class NinoTopFormatter<T>
    {
        /// <summary>The formatter; null when <typeparamref name="T"/> has none (use <see cref="Get"/>).</summary>
        public static readonly NinoFormatter<T> Formatter = NinoFormatterCache<T>.TakeSnapshot(out Replaced);

        /// <summary>Whether <see cref="Formatter"/> was registered with <see cref="NinoFormatterCache{T}.Register{TFormatter}"/>.</summary>
        public static readonly bool Replaced;

        /// <summary><see cref="Formatter"/>, or the error of a type without one.</summary>
        public static NinoFormatter<T> Get
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Formatter ?? NinoFormatterCache<T>.Instance;
        }
    }

    internal static class NinoRaw
    {
        /// <summary>
        /// Whether the top-level APIs copy values of <typeparamref name="T"/> as their raw bytes. Folds to a
        /// constant once inlined; for reference types <see cref="NinoRaw{T}"/> is never touched.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Is<T>() => !NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRaw;
    }
}
