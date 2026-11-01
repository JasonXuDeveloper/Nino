#if NINO_CORE_NETSTANDARD
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// <summary>A <c>[NinoUtf8]</c> string, for the UTF-8 kernels.</summary>
[NinoType]
public partial class Utf8Text
{
    [NinoUtf8] public string Text;

    public static bool Equal(Utf8Text a, Utf8Text b) => a.Text == b.Text;
}

[TestClass]
[DoNotParallelize]
public unsafe class KernelHookTests
{
    /// <summary>
    /// The scalar references as native-callable functions, counting their calls: the kernels Nino calls through the
    /// hook's function pointers.
    /// </summary>
    private static class ScalarKernels
    {
        public static int Permutes, Narrows, Widens, NarrowAsciis, WidenAsciis, MinBlocks, MinChars;

        public static NinoKernels Create(int minBlocks = 16, int minChars = 32)
        {
            Permutes = Narrows = Widens = NarrowAsciis = WidenAsciis = 0;
            MinBlocks = minBlocks;
            MinChars = minChars;
            return new NinoKernels
            {
                MinBlocks = minBlocks,
                MinChars = minChars,
                Permute = &Permute,
                Narrow = &Narrow,
                Widen = &Widen,
                NarrowAscii = &NarrowAscii,
                WidenAscii = &WidenAscii,
            };
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Permute(byte* source, int sourceStride, byte* destination, int destinationStride,
            byte* table, int count)
        {
            Check(count >= MinBlocks && sourceStride is > 0 and <= 64 && destinationStride is > 0 and <= 64);
            Permutes++;
            NinoKernels.PermuteScalar(source, sourceStride, destination, destinationStride, table, count);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int Narrow(ushort* source, byte* destination, int length)
        {
            Check(length >= MinChars);
            Narrows++;
            return NinoKernels.NarrowScalar((char*)source, destination, length) ? 1 : 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Widen(byte* source, ushort* destination, int length)
        {
            Check(length >= MinChars);
            Widens++;
            NinoKernels.WidenScalar(source, (char*)destination, length);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int NarrowAscii(ushort* source, byte* destination, int length)
        {
            Check(length >= MinChars);
            NarrowAsciis++;
            int ascii = NinoKernels.NarrowAsciiScalar((char*)source, destination, length);
            // Garbage past the ASCII chars, which the contract allows: Nino must write over it.
            for (int i = ascii; i < length; i++)
                destination[i] = 0xEE;
            return ascii;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int WidenAscii(byte* source, ushort* destination, int length)
        {
            Check(length >= MinChars);
            WidenAsciis++;
            int ascii = NinoKernels.WidenAsciiScalar(source, (char*)destination, length);
            for (int i = ascii; i < length; i++)
                destination[i] = 0xEEEE;
            return ascii;
        }

        // An exception may not leave a native-callable function: a failed check is counted and asserted after.
        public static int Failures;

        private static void Check(bool condition)
        {
            if (!condition)
                Failures++;
        }
    }

    private static readonly int[] Lengths = { 0, 1, 2, 15, 16, 17, 31, 32, 33, 64, 100, 1000 };

    [TestCleanup]
    public void Cleanup()
    {
        NinoKernels.Register(null);
        Assert.AreEqual(0, ScalarKernels.Failures, "a kernel was called below its threshold or out of its range");
    }

    /// <summary>The bytes with the hook and without, and the values read back each way.</summary>
    private static void Same<T>(T value, Func<T, T, bool> equal)
    {
        NinoKernels.Register(null);
        Assert.IsNull(NinoKernels.Current);
        byte[] scalar = NinoSerializer.Serialize(value);
        var kernels = ScalarKernels.Create();
        NinoKernels.Register(kernels);
        Assert.AreSame(kernels, NinoKernels.Current);
        byte[] hooked = NinoSerializer.Serialize(value);
        CollectionAssert.AreEqual(scalar, hooked, typeof(T).Name);
        Assert.IsTrue(equal(value, NinoDeserializer.Deserialize<T>(hooked)));
        NinoKernels.Register(null);
        Assert.IsTrue(equal(value, NinoDeserializer.Deserialize<T>(hooked)));
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
            Same(a, Equal);
            Assert.AreEqual(2 * expected, ScalarKernels.Permutes, $"(int, long) x {n}");
            Same(b, Equal);
            Assert.AreEqual(2 * expected, ScalarKernels.Permutes, $"(byte, int) x {n}");
            Same(c, Equal);
            Assert.AreEqual(2 * expected, ScalarKernels.Permutes, $"(short, byte, long, bool) x {n}");
            Same(d, Equal);
            Assert.AreEqual(2 * expected, ScalarKernels.Permutes, $"5-tuple x {n}");
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

            Same(values, Equal);
            // Written by the hook (the masked copy); read back as one copy.
            Assert.AreEqual(n >= 16 ? 1 : 0, ScalarKernels.Permutes, $"PaddedPlain x {n}");
            // The padding cleared: the bytes of the same values built clean.
            var clean = values.Select(v => new PaddedPlain { A = v.A, B = v.B }).ToArray();
            NinoKernels.Register(ScalarKernels.Create());
            byte[] hooked = NinoSerializer.Serialize(values);
            NinoKernels.Register(null);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean), hooked);
        }
    }

    [TestMethod]
    public void PaddedCopiesTakeTheKernelsFromSixteenValuesWhateverMinBlocks()
    {
        // Blocks of tuples follow MinBlocks; the masked copy of raw structs with padding starts at 16 (its own path
        // copies whole words, which the kernels' call only beat from there on Unity's players).
        foreach (int n in Lengths)
        {
            var tuples = Enumerable.Range(0, n).Select(i => (i, (long)i << 17)).ToArray();
            var padded = Enumerable.Range(0, n).Select(i => new PaddedPlain { A = (short)i, B = i * 7L }).ToArray();
            NinoKernels.Register(ScalarKernels.Create(minBlocks: 2));
            byte[] bytes = NinoSerializer.Serialize(tuples);
            Assert.AreEqual(n >= 2 ? 1 : 0, ScalarKernels.Permutes, $"(int, long) x {n}");
            ScalarKernels.Permutes = 0;
            NinoSerializer.Serialize(padded);
            Assert.AreEqual(n >= 16 ? 1 : 0, ScalarKernels.Permutes, $"PaddedPlain x {n}");
            NinoKernels.Register(null);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(tuples), bytes);
        }
    }

    [TestMethod]
    public void StringsWriteAndReadTheSameBytesThroughTheHook()
    {
        var random = new Random(5);
        foreach (int n in Lengths.Concat(new[] { 48, 63, 255, 256, 257 }))
        {
            string latin1 = new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(1, 256)).ToArray());
            Same(latin1, string.Equals);
            int expected = n >= 32 ? 1 : 0;
            Assert.AreEqual(expected, ScalarKernels.Narrows, $"Latin-1 x {n}");
            Assert.AreEqual(expected, ScalarKernels.Widens, $"Latin-1 x {n}");
            // A char above U+00FF anywhere: the narrowing fails and the string is written as UTF-16.
            for (int at = 0; at < n; at += Math.Max(1, n / 5))
            {
                var chars = latin1.ToCharArray();
                chars[at] = (char)(0x100 + random.Next(0xD700));
                Same(new string(chars), string.Equals);
                Assert.AreEqual(expected, ScalarKernels.Narrows, $"UTF-16 x {n} at {at}");
            }
        }

        var text = Enumerable.Range(0, 40).Select(i => new string((char)('a' + i % 26), i * 3)).ToArray();
        Same(text, Equal);
    }

    [TestMethod]
    public void Utf8StringsWriteAndReadTheSameBytesThroughTheHook()
    {
        var random = new Random(7);
        // Chars of every UTF-8 length, a surrogate pair and unpaired surrogates (written as U+FFFD either way).
        string[] others = { "\u00E9", "\u0080", "\u07FF", "\u4E2D", "\uFFFF", "\U0001F600", "\uD800", "\uDC00", "\uDBFFx" };
        foreach (int n in Lengths.Concat(new[] { 48, 63, 127, 128, 255, 256, 1000 }))
        {
            string ascii = new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(0, 128)).ToArray());
            var value = new Utf8Text { Text = ascii };
            Same(value, Utf8Text.Equal);
            int expected = n >= 32 ? 1 : 0;
            Assert.AreEqual(expected, ScalarKernels.NarrowAsciis, $"ASCII x {n}");
            Assert.AreEqual(expected, ScalarKernels.WidenAsciis, $"ASCII x {n}");
            foreach (string other in others)
            for (int at = 0; at <= n; at += Math.Max(1, n / 4))
            {
                // A string the encoder replaces in part is read back with U+FFFD: the bytes must match, and read back
                // the same through the hook and without.
                var mixed = new Utf8Text { Text = ascii.Insert(at, other) };
                NinoKernels.Register(null);
                byte[] scalar = NinoSerializer.Serialize(mixed);
                string back = NinoDeserializer.Deserialize<Utf8Text>(scalar).Text;
                NinoKernels.Register(ScalarKernels.Create(minChars: 16));
                CollectionAssert.AreEqual(scalar, NinoSerializer.Serialize(mixed), $"{n} at {at}");
                Assert.AreEqual(back, NinoDeserializer.Deserialize<Utf8Text>(scalar).Text, $"{n} at {at}");
                // A string of whole chars reads back as itself.
                if (other != "\uD800" && other != "\uDC00" && other != "\uDBFFx")
                    Assert.AreEqual(mixed.Text, back);
            }
        }
    }

    [TestMethod]
    public void InvalidUtf8IsRejectedTheSameWayThroughTheHook()
    {
        const int n = 40;
        var value = new Utf8Text { Text = new string('a', n) };
        byte[] valid = NinoSerializer.Serialize(value);
        // The char count (n + 1) and byte count (n), one byte each, then the bytes.
        int at = valid.Length - n - 2;
        Assert.AreEqual(n + 1, valid[at]);
        Assert.AreEqual(n, valid[at + 1]);
        var cases = new List<(string, Action<byte[]>)>
        {
            ("one char fewer than the bytes", b => b[at] = n),
            ("one char more than the bytes", b => b[at] = n + 2),
        };
        foreach (int k in new[] { 0, 1, 15, 16, 17, 31, 32, 38, 39 })
        {
            cases.Add(($"continuation byte at {k}", b => b[at + 2 + k] = 0x80));
            cases.Add(($"overlong lead at {k}", b => b[at + 2 + k] = 0xC0));
            cases.Add(($"invalid byte at {k}", b => b[at + 2 + k] = 0xFF));
            cases.Add(($"truncated three-byte char at {k}", b => b[at + 2 + k] = 0xE4));
            if (k < n - 2)
                cases.Add(($"encoded surrogate at {k}", b =>
                {
                    b[at + 2 + k] = 0xED;
                    b[at + 2 + k + 1] = 0xA0;
                    b[at + 2 + k + 2] = 0x80;
                }));
        }

        foreach (var (what, corrupt) in cases)
        {
            var bytes = (byte[])valid.Clone();
            corrupt(bytes);
            NinoKernels.Register(null);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Utf8Text>(bytes), what);
            NinoKernels.Register(ScalarKernels.Create(minChars: 16));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Utf8Text>(bytes), what);
        }
    }

    [TestMethod]
    public void InvalidBoolsAreRejectedOnTheHookPath()
    {
        var values = Enumerable.Range(0, 40).Select(i => (i, i % 2 == 0)).ToArray();
        byte[] bytes = NinoSerializer.Serialize(values);
        // (int, bool) is 5 bytes a block: the bool of value 21.
        bytes[4 + 21 * 5 + 4] = 2;
        NinoKernels.Register(ScalarKernels.Create());
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<(int, bool)[]>(bytes));
        Assert.AreEqual(0, ScalarKernels.Permutes);
        NinoKernels.Register(null);
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<(int, bool)[]>(bytes));
    }

    [TestMethod]
    public void ThresholdsComeFromTheKernels()
    {
        NinoKernels.Register(ScalarKernels.Create());
        NinoSerializer.Serialize(Enumerable.Range(0, 15).Select(i => (i, (long)i)).ToArray());
        NinoSerializer.Serialize(new string('x', 31));
        NinoSerializer.Serialize(new Utf8Text { Text = new string('x', 31) });
        Assert.AreEqual(0, ScalarKernels.Permutes + ScalarKernels.Narrows + ScalarKernels.NarrowAsciis);
        NinoSerializer.Serialize(Enumerable.Range(0, 16).Select(i => (i, (long)i)).ToArray());
        NinoSerializer.Serialize(new string('x', 32));
        NinoSerializer.Serialize(new Utf8Text { Text = new string('x', 32) });
        Assert.AreEqual(1, ScalarKernels.Permutes);
        Assert.AreEqual(1, ScalarKernels.Narrows);
        Assert.AreEqual(1, ScalarKernels.NarrowAsciis);
        NinoKernels.Register(null);
        NinoSerializer.Serialize(Enumerable.Range(0, 100).Select(i => (i, (long)i)).ToArray());
        NinoSerializer.Serialize(new Utf8Text { Text = new string('x', 100) });
        Assert.AreEqual(1, ScalarKernels.Permutes);
        Assert.AreEqual(1, ScalarKernels.NarrowAsciis);
    }

    [TestMethod]
    public void AbsentKernelsAreNotCalled()
    {
        // Only Narrow: every other path keeps Nino's own code, whatever the thresholds.
        var kernels = ScalarKernels.Create(minBlocks: 1, minChars: 16);
        kernels.Permute = null;
        kernels.Widen = null;
        kernels.NarrowAscii = null;
        kernels.WidenAscii = null;
        NinoKernels.Register(kernels);
        var text = new string('x', 100);
        var bytes = NinoSerializer.Serialize(text);
        Assert.AreEqual(text, NinoDeserializer.Deserialize<string>(bytes));
        var utf8 = NinoSerializer.Serialize(new Utf8Text { Text = text });
        Assert.AreEqual(text, NinoDeserializer.Deserialize<Utf8Text>(utf8).Text);
        var values = Enumerable.Range(0, 100).Select(i => (i, (long)i)).ToArray();
        CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<(int, long)[]>(NinoSerializer.Serialize(values)));
        Assert.AreEqual(1, ScalarKernels.Narrows);
        Assert.AreEqual(0, ScalarKernels.Permutes + ScalarKernels.Widens + ScalarKernels.NarrowAsciis + ScalarKernels.WidenAsciis);
    }
}
#endif
