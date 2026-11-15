using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

namespace Nino.UnitTests
{
    /// <summary>Two values with no padding between them: a struct of any size is a tree of these over bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Packed<TA, TB> where TA : unmanaged where TB : unmanaged
    {
        public TA A;
        public TB B;
    }

    /// <summary>Two values at their natural alignment: a struct of any number of ints or longs is a tree of these.</summary>
    public struct Aligned<TA, TB> where TA : unmanaged where TB : unmanaged
    {
        public TA A;
        public TB B;
    }

    public struct OneNullable
    {
        public int A;
        public long? B;
    }

    public struct TwoNullables
    {
        public int? A;
        public double? B;
        public byte C;
    }

    public struct ByteNullables
    {
        public byte? A;
        public byte? B;
    }

    public struct TwelveBytes
    {
        public short? A;
        public int? B;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NineBytes
    {
        public byte A;
        public int? B;
    }

    public struct GuidNullable
    {
        public Guid? Id;
        public int Tail;
    }

    public struct DecimalNullable
    {
        public decimal? Price;
        public long Tail;
    }

    public struct SixNullables
    {
        public long? A, B, C, D, E, F;
    }

    /// <summary>A Nullable inside another's value: its value is cleared when either has none.</summary>
    public struct NullableOfNullables
    {
        public HoldsNullables? Inner;
        public int Tail;
    }

    /// <summary>Three deep.</summary>
    public struct NullableThreeDeep
    {
        public NullableOfNullables? Inner;
        public byte Tail;
    }

    /// <summary>A flag more than 64 bytes before the end of its value.</summary>
    public struct FarNullable
    {
        public Aligned<Aligned<Aligned<long, long>, Aligned<long, long>>, Aligned<Aligned<long, long>, Aligned<long, Aligned<long, long>>>>? Far;
        public int Tail;
    }

    /// <summary>
    /// Runs of raw <c>T?</c> of every stride, and of raw structs that hold <c>T?</c> fields, written from memory whose
    /// padding and absent values hold garbage: the bytes are the slots built one by one (the flag, zeros up to T's
    /// alignment, T; zeros for an element without a value), whatever kernel the stride, the run's length, the
    /// destination's place in a cache line and the CPU's instruction sets select. The test matrix runs this file with
    /// AVX-512, without it, without AVX2 and without SIMD.
    /// </summary>
    [TestClass]
    public class NullableRunTests
    {
        private static Type Tree(Type open, Type leaf, int leaves) => leaves == 1
            ? leaf
            : open.MakeGenericType(Tree(open, leaf, leaves / 2), Tree(open, leaf, leaves - leaves / 2));

        private static void Invoke(string method, Type type, params object[] arguments)
        {
            try
            {
                typeof(NullableRunTests).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(type).Invoke(null, arguments);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }

        // Run lengths in elements: the first few, and those whose bytes end on both sides of one, two, three and
        // four 64-byte vectors, of a period of five, and of a run long enough for every loop to turn.
        private static int[] Counts(int stride)
        {
            var counts = new SortedSet<int> { 1, 2, 3, 4, 5, 7 };
            foreach (int bytes in new[] { 16, 32, 48, 64, 128, 192, 256, 320, 384, 700, 1500 })
            {
                counts.Add(Math.Max(1, (bytes - 1) / stride));
                counts.Add(bytes / stride + 1);
                counts.Add(bytes / stride + 2);
            }

            return counts.ToArray();
        }

        private static readonly int[] FewOffsets = { 4, 12, 33 };

        private static readonly int[] EveryOffset = Enumerable.Range(0, 64).ToArray();

        /// <summary>
        /// One T as the wire holds it (<paramref name="clean"/>: padding zero, a Nullable without a value zero) and
        /// the same value as memory may hold it (<paramref name="dirty"/>: garbage in both).
        /// </summary>
        private static void Value<T>(Random random, Span<byte> clean, Span<byte> dirty) where T : unmanaged
        {
            var info = NinoLayout<T>.Info;
            random.NextBytes(clean);
            int[] absent = info.Absent ?? Array.Empty<int>();
            for (int i = 0; i < clean.Length; i++)
                if (info.Mask != null)
                    clean[i] &= info.Mask[i];
            // Outer values first: a flag inside a value without one is zero with it.
            for (int i = 0; i < absent.Length; i += 3)
            {
                clean[absent[i]] = (byte)(clean[absent[i]] != 0 && random.Next(3) != 0 ? 1 : 0);
                if (clean[absent[i]] == 0)
                    clean.Slice(absent[i + 1], absent[i + 2]).Clear();
            }

            clean.CopyTo(dirty);
            for (int i = 0; i < dirty.Length; i++)
                if (info.Mask != null && info.Mask[i] == 0)
                    dirty[i] = (byte)random.Next(1, 256);
            for (int i = 0; i < absent.Length; i += 3)
                if (clean[absent[i]] == 0)
                    for (int at = absent[i + 1]; at < absent[i + 1] + absent[i + 2]; at++)
                        dirty[at] = (byte)random.Next(1, 256);
        }

        /// <summary>The memory of an array's elements (a <c>T?</c> is not a type <c>MemoryMarshal.AsBytes</c> takes).</summary>
        private static Span<byte> Memory<TElement>(TElement[] array) => array.Length == 0
            ? default
            : MemoryMarshal.CreateSpan(ref Unsafe.As<TElement, byte>(ref array[0]), array.Length * Unsafe.SizeOf<TElement>());

        private static byte[] WithCount(int count, byte[] elements)
        {
            var bytes = new byte[4 + elements.Length];
            BitConverter.TryWriteBytes(bytes, count);
            elements.CopyTo(bytes, 4);
            return bytes;
        }

        private static void WritesAt<T>(T value, byte[] expected, int[] offsets, string what)
        {
            using var writer = new NinoArrayBufferWriter(expected.Length + 256);
            foreach (int offset in offsets)
            {
                writer.ResetWrittenCount();
                writer.GetSpan(64).Slice(0, offset).Fill(0xEE);
                writer.Advance(offset);
                NinoSerializer.Serialize(value, writer);
                Assert.IsTrue(writer.WrittenSpan.Slice(offset).SequenceEqual(expected), $"{what}, {offset} bytes into the buffer");
                foreach (byte before in writer.WrittenSpan.Slice(0, offset))
                    Assert.AreEqual((byte)0xEE, before, $"{what}: the bytes before");
            }
        }

        private static void NullableRuns<T>(int[] offsets) where T : unmanaged
        {
            if (!NinoNullables<T>.Raw)
                return;
            int size = Unsafe.SizeOf<T>(), stride = Unsafe.SizeOf<T?>();
            var random = new Random(stride * 31 + size);
            foreach (int count in Counts(stride))
            for (int pattern = 0; pattern < 3; pattern++)
            {
                var clean = new byte[count * stride];
                var dirty = new T?[count];
                var dirtyBytes = Memory(dirty);
                for (int i = 0; i < dirtyBytes.Length; i++)
                    dirtyBytes[i] = (byte)random.Next(1, 256);
                for (int i = 0; i < count; i++)
                {
                    bool has = pattern == 0 ? i % 2 == 0 : pattern == 1 ? i % 3 == 2 : i % 7 != 3;
                    dirtyBytes[i * stride] = has ? (byte)1 : (byte)0;
                    if (!has)
                        continue;
                    clean[i * stride] = 1;
                    Value<T>(random, clean.AsSpan(i * stride + stride - size, size),
                        dirtyBytes.Slice(i * stride + stride - size, size));
                }

                string what = $"{typeof(T).Name}?[{count}] (stride {stride}), pattern {pattern}";
                byte[] expected = WithCount(count, clean);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(dirty), what);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(new List<T?>(dirty)), "List of " + what);
                WritesAt(dirty, expected, pattern == 0 ? offsets : FewOffsets, what);
                var read = NinoDeserializer.Deserialize<T?[]>(expected);
                Assert.IsTrue(Memory(read).SequenceEqual(clean), "read back, " + what);
            }
        }

        private static void StructRuns<T>(int[] offsets) where T : unmanaged
        {
            if (!NinoLayout<T>.IsCanonical)
                return;
            int size = Unsafe.SizeOf<T>();
            var random = new Random(size);
            foreach (int count in Counts(size))
            {
                var clean = new byte[count * size];
                var dirty = new T[count];
                var dirtyBytes = MemoryMarshal.AsBytes(dirty.AsSpan());
                for (int i = 0; i < count; i++)
                    Value<T>(random, clean.AsSpan(i * size, size), dirtyBytes.Slice(i * size, size));
                string what = $"{typeof(T).Name}[{count}] ({size} bytes)";
                byte[] expected = WithCount(count, clean);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(dirty), what);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(new List<T>(dirty)), "List of " + what);
                WritesAt(dirty, expected, offsets, what);
                var read = NinoDeserializer.Deserialize<T[]>(expected);
                Assert.IsTrue(MemoryMarshal.AsBytes(read.AsSpan()).SequenceEqual(clean), "read back, " + what);
            }
        }

        [TestMethod]
        public void RawNullableRunsOfEveryStrideWriteTheirSlots()
        {
            // Strides 3 to 144 in steps of one byte (a flag and 2 to 143 bytes), of 4 (ints) and of 8 (longs).
            for (int bytes = 2; bytes <= 143; bytes++)
                Invoke(nameof(NullableRuns), Tree(typeof(Packed<,>), typeof(byte), bytes), FewOffsets);
            for (int ints = 1; ints <= 35; ints++)
                Invoke(nameof(NullableRuns), Tree(typeof(Aligned<,>), typeof(int), ints), FewOffsets);
            for (int longs = 1; longs <= 17; longs++)
                Invoke(nameof(NullableRuns), Tree(typeof(Aligned<,>), typeof(long), longs), FewOffsets);
        }

        [TestMethod]
        public void RawNullableRunsWriteTheirSlotsAtEveryPlaceInACacheLine()
        {
            NullableRuns<byte>(EveryOffset);
            NullableRuns<short>(EveryOffset);
            NullableRuns<int>(EveryOffset);
            NullableRuns<long>(EveryOffset);
            NullableRuns<decimal>(EveryOffset);
            NullableRuns<Guid>(EveryOffset);
#if NET8_0_OR_GREATER
            NullableRuns<Int128>(EveryOffset);
#endif
            NullableRuns<PaddedPlain>(EveryOffset);
            foreach (int bytes in new[] { 2, 5, 11, 39, 71, 143 })
                Invoke(nameof(NullableRuns), Tree(typeof(Packed<,>), typeof(byte), bytes), EveryOffset);
            foreach (int longs in new[] { 3, 4, 5, 7, 8, 9 })
                Invoke(nameof(NullableRuns), Tree(typeof(Aligned<,>), typeof(long), longs), EveryOffset);
        }

        [TestMethod]
        public void RawStructsHoldingNullablesWriteZerosForAbsentValues()
        {
            StructRuns<OneNullable>(EveryOffset);
            StructRuns<TwoNullables>(EveryOffset);
            StructRuns<ByteNullables>(EveryOffset);
            StructRuns<TwelveBytes>(EveryOffset);
            StructRuns<NineBytes>(EveryOffset);
            StructRuns<GuidNullable>(EveryOffset);
            StructRuns<DecimalNullable>(EveryOffset);
            StructRuns<SixNullables>(EveryOffset);
            StructRuns<HoldsNullables>(EveryOffset);
            StructRuns<NestsNullables>(EveryOffset);
            StructRuns<NullableOfNullables>(EveryOffset);
            StructRuns<NullableThreeDeep>(EveryOffset);
            StructRuns<FarNullable>(EveryOffset);
        }

        [TestMethod]
        public void RawNullableRunsOfStructsHoldingNullablesWriteTheirSlots()
        {
            NullableRuns<OneNullable>(EveryOffset);
            NullableRuns<TwoNullables>(EveryOffset);
            NullableRuns<ByteNullables>(EveryOffset);
            NullableRuns<TwelveBytes>(EveryOffset);
            NullableRuns<NineBytes>(EveryOffset);
            NullableRuns<HoldsNullables>(EveryOffset);
            NullableRuns<NullableOfNullables>(EveryOffset);
            NullableRuns<FarNullable>(EveryOffset);
        }

        /// <summary>
        /// A run of raw T? with an invalid presence byte throws wherever the byte lies, and the run is cleared first,
        /// so neither a collection read into nor a new array allocated uninitialized (Memory, List) is left with the
        /// invalid byte or stale memory.
        /// </summary>
        [TestMethod]
        public void InvalidPresenceInARunThrowsAndLeavesNoInvalidOrStaleBytes()
        {
            foreach (int length in new[] { 5, 127, 128, 129, 200, 1000 })
            foreach (int bad in new[] { 0, 1, length / 2, length - 1 })
            {
                var values = Enumerable.Range(0, length).Select(i => i % 3 == 0 ? (long?)null : i).ToArray();
                byte[] bytes = NinoSerializer.Serialize(values);
                bytes[4 + bad * 16] = 2;
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<long?[]>(bytes));
                var list = Enumerable.Repeat((long?)-1, length).ToList();
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bytes, ref list));
                var target = new long?[length];
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bytes, ref target));
                Memory<long?> memory = default;
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bytes, ref memory));
                foreach (var held in new[] { list.ToArray(), target })
                    for (int i = 0; i < held.Length; i++)
                    {
                        byte flag = Unsafe.As<long?, byte>(ref held[i]);
                        Assert.IsTrue(flag <= 1, $"length {length}, bad {bad}: element {i} holds flag {flag}");
                    }

                // A new array that the read published before rejecting the data holds zeros, not the heap's memory.
                // Spans have no IndexOfAnyExcept in netstandard2.1, which this suite also compiles for.
                var published = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<long?, byte>(ref MemoryMarshal.GetReference(memory.Span)), memory.Length * 16);
                for (int i = 0; i < published.Length; i++)
                    Assert.AreEqual((byte)0, published[i],
                        $"length {length}, bad {bad}: the new array holds stale byte {published[i]} at {i}");
            }
        }
    }
}
