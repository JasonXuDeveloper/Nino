#if NINO_CORE_NETSTANDARD
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// The kernel hook of the netstandard builds (<see cref="NinoKernels"/>), which Unity's Burst kernels register through:
/// a managed stand-in that runs the scalar references through the hook, so the paths that call it write and read the
/// same bytes as those that do not. Run with <c>-p:NinoCoreTfm=netstandard2.1</c> (or 2.0); the Burst kernels
/// themselves are tested in the Unity project (Assets/Test/Editor/Burst).
/// </summary>
[TestClass]
[DoNotParallelize]
public unsafe class KernelHookTests
{
    private sealed class ScalarKernels : NinoKernels
    {
        public int Permutes, Narrows, Widens;
        public override int MinBlocks => 16;
        public override int MinChars => 32;

        public override void Permute(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count)
        {
            Assert.IsTrue(count >= MinBlocks && sourceStride is > 0 and <= 64 && destinationStride is > 0 and <= 64);
            Permutes++;
            PermuteScalar(source, sourceStride, destination, destinationStride, table, count);
        }

        public override bool Narrow(char* source, byte* destination, int length)
        {
            Assert.IsTrue(length >= MinChars);
            Narrows++;
            return NarrowScalar(source, destination, length);
        }

        public override void Widen(byte* source, char* destination, int length)
        {
            Assert.IsTrue(length >= MinChars);
            Widens++;
            WidenScalar(source, destination, length);
        }
    }

    private static readonly int[] Lengths = { 0, 1, 2, 15, 16, 17, 31, 32, 33, 64, 100, 1000 };

    [TestCleanup]
    public void Cleanup() => NinoKernels.Register(null);

    /// <summary>The bytes with the hook and without, and the values read back each way.</summary>
    private static ScalarKernels Same<T>(T value, Func<T, T, bool> equal)
    {
        NinoKernels.Register(null);
        Assert.IsNull(NinoKernels.Current);
        byte[] scalar = NinoSerializer.Serialize(value);
        var kernels = new ScalarKernels();
        NinoKernels.Register(kernels);
        Assert.AreSame(kernels, NinoKernels.Current);
        byte[] hooked = NinoSerializer.Serialize(value);
        CollectionAssert.AreEqual(scalar, hooked, typeof(T).Name);
        Assert.IsTrue(equal(value, NinoDeserializer.Deserialize<T>(hooked)));
        NinoKernels.Register(null);
        Assert.IsTrue(equal(value, NinoDeserializer.Deserialize<T>(hooked)));
        return kernels;
    }

    private static bool Equal<T>(T[] a, T[] b) => a.SequenceEqual(b);

    [TestMethod]
    public void BlocksWriteAndReadTheSameBytesThroughTheHook()
    {
        var random = new Random(3);
        foreach (int n in Lengths)
        {
            var a = Enumerable.Range(0, n).Select(_ => (random.Next(), (long)random.Next() << 17)).ToArray();
            var b = Enumerable.Range(0, n).Select(_ => ((byte)random.Next(), random.Next())).ToArray();
            var c = Enumerable.Range(0, n)
                .Select(_ => ((short)random.Next(), (byte)random.Next(), (long)random.Next(), random.Next(2) == 1)).ToArray();
            // 32 bytes in memory, 29 on the wire: two 16-byte vectors each side.
            var d = Enumerable.Range(0, n)
                .Select(_ => (random.NextDouble(), (long)random.Next(), random.NextDouble(), (float)random.NextDouble(), (byte)random.Next()))
                .ToArray();
            int expected = n >= 16 ? 1 : 0;
            Assert.AreEqual(2 * expected, Same(a, Equal).Permutes, $"(int, long) x {n}");
            Assert.AreEqual(2 * expected, Same(b, Equal).Permutes, $"(byte, int) x {n}");
            Assert.AreEqual(2 * expected, Same(c, Equal).Permutes, $"(short, byte, long, bool) x {n}");
            Assert.AreEqual(2 * expected, Same(d, Equal).Permutes, $"5-tuple x {n}");
        }
    }

    [TestMethod]
    public void RawStructsHaveTheirPaddingClearedThroughTheHook()
    {
        foreach (int n in Lengths)
        {
            var values = new PaddedPlain[n];
            // Garbage in the padding, as uninitialized memory would hold.
            Unsafe.InitBlockUnaligned(ref Unsafe.As<PaddedPlain, byte>(ref values.AsSpan().GetPinnableReference()), 0xAB,
                (uint)(n * Unsafe.SizeOf<PaddedPlain>()));
            for (int i = 0; i < n; i++)
            {
                values[i].A = (short)i;
                values[i].B = i * 7L;
            }

            var kernels = Same(values, Equal);
            // Written by the hook (the masked copy); read back as one copy.
            Assert.AreEqual(n >= 16 ? 1 : 0, kernels.Permutes, $"PaddedPlain x {n}");
            // The padding cleared: the bytes of the same values built clean.
            var clean = values.Select(v => new PaddedPlain { A = v.A, B = v.B }).ToArray();
            NinoKernels.Register(new ScalarKernels());
            byte[] hooked = NinoSerializer.Serialize(values);
            NinoKernels.Register(null);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean), hooked);
        }
    }

    [TestMethod]
    public void StringsWriteAndReadTheSameBytesThroughTheHook()
    {
        var random = new Random(5);
        foreach (int n in Lengths.Concat(new[] { 48, 63, 255, 256, 257 }))
        {
            string latin1 = new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(1, 256)).ToArray());
            var kernels = Same(latin1, string.Equals);
            int expected = n >= 32 ? 1 : 0;
            Assert.AreEqual(expected, kernels.Narrows, $"Latin-1 x {n}");
            Assert.AreEqual(expected, kernels.Widens, $"Latin-1 x {n}");
            // A char above U+00FF anywhere: the narrowing fails and the string is written as UTF-16.
            for (int at = 0; at < n; at += Math.Max(1, n / 5))
            {
                var chars = latin1.ToCharArray();
                chars[at] = (char)(0x100 + random.Next(0xD700));
                Assert.AreEqual(expected, Same(new string(chars), string.Equals).Narrows, $"UTF-16 x {n} at {at}");
            }
        }

        var text = Enumerable.Range(0, 40).Select(i => new string((char)('a' + i % 26), i * 3)).ToArray();
        Same(text, Equal);
    }

    [TestMethod]
    public void InvalidBoolsAreRejectedOnTheHookPath()
    {
        var values = Enumerable.Range(0, 40).Select(i => (i, i % 2 == 0)).ToArray();
        byte[] bytes = NinoSerializer.Serialize(values);
        // (int, bool) is 5 bytes a block: the bool of value 21.
        bytes[4 + 21 * 5 + 4] = 2;
        var kernels = new ScalarKernels();
        NinoKernels.Register(kernels);
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<(int, bool)[]>(bytes));
        Assert.AreEqual(0, kernels.Permutes);
        NinoKernels.Register(null);
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<(int, bool)[]>(bytes));
    }

    [TestMethod]
    public void ThresholdsComeFromTheKernels()
    {
        var kernels = new ScalarKernels();
        NinoKernels.Register(kernels);
        NinoSerializer.Serialize(Enumerable.Range(0, 15).Select(i => (i, (long)i)).ToArray());
        NinoSerializer.Serialize(new string('x', 31));
        Assert.AreEqual(0, kernels.Permutes + kernels.Narrows);
        NinoSerializer.Serialize(Enumerable.Range(0, 16).Select(i => (i, (long)i)).ToArray());
        NinoSerializer.Serialize(new string('x', 32));
        Assert.AreEqual(1, kernels.Permutes);
        Assert.AreEqual(1, kernels.Narrows);
        NinoKernels.Register(null);
        NinoSerializer.Serialize(Enumerable.Range(0, 100).Select(i => (i, (long)i)).ToArray());
        Assert.AreEqual(1, kernels.Permutes);
    }
}
#endif
