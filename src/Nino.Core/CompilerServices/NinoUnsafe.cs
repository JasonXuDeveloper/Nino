using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.Internal;
#if NET8_0_OR_GREATER
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
    public static partial class NinoUnsafe
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
        internal const int MinUninitializedArrayBytes = 6 * 1024;
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
        /// worth using: the JIT accelerates <c>Vector&lt;T&gt;</c> and, in the netstandard2.1 build, the runtime is .NET
        /// (Core), whose description start with ".NET". Never on Mono or Unity: classic Mono's JIT
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
        /// <see cref="NumericVectors"/> and <see cref="VectorMasks"/> in the netstandard2.1 build (a class of its own,
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
        public static bool ReadBool(ref byte source, int offset) => CheckedBool(Unsafe.Add(ref source, offset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool CheckedBool(byte value)
        {
            if (value > 1)
                NinoThrow.InvalidBool(value);
            return Unsafe.As<byte, bool>(ref value);
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
        /// Constant-folded by the JIT.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsReferenceOrContainsReferences<T>()
        {
            return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        }

        /// <summary>Whether <paramref name="type"/> is an unmanaged type (no references anywhere in its
        /// layout).</summary>
        // Reached from the runtime resolver (dynamic code only) and reflection over layouts.
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
                // A function pointer (delegate*) as the UIntPtr it is laid out as.
                if (!IsUnmanaged(NinoReflection.LayoutFieldType(field)))
                    return false;
            }

            return true;
        }
    }
}
