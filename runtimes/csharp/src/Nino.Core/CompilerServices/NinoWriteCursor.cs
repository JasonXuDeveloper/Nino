using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// The pieces of a generated subtype dispatch's write (#460): the writer's next byte and its free bytes, taken from
    /// the writer once (<see cref="NinoWriteCursor(ref Writer)"/>) and held in a local the JIT keeps in two registers,
    /// and the writes the dispatch composes from them: a null (<see cref="Null"/>), a subtype whose body is one raw
    /// block (<see cref="Raw{TSub,TFormatter}(object, ref NinoWriteCursor, ref Writer)"/>), any other subtype's body
    /// (<see cref="Body{TSub,TFormatter}(object, ref NinoWriteCursor, ref Writer)"/>), a subtype the dispatch does not
    /// know (<see cref="Other{TBase}(TBase, ref NinoWriteCursor, ref Writer)"/>) and a run entry's key
    /// (<see cref="Key"/>). Each piece writes at the cursor or gives it back to the writer around a write through it,
    /// so a generated run loop and the single-value dispatch (<c>__WriteSubtype</c>, the forms taking only the
    /// writer) are the same calls in the same order, and no grow or hand-off is written in generated code.
    /// </summary>
    /// <remarks>
    /// Every member is inlined, and the cursor's address never reaches a call that is not: a writer copied into a
    /// loop's local stays in memory once its address reaches a call (the registry's fallback), and every element then
    /// stored the cursor and the free bytes and loaded them back (#305's lead step). The grow is out of line
    /// (<see cref="Writer.UnsafeGrowAt"/>), with the cursor's values, not its address.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ref struct NinoWriteCursor
    {
        // The next byte, as Writer keeps it per target: a reference field on .NET 8+, an empty span over it on
        // netstandard2.1 (no reference fields before .NET 7).
#if NET8_0_OR_GREATER
        private ref byte _cur;
#else
        private Span<byte> _cur;
#endif
        private int _avail;

        /// <summary>The cursor of <paramref name="writer"/>, which the caller gives back with <see cref="Back"/>
        /// before anything else writes to it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoWriteCursor(scoped ref Writer writer)
        {
#if NET8_0_OR_GREATER
            _cur = ref writer.UnsafeCursor(out _avail);
#else
            _cur = MemoryMarshal.CreateSpan(ref writer.UnsafeCursor(out _avail), 0);
#endif
        }

        private ref byte Cur
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => ref _cur;
#else
            get => ref MemoryMarshal.GetReference(_cur);
#endif
        }

        /// <summary>Gives the cursor back to <paramref name="writer"/>, the writer it was taken from.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Back(ref Writer writer) => writer.UnsafeSetCursor(ref Cur, _avail);

        /// <summary>Takes the cursor again from <paramref name="writer"/>, after a write through it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Take(ref Writer writer)
        {
#if NET8_0_OR_GREATER
            _cur = ref writer.UnsafeCursor(out _avail);
#else
            _cur = MemoryMarshal.CreateSpan(ref writer.UnsafeCursor(out _avail), 0);
#endif
        }

        /// <summary>Room for <paramref name="size"/> bytes at the cursor, the buffer grown out of line where they do
        /// not fit; returns the cursor.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref byte Room(int size, ref Writer writer)
        {
            if ((uint)size > (uint)_avail)
            {
                writer.UnsafeGrowAt(ref Cur, _avail, size);
                Take(ref writer);
            }

            return ref Cur;
        }

        /// <summary>Moves the cursor past <paramref name="size"/> bytes written at it (<see cref="Room"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Advance(int size)
        {
#if NET8_0_OR_GREATER
            _cur = ref Unsafe.Add(ref _cur, (nuint)(uint)size);
#else
            _cur = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Cur, (nuint)(uint)size), 0);
#endif
            _avail -= size;
        }

        /// <summary>Writes a null value: the type id 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Null(ref NinoWriteCursor cursor, ref Writer writer)
        {
            NinoUnsafe.Write(ref cursor.Room(sizeof(int), ref writer), 0);
            cursor.Advance(sizeof(int));
        }

        /// <summary>
        /// Writes <paramref name="value"/>, exactly a <typeparamref name="TSub"/> (the caller compared its type) whose
        /// body is one raw block: its type id and its block at the cursor, after one check of the free bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Raw<TSub, TFormatter>(object value, ref NinoWriteCursor cursor, ref Writer writer)
            where TSub : class
            where TFormatter : struct, INinoRawBodyFormatter<TSub>
        {
            int size = sizeof(int) + default(TFormatter).BodySize;
            ref byte block = ref cursor.Room(size, ref writer);
            NinoUnsafe.Write(ref block, default(TFormatter).TypeId);
            default(TFormatter).WriteRawBody(Unsafe.As<TSub>(value), ref block, sizeof(int));
            cursor.Advance(size);
        }

        /// <summary>
        /// <see cref="Raw{TSub,TFormatter}(object, ref NinoWriteCursor, ref Writer)"/> through <paramref name="writer"/>
        /// alone (the single-value dispatch): the cursor taken and given back around it, which is
        /// <see cref="Writer.Reserve"/>'s work.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Raw<TSub, TFormatter>(object value, ref Writer writer)
            where TSub : class
            where TFormatter : struct, INinoRawBodyFormatter<TSub>
        {
            var cursor = new NinoWriteCursor(ref writer);
            Raw<TSub, TFormatter>(value, ref cursor, ref writer);
            cursor.Back(ref writer);
        }

        /// <summary>
        /// Writes <paramref name="value"/>, exactly a <typeparamref name="TSub"/> (the caller compared its type), with
        /// a body that is not one raw block: its type id and its body through <paramref name="writer"/>, given the
        /// cursor back around it (<see cref="NinoObject.WriteExactShallow{TSub,TFormatter}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Body<TSub, TFormatter>(object value, ref NinoWriteCursor cursor, ref Writer writer)
            where TSub : class
            where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            cursor.Back(ref writer);
            Body<TSub, TFormatter>(value, ref writer);
            cursor.Take(ref writer);
        }

        /// <summary><see cref="Body{TSub,TFormatter}(object, ref NinoWriteCursor, ref Writer)"/> through
        /// <paramref name="writer"/> alone (the single-value dispatch).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Body<TSub, TFormatter>(object value, ref Writer writer)
            where TSub : class
            where TFormatter : struct, INinoBodyFormatter<TSub> =>
            NinoObject.WriteExactShallow<TSub, TFormatter>(value, ref writer);

        /// <summary>
        /// Writes <paramref name="value"/>, of a subtype the dispatch does not know, through the registry
        /// (<see cref="NinoSubtypes{TBase}.Serialize"/>), given the cursor back around it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Other<TBase>(TBase value, ref NinoWriteCursor cursor, ref Writer writer) where TBase : class
        {
            cursor.Back(ref writer);
            Other(value, ref writer);
            cursor.Take(ref writer);
        }

        /// <summary><see cref="Other{TBase}(TBase, ref NinoWriteCursor, ref Writer)"/> through
        /// <paramref name="writer"/> alone (the single-value dispatch).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Other<TBase>(TBase value, ref Writer writer) where TBase : class =>
            NinoSubtypes<TBase>.Serialize(value, ref writer);

        /// <summary>
        /// Writes the key of the run entry at <paramref name="entry"/> (<see cref="NinoRun.WriteKey"/>; nothing where
        /// <paramref name="key"/> is <see cref="NinoRun.NoKey"/>), given the cursor back around it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Key(ref byte entry, int key, ref NinoWriteCursor cursor, ref Writer writer)
        {
            if (key == NinoRun.NoKey)
                return;
            cursor.Back(ref writer);
            NinoRun.WriteKey(ref entry, key, ref writer);
            cursor.Take(ref writer);
        }
    }
}
