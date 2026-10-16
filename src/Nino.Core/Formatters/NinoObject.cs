using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Header handling for generated class formatters. A generated formatter implements
    /// <see cref="INinoBodyFormatter{T}"/> and forwards <c>Serialize</c>/<c>Deserialize</c> to one of these.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class NinoObject
    {
        /// <summary>
        /// Generated subtype dispatch: writes the type id and the body of a value whose runtime type the caller
        /// checked to be exactly <typeparamref name="TSub"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteExact<TSub, TFormatter>(object value, ref Writer writer)
            where TSub : class
            where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            writer.UnsafeWrite(default(TFormatter).TypeId);
            WriteBody<TSub, TFormatter>(Unsafe.As<TSub>(value), ref writer);
        }

        /// <summary>
        /// Generated subtype dispatch: reads the body of <typeparamref name="TSub"/> (its type id consumed) into a
        /// slot declared as <typeparamref name="TBase"/>, reusing the instance only if it is exactly
        /// <typeparamref name="TSub"/>. The type check is inlined into the (non-shared) dispatch, where it is one
        /// method table compare; the body is not, since a dispatch method holding every subtype's body reads slower.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadExact<TBase, TSub, TFormatter>(ref TBase value, ref Reader reader)
            where TBase : class
            where TSub : class, TBase
            where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            var old = value;
            if (old != null && old.GetType() != typeof(TSub))
                NinoRecycler.Discard(ref value);
            var sub = Unsafe.As<TSub>(value);
            ReadBodyOutOfLine<TSub, TFormatter>(ref sub, ref reader);
            value = sub;
        }

        /// <summary>
        /// Generated subtype dispatch: a slot of a derived type seen as a slot of its base, whose dispatch reads it
        /// (followed by <see cref="EnsureSubtype{T}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TBase AsBaseSlot<TSub, TBase>(ref TSub slot)
            where TSub : class, TBase
            where TBase : class =>
            ref Unsafe.As<TSub, TBase>(ref slot);

        /// <summary>
        /// Generated subtype dispatch: after a base's dispatch read a slot declared as <typeparamref name="T"/>,
        /// rejects data that named a type of the base that is not a <typeparamref name="T"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureSubtype<T>(object value) where T : class
        {
            if (value != null && !(value is T))
                NinoThrow.NotASubtype(typeof(T), value.GetType());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadBodyOutOfLine<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T> =>
            ReadBody<T, TFormatter>(ref value, ref reader);

        /// <summary>
        /// Writes the body of a class, or defers it when <see cref="NinoDepth.Inline"/> bodies are open (see
        /// <see cref="NinoDepth"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBody<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (writer.Depth >= NinoDepth.Inline)
            {
                WriteDeep<T, TFormatter>(value, ref writer);
                return;
            }

            writer.Depth++;
            default(TFormatter).WriteBody(value, ref writer);
            writer.ExitBody();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteDeep<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (default(TFormatter).Deferrable)
            {
                writer.Defer(value, NinoDeferredBody<T, TFormatter>.Instance);
                return;
            }

            if (writer.Depth >= NinoDepth.Max)
                NinoThrow.TooDeep(typeof(T));
            writer.Depth++;
            default(TFormatter).WriteBody(value, ref writer);
            writer.ExitBody();
        }

        /// <summary>
        /// Reads the body of a class (into <paramref name="value"/>, null or exactly <typeparamref name="T"/>), or,
        /// as deep as <see cref="WriteBody{T,TFormatter}"/> deferred it, creates the instance and reads the body
        /// later.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadBody<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (reader.Depth >= NinoDepth.Inline)
            {
                // Through a copy: passing the reader's address to a call that is not inlined would keep the JIT
                // from holding its fields in registers.
                var deep = reader;
                ReadDeep<T, TFormatter>(ref value, ref deep);
                reader = deep;
                return;
            }

            reader.Depth++;
            default(TFormatter).ReadBody(ref value, ref reader);
            reader.ExitBody();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadDeep<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (default(TFormatter).Deferrable)
            {
                if (value == null)
                    value = default(TFormatter).Create();
                // A tracked object is recorded now: back references to it may come before its body.
                reader.SetReference(value);
                reader.Defer(value, NinoDeferredBody<T, TFormatter>.Instance);
                return;
            }

            if (reader.Depth >= NinoDepth.Max)
                NinoThrow.TooDeep(typeof(T));
            reader.Depth++;
            default(TFormatter).ReadBody(ref value, ref reader);
            reader.ExitBody();
        }

        /// <summary>
        /// Non-sealed class with generated subtype dispatch: writes null (type id 0) or a value of exactly
        /// <typeparamref name="T"/>; false for a subtype, which the caller dispatches.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteOwn<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.NullTypeId);
                return true;
            }

            if (value.GetType() != typeof(T))
                return false;
            writer.UnsafeWrite(default(TFormatter).TypeId);
            WriteBody<T, TFormatter>(value, ref writer);
            return true;
        }

        /// <summary>
        /// Non-sealed class with generated subtype dispatch: reads the body if <paramref name="id"/> is
        /// <typeparamref name="T"/>'s own; false otherwise, for the caller to dispatch.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadOwn<T, TFormatter>(int id, ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (id != default(TFormatter).TypeId)
                return false;
            // ReadBody takes null or exactly T: an instance of a subtype is recycled here.
            NinoRecycler.ReuseExact(ref value, typeof(T));
            ReadBody<T, TFormatter>(ref value, ref reader);
            return true;
        }

        /// <summary>Non-sealed class: <c>int32</c> type id (0 = null), then the body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.NullTypeId);
                return;
            }

            if (value.GetType() != typeof(T))
            {
                NinoPolymorphic<T>.Serialize(value, ref writer);
                return;
            }

            writer.UnsafeWrite(default(TFormatter).TypeId);
            WriteBody<T, TFormatter>(value, ref writer);
        }

        /// <summary>Non-sealed class: reads what <see cref="Serialize{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int id = reader.UnsafeRead<int>();
            if (id != default(TFormatter).TypeId)
            {
                NinoPolymorphic<T>.Deserialize(id, ref value, ref reader);
                return;
            }

            // ReadBody takes null or exactly T: an instance of a subtype is recycled here.
            NinoRecycler.ReuseExact(ref value, typeof(T));
            ReadBody<T, TFormatter>(ref value, ref reader);
        }

        /// <summary>
        /// Non-sealed class marked <c>trackReferences</c>, after its reference header said a new object follows:
        /// <c>int32</c> type id, then the body. Subtypes skip the reference header they write through a declared type
        /// that is not tracked.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeReferenced<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (value.GetType() != typeof(T))
            {
                NinoPolymorphic<T>.SerializeReferenced(value, ref writer);
                return;
            }

            writer.UnsafeWrite(default(TFormatter).TypeId);
            WriteBody<T, TFormatter>(value, ref writer);
        }

        /// <summary>Reads what <see cref="SerializeReferenced{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeReferenced<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int id = reader.UnsafeRead<int>();
            if (id != default(TFormatter).TypeId)
            {
                NinoPolymorphic<T>.DeserializeReferenced(id, ref value, ref reader);
                return;
            }

            NinoRecycler.ReuseExact(ref value, typeof(T));
            ReadBody<T, TFormatter>(ref value, ref reader);
        }

        /// <summary>Sealed class: 1-byte null flag, then the body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeSealed<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            writer.UnsafeWrite(NinoWire.NotNull);
            WriteBody<T, TFormatter>(value, ref writer);
        }

        /// <summary>Sealed class: reads what <see cref="SerializeSealed{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeSealed<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (reader.UnsafeRead<byte>() == NinoWire.Null)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            ReadBody<T, TFormatter>(ref value, ref reader);
        }
    }
}
