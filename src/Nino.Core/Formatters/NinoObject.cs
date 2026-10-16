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
            // WriteBody spelled out: where this is not inlined it is shared code, and an inlined WriteBody (a
            // generic method) would look up its generic context on every call, though only its deep path needs it.
            var sub = Unsafe.As<TSub>(value);
            int depth = writer.Depth;
            if (depth >= NinoDepth.Inline)
            {
                WriteDeep<TSub, TFormatter>(sub, ref writer);
                return;
            }

            if (!default(TFormatter).Nests)
            {
                default(TFormatter).WriteBody(sub, ref writer);
                return;
            }

            writer.Depth = depth + 1;
            default(TFormatter).WriteBody(sub, ref writer);
            writer.ExitBody(depth);
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
            // Every path ends with a call (a tail call): nothing is live across a call in the dispatch, which then
            // saves no registers in its prolog.
            if (old != null && old.GetType() != typeof(TSub))
            {
                ReadReplacing<TBase, TSub, TFormatter>(ref value, ref reader);
                return;
            }

            // The slot itself, seen as a TSub slot (it now holds null or a TSub): a local copy would reach the call
            // by address, an untracked GC slot per subtype of the dispatch, all zeroed in its prolog on every call
            // (and stored back through a second write barrier).
            ReadBodyOutOfLine<TSub, TFormatter>(ref Unsafe.As<TBase, TSub>(ref value), ref reader);
        }

        /// <summary><see cref="ReadExact{TBase,TSub,TFormatter}"/> into a slot holding an instance of another type.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadReplacing<TBase, TSub, TFormatter>(ref TBase value, ref Reader reader)
            where TBase : class
            where TSub : class, TBase
            where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            NinoRecycler.Discard(ref value);
            ReadBodyOutOfLine<TSub, TFormatter>(ref Unsafe.As<TBase, TSub>(ref value), ref reader);
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

        /// <summary>
        /// <see cref="ReadBody{T,TFormatter}"/>, spelled out: this is shared code, and an inlined ReadBody (a generic
        /// method) would look up its generic context on every call, though only its deep path needs it. The
        /// reader is a <c>ref</c> parameter here, in memory anyway.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadBodyOutOfLine<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int depth = reader.Depth;
            if (depth >= NinoDepth.Inline)
            {
                if (default(TFormatter).Deferrable)
                {
                    if (value == null)
                        value = default(TFormatter).Create();
                    reader.DeferBody<T, TFormatter>(value);
                }
                else
                    ReadTooDeep<T, TFormatter>(ref value, ref reader);
                return;
            }

            if (!default(TFormatter).Nests)
            {
                default(TFormatter).ReadBody(ref value, ref reader);
                return;
            }

            reader.Depth = depth + 1;
            default(TFormatter).ReadBody(ref value, ref reader);
            reader.ExitBody(depth);
        }

        /// <summary>
        /// Writes the body of a class, or defers it when <see cref="NinoDepth.Inline"/> bodies are open (see
        /// <see cref="NinoDepth"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBody<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int depth = writer.Depth;
            if (depth >= NinoDepth.Inline)
            {
                WriteDeep<T, TFormatter>(value, ref writer);
                return;
            }

            if (!default(TFormatter).Nests)
            {
                // Nothing nests in this body: the depth it would count is seen by no other body.
                default(TFormatter).WriteBody(value, ref writer);
                return;
            }

            writer.Depth = depth + 1;
            default(TFormatter).WriteBody(value, ref writer);
            writer.ExitBody(depth);
        }

        /// <summary>A body nested too deep: deferred, or (if it cannot be) written in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteDeep<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (default(TFormatter).Deferrable)
            {
                writer.DeferBody<T, TFormatter>(value);
                return;
            }

            // Through a copy: passing the writer's address to a call that is not inlined would keep the JIT from
            // holding its fields in registers. Only for types that cannot be deferred: the copy is zeroed in the
            // caller's prolog.
            var deep = writer;
            WriteTooDeep<T, TFormatter>(value, ref deep);
            writer = deep;
        }

        /// <summary>A body that cannot be deferred, written in place up to <see cref="NinoDepth.Max"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteTooDeep<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int depth = writer.Depth;
            if (depth >= NinoDepth.Max)
                NinoThrow.TooDeep(typeof(T));
            writer.Depth = depth + 1;
            default(TFormatter).WriteBody(value, ref writer);
            writer.ExitBody(depth);
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
            int depth = reader.Depth;
            if (depth >= NinoDepth.Inline)
            {
                if (default(TFormatter).Deferrable)
                {
                    // The instance is created here and passed by value: a local whose address reaches a call is
                    // an untracked GC slot, zeroed in the prolog and again after every inlined copy of this path
                    // (once per element in a collection loop).
                    var instance = value;
                    if (instance == null)
                        value = instance = default(TFormatter).Create();
                    reader.DeferBody<T, TFormatter>(instance);
                }
                else
                {
                    // The value and the reader through copies: their addresses reaching a call that is not
                    // inlined would keep the JIT from holding them in registers.
                    var instance = value;
                    var deep = reader;
                    ReadTooDeep<T, TFormatter>(ref instance, ref deep);
                    reader = deep;
                    value = instance;
                }

                return;
            }

            if (!default(TFormatter).Nests)
            {
                // Nothing nests in this body: the depth it would count is seen by no other body.
                default(TFormatter).ReadBody(ref value, ref reader);
                return;
            }

            reader.Depth = depth + 1;
            default(TFormatter).ReadBody(ref value, ref reader);
            reader.ExitBody(depth);
        }

        /// <summary>A body that cannot be deferred, read in place up to <see cref="NinoDepth.Max"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadTooDeep<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int depth = reader.Depth;
            if (depth >= NinoDepth.Max)
                NinoThrow.TooDeep(typeof(T));
            reader.Depth = depth + 1;
            default(TFormatter).ReadBody(ref value, ref reader);
            reader.ExitBody(depth);
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

        /// <summary>
        /// Generated <c>__Top</c> formatters: <see cref="SerializeSealed{T,TFormatter}"/> with a writer that the
        /// top-level call just created (no body open, so none is deferred), for a body that opens no class body
        /// (<see cref="INinoBodyFormatter{T}.Nests"/> false): the depth stays 0. Little IL: inlined into the caller
        /// with the body, it leaves the JIT's inlining budget to the body's own helpers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeTop<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            writer.UnsafeWrite(NinoWire.NotNull);
            default(TFormatter).WriteBody(value, ref writer);
        }

        /// <summary>
        /// Generated <c>__Top</c> formatters: <see cref="DeserializeSealed{T,TFormatter}"/> with a reader that the
        /// top-level call just created (see <see cref="SerializeTop{T,TFormatter}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeTop<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (reader.UnsafeRead<byte>() == NinoWire.Null)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            default(TFormatter).ReadBody(ref value, ref reader);
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
