using System;
using System.Collections.Concurrent;
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
        /// <see cref="CachedFormatter{T}"/>. Register before first use.
        /// </summary>
        public static void Register<TFormatter>() where TFormatter : struct, INinoFormatter<T>
        {
            Volatile.Write(ref _instance, new StructFormatter<T, TFormatter>());
            NinoFormatterCache.MarkRegistered(typeof(T));
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
        private static readonly ConcurrentDictionary<Type, bool> Registered = new ConcurrentDictionary<Type, bool>();

        /// <summary>Called after the formatter of <paramref name="type"/> has been stored.</summary>
        public static void MarkRegistered(Type type) => Registered.TryAdd(type, true);

        /// <summary>Same as <see cref="NinoFormatterCache{T}.IsRegistered"/>.</summary>
        public static bool IsRegistered(Type type) => Registered.ContainsKey(type);
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
            try
            {
                return NinoFormatterCache<T>.Instance.IsRaw;
            }
            catch
            {
                return false; // no formatter: the regular path reports it
            }
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
