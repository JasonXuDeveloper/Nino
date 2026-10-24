using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nino.Core.CompilerServices;

namespace Nino.Core
{
    internal sealed class SubtypeHandler<TSub, TFormatter> : NinoSubtypeHandler
        where TFormatter : struct, INinoBodyFormatter<TSub>
    {
        public SubtypeHandler(int id) : base(typeof(TSub), id)
        {
        }

        // For a class, NinoObject.WriteBody/ReadBody spelled out (as in NinoObject.WriteExact): this is shared code
        // (TSub is a reference type), where the inlined generic method would look up its generic context on every
        // call, and ReadBody's copy of the reader for a deep body (in memory here anyway) would be zeroed in the
        // prolog. Only the deep path goes through ClassBody, out of line.
        public override void Serialize(object value, ref Writer writer)
        {
            writer.UnsafeWrite(Id);
            // Only class bodies count toward the depth (NinoDepth).
            if (typeof(TSub).IsValueType)
            {
                default(TFormatter).WriteBody(As(ref value), ref writer);
                return;
            }

            int depth = writer.Depth;
            if (depth >= NinoDepth.Inline)
            {
                WriteDeep(value, ref writer);
                return;
            }

            if ((default(TFormatter).Traits & NinoBody.Nests) == 0)
            {
                default(TFormatter).WriteBody(As(ref value), ref writer);
                return;
            }

            writer.Depth = depth + 1;
            default(TFormatter).WriteBody(As(ref value), ref writer);
            writer.ExitBody(depth);
        }

        public override void Deserialize(ref object value, ref Reader reader)
        {
            if (!typeof(TSub).IsValueType)
            {
                int depth = reader.Depth;
                if (depth >= NinoDepth.Inline)
                {
                    ReadDeep(ref value, ref reader);
                    return;
                }

                // value is null or exactly a TSub, and the body stores a TSub: the slot is read as a TSub slot.
                if ((default(TFormatter).Traits & NinoBody.Nests) == 0)
                {
                    default(TFormatter).ReadBody(ref Unsafe.As<object, TSub>(ref value), ref reader);
                    return;
                }

                reader.Depth = depth + 1;
                default(TFormatter).ReadBody(ref Unsafe.As<object, TSub>(ref value), ref reader);
                reader.ExitBody(depth);
                return;
            }

            TSub sub = value == null ? default : As(ref value);
            default(TFormatter).ReadBody(ref sub, ref reader);
            value = sub;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteDeep(object value, ref Writer writer) =>
            NinoObject.WriteBody<object, ClassBody>(value, ref writer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadDeep(ref object value, ref Reader reader) =>
            NinoObject.ReadBody<object, ClassBody>(ref value, ref reader);

        /// <summary><typeparamref name="TFormatter"/> over <c>object</c>, for a class <typeparamref name="TSub"/>.</summary>
        private struct ClassBody : INinoBodyFormatter<object>
        {
            public int TypeId => default(TFormatter).TypeId;

            public int Traits => default(TFormatter).Traits;

            public object Create() => default(TFormatter).Create();

            public void WriteBody(object value, ref Writer writer) =>
                default(TFormatter).WriteBody(Unsafe.As<object, TSub>(ref value), ref writer);

            public void ReadBody(ref object value, ref Reader reader)
            {
                TSub sub = value == null ? default : Unsafe.As<object, TSub>(ref value);
                default(TFormatter).ReadBody(ref sub, ref reader);
                value = sub;
            }

            public void Serialize(object value, ref Writer writer) => WriteBody(value, ref writer);

            public void Deserialize(ref object value, ref Reader reader) => ReadBody(ref value, ref reader);
        }

        // The caller guarantees the exact runtime type, so reference types skip the cast check.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TSub As(ref object value) =>
            typeof(TSub).IsValueType ? (TSub)value : Unsafe.As<object, TSub>(ref value);
    }

    /// <summary>
    /// Handler of a class marked <c>trackReferences</c> (or deriving from one). Through a declared type that writes
    /// no reference header (<c>object</c>, a base class or interface that is not tracked) it writes
    /// <c>type id | reference header | body</c>, so the instance keeps its identity whatever slot holds it; a back
    /// reference is the type id and the index. Through a tracked declared type the header comes first
    /// (<c>reference header | type id | body</c>, <see cref="SerializeReferenced"/>). Untracked types keep
    /// <see cref="SubtypeHandler{TSub,TFormatter}"/>, so their dispatch does not test for tracking.
    /// </summary>
    internal sealed class TrackedSubtypeHandler<TSub, TFormatter> : NinoSubtypeHandler
        where TSub : class
        where TFormatter : struct, INinoBodyFormatter<TSub>
    {
        public TrackedSubtypeHandler(int id) : base(typeof(TSub), id)
        {
        }

        public override void Serialize(object value, ref Writer writer)
        {
            writer.UnsafeWrite(Id);
            if (writer.WriteReference(value))
                NinoObject.WriteBody<TSub, TFormatter>(Unsafe.As<TSub>(value), ref writer);
        }

        public override void Deserialize(ref object value, ref Reader reader)
        {
            var sub = Unsafe.As<TSub>(value);
            // A back reference must be a TSub (ReadReference checks it); the old instance is not recycled, since
            // it may be referenced elsewhere in the graph.
            if (reader.ReadReference(ref sub, out int index))
            {
                NinoObject.ReadBody<TSub, TFormatter>(ref sub, ref reader);
                reader.EndReference(index, sub);
            }

            value = sub;
        }

        internal override void SerializeReferenced(object value, ref Writer writer)
        {
            writer.UnsafeWrite(Id);
            NinoObject.WriteBody<TSub, TFormatter>(Unsafe.As<TSub>(value), ref writer);
        }

        internal override void DeserializeReferenced(ref object value, ref Reader reader)
        {
            var sub = Unsafe.As<TSub>(value);
            NinoObject.ReadBody<TSub, TFormatter>(ref sub, ref reader);
            value = sub;
        }
    }

    /// <summary>Registration of the types that can be written polymorphically.</summary>
    public static class NinoPolymorphic
    {
        /// <summary>
        /// Registers <typeparamref name="TSub"/> with its generated formatter, once. Every base type finds it
        /// on first use, so a type is not registered once per base type.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Register<TSub, TFormatter>(int id) where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            NinoPolymorphic<object>.Register(new SubtypeHandler<TSub, TFormatter>(id));
        }

        /// <summary>
        /// Registers a class marked <c>trackReferences</c> (or deriving from one): written through a declared type
        /// that is not tracked, its reference header follows the type id.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterTracked<TSub, TFormatter>(int id)
            where TSub : class
            where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            NinoPolymorphic<object>.Register(new TrackedSubtypeHandler<TSub, TFormatter>(id));
        }

        /// <summary>Registers <typeparamref name="T"/> for polymorphic use; see <see cref="Register(Type)"/>.</summary>
        public static bool Register<T>() => Register(typeof(T));

        /// <summary>
        /// Registers a <c>[NinoType]</c>, typically a closed generic such as <c>Box&lt;int&gt;</c>, so that it can be
        /// written through a base type and read back from its type id.
        /// <para>
        /// Generated code registers the closed generic <c>[NinoType]</c>s its compilation names (member types, base
        /// types, type arguments of <c>NinoSerializer</c>/<c>NinoDeserializer</c> calls, and the closed types of
        /// arguments of those), and writing an instance registers its type. Call this at startup for an
        /// instantiation that a process reads but never names nor writes (for example one only created by another
        /// assembly or through reflection). Generic types are closed with <c>MakeGenericType</c>, which needs dynamic
        /// code: without it (NativeAOT), only the generated registrations are found.
        /// </para>
        /// Returns false when the type cannot be written polymorphically (abstract, open, or not a <c>[NinoType]</c>).
        /// </summary>
        public static bool Register(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                return false;
            if (NinoPolymorphic<object>.FindRegistered(type) != null)
                return true;

            // Non-generic [NinoType]s, and the closed generics the generator saw, are registered by generated code.
            NinoRegistry.RegisterType(type);
            if (NinoPolymorphic<object>.FindRegistered(type) != null)
                return true;
            if (!type.IsGenericType)
                return false;
#if NET8_0_OR_GREATER
            if (!RuntimeFeature.IsDynamicCodeSupported)
                return false;
#endif
            return RegisterGeneric(type);
        }

        /// <summary>Registers a closed generic <c>[NinoType]</c> from its formatter definition.</summary>
        [RequiresDynamicCode(NinoFormatterResolver.DynamicCodeMessage)]
        // IL2076; IL2075 from the .NET 8 analyzer, which reports the same thing on MakeGenericType itself.
        [UnconditionalSuppressMessage("Trimming", "IL2076",
            Justification = NinoFormatterResolver.ValueTypeConstraintJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = NinoFormatterResolver.ValueTypeConstraintJustification)]
        private static bool RegisterGeneric(Type type)
        {
            if (!NinoReflection.IsNinoType(type))
                return false;

            // The formatter definition of a generic [NinoType] is an [assembly: NinoFormatter] registration.
            var formatter = NinoFormatterResolver.GetFormatterType(type);
            if (formatter == null || !typeof(INinoBodyFormatter<>).MakeGenericType(type).IsAssignableFrom(formatter))
                return false;
            var handler = !type.IsValueType && NinoReflection.IsTracked(type)
                ? typeof(TrackedSubtypeHandler<,>)
                : typeof(SubtypeHandler<,>);
            NinoPolymorphic<object>.Register((NinoSubtypeHandler)Activator.CreateInstance(
                handler.MakeGenericType(type, formatter), NinoTypeId.Of(type)));
            return true;
        }

        /// <summary>
        /// Registers the closed generic <c>[NinoType]</c> with type id <paramref name="id"/> among the types whose
        /// formatter this process has resolved; false if there is none. The last resort of a read by type id.
        /// </summary>
        internal static bool RegisterResolvedGeneric(int id)
        {
            foreach (var type in NinoFormatterCache.RegisteredTypes)
            {
                if (type.IsGenericType && !type.IsAbstract && !type.IsInterface &&
                    NinoTypeId.Of(type) == id && NinoReflection.IsNinoType(type))
                    return Register(type);
            }

            return false;
        }
    }
}
