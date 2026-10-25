using System.Runtime.CompilerServices;

namespace Nino.Core.CompilerServices
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

            if ((default(TFormatter).Traits & NinoBody.Nests) == 0)
            {
                default(TFormatter).WriteBody(sub, ref writer);
                return;
            }

            writer.Depth = depth + 1;
            default(TFormatter).WriteBody(sub, ref writer);
            writer.ExitBody(depth);
        }

        /// <summary>
        /// <see cref="WriteExact{TSub,TFormatter}"/> of a subtype whose body is one raw block: the type id and the body
        /// in one reservation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteExactRaw<TSub, TFormatter>(object value, ref Writer writer)
            where TSub : class
            where TFormatter : struct, INinoRawBodyFormatter<TSub>
        {
            // WriteOwnRaw's body written out, not called: one more level of inlining per subtype used up the inlining
            // budget of a dispatch of 20 subtypes on Arm64, which then called the last one's WriteRawWithId with the
            // writer's address and kept the writer in memory (an array of them serialized 10% slower than in rc.6).
            var sub = Unsafe.As<TSub>(value);
            if (writer.Depth >= NinoDepth.Inline)
            {
                writer.UnsafeWrite(default(TFormatter).TypeId);
                WriteDeep<TSub, TFormatter>(sub, ref writer);
                return;
            }

            WriteRawWithId<TSub, TFormatter>(sub, ref writer);
        }

        /// <summary>
        /// The type id and the raw body of <paramref name="value"/> (exactly <typeparamref name="T"/>): in one
        /// reservation, or deferred when <see cref="NinoDepth.Inline"/> bodies are open.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteOwnRaw<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoRawBodyFormatter<T>
        {
            if (writer.Depth >= NinoDepth.Inline)
            {
                writer.UnsafeWrite(default(TFormatter).TypeId);
                WriteDeep<T, TFormatter>(value, ref writer);
                return;
            }

            WriteRawWithId<T, TFormatter>(value, ref writer);
        }

        /// <summary>The type id and the raw body of <paramref name="value"/>, in one reservation (no depth check).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteRawWithId<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoRawBodyFormatter<T>
        {
            ref byte block = ref writer.Reserve(sizeof(int) + default(TFormatter).BodySize);
            NinoUnsafe.Write(ref block, default(TFormatter).TypeId);
            default(TFormatter).WriteRawBody(value, ref block, sizeof(int));
        }

        /// <summary>The presence byte and the raw body of <paramref name="value"/>, in one reservation (no depth check).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteRawWithFlag<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoRawBodyFormatter<T>
        {
            ref byte block = ref writer.Reserve(1 + default(TFormatter).BodySize);
            block = NinoWire.NotNull;
            default(TFormatter).WriteRawBody(value, ref block, 1);
        }

        /// <summary>
        /// Generated subtype dispatch: reads the body of <typeparamref name="TSub"/> (its type id consumed) into a
        /// slot declared as <typeparamref name="TBase"/>, reusing the instance only if it is exactly
        /// <typeparamref name="TSub"/>. Inlined into a method of the dispatching formatter (one per subtype), which
        /// the dispatch's switch calls as a tail call: there it is not shared code, which it would be called from the
        /// switch (<typeparamref name="TSub"/> is a class), taking a generic context in a register and a frame slot.
        /// The body is not inlined into the switch either, since a dispatch method holding every subtype's body reads
        /// slower.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadExact<TBase, TSub, TFormatter>(ref TBase value, ref Reader reader)
            where TBase : class
            where TSub : class, TBase
            where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            var old = value;
            if (old != null && old.GetType() != typeof(TSub))
            {
                ReadReplacing<TBase, TSub, TFormatter>(ref value, ref reader);
                return;
            }

            // The slot itself, seen as a TSub slot (it now holds null or a TSub): a local copy would reach the body
            // by address, an untracked GC slot zeroed in the prolog (and stored back through a second write barrier).
            ReadExactBody<TSub, TFormatter>(ref Unsafe.As<TBase, TSub>(ref value), ref reader);
        }

        /// <summary><see cref="ReadExact{TBase,TSub,TFormatter}"/> into a slot holding an instance of another type.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadReplacing<TBase, TSub, TFormatter>(ref TBase value, ref Reader reader)
            where TBase : class
            where TSub : class, TBase
            where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            NinoRecycler.Discard(ref value);
            ReadExactBody<TSub, TFormatter>(ref Unsafe.As<TBase, TSub>(ref value), ref reader);
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
        /// Generated dispatch of a generic base: <paramref name="value"/> as <typeparamref name="T"/>, which the caller
        /// checked is the type it is declared as (<c>typeof(Base&lt;TArg&gt;) == typeof(T)</c>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T SameType<T>(object value) where T : class => Unsafe.As<T>(value);

        /// <summary>
        /// Generated dispatch of a generic base: a slot as a slot of <typeparamref name="TTo"/>, which the caller checked
        /// is <typeparamref name="TFrom"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TTo SameTypeSlot<TFrom, TTo>(ref TFrom slot) => ref Unsafe.As<TFrom, TTo>(ref slot);

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
        /// Generated read of a <c>[NinoType]</c> collection (a class deriving from <c>List&lt;T&gt;</c>...): its elements
        /// are the object itself, so data whose element count says null (-1) is rejected before its members are read
        /// into it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureElements<T>(T value) where T : class
        {
            if (value == null)
                NinoThrow.NullElements(typeof(T));
        }

        /// <summary>
        /// <see cref="ReadBody{T,TFormatter}"/>, spelled out for <see cref="ReadExact{TBase,TSub,TFormatter}"/>: where
        /// it is shared code (<see cref="ReadReplacing{TBase,TSub,TFormatter}"/>) an inlined ReadBody (a generic
        /// method) would look up its generic context on every call, though only its deep path needs it, and the
        /// reader is a <c>ref</c> parameter, in memory anyway (ReadBody's copy of it for a deep body would be zeroed
        /// in the prolog).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadExactBody<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int depth = reader.Depth;
            if (depth >= NinoDepth.Inline)
            {
                if ((default(TFormatter).Traits & NinoBody.Deferrable) != 0)
                {
                    if (value == null)
                        value = default(TFormatter).Create();
                    reader.DeferBody<T, TFormatter>(value);
                }
                else
                    ReadTooDeep<T, TFormatter>(ref value, ref reader);
                return;
            }

            if ((default(TFormatter).Traits & NinoBody.Nests) == 0)
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

            if ((default(TFormatter).Traits & NinoBody.Nests) == 0)
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
            if ((default(TFormatter).Traits & NinoBody.Deferrable) != 0)
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

        /// <summary>A body that cannot be deferred, written in place up to <see cref="NinoSettings.MaxDepth"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteTooDeep<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int depth = writer.Depth;
            // Struct bodies that can hold themselves count too (Writer.EnterStruct), as the reader counts them: a graph
            // the reader would reject is not written.
            if (depth + writer.OpenStructs >= NinoLimits.MaxDepth || !NinoDepth.StackLeft())
                NinoThrow.WriteTooDeep(typeof(T));
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
                if ((default(TFormatter).Traits & NinoBody.Deferrable) != 0)
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

            if ((default(TFormatter).Traits & NinoBody.Nests) == 0)
            {
                // Nothing nests in this body: the depth it would count is seen by no other body.
                default(TFormatter).ReadBody(ref value, ref reader);
                return;
            }

            reader.Depth = depth + 1;
            default(TFormatter).ReadBody(ref value, ref reader);
            reader.ExitBody(depth);
        }

        /// <summary>A body that cannot be deferred, read in place up to <see cref="NinoSettings.MaxDepth"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadTooDeep<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int depth = reader.Depth;
            // Struct bodies that can hold themselves count too (Reader.EnterStruct), and the stack is checked: this
            // many bodies of a large type take more than a small stack has.
            if (depth + reader.OpenStructs >= NinoLimits.MaxDepth || !NinoDepth.StackLeft())
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

        /// <summary><see cref="TryWriteOwn{T,TFormatter}"/> of a class whose body is one raw block.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteOwnRaw<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoRawBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.NullTypeId);
                return true;
            }

            if (value.GetType() != typeof(T))
                return false;
            WriteOwnRaw<T, TFormatter>(value, ref writer);
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
            if (!TryWriteOwn<T, TFormatter>(value, ref writer))
                NinoPolymorphic<T>.Serialize(value, ref writer);
        }

        /// <summary><see cref="Serialize{T,TFormatter}"/> of a class whose body is one raw block.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeRaw<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoRawBodyFormatter<T>
        {
            if (!TryWriteOwnRaw<T, TFormatter>(value, ref writer))
                NinoPolymorphic<T>.Serialize(value, ref writer);
        }

        /// <summary>Non-sealed class: reads what <see cref="Serialize{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int id = reader.UnsafeRead<int>();
            if (!TryReadOwn<T, TFormatter>(id, ref value, ref reader))
                NinoPolymorphic<T>.Deserialize(id, ref value, ref reader);
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
            if (!TryReadOwn<T, TFormatter>(id, ref value, ref reader))
                NinoPolymorphic<T>.DeserializeReferenced(id, ref value, ref reader);
        }

        /// <summary>Sealed class: 1-byte null flag, then the body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeSealed<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (!writer.WritePresence(value))
                return;
            WriteBody<T, TFormatter>(value, ref writer);
        }

        /// <summary><see cref="SerializeSealed{T,TFormatter}"/> of a class whose body is one raw block.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeSealedRaw<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoRawBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            if (writer.Depth >= NinoDepth.Inline)
            {
                writer.UnsafeWrite(NinoWire.NotNull);
                WriteDeep<T, TFormatter>(value, ref writer);
                return;
            }

            WriteRawWithFlag<T, TFormatter>(value, ref writer);
        }

        /// <summary>
        /// <see cref="SerializeTop{T,TFormatter}"/> of a class whose body is one raw block (generated <c>__Top</c> and
        /// <c>__Shallow</c> formatters): the presence byte and the body in one reservation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeTopRaw<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoRawBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            WriteRawWithFlag<T, TFormatter>(value, ref writer);
        }

        /// <summary>
        /// Sealed class whose body checks the depth at most <paramref name="bound"/> levels below its own check (a
        /// generator constant: the longest chain of class bodies through its members' types; recursive and
        /// polymorphic types have none). When the depth leaves room for all of them below
        /// <see cref="NinoDepth.Inline"/>, no body in it is deferred and nothing sees the count: the body is
        /// <typeparamref name="TShallow"/>'s, which counts and checks no depth (nor do its members). Otherwise as
        /// <see cref="SerializeSealed{T,TFormatter}"/>. The same bytes either way.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeSealed<T, TFormatter, TShallow>(T value, ref Writer writer, int bound)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
            where TShallow : struct, INinoBodyFormatter<T>
        {
            if (!writer.WritePresence(value))
                return;
            if (writer.Depth < NinoDepth.Inline - bound)
            {
                default(TShallow).WriteBody(value, ref writer);
                return;
            }

            WriteBody<T, TFormatter>(value, ref writer);
        }

        /// <summary>Reads what <see cref="SerializeSealed{T,TFormatter,TShallow}"/> wrote, choosing the same way.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeSealed<T, TFormatter, TShallow>(ref T value, ref Reader reader, int bound)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
            where TShallow : struct, INinoBodyFormatter<T>
        {
            if (!reader.TryReadPresent(ref value))
                return;

            if (reader.Depth < NinoDepth.Inline - bound)
            {
                default(TShallow).ReadBody(ref value, ref reader);
                return;
            }

            ReadBody<T, TFormatter>(ref value, ref reader);
        }

        /// <summary>
        /// Generated <c>__Top</c> formatters: <see cref="SerializeSealed{T,TFormatter}"/> with a writer that the
        /// top-level call just created (no body open, so none is deferred), for a body that opens no class body
        /// (no <see cref="NinoBody.Nests"/>): the depth stays 0. Little IL: inlined into the caller
        /// with the body, it leaves the JIT's inlining budget to the body's own helpers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeTop<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (!writer.WritePresence(value))
                return;
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
            if (!reader.TryReadPresent(ref value))
                return;

            default(TFormatter).ReadBody(ref value, ref reader);
        }

        /// <summary>Sealed class: reads what <see cref="SerializeSealed{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeSealed<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (!reader.TryReadPresent(ref value))
                return;

            ReadBody<T, TFormatter>(ref value, ref reader);
        }
    }
}
