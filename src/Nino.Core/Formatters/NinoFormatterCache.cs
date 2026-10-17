using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
        /// Registers the formatter unless one is already registered. Used by generated code: users declare
        /// formatters with <c>[assembly: NinoFormatter]</c> or <c>[NinoCustomFormatter]</c> instead.
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static void TryRegister<TFormatter>() where TFormatter : struct, INinoFormatter<T>
        {
            if (Volatile.Read(ref _instance) != null)
                return;
            Interlocked.CompareExchange(ref _instance, new StructFormatter<T, TFormatter>(), null);
            NinoFormatterCache.MarkRegistered(typeof(T), _instance);
        }

        /// <summary>
        /// <see cref="TryRegister{TFormatter}"/> for a formatter of fixed size, which the regular byte array call
        /// uses to write into an array of that size. Used by generated code.
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static void TryRegisterFixed<TFormatter>() where TFormatter : struct, INinoFixedSizeFormatter<T>
        {
            if (Volatile.Read(ref _instance) != null)
                return;
            Interlocked.CompareExchange(ref _instance, new FixedStructFormatter<T, TFormatter>(), null);
            NinoFormatterCache.MarkRegistered(typeof(T), _instance);
        }

        /// <summary>
        /// <see cref="TryRegister{TFormatter}"/> for a formatter with a <c>__Top</c> twin, which the regular top-level
        /// calls write through (see <see cref="INinoTopFormatter{T}"/>). Used by generated code.
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static void TryRegisterTop<TFormatter, TTop>()
            where TFormatter : struct, INinoFormatter<T>
            where TTop : struct, INinoTopFormatter<T>
        {
            if (Volatile.Read(ref _instance) != null)
                return;
            Interlocked.CompareExchange(ref _instance, new TopStructFormatter<T, TFormatter, TTop>(), null);
            NinoFormatterCache.MarkRegistered(typeof(T), _instance);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoFormatter<T> Resolve()
        {
            NinoRegistry.RegisterType(typeof(T));
            var instance = Volatile.Read(ref _instance);
            if (instance != null)
                return instance;
#if NET8_0_OR_GREATER
            // Building a formatter needs dynamic code: without it (NativeAOT), only generated registrations exist,
            // and the formatters that need no generic type closed at run time.
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                var fixedFormatter = CreateWithoutDynamicCode();
                if (fixedFormatter == null)
                    NinoThrow.NoGeneratedFormatter(typeof(T));
                Interlocked.CompareExchange(ref _instance, fixedFormatter, null);
                NinoFormatterCache.MarkRegistered(typeof(T), _instance);
                return _instance;
            }
#endif
            return Build();
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// What the resolver builds for <typeparamref name="T"/> when that needs no type made at run time: strings,
        /// <c>object</c>, and raw unmanaged values (which generated code does not register: a generic
        /// <c>[NinoType]</c>'s <c>CachedFormatter&lt;T&gt;</c> of an <c>int</c> lands here). Null for anything else.
        /// </summary>
        private static NinoFormatter<T> CreateWithoutDynamicCode()
        {
            if (typeof(T) == typeof(string))
                return (NinoFormatter<T>)(object)new StructFormatter<string, StringFormatter>();
            if (typeof(T) == typeof(object))
                return (NinoFormatter<T>)(object)new StructFormatter<object, ObjectFormatter>();
            // A constant per T: reference types (and structs holding references) compile none of this. Generic
            // structs are left out (their formats are their own: Nullable, KeyValuePair, tuples, generic [NinoType]s),
            // and so are [NinoType] structs, registered by their generated code (RegisterType, above) when they exist.
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && !typeof(T).IsGenericType &&
                typeof(T) != typeof(IntPtr) && typeof(T) != typeof(UIntPtr) && typeof(T) != typeof(DateTimeOffset))
                return new StructFormatter<T, RawValueFormatter<T>>();
            return null;
        }
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        [RequiresDynamicCode(NinoFormatterResolver.DynamicCodeMessage)]
        // IL2076; IL2075 from the .NET 8 analyzer, which reports the same thing on MakeGenericType itself.
        [UnconditionalSuppressMessage("Trimming", "IL2076",
            Justification = NinoFormatterResolver.ValueTypeConstraintJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = NinoFormatterResolver.ValueTypeConstraintJustification)]
        private static NinoFormatter<T> Build()
        {
            var formatterType = NinoFormatterResolver.GetFormatterType(typeof(T), topLevel: true);
            if (formatterType == null)
                NinoThrow.NoFormatter(typeof(T));

            var created = (NinoFormatter<T>)Activator.CreateInstance(
                typeof(StructFormatter<,>).MakeGenericType(typeof(T), formatterType));
            Interlocked.CompareExchange(ref _instance, created, null);
            NinoFormatterCache.MarkRegistered(typeof(T), _instance);
            return _instance;
        }
    }

    /// <summary>
    /// The types whose <see cref="NinoFormatterCache{T}"/> holds a formatter (registered or resolved), for the
    /// resolver, which only has a <see cref="Type"/> and would otherwise need reflection to ask.
    /// </summary>
    internal static class NinoFormatterCache
    {
        // The formatter itself too: the boxed APIs reach it from a Type without closing a generic type at run time.
        private static readonly ConcurrentDictionary<Type, NinoFormatter> Registered =
            new ConcurrentDictionary<Type, NinoFormatter>();

        /// <summary>Called after the formatter of <paramref name="type"/> has been stored.</summary>
        public static void MarkRegistered(Type type, NinoFormatter formatter) => Registered.TryAdd(type, formatter);

        /// <summary>Same as <see cref="NinoFormatterCache{T}.IsRegistered"/>.</summary>
        public static bool IsRegistered(Type type) => Registered.ContainsKey(type);

        /// <summary>The formatter stored for <paramref name="type"/>, or null.</summary>
        public static NinoFormatter Get(Type type) => Registered.TryGetValue(type, out var formatter) ? formatter : null;

        /// <summary>Every type with a formatter so far (a snapshot), for the cold path of polymorphic reads.</summary>
        public static ICollection<Type> RegisteredTypes => Registered.Keys;
    }

    /// <summary>
    /// Whether the registered formatter of <typeparamref name="T"/> writes its raw memory. Evaluated once, on the first
    /// top-level call for a value type; as a <c>static readonly</c> field it is a JIT-time constant afterwards, so the
    /// top-level APIs copy raw values directly instead of calling the formatter.
    /// </summary>
    internal static class NinoRaw<T>
    {
        public static readonly bool IsRaw = Compute();

        private static bool Compute()
        {
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
                return false;
            var formatter = NinoTopFormatter<T>.Formatter;
            return formatter != null && formatter.IsRaw;
        }
    }

    /// <summary>
    /// The formatter of <typeparamref name="T"/> as the top-level APIs and <see cref="CachedFormatter{T}"/> use it.
    /// A registered formatter never changes (the first registration wins), so it is read once into a
    /// <c>static readonly</c> field: Tier1 code knows the exact class of <see cref="Formatter"/>, so it calls the
    /// struct formatter directly (and inlines it) instead of virtually.
    /// </summary>
    internal static class NinoTopFormatter<T>
    {
        /// <summary>The formatter; null when <typeparamref name="T"/> has none (use <see cref="Get"/>).</summary>
        public static readonly NinoFormatter<T> Formatter = Load();

        private static NinoFormatter<T> Load()
        {
            try
            {
                return NinoFormatterCache<T>.Instance;
            }
            catch (Exception)
            {
                // No formatter: the regular path reports it on every call.
                return null;
            }
        }

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
