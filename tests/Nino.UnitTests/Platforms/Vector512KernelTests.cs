#if NET8_0_OR_GREATER && !NINO_CORE_NETSTANDARD
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// The x64 512-bit kernels, called directly. Nino dispatches to them only where <c>NinoSimd.Prefer512</c> holds
/// (AVX-512, not Ice Lake), so CI on Ice Lake runners never reaches them through the public API; these tests need only
/// the instructions, so any AVX-512 host checks them. Each kernel is compared with a scalar reference at the lengths
/// around its vector widths, its source placed against a guard page (<see cref="GuardedMemory"/>) so a read past it
/// crashes, its destination followed by bytes that must stay untouched; kernels that reject data get one invalid
/// element at the first, a middle and the last position. Hosts without the instructions report inconclusive.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Vector512KernelTests
{
    private const byte Sentinel = 0xCC;
    private const int Long = 1031;

    private static void Require(bool supported, string isa)
    {
        if (!supported)
            Assert.Inconclusive($"{isa} is not supported on this host.");
    }

    /// <summary>A copy of <paramref name="data"/> ending at a guard page (one window: the previous copy is gone).</summary>
    private static Span<byte> Guard(ReadOnlySpan<byte> data)
    {
        if (GuardedMemory.AtEnd(new byte[1]).IsEmpty)
            Assert.Inconclusive("Guard pages are unavailable on this platform.");
        return GuardedMemory.AtEnd(data);
    }

    /// <summary>0 to 3, then around one, two and three vectors of each lane count, and a long length.</summary>
    private static int[] Lengths(int minimum, params int[] lanes)
    {
        var set = new SortedSet<int> { 0, 1, 2, 3, Long };
        foreach (int l in lanes)
            foreach (int n in new[] { l - 1, l, l + 1, 2 * l - 1, 2 * l, 2 * l + 1, 3 * l + 1 })
                set.Add(n);
        set.RemoveWhere(n => n < minimum);
        return set.ToArray();
    }

    private static void AssertBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string what)
    {
        Assert.AreEqual(expected.Length, actual.Length, what);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"{what}: byte {i} is {actual[i]:X2}, expected {expected[i]:X2}");
        }
    }

    private static void AssertUntouched(ReadOnlySpan<byte> bytes, string what)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != Sentinel)
                Assert.Fail($"{what}: byte {i} outside the destination was written");
        }
    }

    // NinoSimd.Pairs.cs: Interleave512 (whole 512-bit vectors) and InterleaveBest, which takes it on Prefer512 hosts.

    [TestMethod]
    public void Interleave512WritesPairsInOrder()
    {
        Require(Avx512F.IsSupported && Avx512BW.IsSupported, "AVX-512F and AVX-512BW");
        CheckInterleave<short, NinoSimd.Zip16>(wide: true);
        CheckInterleave<int, NinoSimd.Zip32>(wide: true);
        CheckInterleave<long, NinoSimd.Zip64>(wide: true);
    }

    [TestMethod]
    public void InterleaveBestWritesPairsAtEveryWidth()
    {
        Require(Sse2.IsSupported || System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported, "SSE2 or AdvSimd");
        CheckInterleave<short, NinoSimd.Zip16>(wide: false);
        CheckInterleave<int, NinoSimd.Zip32>(wide: false);
        CheckInterleave<long, NinoSimd.Zip64>(wide: false);
    }

    private static void CheckInterleave<T, TZip>(bool wide) where T : unmanaged where TZip : struct, NinoSimd.IZip<T>
    {
        int size = Unsafe.SizeOf<T>();
        int lanes = Vector512<T>.Count;
        // Interleave512 takes whole 512-bit vectors only; InterleaveBest at least one 128-bit vector.
        int[] lengths = wide
            ? new[] { lanes, 2 * lanes, 3 * lanes, Long & -lanes }
            : Lengths(Vector128<T>.Count, Vector128<T>.Count, Vector256<T>.Count, lanes);
        var random = new Random(size);
        foreach (int count in lengths)
        {
            int bytes = count * size;
            var first = new byte[bytes];
            var second = new byte[bytes];
            random.NextBytes(first);
            random.NextBytes(second);
            var expected = new byte[2 * bytes];
            for (int i = 0; i < count; i++)
            {
                first.AsSpan(i * size, size).CopyTo(expected.AsSpan(2 * i * size));
                second.AsSpan(i * size, size).CopyTo(expected.AsSpan((2 * i + 1) * size));
            }

            // Either source against the guard page in turn.
            foreach (bool secondLast in new[] { true, false })
            {
                var both = new byte[2 * bytes];
                (secondLast ? first : second).CopyTo(both, 0);
                (secondLast ? second : first).CopyTo(both, bytes);
                var guarded = Guard(both);
                ref byte low = ref MemoryMarshal.GetReference(guarded);
                ref byte high = ref Unsafe.Add(ref low, bytes);
                ref T a = ref Unsafe.As<byte, T>(ref (secondLast ? ref low : ref high));
                ref T b = ref Unsafe.As<byte, T>(ref (secondLast ? ref high : ref low));

                var target = new byte[2 * bytes + 64];
                target.AsSpan().Fill(Sentinel);
                if (wide)
                    NinoSimd.Interleave512<T, TZip>(ref a, ref b, ref target[0], count);
                else
                    NinoSimd.InterleaveBest<T, TZip>(ref a, ref b, ref target[0], count);
                string what = $"{typeof(T).Name} x {count}, second last {secondLast}";
                AssertBytes(expected, target.AsSpan(0, 2 * bytes), what);
                AssertUntouched(target.AsSpan(2 * bytes), what);
            }
        }
    }

    // NinoSimd.Pairs.cs: DeinterleaveInt32_512 (whole 512-bit vectors) and DeinterleaveInt32, which takes it on
    // Prefer512 hosts.

    [TestMethod]
    public void DeinterleaveInt32_512SplitsPairs()
    {
        Require(Avx512F.IsSupported, "AVX-512F");
        foreach (int count in new[] { 16, 32, 48, Long & -16 })
            CheckDeinterleave(count, wide: true);
    }

    [TestMethod]
    public void DeinterleaveInt32SplitsPairsAtEveryWidth()
    {
        foreach (int count in Lengths(0, 4, 8, 16))
            CheckDeinterleave(count, wide: false);
    }

    private static void CheckDeinterleave(int count, bool wide)
    {
        const int padding = 16, marker = 0x5A5A5A5A;
        var random = new Random(count);
        var source = new byte[count * 8];
        random.NextBytes(source);
        var guarded = Guard(source);
        var first = new int[count + padding];
        var second = new int[count + padding];
        first.AsSpan().Fill(marker);
        second.AsSpan().Fill(marker);
        if (wide)
            NinoSimd.DeinterleaveInt32_512(ref MemoryMarshal.GetReference(guarded), ref first[0], ref second[0], count);
        else
            NinoSimd.DeinterleaveInt32(ref MemoryMarshal.GetReference(guarded), ref first[0], ref second[0], count);
        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual(BitConverter.ToInt32(source, 8 * i), first[i], $"{count}: key {i}");
            Assert.AreEqual(BitConverter.ToInt32(source, 8 * i + 4), second[i], $"{count}: value {i}");
        }

        for (int i = count; i < count + padding; i++)
        {
            Assert.AreEqual(marker, first[i], $"{count}: key {i} past the end was written");
            Assert.AreEqual(marker, second[i], $"{count}: value {i} past the end was written");
        }
    }

    // NinoSimd.Text.cs: Narrow512 and Widen512 (n >= 64; Nino calls them from 128), their destinations at several
    // offsets from a 64-byte boundary, which their aligned middle stores depend on.

    [TestMethod]
    public void Narrow512NarrowsLatin1AndStopsAboveIt()
    {
        Require(Avx512BW.IsSupported, "AVX-512BW");
        foreach (int n in Lengths(64, 16, 32, 64))
        foreach (int shift in new[] { 0, 1, 31, 63 })
        {
            var random = new Random(n * 64 + shift);
            var chars = new ushort[n];
            for (int i = 0; i < n; i++)
                chars[i] = (ushort)random.Next(256);
            var guarded = MemoryMarshal.Cast<byte, ushort>(Guard(MemoryMarshal.AsBytes(chars.AsSpan())));
            ref ushort source = ref MemoryMarshal.GetReference(guarded);
            var target = GC.AllocateArray<byte>(n + 128, pinned: true);
            string what = $"{n} chars at +{shift}";

            target.AsSpan().Fill(Sentinel);
            Assert.IsTrue(NinoSimd.Narrow512(ref source, ref target[shift], (nuint)n), what);
            AssertUntouched(target.AsSpan(0, shift), what);
            for (int i = 0; i < n; i++)
                Assert.AreEqual((byte)chars[i], target[shift + i], $"{what}: char {i}");
            AssertUntouched(target.AsSpan(shift + n), what);

            foreach (int bad in new[] { 0, n / 2, n - 1 })
            {
                // The low byte stays a valid Latin-1 value: the whole char must be checked.
                guarded[bad] = (ushort)(0x100 | chars[bad]);
                target.AsSpan().Fill(Sentinel);
                Assert.IsFalse(NinoSimd.Narrow512(ref source, ref target[shift], (nuint)n), $"{what}: U+0100 at {bad}");
                // Only whole vectors of Latin-1 are stored: each byte is untouched or its char's.
                for (int i = 0; i < n; i++)
                {
                    byte written = target[shift + i];
                    Assert.IsTrue(written == Sentinel || (i != bad && written == (byte)chars[i]),
                        $"{what}: U+0100 at {bad}, byte {i} is {written:X2}");
                }

                Assert.AreEqual(Sentinel, target[shift + bad], $"{what}: the char above U+00FF at {bad} was stored");
                AssertUntouched(target.AsSpan(0, shift), what);
                AssertUntouched(target.AsSpan(shift + n), what);
                guarded[bad] = chars[bad];
            }
        }
    }

    [TestMethod]
    public void Widen512WidensEveryByte()
    {
        Require(Avx512BW.IsSupported, "AVX-512BW");
        const ushort marker = 0xCCCC;
        foreach (int n in Lengths(64, 16, 32, 64))
        foreach (int shift in new[] { 0, 1, 15, 31 })
        {
            var random = new Random(n * 32 + shift);
            var bytes = new byte[n];
            random.NextBytes(bytes);
            var guarded = Guard(bytes);
            var target = GC.AllocateArray<ushort>(n + 64, pinned: true);
            target.AsSpan().Fill(marker);
            NinoSimd.Widen512(ref MemoryMarshal.GetReference(guarded), ref target[shift], (nuint)n);
            string what = $"{n} bytes at +{shift} chars";
            for (int i = 0; i < shift; i++)
                Assert.AreEqual(marker, target[i], $"{what}: char {i} before the destination was written");
            for (int i = 0; i < n; i++)
                Assert.AreEqual((ushort)bytes[i], target[shift + i], $"{what}: char {i}");
            for (int i = shift + n; i < target.Length; i++)
                Assert.AreEqual(marker, target[i], $"{what}: char {i} past the destination was written");
        }
    }

    // NinoSimd.Text.cs: CompareLatin1_512, the long-text comparison (count >= 64), which compares whole chars.

    [TestMethod]
    public void CompareLatin1_512ComparesWholeCharsAtEveryTail()
    {
        Require(Avx512BW.IsSupported, "AVX-512BW");
        foreach (int length in Lengths(64, 16, 32, 64))
        foreach (int tailPadding in new[] { 0, 1, 7 })
        {
            // Padding shifts the source address; zero padding ends the payload at the guard page.
            var bytes = new byte[length + tailPadding];
            var chars = new ushort[length];
            for (int i = 0; i < length; i++)
                chars[i] = bytes[i] = (byte)(i * 31 + 0xE9);
            var guarded = Guard(bytes);
            ref byte source = ref MemoryMarshal.GetReference(guarded);
            Assert.IsTrue(NinoSimd.SameLatin1Blocks<NinoSimd.CompareLatin1_512>(ref source, ref chars[0], (nuint)length),
                $"{length}+{tailPadding}");
            for (int at = 0; at < length; at++)
            {
                ushort original = chars[at];
                chars[at] = (ushort)(original + 0x100);
                Assert.IsFalse(NinoSimd.SameLatin1Blocks<NinoSimd.CompareLatin1_512>(ref source, ref chars[0], (nuint)length),
                    $"high byte at {at}/{length}");
                chars[at] = (ushort)(original ^ 1);
                Assert.IsFalse(NinoSimd.SameLatin1Blocks<NinoSimd.CompareLatin1_512>(ref source, ref chars[0], (nuint)length),
                    $"low byte at {at}/{length}");
                chars[at] = original;
            }
        }
    }

    // NinoChecks.cs: FlagVectors512 through ValidFlagBlocks (presence bytes or bools stride bytes apart, any length)
    // and CopyFlags (a copy checked in the same pass; at least one vector, a whole number of strides).

    [TestMethod]
    public void FlagVectors512FindsEveryInvalidFlag()
    {
        Require(Vector512.IsHardwareAccelerated, "Vector512");
        foreach (int stride in new[] { 2, 4, 8, 16 })
        foreach (int bytes in Lengths(0, 16, 32, 64, 128))
        {
            var random = new Random(stride * 4096 + bytes);
            var data = new byte[bytes];
            // Every byte between the flags may hold anything.
            random.NextBytes(data);
            for (int at = 0; at < bytes; at += stride)
                data[at] = (byte)random.Next(2);
            var guarded = Guard(data);
            ref byte first = ref MemoryMarshal.GetReference(guarded);
            string what = $"stride {stride}, {bytes} bytes";
            Assert.IsTrue(NinoChecks.ValidFlagBlocks<Vector512<byte>, NinoChecks.FlagVectors512>(ref first, bytes, stride),
                what);
            if (bytes == 0)
                continue;

            int flags = (bytes - 1) / stride + 1;
            foreach (int index in new[] { 0, flags / 2, flags - 1 })
            foreach (byte invalid in new byte[] { 2, 0x80, 0xFF })
            {
                ref byte flag = ref guarded[index * stride];
                byte original = flag;
                flag = invalid;
                Assert.IsFalse(
                    NinoChecks.ValidFlagBlocks<Vector512<byte>, NinoChecks.FlagVectors512>(ref first, bytes, stride),
                    $"{what}: {invalid} at flag {index}");
                flag = original;
            }
        }
    }

    [TestMethod]
    public void FlagVectors512CopiesAndFindsEveryInvalidFlag()
    {
        Require(Vector512.IsHardwareAccelerated, "Vector512");
        foreach (int stride in new[] { 1, 2, 4, 8, 16 })
        {
            Vector128<ulong> mask;
            if (stride == 1)
                mask = Vector128.Create(0xFEFE_FEFE_FEFE_FEFEUL); // As TryCopyBools.
            else
                Assert.IsTrue(NinoChecks.TryFlagMask(stride, out mask), $"stride {stride}");

            var lengths = new SortedSet<int>(Lengths(64, 16, 32, 64, 128).Select(n => (n + stride - 1) / stride * stride));
            foreach (int bytes in lengths)
            {
                var random = new Random(stride * 4096 + bytes);
                var data = new byte[bytes];
                random.NextBytes(data);
                for (int at = 0; at < bytes; at += stride)
                    data[at] = (byte)random.Next(2);
                var guarded = Guard(data);
                ref byte source = ref MemoryMarshal.GetReference(guarded);
                var target = new byte[bytes + 64];
                string what = $"stride {stride}, {bytes} bytes";

                target.AsSpan().Fill(Sentinel);
                Assert.IsTrue(CopyFlags512(ref target[0], ref source, bytes, mask), what);
                AssertBytes(data, target.AsSpan(0, bytes), what);
                AssertUntouched(target.AsSpan(bytes), what);

                int flags = bytes / stride;
                foreach (int index in new[] { 0, flags / 2, flags - 1 })
                foreach (byte invalid in new byte[] { 2, 0x80, 0xFF })
                {
                    ref byte flag = ref guarded[index * stride];
                    byte original = flag;
                    flag = invalid;
                    target.AsSpan().Fill(Sentinel);
                    // The destination is fresh and discarded on failure: only the result and its bounds count.
                    Assert.IsFalse(CopyFlags512(ref target[0], ref source, bytes, mask), $"{what}: {invalid} at flag {index}");
                    AssertUntouched(target.AsSpan(bytes), what);
                    flag = original;
                }
            }
        }
    }

    private static bool CopyFlags512(ref byte target, ref byte source, int bytes, Vector128<ulong> mask) =>
        NinoChecks.CopyFlags<Vector512<byte>, NinoChecks.FlagVectors512>(ref target, ref source, bytes, mask);

    // NinoNullables.cs: BoolSlots512 through WriteBoolSlots and ReadBoolSlots (bool? slots, at least 32 of them):
    // a slot is its presence byte and its bool, zero when null; reads throw on a bool other than 0 or 1 after storing
    // the slots before it.

    private static ushort[] BoolSlots(int count, Random random, out ushort[] expected)
    {
        var slots = new ushort[count];
        expected = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            bool present = random.Next(2) == 1;
            // A null slot's value byte is ignored, whatever it holds.
            int value = present ? random.Next(2) : random.Next(256);
            slots[i] = (ushort)((present ? 1 : 0) | value << 8);
            expected[i] = present ? slots[i] : (ushort)0;
        }

        return slots;
    }

    [TestMethod]
    public void BoolSlots512WritesCanonicalSlots()
    {
        Require(Vector512.IsHardwareAccelerated, "Vector512");
        const ushort marker = 0xCCCC;
        foreach (int count in Lengths(Vector512<ushort>.Count, 16, 32, 64))
        {
            var slots = BoolSlots(count, new Random(count), out var expected);
            var guarded = MemoryMarshal.Cast<byte, ushort>(Guard(MemoryMarshal.AsBytes(slots.AsSpan())));
            var target = new ushort[count + 32];
            target.AsSpan().Fill(marker);
            NinoNullables<bool>.WriteBoolSlots<Vector512<ushort>, NinoNullables<bool>.BoolSlots512>(
                ref MemoryMarshal.GetReference(guarded), ref target[0], count);
            for (int i = 0; i < count; i++)
                Assert.AreEqual(expected[i], target[i], $"{count}: slot {i}");
            for (int i = count; i < target.Length; i++)
                Assert.AreEqual(marker, target[i], $"{count}: slot {i} past the end was written");
        }
    }

    [TestMethod]
    public void BoolSlots512ReadsSlotsAndRejectsInvalidBools()
    {
        Require(Vector512.IsHardwareAccelerated, "Vector512");
        const ushort marker = 0xCCCC;
        foreach (int count in Lengths(Vector512<ushort>.Count, 16, 32, 64))
        {
            var slots = BoolSlots(count, new Random(count), out var expected);
            var guarded = MemoryMarshal.Cast<byte, ushort>(Guard(MemoryMarshal.AsBytes(slots.AsSpan())));
            ref ushort source = ref MemoryMarshal.GetReference(guarded);
            var target = new ushort[count + 32];
            target.AsSpan().Fill(marker);
            NinoNullables<bool>.ReadBoolSlots<Vector512<ushort>, NinoNullables<bool>.BoolSlots512>(
                ref source, ref target[0], count);
            for (int i = 0; i < count; i++)
                Assert.AreEqual(expected[i], target[i], $"{count}: slot {i}");
            for (int i = count; i < target.Length; i++)
                Assert.AreEqual(marker, target[i], $"{count}: slot {i} past the end was written");

            foreach (int bad in new[] { 0, count / 2, count - 1 })
            foreach (int invalid in new[] { 2, 0xFF })
            {
                guarded[bad] = (ushort)(1 | invalid << 8);
                target.AsSpan().Fill(marker);
                bool threw = false;
                try
                {
                    NinoNullables<bool>.ReadBoolSlots<Vector512<ushort>, NinoNullables<bool>.BoolSlots512>(
                        ref source, ref target[0], count);
                }
                catch (NinoDataException)
                {
                    threw = true;
                }

                string what = $"{count}: bool {invalid} at slot {bad}";
                Assert.IsTrue(threw, what);
                // The slots before the invalid one are stored, as the scalar loop stores them; it is not.
                for (int i = 0; i < bad; i++)
                    Assert.AreEqual(expected[i], target[i], $"{what}: slot {i}");
                Assert.AreEqual(marker, target[bad], $"{what}: the invalid slot was stored");
                for (int i = count; i < target.Length; i++)
                    Assert.AreEqual(marker, target[i], $"{what}: slot {i} past the end was written");
                guarded[bad] = slots[bad];
            }
        }
    }
}
#endif
