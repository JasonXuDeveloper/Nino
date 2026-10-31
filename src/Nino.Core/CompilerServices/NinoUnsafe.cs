using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.Internal;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.X86;
#endif

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// Low-level unaligned memory access used by <see cref="Writer"/>, <see cref="Reader"/> and generated code.
    /// All values are stored little-endian, exactly as laid out in memory.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoUnsafe
    {
        /// <summary>The most elements an array (of bytes, or a dimension of any array) can have.</summary>
        internal const int MaxArrayLength = 0x7FFFFFC7;

#if NET8_0_OR_GREATER
        /// <summary>
        /// Smallest array (in bytes) allocated uninitialized. GC.AllocateUninitializedArray zeroes below 2 KB anyway,
        /// and from there takes a slower allocation path than new T[], which only zeroing a larger array pays for.
        /// Measured on GitHub's runners (.NET 10, quick BenchmarkDotNet runs), a new int[] filled by one copy, against
        /// new int[]: 1.42x (x64) and 2.18x (Arm64) the time at 2 KB, 1.09x and 1.11x at 4 KB, 0.66x and 0.73x at
        /// 64 KB, the same at 256 KB (the large object heap). At 8,000 bytes uninitialized already wins: zeroed,
        /// arrays of 1,000 KeyValuePair&lt;int, int&gt; and of 1,000 8-byte structs deserialized 14 to 28% more
        /// slowly. Between that and 4 KB, 6 KB.
        /// </summary>
        private const int MinUninitializedArrayBytes = 6 * 1024;
#endif

        /// <summary>
        /// Whether the process is 64-bit. Constant-folded by the JIT.
        /// </summary>
        public static bool Is64Bit
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IntPtr.Size == 8;
        }

        /// <summary>
        /// In a 32-bit process, whether a value of 8 bytes or more is loaded and stored at any address in one access
        /// (<c>Unsafe.ReadUnaligned</c> / <c>WriteUnaligned</c>), as every 64-bit process does in
        /// <see cref="Read{T}(ref byte)"/>, <see cref="Write{T}(ref byte, T)"/> and the sizes up to 64 of
        /// <see cref="Copy"/>: x86 (<see cref="IsX86"/>, a constant) and the processes <see cref="WideAccess"/> names.
        /// Elsewhere such values are copied byte by byte, and <see cref="Copy"/> calls Memmove. Each test is written
        /// <c>Is64Bit || IsX86 || ... || UnalignedWide32</c>: a 64-bit process folds the first and an x86 one the
        /// second before the rest is imported, so their code stays as it was (a property holding the whole test, with
        /// a branch, was inlined through a temporary and changed how x64 copied structs); the probe folds once its
        /// class is initialized (Tier1).
        /// </summary>
        private static bool UnalignedWide32
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => WideAccess.Unaligned;
        }

        /// <summary>
        /// Tested after <see cref="Is64Bit"/>: whether a 32-bit process runs on x86 (the .NET 8 and later builds; a
        /// constant to the JIT, without a branch, so that it folds where it is inlined).
        /// </summary>
        private static bool IsX86
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => X86Base.IsSupported;
#else
            get => false;
#endif
        }

        /// <summary>
        /// The 32-bit processes where an unaligned access of 8 bytes or more cannot fault: .NET Framework and .NET on
        /// x86 and .NET on WebAssembly, which never fault on alignment, and .NET (CoreCLR) on 32-bit Arm, whose JIT
        /// compiles an unaligned load or store of 8 bytes or more to ldr/str of 4 bytes each (and vmov for a double),
        /// which take any address. Mono (Unity's, and .NET's on Android) and IL2CPP on 32-bit Arm may emit
        /// ldrd/strd/vldr/vstr, which fault on an address that is not a multiple of 4: they keep the byte-wise copies,
        /// as does every runtime not named here (Unity, Mono and IL2CPP on any 32-bit CPU included). Its own class, so
        /// that NinoUnsafe gets no static constructor.
        /// </summary>
        private static class WideAccess
        {
            internal static readonly bool Unaligned = !Is64Bit && Probe();

            private static bool Probe()
            {
                var architecture = RuntimeInformation.ProcessArchitecture;
#if NET8_0_OR_GREATER
                if (architecture == Architecture.Wasm)
                    return true;
#endif
                if (architecture != Architecture.X86 && architecture != Architecture.Arm)
                    return false;
                // .NET Framework, .NET Core and .NET 5+ start with ".NET"; Mono's and IL2CPP's class libraries say
                // "Mono ...". .NET's Mono (Android, .NET 6 and later) says ".NET" too, and is told by its types.
                string framework = RuntimeInformation.FrameworkDescription;
                if (!framework.StartsWith(".NET", StringComparison.Ordinal) ||
                    Type.GetType("Mono.RuntimeStructs") != null || Type.GetType("Mono.Runtime") != null)
                    return false;
                return architecture == Architecture.X86 ||
                       !framework.StartsWith(".NET Framework", StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Whether <c>Vector.Narrow</c> and <c>Vector.Widen</c> (with <c>Vector.EqualsAll</c>, the text kernels) are
        /// worth using: the JIT accelerates <c>Vector&lt;T&gt;</c> and, in the netstandard builds, the runtime is .NET
        /// Framework or .NET (Core), whose descriptions start with ".NET". Never on Mono or Unity: classic Mono's JIT
        /// (mono/mono 6.x, and Unity's fork) accelerates only <c>Vector&lt;T&gt;</c>'s own operators, constructors,
        /// <c>Equals</c> and <c>CopyTo</c> (<c>vector_t_intrinsics</c> in mono/mini/simd-intrinsics.c), and runs
        /// <c>Vector.Narrow</c> and <c>Widen</c> in the package's software form (element by element through a stack
        /// buffer): with plain loads (see <see cref="VectorMasks"/>), Mono 6.8 on x64 narrowed 1,024 chars in 5,400
        /// ns against 550 for the 8-byte loop and widened them in 6,700 against 400 (local), while the check before
        /// Narrow (<c>|</c>, <c>&amp;</c>, <c>EqualsAll</c>) took 120. Unity's runtimes say "Mono ..." too and take
        /// their Burst kernels (<see cref="NinoKernels"/>). Constant-folded by the JIT (the probe once its class is
        /// initialized).
        /// </summary>
        internal static bool NumericVectors
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => System.Numerics.Vector.IsHardwareAccelerated;
#else
            get => NumericVectorProbe.Fast;
#endif
        }

        /// <summary>
        /// Whether loads, stores and <c>&amp;</c> of <c>Vector&lt;byte&gt;</c> (<see cref="NinoMask"/>) are worth
        /// using: where <see cref="NumericVectors"/> is, and on classic Mono with the System.Numerics.Vectors package
        /// Nino.Core brings, the one <c>Vector&lt;T&gt;</c> its JIT compiles to SSE registers (class-init.c marks
        /// <c>Vector`1</c> a SIMD type only in an assembly of that name) as <c>movups</c> and <c>pand</c>. Mono 6.8 on
        /// x64 serialized an array of 1,000 padded structs of 16 bytes in 8.4 to 9.2 µs against 11.8 to 13.4 with
        /// ulongs, of 12 bytes in 6.6 to 7.7 against 8.2 to 8.7, 16 of them in 90 to 110 ns against 140 (local); with
        /// <c>Unsafe.ReadUnaligned</c> loads, which Mono compiles to calls to memcpy, 36 to 40 µs (see
        /// <see cref="NinoMask"/>). Not where <c>Vector&lt;T&gt;</c> is Mono's own (in mscorlib): no Mono JIT
        /// accelerates it, and Unity 2022.3.47f1 and 6000.0.20f1 reported it accelerated all the same (UUM-77339;
        /// Unity-Technologies/mono made <c>IsHardwareAccelerated</c> true for mscorlib's type in July 2024 and reverted
        /// it in February 2025).
        /// </summary>
        internal static bool VectorMasks
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => System.Numerics.Vector.IsHardwareAccelerated;
#else
            get => NumericVectorProbe.Masks;
#endif
        }

#if !NET8_0_OR_GREATER
        /// <summary>
        /// <see cref="NumericVectors"/> and <see cref="VectorMasks"/> in the netstandard builds (a class of its own,
        /// see <see cref="WideAccess"/>).
        /// </summary>
        private static class NumericVectorProbe
        {
            internal static readonly bool Fast = System.Numerics.Vector.IsHardwareAccelerated &&
                                                 RuntimeInformation.FrameworkDescription.StartsWith(".NET",
                                                     StringComparison.Ordinal);

            internal static readonly bool Masks = Fast || System.Numerics.Vector.IsHardwareAccelerated &&
                typeof(System.Numerics.Vector<byte>).Assembly.GetName().Name == "System.Numerics.Vectors";
        }
#endif

        /// <summary>Size of <typeparamref name="T"/> in bytes. Constant-folded by the JIT.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizeOf<T>() => Unsafe.SizeOf<T>();

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="destination"/> without alignment requirements.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T>(ref byte destination, T value)
        {
            // 32-bit ARM faults on unaligned 8-byte (LDRD/STRD/VSTR) accesses, which some runtimes emit for
            // unaligned stores of 8+ byte values (UnalignedWide32). A byte-wise block copy is always safe there. It
            // takes the value's address, so it is in another method: an address taken here, even on a branch the JIT
            // folds away, would make every inlined caller pass the value through a stack temporary.
            if (Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32)
                Unsafe.WriteUnaligned(ref destination, value);
            else
                WriteBytes(ref destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBytes<T>(ref byte destination, T value) => WriteIn(ref destination, in value);

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="destination"/> + <paramref name="offset"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T>(ref byte destination, int offset, T value)
        {
            // The offset is added here, in each access, rather than by passing Unsafe.Add(...) on to Write(ref, T):
            // a computed reference handed to another inlined method becomes a temporary when the offset is not an
            // IL constant (offset + NinoUnsafe.SizeOf<S>()), and the store then no longer folds the offset into its
            // address (an extra lea/add per member). Same in WriteIn, WriteValue(In) and Read below.
            if (Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, offset), value);
            else
                WriteBytes(ref Unsafe.Add(ref destination, offset), value);
        }

        /// <summary>
        /// Stores <paramref name="value"/> at <paramref name="destination"/> + <paramref name="offset"/> with its
        /// padding bytes cleared (see <see cref="NinoLayout{T}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteValue<T>(ref byte destination, int offset, T value)
        {
            Write(ref destination, offset, value);
            NinoLayout<T>.ClearPadding(ref Unsafe.Add(ref destination, offset));
        }

        /// <summary>
        /// <see cref="Write{T}(ref byte, T)"/> from a reference: for values wider than a register that live in the
        /// heap (fields of the object being written), which by value would be copied to the stack and loaded
        /// again (a Guid: a 16-byte store and reload). Values of at most 8 bytes are better passed by value, where
        /// the load folds the field offset.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteIn<T>(ref byte destination, in T value)
        {
            if (Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32)
                Unsafe.WriteUnaligned(ref destination, value);
            else
                Unsafe.CopyBlockUnaligned(ref destination, ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)),
                    (uint)Unsafe.SizeOf<T>());
        }

        /// <summary><see cref="WriteIn{T}(ref byte, in T)"/> at <paramref name="destination"/> +
        /// <paramref name="offset"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteIn<T>(ref byte destination, int offset, in T value)
        {
            if (Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, offset), value);
            else
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref destination, offset),
                    ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)), (uint)Unsafe.SizeOf<T>());
        }

        /// <summary><see cref="WriteValue{T}"/> from a reference (see
        /// <see cref="WriteIn{T}(ref byte, in T)"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteValueIn<T>(ref byte destination, int offset, in T value)
        {
            WriteIn(ref destination, offset, in value);
            NinoLayout<T>.ClearPadding(ref Unsafe.Add(ref destination, offset));
        }

        /// <summary>
        /// Loads a <typeparamref name="T"/> from <paramref name="source"/> without alignment requirements. Unchecked:
        /// a <c>bool</c> of the data is read with <see cref="ReadBool"/> or checked by its caller (see
        /// <see cref="NinoChecks"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(ref byte source) =>
            Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32
                ? Unsafe.ReadUnaligned<T>(ref source)
                : ReadBytes<T>(ref source);

        // Not AggressiveInlining, unlike WriteBytes: the 32-bit branch of Read is imported before it folds away, and
        // this body inlined there takes inlining budget from large methods (with it, on x64, a generated __WriteBody
        // made 9 stack accesses instead of 3, and a generated __ReadTop 83 instead of 69). Only the 32-bit runtimes
        // UnalignedWide32 leaves out call it.
        private static T ReadBytes<T>(ref byte source)
        {
            T value = default;
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref value), ref source, (uint)Unsafe.SizeOf<T>());
            return value;
        }

        /// <summary>
        /// Loads a <typeparamref name="T"/> from <paramref name="source"/> + <paramref name="offset"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(ref byte source, int offset) =>
            Is64Bit || IsX86 || Unsafe.SizeOf<T>() <= 4 || UnalignedWide32
                ? Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref source, offset))
                : ReadBytes<T>(ref Unsafe.Add(ref source, offset));

        /// <summary>
        /// Generated code: loads a bool member from <paramref name="source"/> + <paramref name="offset"/> (a block of
        /// raw members), a byte of 0 or 1; any other is invalid data (<see cref="NinoDataException"/>).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadBool(ref byte source, int offset)
        {
            byte value = Unsafe.Add(ref source, offset);
            if (value > 1)
                NinoThrow.InvalidBool(value);
            return value != 0;
        }

        /// <summary>
        /// Generated code: <see cref="Read{T}(ref byte, int)"/> of a raw member whose type may hold values no
        /// constructor makes (a struct, <see cref="DateTime"/>, <c>decimal</c>...): checked in
        /// <see cref="NinoSettings.Untrusted"/> mode (<see cref="NinoLayout{T}.Checks"/>), out of line; otherwise the
        /// same load.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T ReadChecked<T>(ref byte source, int offset)
        {
            T value = Read<T>(ref source, offset);
            return NinoLayout<T>.Checks == null ? value : NinoChecks.Checked(value);
        }

        /// <summary><paramref name="source"/> + <paramref name="offset"/> bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte Add(ref byte source, int offset) => ref Unsafe.Add(ref source, offset);

        /// <summary>
        /// Returns a reference to the first element of <paramref name="array"/> without a bounds check.
        /// The array must not be null.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetArrayDataReference<T>(T[] array)
        {
#if NET8_0_OR_GREATER
            return ref MemoryMarshal.GetArrayDataReference(array);
#else
            return ref MemoryMarshal.GetReference(array.AsSpan());
#endif
        }

        /// <summary>
        /// Copies <paramref name="size"/> bytes. Up to 64 (most strings) inline, as overlapping loads and stores of the
        /// largest width that fits (33 to 64 bytes: two 32-byte vectors where they are accelerated, else four 16-byte
        /// moves); more out of line (<see cref="CopyOver64"/>, as every size in a 32-bit process, where up to 64 bytes
        /// are copied the same way on x86 and where <see cref="UnalignedWide32"/>), 65 to 128 as four 32-byte vectors
        /// where they are accelerated: a block copy of a size that is not a constant calls Memmove. Beyond, up to 32 KB
        /// with 64-byte vectors where they are accelerated, and from there on Intel (else from 256 bytes) to 1 MB with
        /// 32-byte ones (<see cref="CopyVectors{TVector}"/>), and Memmove beyond. Measured on an AVX-512 Xeon (.NET 10,
        /// source and destination of different alignments), with AVX-512 on or off: 33 to 64 bytes in 4.0 to 4.5 ns
        /// instead of 4.7 to 5.4 (Memmove), 65 to 128 in about 4.2 ns instead of 5.4 to 6.6. A loop of 16-byte vectors
        /// (WebAssembly, SSE only) is slower than Memmove from 2 KB, so those CPUs call Memmove from 65 bytes; Arm64
        /// copies up to 64 KB with <see cref="CopyPairs"/>, out of line.
        /// </summary>
        // In parts, each AggressiveInlining: the JIT checks an inline against the caller's inlining budget by the
        // callee's whole IL, before the branches the sizes and the CPU decide fold away. Copy in one method (662 bytes
        // of IL) was over the budget of every caller of less than 30 bytes (a generated body that forwards to its
        // members' writes) and stayed a call there; inline, its parts for more than 64 bytes took a large share of the
        // budget of every method that copies (a collection of an Immutable or polymorphic element, ...) for the sizes
        // where a call costs least. The inline parts have at most 173 bytes of IL.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Copy(ref byte destination, ref byte source, uint size)
        {
            if (Is64Bit && size <= 64)
            {
                if (size > 32)
                    Copy33To64(ref destination, ref source, size);
                else if (size >= 16)
                    Copy16To32(ref destination, ref source, size);
                else
                    CopyUpTo15(ref destination, ref source, size);
                return;
            }

            CopyOver64(ref destination, ref source, size);
        }

        /// <summary>33 to 64 bytes: two 32-byte vectors where they are accelerated, else four 16-byte moves.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Copy33To64(ref byte destination, ref byte source, uint size)
        {
#if NET8_0_OR_GREATER
            if (Vector256.IsHardwareAccelerated)
            {
                var head = Vector256.LoadUnsafe(ref source);
                var tail = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 32));
                head.StoreUnsafe(ref destination);
                tail.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 32));
                return;
            }
#endif
            var a = Unsafe.ReadUnaligned<Bytes16>(ref source);
            var b = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, 16));
            var c = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, (nint)size - 32));
            var d = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, (nint)size - 16));
            Unsafe.WriteUnaligned(ref destination, a);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), b);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 32), c);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 16), d);
        }

        /// <summary>16 to 32 bytes: two overlapping 16-byte moves.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Copy16To32(ref byte destination, ref byte source, uint size)
        {
            var head = Unsafe.ReadUnaligned<Bytes16>(ref source);
            var tail = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, (nint)size - 16));
            Unsafe.WriteUnaligned(ref destination, head);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 16), tail);
        }

        /// <summary>0 to 15 bytes: two overlapping moves of the largest width that fits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyUpTo15(ref byte destination, ref byte source, uint size)
        {
            if (size >= 8)
            {
                ulong head = Unsafe.ReadUnaligned<ulong>(ref source);
                ulong tail = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, (nint)size - 8));
                Unsafe.WriteUnaligned(ref destination, head);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 8), tail);
            }
            else if (size >= 4)
            {
                uint head = Unsafe.ReadUnaligned<uint>(ref source);
                uint tail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, (nint)size - 4));
                Unsafe.WriteUnaligned(ref destination, head);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 4), tail);
            }
            else if (size != 0)
            {
                // 1 to 3 bytes: the first, the middle and the last (some the same).
                byte first = source, middle = Unsafe.Add(ref source, (nint)(size >> 1)),
                    last = Unsafe.Add(ref source, (nint)size - 1);
                destination = first;
                Unsafe.Add(ref destination, (nint)(size >> 1)) = middle;
                Unsafe.Add(ref destination, (nint)size - 1) = last;
            }
        }

        /// <summary>
        /// More than 64 bytes, or any size in a 32-bit process (up to 64 as Copy does on x86 and where
        /// <see cref="UnalignedWide32"/>): 65 to 128 as
        /// four 32-byte vectors where they are accelerated, then <see cref="CopyVectors{TVector}"/> or
        /// <see cref="CopyBlock"/>. Out of line (see <see cref="Copy"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CopyOver64(ref byte destination, ref byte source, uint size)
        {
            // Up to 64 bytes in a 32-bit process that accesses 8 bytes at any address: the parts Copy inlines, here
            // so that Copy's IL, which callers' inlining budgets count, is the same in every process (written in Copy,
            // this test changed what the JIT inlined into an ImmutableSortedDictionary write on x64).
            if (!Is64Bit && (IsX86 || UnalignedWide32) && size <= 64)
            {
                if (size > 32)
                    Copy33To64(ref destination, ref source, size);
                else if (size >= 16)
                    Copy16To32(ref destination, ref source, size);
                else
                    CopyUpTo15(ref destination, ref source, size);
                return;
            }

#if NET8_0_OR_GREATER
            if (Vector256.IsHardwareAccelerated)
            {
                if (size <= 128)
                {
                    // 65 to 128 bytes (processes that skip the small sizes above go to Memmove).
                    if (Is64Bit || IsX86 || UnalignedWide32)
                    {
                        var a = Vector256.LoadUnsafe(ref source);
                        var b = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, 32));
                        var c = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 64));
                        var d = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 32));
                        a.StoreUnsafe(ref destination);
                        b.StoreUnsafe(ref Unsafe.Add(ref destination, 32));
                        c.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 64));
                        d.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 32));
                        return;
                    }
                }
                else if (Vector512.IsHardwareAccelerated
                             ? size <= (AmdCopy ? MaxVectorCopy : MaxVector256Copy)
                             : size - 256 <= MaxVector256Copy - 256)
                {
                    // With 64-byte vectors up to MaxVectorCopy, then on Intel 32-byte ones up to MaxVector256Copy:
                    // beyond 32 KB the 64-byte loop lost to Memmove, the 32-byte one still beats it there (GitHub's
                    // Xeon Platinum 8370C, 160,000 bytes into a new zeroed array: 3.18 µs instead of 3.51); on AMD's
                    // Zen 5 Memmove beats it into such an array (EPYC 9V45: 1.81 µs against 1.67). CopyToNew is the
                    // other way round.
                    if (Vector512.IsHardwareAccelerated && size <= MaxVectorCopy)
                        CopyVectors<Vector512<byte>>(ref destination, ref source, size);
                    else
                        CopyVectors<Vector256<byte>>(ref destination, ref source, size);
                    return;
                }
            }

#endif
            CopyBlock(ref destination, ref source, size);
        }

        /// <summary>
        /// Memmove, or <see cref="CopyPairs"/> on Arm64. A call of its own, so Copy's code is the same size on every
        /// platform: with the Arm64 branch written in Copy, the JIT inlined Copy where it had not before (an
        /// ImmutableSortedDictionary write) and left the writer in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyBlock(ref byte destination, ref byte source, uint size)
        {
#if NET8_0_OR_GREATER
            if (AdvSimd.Arm64.IsSupported)
            {
                CopyPairs(ref destination, ref source, size);
                return;
            }
#endif
            Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// An AMD x64 CPU, whose rep movsb (Memmove's copy of a large block) is slower than vector stores also into a
        /// new array, where Intel's writes whole lines without reading them first. Measured on GitHub's EPYC 7763
        /// (.NET 10), a new uninitialized array filled from offset 4 of a buffer, vector stores against Memmove: 8,000
        /// bytes in 279 to 288 ns instead of 341 to 350, 16,000 in 556 to 567 instead of 658 to 677, 64,000 in 1,989 to
        /// 2,018 instead of 2,680 to 2,750; on an AVX-512 Xeon, Memmove 1.3 to 2 times as fast; on Arm64 (Neoverse
        /// N2) Memmove as fast or faster. One CPUID at startup.
        /// </summary>
        private static readonly bool AmdCopy = X86Base.IsSupported && IsAmd();

        private static bool IsAmd()
        {
            // "AuthenticAMD", in EBX, EDX and ECX.
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
            return ebx == 0x68747541 && edx == 0x69746E65 && ecx == 0x444D4163;
        }

        /// <summary>
        /// An x64 CPU whose 64-byte vector work beats 32-byte vectors where the two disagree: AMD's (Zen 4 and later)
        /// and Intel's other than the Sunny Cove generation (<see cref="IsIceLake"/>), where both kernels below lost.
        /// Not by AVX512-FP16 (Sapphire Rapids and later): Azure's Xeon Platinum 8573C (Emerald Rapids) does not report
        /// it, and with 32-byte masks there it wrote 1,000 8-byte structs with a padding byte in 92 ns instead of 60
        /// (.NET A/B run 41020623184). Two CPUIDs at startup.
        /// </summary>
        internal static readonly bool Wide512 = X86Base.IsSupported && (IsAmd() || !IsIceLake());

        /// <summary>
        /// An Intel CPU of the Sunny Cove generation (<see cref="IsIceLake"/>): on GitHub's Xeon Platinum 8370C a
        /// 64-byte load across two cache lines costs about twice an aligned one, so its block writes load memory
        /// vectors from 64-byte boundaries (<see cref="NinoBlockCollections"/>.WriteAligned).
        /// </summary>
        internal static readonly bool IceLake = X86Base.IsSupported && !IsAmd() && IsIceLake();

        /// <summary>
        /// An Intel CPU of the Sunny Cove generation and its client relatives (Ice Lake server and client, Tiger Lake,
        /// Rocket Lake), by family 6 and model: the first cores with AVX-512 VBMI, whose 64-byte permutes and masked
        /// copies ran slower than 32-byte ones on GitHub's Xeon Platinum 8370C.
        /// </summary>
        private static bool IsIceLake()
        {
            int eax = X86Base.CpuId(1, 0).Eax;
            int family = (eax >> 8) & 0xF, model = ((eax >> 4) & 0xF) | ((eax >> 12) & 0xF0);
            return family == 6 && model is 0x6A or 0x6C or 0x7D or 0x7E or 0x9D or 0x8C or 0x8D or 0xA7;
        }

        /// <summary>
        /// An x64 CPU with AVX-512 VBMI whose two-source 64-byte byte permute (vpermt2b) writes values of more than 16
        /// bytes faster than lane groups of 32-byte vectors (<see cref="NinoBlockCollections"/>.WritePairs),
        /// <see cref="Wide512"/>. Measured on GitHub's runners, 1,000 values of 24 bytes as 15-byte blocks (.NET 10):
        /// Xeon Platinum 8573C 0.65 and EPYC 9V45 0.76 times the time of lane groups, Xeon Platinum 8370C (Ice Lake)
        /// 1.44 times.
        /// </summary>
        internal static readonly bool PairPermutes = Avx512Vbmi.IsSupported && Wide512;

#endif
        /// <summary>
        /// <see cref="Copy"/> into memory that may have just been allocated (a read into a new array): Memmove from
        /// <see cref="MinUninitializedArrayBytes"/>, where a new array is uninitialized and vector stores read each of
        /// its lines from memory first (Memmove's rep movsb writes whole lines without reading them). Below, where a
        /// new array was zeroed into the cache, as <see cref="Copy"/>. Measured on an AVX-512 Xeon (.NET 10): 1,000
        /// ints read into a new uninitialized array in 470 to 710 ns with Memmove, 760 to 830 with vector stores; into
        /// an existing array of 250, 14 ns instead of 23 with vector stores. Out of line: a size that is not a constant
        /// takes every branch of Copy, and inlined into each raw list and array read that code slowed the reads around
        /// it (a list of 8 classes holding a List&lt;int&gt; each, read in place: 680 to 710 ns inline, 570 to 610 out
        /// of line).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CopyToNew(ref byte destination, ref byte source, uint size)
        {
#if NET8_0_OR_GREATER
            if (size >= MinUninitializedArrayBytes && !AmdCopy)
            {
                Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
                return;
            }

            // On AMD with 64-byte vectors, 32-byte ones beyond 32 KB too, where Copy calls Memmove: into a new
            // uninitialized array they beat it (GitHub's EPYC 9V45 and 9V74, 160,000 bytes: 0.92 to 0.96 times the
            // time).
            if (AmdCopy && Vector512.IsHardwareAccelerated &&
                size - (MaxVectorCopy + 1) <= MaxVector256Copy - (MaxVectorCopy + 1))
            {
                CopyVectors<Vector256<byte>>(ref destination, ref source, size);
                return;
            }
#endif
            Copy(ref destination, ref source, size);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Largest copy done with 64-byte vectors by <see cref="CopyVectors{TVector}"/>; Memmove (the C library's from
        /// 2 KB) beyond. Measured on an AVX-512 Xeon (.NET 10, the destination 4 bytes past a 64-byte boundary): 16,000
        /// bytes in 111 ns instead of 145, 32,000 in 249 instead of 283, on par at 64,000, slower beyond (160,000: 3.2
        /// µs instead of 2.9).
        /// </summary>
        private const uint MaxVectorCopy = 32 * 1024;

        /// <summary>
        /// The range copied with 32-byte vectors where 64-byte ones are not accelerated (AVX2 without AVX-512, most
        /// desktop CPUs). Measured on a Xeon with AVX-512 turned off (.NET 10, source and destination of different
        /// alignments) against Memmove: 256 bytes 3.9 ns instead of 6.3, 1,200 bytes 12.4 instead of 15.7, 8,000 bytes
        /// 68 instead of 85; on par from 129 to 255 bytes. On GitHub's AMD EPYC runners (7763 and 9V74), whose C
        /// library copies with rep movsb, it is 2 to 30 times as fast up to 16,000 bytes (8,000: 83 to 85 ns instead of
        /// 236 to 2,775). Larger blocks written into a serializer's buffer keep a gain, even against the C library's
        /// AVX-512 copy on that Xeon: 32,000 bytes in 745 ns instead of 837, 160,000 in 2.9 µs instead of 3.2, 480,000
        /// in 8.8 µs instead of 10.0. Beyond 1 MB, Memmove, whose stores bypass the cache for blocks larger than it.
        /// </summary>
        private const uint MaxVector256Copy = 1024 * 1024;

        /// <summary>
        /// Copies at least one vector (<typeparamref name="TVector"/>, a <c>Vector256</c> or <c>Vector512</c> of bytes)
        /// with vector loads and stores: the first one unaligned, then four at a time with aligned stores (an unaligned
        /// store straddles two cache lines), and the last one unaligned, overlapping. Measured on an AVX-512 Xeon
        /// against Memmove (.NET 10), source and destination of different alignments, with 64-byte vectors: 256 bytes
        /// 5.9 ns instead of 7.9 ns, 1,200 bytes 11.9 ns instead of 18.6 ns, 4,000 bytes 34 ns instead of 48 ns.
        /// </summary>
        // Fully optimized from the first call (see NinoMask.CopyVectors).
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static unsafe void CopyVectors<TVector>(ref byte destination, ref byte source, uint size)
            where TVector : struct
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<TVector>(ref source));
            nuint i = width - ((nuint)Unsafe.AsPointer(ref destination) & (width - 1));
            nuint end = size - width;
            for (; i + 3 * width <= end; i += 4 * width)
            {
                var a = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i));
                var b = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + width));
                var c = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + 2 * width));
                var d = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + 3 * width));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i), a);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + width), b);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + 2 * width), c);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + 3 * width), d);
            }

            for (; i <= end; i += width)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i),
                    Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, end),
                Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, end)));
        }
#endif

#if NET8_0_OR_GREATER
        /// <summary>
        /// Largest copy done by <see cref="CopyPairs"/> on Arm64 instead of Memmove; above it the two take the same
        /// time.
        /// </summary>
        private const uint MaxArm64VectorCopy = 64 * 1024;

        /// <summary>
        /// Copies more than 64 bytes on Arm64 (Memmove above <see cref="MaxArm64VectorCopy"/>, or in a 32-bit process;
        /// out of line, so Copy inlined into every read and write gains only this call): the first 16 bytes, then from
        /// the first 16-byte aligned source address 64 bytes a step through cursors, four 16-byte loads and stores that
        /// the JIT pairs into two ldp and two stp of q registers, then the last 64 bytes, overlapping. Measured on
        /// GitHub's Arm64 runners (Neoverse N2, .NET 10, the destination 4 bytes past a 64-byte boundary, the source 0,
        /// 4 or 8 bytes past one) against the C library's memcpy: 128 bytes in 0.80 to 0.84 of its time, 1,024 in 0.73
        /// to 0.91, 8,000 in 0.76 to 1.00, 64,000 and more the same. Without aligning the loads the loop was faster
        /// than memcpy from an aligned source but up to 1.31 times slower from one 4 bytes past it.
        /// </summary>
        // Fully optimized from the first call (see NinoMask.CopyVectors).
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static unsafe void CopyPairs(ref byte destination, ref byte source, uint size)
        {
            if (size > MaxArm64VectorCopy || !Is64Bit)
            {
                Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
                return;
            }

            Vector128.LoadUnsafe(ref source).StoreUnsafe(ref destination);
            // 1 to 16 bytes, to the next aligned source address (a moving GC may change it: only slower, still right).
            nint skip = 16 - (nint)((nuint)Unsafe.AsPointer(ref source) & 15);
            ref byte from = ref Unsafe.Add(ref source, skip);
            ref byte to = ref Unsafe.Add(ref destination, skip);
            for (nuint n = (size - (nuint)skip) / 64; n != 0; n--)
            {
                var a = Vector128.LoadUnsafe(ref from);
                var b = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 16));
                var c = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 32));
                var d = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 48));
                a.StoreUnsafe(ref to);
                b.StoreUnsafe(ref Unsafe.Add(ref to, 16));
                c.StoreUnsafe(ref Unsafe.Add(ref to, 32));
                d.StoreUnsafe(ref Unsafe.Add(ref to, 48));
                from = ref Unsafe.Add(ref from, 64);
                to = ref Unsafe.Add(ref to, 64);
            }

            // The last 64 bytes, overlapping what the loop copied.
            from = ref Unsafe.Add(ref source, (nint)size - 64);
            to = ref Unsafe.Add(ref destination, (nint)size - 64);
            var e = Vector128.LoadUnsafe(ref from);
            var f = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 16));
            var g = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 32));
            var h = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 48));
            e.StoreUnsafe(ref to);
            f.StoreUnsafe(ref Unsafe.Add(ref to, 16));
            g.StoreUnsafe(ref Unsafe.Add(ref to, 32));
            h.StoreUnsafe(ref Unsafe.Add(ref to, 48));
        }
#endif

        /// <summary>16 bytes, moved with one load and one store.</summary>
        private struct Bytes16
        {
#pragma warning disable CS0649
            public ulong A, B;
#pragma warning restore CS0649
        }

        /// <summary>
        /// Allocates an array whose contents are not zeroed when the runtime supports it and it is large enough for
        /// that to be cheaper (<see cref="NewArray{T}"/>). Only use when every element is overwritten before being
        /// read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] AllocateUninitializedArray<T>(int length) => NewArray<T>(length, uninitialized: true);

        /// <summary>
        /// A new array of <paramref name="length"/> (not negative) elements, the shared empty array for 0, and
        /// allocated uninitialized from 6 KB (MinUninitializedArrayBytes) on .NET 5 and later when
        /// <paramref name="uninitialized"/> (the caller overwrites every element). One unsigned compare picks the
        /// common case, a zeroed array of 1 element or more. The others are inline too: a call in between would cost
        /// a frame on the large arrays (where an uninitialized one is called for).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T[] NewArray<T>(int length, bool uninitialized)
        {
            if ((uint)(length - 1) < (uint)MaxZeroedLength<T>(uninitialized))
                return new T[length];
            if (length == 0)
                return Array.Empty<T>();
#if NET8_0_OR_GREATER
            // At least MinUninitializedArrayBytes (MaxZeroedLength): no size test of its own.
            if (uninitialized)
                return GC.AllocateUninitializedArray<T>(length);
#endif
            return new T[length];
        }

        /// <summary>
        /// The longest array of <typeparamref name="T"/> that <see cref="NewArray{T}"/> allocates zeroed (when
        /// <paramref name="uninitialized"/>; otherwise any length), as <c>new T[length]</c> does. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MaxZeroedLength<T>(bool uninitialized)
        {
#if NET8_0_OR_GREATER
            // length * size < MinUninitializedArrayBytes.
            if (uninitialized)
                return (MinUninitializedArrayBytes - 1) / Unsafe.SizeOf<T>();
#endif
            return int.MaxValue;
        }

        /// <summary>
        /// Whether <typeparamref name="T"/> is a reference type or contains references (i.e. is not unmanaged).
        /// Constant-folded by the JIT on .NET Core and .NET Standard 2.1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsReferenceOrContainsReferences<T>()
        {
#if NETSTANDARD2_0
            return TypeInfo<T>.IsReferenceOrContainsReferences;
#else
            return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#endif
        }

#if NETSTANDARD2_0
        private static class TypeInfo<T>
        {
            public static readonly bool IsReferenceOrContainsReferences = !IsUnmanaged(typeof(T));
        }
#endif

        /// <summary>Whether <paramref name="type"/> is an unmanaged type (no references anywhere in its
        /// layout).</summary>
        // Reached from the runtime resolver (dynamic code only) and the netstandard2.0 build.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = NinoLayout.FieldsJustification)]
        public static bool IsUnmanaged(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type.IsPointer)
                return true;
            if (!type.IsValueType || type.IsGenericTypeDefinition)
                return false;

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic))
            {
                if (!IsUnmanaged(field.FieldType))
                    return false;
            }

            return true;
        }
    }
}
