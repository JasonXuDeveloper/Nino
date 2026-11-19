using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// A run of a collection's storage: <see cref="Count"/> entries <see cref="Stride"/> bytes apart, starting at the
    /// value of the first (<see cref="First"/>). One cursor for every contiguous storage (#355): an array, a list or a
    /// span (each entry its value), each half of a ring, the entries of a set (each value inside its entry) and of a
    /// dictionary (each element its pair, a <c>KeyValuePair</c> or the entry itself, composed by the pair's formatter
    /// from its key and value). Entries a set or a dictionary freed are skipped by the in-use test <see cref="InUse"/>
    /// describes. A pair's formatter hands a polymorphic value's generated loop the run of its values, each with its
    /// key at <see cref="Key"/>'s offset (<see cref="Values"/>): the one place a key is written apart from its value. Not generic: generated code
    /// takes it (<see cref="INinoFormatter.WriteRun"/>), so one member of a polymorphic base serves every collection of
    /// it, whatever holds the values, without a generic member IL2CPP 2022.3 cannot build (#405).
    /// </summary>
    /// <remarks>
    /// The loop over a run in generated code reads <see cref="First"/>, <see cref="Count"/>, <see cref="Stride"/>,
    /// <see cref="Key"/> and <see cref="InUse"/> into locals once, then calls the static helpers here with them
    /// (<see cref="Next"/>, <see cref="Skips"/>, <see cref="WriteKey"/>, <see cref="Value{T}"/>), so they stay in
    /// registers: through the run each would be a load per entry, since the writer's stores may alias it.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly ref struct NinoRun
    {
        /// <summary><see cref="Key"/> of a run without keys.</summary>
        public const int NoKey = 0;

        /// <summary><see cref="InUse"/> of a run whose every entry is in use (all but the entries of a set or a
        /// dictionary that freed some).</summary>
        public const int AllInUse = int.MinValue;

        // The low byte of Key: the bytes of a key written as its memory, or StringKey.
        private const int KeySizeMask = 0xFF;
        private const int StringKey = 0xFF;

#if NET8_0_OR_GREATER
        private readonly ref byte _first;
#else
        // A reference field needs .NET 7: the netstandard2.1 build carries the reference in an empty span.
        private readonly Span<byte> _first;
#endif

        // Inlined, or every collection's write calls it once (asm.py compare: NinoRun:.ctor in SimpleTest, ProfileV2).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NinoRun(ref byte first, int count, int stride, int key, int inUse)
        {
#if NET8_0_OR_GREATER
            _first = ref first;
#else
            _first = MemoryMarshal.CreateSpan(ref first, 0);
#endif
            Count = count;
            Stride = stride;
            Key = key;
            InUse = inUse;
        }

        /// <summary>The value of the first entry.</summary>
        public ref byte First
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => ref _first;
#else
            get => ref MemoryMarshal.GetReference(_first);
#endif
        }

        /// <summary>The entries of the run, in use or not.</summary>
        public int Count { get; }

        /// <summary>The bytes from one entry to the next.</summary>
        public int Stride { get; }

        /// <summary>
        /// How <see cref="WriteKey"/> writes the key in front of each value, or <see cref="NoKey"/>: its offset from the
        /// value (above the low byte) and how it is written (the low byte: its bytes, a key whose memory is its wire
        /// form, or a string in the default encoding).
        /// </summary>
        public int Key { get; }

        /// <summary>
        /// How <see cref="Skips"/> tells a free entry, or <see cref="AllInUse"/>: the offset from the value of the
        /// entry's <c>int</c> that marks it (above the low bit), and the low bit set when that field is in use from 0
        /// up (a hash code), clear when from -1 up (a next index).
        /// </summary>
        public int InUse { get; }

        /// <summary>The value of the entry after <paramref name="value"/>'s.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte Next(ref byte value, int stride) => ref Unsafe.Add(ref value, stride);

        /// <summary>Whether the entry of <paramref name="value"/> is free (never, where <paramref name="inUse"/> is
        /// <see cref="AllInUse"/>): the loop skips it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Skips(ref byte value, int inUse) =>
            inUse != AllInUse &&
            Unsafe.ReadUnaligned<int>(ref Unsafe.AddByteOffset(ref value, (nint)(inUse >> 1))) < (inUse & 1) - 1;

        /// <summary>
        /// Writes the key of <paramref name="value"/>'s entry as its formatter writes it (nothing where
        /// <paramref name="key"/> is <see cref="NoKey"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteKey(ref byte value, int key, ref Writer writer)
        {
            if (key == NoKey)
                return;
            ref byte at = ref Unsafe.AddByteOffset(ref value, (nint)(key >> 8));
            int size = key & KeySizeMask;
            if (size == StringKey)
                writer.Write(Unsafe.As<byte, string>(ref at));
            else if (size == sizeof(int))
                Unsafe.WriteUnaligned(ref writer.Reserve(sizeof(int)), Unsafe.ReadUnaligned<int>(ref at));
            else if (size == sizeof(long))
                Unsafe.WriteUnaligned(ref writer.Reserve(sizeof(long)), Unsafe.ReadUnaligned<long>(ref at));
            else
                NinoUnsafe.Copy(ref writer.Reserve(size), ref at, (uint)size);
        }

        /// <summary>The value at <paramref name="value"/>, a reference.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Value<T>(ref byte value) where T : class => Unsafe.As<byte, T>(ref value);

        /// <summary>
        /// Writes each value of <paramref name="run"/> (and its key) through <typeparamref name="TFormatter"/>'s
        /// <c>Serialize</c>, with the caller's writer: what a generated loop over a run does once the writer is too deep
        /// for its shallow writes (<see cref="NinoObject.IsDeep"/>). Out of line: rare.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteEach<T, TFormatter>(NinoRun run, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            ref byte value = ref run.First;
            int stride = run.Stride, key = run.Key, inUse = run.InUse;
            for (int left = run.Count; left > 0; left--, value = ref Next(ref value, stride))
            {
                if (Skips(ref value, inUse))
                    continue;
                WriteKey(ref value, key, ref writer);
                default(TFormatter).Serialize(Unsafe.As<byte, T>(ref value), ref writer);
            }
        }

        /// <summary>The run of the elements of <paramref name="values"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static NinoRun Of<T>(ReadOnlySpan<T> values) =>
            new(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)), values.Length, Unsafe.SizeOf<T>(),
                NoKey, AllInUse);

        /// <summary>
        /// The <see cref="Key"/> of a key at <paramref name="offset"/> from the value, written as its
        /// <paramref name="size"/> bytes (from 1 to 254), or a string where <paramref name="size"/> is -1.
        /// </summary>
        internal static int KeyAt(int offset, int size) => (offset << 8) | (size < 0 ? StringKey : size);

        /// <summary>
        /// The run of the values of this run's pairs (a run without keys): each value at <paramref name="value"/>
        /// bytes from its pair, its key at <paramref name="key"/> bytes, written as <paramref name="keyForm"/> says (a
        /// <see cref="Key"/> without its offset, not <see cref="NoKey"/>). What a pair's formatter hands its value's
        /// generated loop (<see cref="INinoFormatter.WriteRun"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NinoRun Values(int value, int key, int keyForm) =>
            new(ref Unsafe.Add(ref First, value), Count, Stride, keyForm + ((key - value) << 8), Rebase(InUse, value));

        /// <summary>
        /// The <see cref="InUse"/> of <paramref name="inUse"/> for a run whose values start <paramref name="shift"/>
        /// bytes further into each entry.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Rebase(int inUse, int shift) =>
            inUse == AllInUse ? inUse : (((inUse >> 1) - shift) << 1) | (inUse & 1);

        /// <summary>
        /// The <see cref="InUse"/> of entries whose <c>int</c> at <paramref name="offset"/> from the value is at least
        /// <paramref name="min"/> (0 or -1) while in use.
        /// </summary>
        internal static int InUseAt(int offset, int min) => (offset << 1) | (min + 1);
    }
}
