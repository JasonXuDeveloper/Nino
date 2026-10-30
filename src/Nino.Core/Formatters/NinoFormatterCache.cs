using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

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
        /// Registers the formatter unless one is already registered, and returns whether this call registered it.
        /// Used by generated code: users declare formatters with <c>[assembly: NinoFormatter]</c> or
        /// <c>[NinoMemberFormatter]</c> instead.
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static bool TryRegister<TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            Volatile.Read(ref _instance) == null && TryPublish(new StructFormatter<T, TFormatter>());

        /// <summary>
        /// <see cref="TryRegister{TFormatter}"/> for a formatter of fixed size, which the regular byte array call
        /// uses to write into an array of that size. Used by generated code.
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static bool TryRegisterFixed<TFormatter>() where TFormatter : struct, INinoFixedSizeFormatter<T> =>
            Volatile.Read(ref _instance) == null && TryPublish(new FixedStructFormatter<T, TFormatter>());

        /// <summary>
        /// <see cref="TryRegister{TFormatter}"/> for a formatter with a <c>__Top</c> twin, which the regular top-level
        /// calls write through (see <see cref="INinoTopFormatter{T}"/>). Used by generated code.
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static bool TryRegisterTop<TFormatter, TTop>()
            where TFormatter : struct, INinoFormatter<T>
            where TTop : struct, INinoTopFormatter<T> =>
            Volatile.Read(ref _instance) == null && TryPublish(new TopStructFormatter<T, TFormatter, TTop>());

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
                return Publish(fixedFormatter);
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
            // A constant per T: reference types (and structs holding references) compile none of this. The rest by
            // the resolver's rule (NinoReflection.IsRawShape), so a type is raw here exactly when it is with
            // dynamic code: [NinoType] structs (registered by their generated code, RegisterType above, when they
            // exist), pointer-sized values and the types with a format of their own are left out; other generic
            // structs are raw, as everywhere else.
            if (!NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoReflection.IsRawShape(typeof(T)))
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
            return Publish(created);
        }

        /// <summary>
        /// Stores <paramref name="formatter"/> unless another one was stored first, records the one stored for the
        /// resolver (<see cref="NinoFormatterCache.MarkRegistered"/>) and returns it.
        /// </summary>
        private static NinoFormatter<T> Publish(NinoFormatter<T> formatter)
        {
            Publish(ref formatter);
            return formatter;
        }

        /// <summary>Publishes <paramref name="formatter"/> unless another was first; whether it was.</summary>
        private static bool TryPublish(NinoFormatter<T> formatter) => Publish(ref formatter);

        /// <summary>
        /// Stores <paramref name="formatter"/> unless a formatter is already stored, and sets it to the one stored;
        /// returns whether it was this one.
        /// </summary>
        private static bool Publish(ref NinoFormatter<T> formatter)
        {
            if (NinoNesting.Holds(typeof(T)))
                formatter = formatter.Counted();
            var existing = Interlocked.CompareExchange(ref _instance, formatter, null);
            if (existing != null)
                formatter = existing;
            NinoFormatterCache.MarkRegistered(typeof(T), formatter);
            return existing == null;
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
            NinoUnload.Cache<NinoFormatter>(formatter => formatter.GetType());

        /// <summary>Called after the formatter of <paramref name="type"/> has been stored.</summary>
        public static void MarkRegistered(Type type, NinoFormatter formatter)
        {
            // A new formatter type is one more closed generic a read by type id may find (NinoUnknownIds).
            if (Registered.TryAdd(type, formatter))
                NinoRegistry.Changed();
        }

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

        /// <summary>
        /// <see cref="IsRaw"/>, but not for values that are checked (<see cref="NinoLayout{T}.CopiesAsIs"/>: a
        /// <c>bool</c>, and in <see cref="NinoSettings.Untrusted"/> mode types with fields to check): the top-level
        /// reads take their formatter, which checks them, so that the raw reads of the other types stay a length
        /// check and a load.
        /// </summary>
        public static readonly bool IsRawRead = IsRaw && NinoLayout<T>.CopiesAsIs;

        private static bool Compute()
        {
            // A big-endian host takes the formatter, whose writer or reader rejects it (BitConverter.IsLittleEndian
            // is a JIT-time constant: this field stays one).
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>() || !BitConverter.IsLittleEndian)
                return false;
            return NinoTopFormatter<T>.Formatter.IsRaw;
        }
    }

    /// <summary>
    /// The formatter of <typeparamref name="T"/> as the top-level APIs and <see cref="CachedFormatter{T}"/> use it.
    /// A registered formatter never changes (the first registration wins), so it is read once into a
    /// <c>static readonly</c> field: Tier1 code knows the exact class of <see cref="Formatter"/>, so it calls the
    /// struct formatter directly (and inlines it) instead of virtually.
    /// <para>
    /// The field is never null: a type without a formatter holds a <see cref="NinoMissingFormatter{T}"/>. Read through
    /// <c>Formatter ?? NinoFormatterCache&lt;T&gt;.Instance</c>, the JIT lost the exact class at the merge: every
    /// <see cref="CachedFormatter{T}"/> call (members of generic <c>[NinoType]</c>s and of <c>[NinoType]</c>s generated
    /// in another assembly) compared the object's type and kept a virtual call behind it.
    /// </para>
    /// </summary>
    internal static class NinoTopFormatter<T>
    {
        /// <summary>
        /// The formatter; a <see cref="NinoMissingFormatter{T}"/> when <typeparamref name="T"/> has none, whose calls
        /// report that error.
        /// </summary>
        public static readonly NinoFormatter<T> Formatter = Load();

        /// <summary>Whether <see cref="Formatter"/> is a formatter of <typeparamref name="T"/> (not the missing one).</summary>
        public static readonly bool Exists = !(Formatter is NinoMissingFormatter<T>);

        private static NinoFormatter<T> Load()
        {
            try
            {
                return NinoFormatterCache<T>.Instance;
            }
            catch (Exception)
            {
                // No formatter: the regular path reports it on every call.
                return new NinoMissingFormatter<T>();
            }
        }
    }

    /// <summary>
    /// What <see cref="NinoTopFormatter{T}.Formatter"/> holds for a type that had no formatter when it was read: every
    /// call asks <see cref="NinoFormatterCache{T}.Instance"/> again, which throws the type's error (or finds a formatter
    /// registered since), as the calls did when the field was null. <see cref="NinoFormatter{T}.IsRaw"/> and
    /// <see cref="NinoFormatter{T}.RawArray"/> keep their defaults: static initializers read them, and they read a
    /// missing formatter as none.
    /// </summary>
    internal sealed class NinoMissingFormatter<T> : NinoFormatter<T>
    {
        public override void Serialize(T value, ref Writer writer) =>
            NinoFormatterCache<T>.Instance.Serialize(value, ref writer);

        public override void Deserialize(ref T value, ref Reader reader) =>
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);

        internal override void Write(T value, NinoArrayBufferWriter buffer) =>
            NinoFormatterCache<T>.Instance.Write(value, buffer);

        internal override T Read(ReadOnlySpan<byte> data) => NinoFormatterCache<T>.Instance.Read(data);

        internal override int FixedSize => NinoFormatterCache<T>.Instance.FixedSize;

        internal override byte[] SerializeFixed(T value) => NinoFormatterCache<T>.Instance.SerializeFixed(value);

        internal override bool Sized => NinoFormatterCache<T>.Instance.Sized;

        internal override byte[] SerializeSized(T value) => NinoFormatterCache<T>.Instance.SerializeSized(value);
    }

    /// <summary>
    /// The formatter of <typeparamref name="T"/> when it is an array of raw elements (null otherwise). A static
    /// readonly field: Tier1 code knows its exact type and calls it directly (inlined, with the element size a
    /// constant), without a type guard or interface dispatch. Arrays are reference types, so code that is not
    /// inlined into a caller of the exact type is shared, and this field is a generic dictionary lookup there.
    /// </summary>
    internal static class NinoRawArray<T>
    {
        public static readonly INinoRawArrayFormatter Formatter = Create();

        private static INinoRawArrayFormatter Create()
        {
            // A big-endian host takes the formatter, whose writer or reader rejects it.
            if (!typeof(T).IsArray || typeof(T).GetArrayRank() != 1 || !BitConverter.IsLittleEndian)
                return null;
            // Through the snapshot (a use of T's formatter); none without a formatter: the regular path reports it.
            return NinoTopFormatter<T>.Formatter.RawArray;
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

        /// <summary>
        /// Whether the top-level APIs read values of <typeparamref name="T"/> as their raw bytes: <see cref="Is{T}"/>,
        /// but not for a <c>bool</c> (<see cref="NinoRaw{T}.IsRawRead"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsRead<T>() => !NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoRaw<T>.IsRawRead;
    }
}
