using System;
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
        public static NinoFormatter<T> Instance
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _instance ?? Resolve();
        }

        /// <summary>Whether a formatter has been registered or resolved.</summary>
        public static bool IsRegistered => Volatile.Read(ref _instance) != null;

        /// <summary>Registers (or replaces) the formatter for <typeparamref name="T"/>.</summary>
        public static void Register(NinoFormatter<T> formatter)
        {
            Volatile.Write(ref _instance, formatter ?? throw new ArgumentNullException(nameof(formatter)));
        }

        /// <summary>Registers (or replaces) the formatter for <typeparamref name="T"/>.</summary>
        public static void Register<TFormatter>() where TFormatter : struct, INinoFormatter<T>
        {
            Register(new StructFormatter<T, TFormatter>());
        }

        /// <summary>Registers the formatter unless one is already registered. Used by generated code.</summary>
        public static void TryRegister<TFormatter>() where TFormatter : struct, INinoFormatter<T>
        {
            if (Volatile.Read(ref _instance) == null)
                Interlocked.CompareExchange(ref _instance, new StructFormatter<T, TFormatter>(), null);
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
            return _instance;
        }
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
}
