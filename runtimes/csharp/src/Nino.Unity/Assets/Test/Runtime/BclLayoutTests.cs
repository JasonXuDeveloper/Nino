using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nino.Core;
using NUnit.Framework;
using UnityEngine;

// A formatter of the user's whose parameter is [NinoRaw]: the generated registry registers that mark, which IL2CPP,
// keeping no attributes on generic parameters, binds it by.
[assembly: NinoFormatter(typeof(Test.Runtime.CellFormatter<>))]

namespace Test.Runtime
{
    /// <summary>One value, or null: a type Nino has no formatter for but <see cref="CellFormatter{T}"/>.</summary>
    public sealed class Cell<T>
    {
        public T Value;
    }

    /// <summary>A presence byte, then the value as raw bytes: only for types written raw ([NinoRaw]).</summary>
    public struct CellFormatter<[NinoRaw] T> : INinoFormatter<Cell<T>> where T : unmanaged
    {
        public void Serialize(Cell<T> value, ref Writer writer)
        {
            writer.Write(value != null);
            if (value != null)
                writer.WriteValue(value.Value);
        }

        public void Deserialize(ref Reader reader, ref Cell<T> value)
        {
            reader.Read(out bool present);
            value = present ? new Cell<T> { Value = reader.ReadValue<T>() } : null;
        }
    }

    /// <summary>
    /// BCL structs inside plain structs (value tuples, pairs, DateTimeOffset, nint, Nullable) and tuples and pairs on
    /// their own: this runtime (Mono in the editor, IL2CPP in a player) must write the bytes 64-bit CoreCLR writes (the
    /// canonical layout: <see cref="BclFixtures"/>, which runtimes/csharp/tests/Nino.RuntimeTests writes on CoreCLR x64 and checks on
    /// every other runtime) and read them back. It also logs how this runtime lays the tuples out (docs/layouts.md).
    /// </summary>
    public class BclLayoutTests
    {
        private static readonly DateTime Dt1 = new DateTime(0x0123456789ABCDEF & 0x0FFFFFFFFFFFFFFF, DateTimeKind.Utc);
        private static readonly DateTimeOffset Dto1 = new DateTimeOffset(637_000_000_000_000_000, TimeSpan.FromMinutes(-150));
        private static readonly Guid G1 = new Guid(0x01020304, 0x0506, 0x0708, 9, 10, 11, 12, 13, 14, 15, 16);

        [Test]
        public void CoreClrBytesBothWays()
        {
            Debug.Log($"Nino layouts ({Application.platform}, {RuntimeName()}): " + Layouts());
            var failed = new List<string>();
            void Check<T>(string name, T value)
            {
                try
                {
                    var expected = BclFixtures.FromHex(BclFixtures.Bytes[name]);
                    byte[] bytes = NinoSerializer.Serialize(value);
                    if (!bytes.SequenceEqual(expected))
                        failed.Add($"{name} writes {BitConverter.ToString(bytes)}");
                    byte[] again = NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(expected));
                    if (!again.SequenceEqual(expected))
                        failed.Add($"{name} reads back as {BitConverter.ToString(again)}");
                }
                catch (Exception e)
                {
                    failed.Add($"{name}: {e.GetType().Name}: {e.Message}");
                }
            }

            Check("Around<(int,long)>[]", new[] { new Around<(int, long)> { A = 1, B = (-1, long.MinValue), C = -1 }, new Around<(int, long)> { A = 0xFE, B = (2, 0x0102030405060708), C = int.MaxValue } });
            Check("Around<(byte,short)>", new Around<(byte, short)> { A = 1, B = (1, -2), C = -1 });
            Check("Around<(byte,long,int)>", new Around<(byte, long, int)> { A = 1, B = (7, -8, 9), C = -1 });
            Check("Around<(int,int,int)>", new Around<(int, int, int)> { A = 1, B = (1, 2, 3), C = -1 });
            Check("Around<(bool,char,byte)>", new Around<(bool, char, byte)> { A = 1, B = (true, 'x', 3), C = -1 });
            Check("Around<(int,DateTime)>", new Around<(int, DateTime)> { A = 1, B = (1, Dt1), C = -1 });
            Check("Around<(byte,decimal)>", new Around<(byte, decimal)> { A = 1, B = (1, 1.5m), C = -1 });
            Check("Around<(byte,Guid)>", new Around<(byte, Guid)> { A = 1, B = (1, G1), C = -1 });
            Check("Around<(byte,int?)>", new Around<(byte, int?)> { A = 1, B = (1, null), C = -1 });
            Check("Around<(byte,(int,long))>", new Around<(byte, (int, long))> { A = 1, B = (1, (2, 3L)), C = -1 });
            Check("Around<(int,(byte,short),long)>", new Around<(int, (byte, short), long)> { A = 1, B = (1, (2, 3), 4L), C = -1 });
            Check("Around<(int,IntPtr)>", new Around<(int, IntPtr)> { A = 1, B = (1, new IntPtr(-2)), C = -1 });
            Check("Around<(byte,DateTimeOffset)>", new Around<(byte, DateTimeOffset)> { A = 1, B = (1, Dto1), C = -1 });
            Check("Around<(byte,long,byte,long,byte,long,byte,int)>", new Around<(byte, long, byte, long, byte, long, byte, int)> { A = 1, B = (1, 2L, 3, 4L, 5, 6L, 7, 8), C = -1 });
            Check("Around<KVP<int,long>>", new Around<KeyValuePair<int, long>> { A = 1, B = new KeyValuePair<int, long>(1, -2), C = -1 });
            Check("Around<KVP<long,byte>>", new Around<KeyValuePair<long, byte>> { A = 1, B = new KeyValuePair<long, byte>(-1, 2), C = -1 });
            Check("Around<DateTimeOffset>", new Around<DateTimeOffset> { A = 1, B = Dto1, C = -1 });
            Check("Around<decimal>", new Around<decimal> { A = 1, B = 1.5m, C = -1 });
            Check("Around<IntPtr>", new Around<IntPtr> { A = 1, B = new IntPtr(-1), C = -1 });
            Check("Around<(int,long)?>", new Around<(int, long)?> { A = 1, B = (1, 2L), C = -1 });
            Check("(int,long)[]", new[] { (1, 2L), (-3, long.MinValue) });
            Check("List<(byte,long,int)>", new List<(byte, long, int)> { (1, 2, 3), (255, -1, int.MaxValue) });
            Check("KVP<int,long>[]", new[] { new KeyValuePair<int, long>(1, 2), new KeyValuePair<int, long>(-3, -4) });
            Check("Dictionary<int,(byte,long)>", new Dictionary<int, (byte, long)> { [1] = (2, 3), [-4] = (5, -6) });
            CollectionAssert.IsEmpty(failed);
        }

        /// <summary>
        /// Structs under every StructLayout form (Pack, Size, Explicit, Auto; BclFixtureTypes.cs, LayoutShapes) against
        /// CoreCLR's bytes both ways: IL2CPP's Pack never moves a BCL struct field (DateTime, decimal, Guid), so such a
        /// struct is laid out apart there (#378). Logs this runtime's sizes, and each failure as it is found.
        /// </summary>
        [Test]
        public void StructLayoutsWriteCoreClrBytes()
        {
            var cases = new LayoutCases();
            LayoutShapes.Visit(cases);
            Debug.Log($"Nino StructLayout sizes ({RuntimeName()}): " + string.Join(", ", cases.SizeLines));
#if ENABLE_IL2CPP
            // Generic structs IL2CPP lays out in two ways (#378). Those code here names (SplitHolder's members, the
            // closed calls of SplitStructsNamedInCodeWriteCoreClrBytes) have registered formatters and offsets, so
            // generic code writes them right too (run 44140455419); those reached only through generic code are refused.
            foreach (string named in new[]
                     {
                         "Around<PackedIntDateTime4>", "Around<HoldsPacked>", "Around<PackedLeaves1>",
                         "Around<PackedMix4>", "Around<ExplicitHoldsPacked>", "HoldsPacked?", "PackedLeaves1?",
                         "PackedMix4?", "ExplicitHoldsPacked?", "PackedPair1<long>?", "PackedPair1<long>[]",
                         "List<PackedPair1<long>>",
                     })
            {
                if (cases.Refused.Contains("layout " + named))
                    cases.Failed.Add($"layout {named} was refused on IL2CPP, though code names it");
            }

            foreach (string generic in new[] { "PackedPair1<DateTime>", "PackedPair1<DateTime>?", "PacksNatural?" })
            {
                if (!cases.Refused.Contains("layout " + generic))
                    cases.Failed.Add($"layout {generic}, reached only through generic code, was not refused on IL2CPP");
            }
#else
            foreach (string refused in cases.Refused)
                cases.Failed.Add($"{refused} was refused outside IL2CPP");
#endif
            // One string, never NUnit's formatting of a list: the IL2CPP player crashed formatting one (SIGSEGV in
            // MsgUtils.FormatCollection), and lost the failures, which are also logged one per line as they happen.
            if (cases.Failed.Count != 0)
                Assert.Fail($"{cases.Failed.Count} StructLayout cases differ from CoreCLR:\n" + string.Join("\n", cases.Failed));
        }

        /// <summary>
        /// LayoutShapes' cases that IL2CPP refuses through generic code (StructLayoutsWriteCoreClrBytes), each named in a
        /// top-level call here: the generator registers their formatters and the compiled offsets of their generic
        /// structs, so every runtime, IL2CPP included, writes and reads CoreCLR's bytes. SplitHolder, a case of
        /// LayoutShapes, holds them as members.
        /// </summary>
        [Test]
        public void SplitStructsNamedInCodeWriteCoreClrBytes()
        {
            // How this runtime lays each type out in compiled code and in its arrays (the runtime's element size, which
            // IL2CPP computes itself for a generic instance): logged before any case runs.
            Debug.Log("NINO_SPLIT_SIZES|" + string.Join(", ", new[]
            {
                Sizes<Around<PackedIntDateTime4>>("Around<PackedIntDateTime4>"), Sizes<HoldsPacked?>("HoldsPacked?"),
                Sizes<PackedLeaves1?>("PackedLeaves1?"), Sizes<PackedMix4?>("PackedMix4?"),
                Sizes<ExplicitHoldsPacked?>("ExplicitHoldsPacked?"), Sizes<PackedPair1<long>>("PackedPair1<long>"),
                Sizes<PackedPair1<long>?>("PackedPair1<long>?"), Sizes<Around<PackedPair1<long>>>("Around<PackedPair1<long>>"),
            }));
            var failed = new List<string>();
            // write is null where the value cannot be built here: IL2CPP allocates some arrays with smaller elements
            // than its compiled code writes (#378). Nino's own arrays have room (NinoElementLayouts), so it reads them.
            void Check(string name, Func<byte[]> write, Func<byte[], byte[]> readBack)
            {
                name = "layout " + name;
                // Logged first, and the heap walked after: a case that writes past a value crashes at once, here.
                Debug.Log("NINO_SPLIT|" + name);
                try
                {
                    var expected = BclFixtures.FromHex(BclFixtures.Bytes[name]);
                    if (write != null)
                    {
                        byte[] bytes = write();
                        if (!bytes.SequenceEqual(expected))
                            failed.Add($"{name} writes {BitConverter.ToString(bytes)}, CoreCLR {BitConverter.ToString(expected)}");
                    }

                    byte[] again = readBack(expected);
                    if (!again.SequenceEqual(expected))
                        failed.Add($"{name} reads back as {BitConverter.ToString(again)}, CoreCLR {BitConverter.ToString(expected)}");
                }
                catch (Exception e)
                {
                    failed.Add($"{name}: {e.GetType().Name}: {e.Message}");
                }

                GC.Collect();
            }

            Check("Around<PackedIntDateTime4>", () => NinoSerializer.Serialize(new Around<PackedIntDateTime4> { A = 1, B = new PackedIntDateTime4 { Id = 0x11223344, At = LayoutShapes.At1 }, C = -1 }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<Around<PackedIntDateTime4>>(b)));
            Check("Around<HoldsPacked>", () => NinoSerializer.Serialize(new Around<HoldsPacked> { A = 1, B = new HoldsPacked { A = 1, P = new PackedIntDateTime4 { Id = 2, At = LayoutShapes.At1 }, C = 3 }, C = -1 }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<Around<HoldsPacked>>(b)));
            Check("Around<PackedLeaves1>", () => NinoSerializer.Serialize(new Around<PackedLeaves1> { A = 1, B = LayoutShapes.Leaves(1), C = -1 }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<Around<PackedLeaves1>>(b)));
            Check("Around<PackedMix4>", () => NinoSerializer.Serialize(new Around<PackedMix4> { A = 1, B = LayoutShapes.Mix(1), C = -1 }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<Around<PackedMix4>>(b)));
            Check("Around<ExplicitHoldsPacked>", () => NinoSerializer.Serialize(new Around<ExplicitHoldsPacked> { A = 1, B = new ExplicitHoldsPacked { A = 1, P = new PackedIntDateTime4 { Id = 2, At = LayoutShapes.At1 } }, C = -1 }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<Around<ExplicitHoldsPacked>>(b)));
            Check("HoldsPacked?", () => NinoSerializer.Serialize((HoldsPacked?)new HoldsPacked { A = 0xFE, P = new PackedIntDateTime4 { Id = -4, At = LayoutShapes.At2 }, C = int.MaxValue }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<HoldsPacked?>(b)));
            Check("PackedLeaves1?", () => NinoSerializer.Serialize((PackedLeaves1?)LayoutShapes.Leaves(-2)),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<PackedLeaves1?>(b)));
            Check("PackedMix4?", () => NinoSerializer.Serialize((PackedMix4?)LayoutShapes.Mix(-2)),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<PackedMix4?>(b)));
            Check("ExplicitHoldsPacked?", () => NinoSerializer.Serialize((ExplicitHoldsPacked?)new ExplicitHoldsPacked { A = 0xFE, P = new PackedIntDateTime4 { Id = -3, At = LayoutShapes.At2 } }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<ExplicitHoldsPacked?>(b)));
            Check("PackedPair1<long>?", () => NinoSerializer.Serialize((PackedPair1<long>?)new PackedPair1<long> { A = 2, B = -3 }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<PackedPair1<long>?>(b)));
            Check("HoldsPacked?[]", !LayoutShapes.ElementsFit<HoldsPacked?>() ? null : () => NinoSerializer.Serialize(new HoldsPacked?[] { new HoldsPacked { A = 1, P = new PackedIntDateTime4 { Id = 2, At = LayoutShapes.At1 }, C = 3 }, null, new HoldsPacked { A = 0xFE, P = new PackedIntDateTime4 { Id = -4, At = LayoutShapes.At2 }, C = int.MaxValue } }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<HoldsPacked?[]>(b)));
            Check("PackedLeaves1?[]", !LayoutShapes.ElementsFit<PackedLeaves1?>() ? null : () => NinoSerializer.Serialize(new PackedLeaves1?[] { LayoutShapes.Leaves(1), null, LayoutShapes.Leaves(-2) }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<PackedLeaves1?[]>(b)));
            Check("PackedMix4?[]", !LayoutShapes.ElementsFit<PackedMix4?>() ? null : () => NinoSerializer.Serialize(new PackedMix4?[] { LayoutShapes.Mix(1), null, LayoutShapes.Mix(-2) }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<PackedMix4?[]>(b)));
            Check("ExplicitHoldsPacked?[]", !LayoutShapes.ElementsFit<ExplicitHoldsPacked?>() ? null : () => NinoSerializer.Serialize(new ExplicitHoldsPacked?[] { new ExplicitHoldsPacked { A = 1, P = new PackedIntDateTime4 { Id = 2, At = LayoutShapes.At1 } }, null, new ExplicitHoldsPacked { A = 0xFE, P = new PackedIntDateTime4 { Id = -3, At = LayoutShapes.At2 } } }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<ExplicitHoldsPacked?[]>(b)));
            Check("PackedPair1<long>[]", !LayoutShapes.ElementsFit<PackedPair1<long>>() ? null : () => NinoSerializer.Serialize(new[] { new PackedPair1<long> { A = 1, B = 0x0102030405060708 }, new PackedPair1<long> { A = 2, B = -3 } }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<PackedPair1<long>[]>(b)));
            Check("List<PackedPair1<long>>", !LayoutShapes.ElementsFit<PackedPair1<long>>() ? null : () => NinoSerializer.Serialize(new List<PackedPair1<long>> { new PackedPair1<long> { A = 2, B = -3 }, new PackedPair1<long> { A = 1, B = 0x0102030405060708 } }),
                b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<List<PackedPair1<long>>>(b)));
            // SplitHolder read first: LayoutShapes builds it only where its arrays' elements fit (not on IL2CPP).
            Check("SplitHolder", null, b => NinoSerializer.Serialize(NinoDeserializer.Deserialize<SplitHolder>(b)));
            if (failed.Count != 0)
                Assert.Fail($"{failed.Count} split structs differ from CoreCLR:\n" + string.Join("\n", failed));
        }

        /// <summary>
        /// <typeparamref name="T"/>'s size in compiled code and the distance between elements of a <c>T[]</c> as the
        /// runtime lays the array out (UnsafeAddrOfPinnedArrayElement uses the runtime's element size).
        /// </summary>
        private static string Sizes<T>(string name)
        {
            var array = new T[2];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(array, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                long stride = (long)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(array, 1) -
                              (long)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(array, 0);
                return $"{name} {Unsafe.SizeOf<T>()} (array {stride})";
            }
            finally
            {
                handle.Free();
            }
        }


        /// <summary>
        /// Without Nino: whether the runtime copies an array of each struct in the elements compiled code indexes.
        /// Compiled code writes a distinct value into each element, Array.Copy (the runtime's element size) shifts the
        /// array by one, and compiled code compares each copied element with the one it came from, byte for byte. Logs
        /// the compiled size, the compiled distance between elements and the runtime's (Marshal) for each type. Mono
        /// must copy every type right; on IL2CPP the result is logged for #378.
        /// </summary>
        [Test]
        public void ArraysOfSplitStructsCopyAsCompiled()
        {
            var lines = new List<string>();
            var wrong = new List<string>();
            void Probe<T>(string name, Func<int, T> make)
            {
                const int n = 8;
                var a = new T[n];
                long compiled = (long)Unsafe.ByteOffset(ref a[0], ref a[1]);
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(a, System.Runtime.InteropServices.GCHandleType.Pinned);
                long runtime;
                try
                {
                    runtime = (long)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(a, 1) -
                              (long)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(a, 0);
                }
                finally
                {
                    handle.Free();
                }

                // Where compiled elements are larger than the runtime's, filling the array would write past it.
                string copy = "not tried (compiled elements larger)";
                if (compiled <= runtime)
                {
                    for (int i = 0; i < n; i++)
                        a[i] = make(i + 1);
                    var b = new T[n];
                    Array.Copy(a, 1, b, 0, n - 1);
                    bool same = true;
                    for (int i = 0; i < n - 1 && same; i++)
                    {
                        ref byte x = ref Unsafe.As<T, byte>(ref b[i]);
                        ref byte y = ref Unsafe.As<T, byte>(ref a[i + 1]);
                        for (int k = 0; k < Unsafe.SizeOf<T>(); k++)
                            same &= Unsafe.Add(ref x, k) == Unsafe.Add(ref y, k);
                    }

                    copy = same ? "same" : "DIFFERENT";
                }

                lines.Add($"{name}: size {Unsafe.SizeOf<T>()}, compiled stride {compiled}, runtime stride {runtime}, Array.Copy {copy}");
                if (compiled != runtime || copy == "DIFFERENT")
                    wrong.Add(name);
            }

            PackedIntDateTime4 P(int i) => new PackedIntDateTime4 { Id = i * 7, At = new DateTime(i * 1000L) };
            HoldsPacked H(int i) => new HoldsPacked { A = (byte)i, P = P(i), C = -i };
            Probe("PackedIntDateTime4", P);
            Probe("HoldsPacked", H);
            Probe("PackedIntDateTime4?", i => (PackedIntDateTime4?)P(i));
            Probe("HoldsPacked?", i => (HoldsPacked?)H(i));
            Probe("Around<PackedIntDateTime4>", i => new Around<PackedIntDateTime4> { A = (byte)i, B = P(i), C = -i });
            Probe("PackedPair1<long>", i => new PackedPair1<long> { A = (byte)i, B = -i * 0x0102030405L });
            Probe("PackedPair1<long>?", i => (PackedPair1<long>?)new PackedPair1<long> { A = (byte)i, B = -i * 0x0102030405L });
            Probe("Around<PackedPair1<long>>", i => new Around<PackedPair1<long>> { A = (byte)i, B = new PackedPair1<long> { A = (byte)(i + 1), B = i }, C = -i });
            Probe("PackedMix4?", i => (PackedMix4?)LayoutShapes.Mix(i));
            // NinoNullable<T> holds two T: laid out alike by both where T is not generic (the workaround for T?).
            Probe("NinoNullable<HoldsPacked>", i => new NinoNullable<HoldsPacked>(H(i)));
            Probe("NinoNullable<PackedIntDateTime4>", i => new NinoNullable<PackedIntDateTime4>(P(i)));
            Debug.Log("NINO_ARRAYS|" + string.Join(" | ", lines));
            // Non-generic structs take their layout from il2cpp.exe on both sides, and NinoNullable<T> of one holds two
            // of them: laid out alike everywhere, IL2CPP included (the workarounds docs/pitfalls.md gives).
            var alike = new[] { "PackedIntDateTime4", "HoldsPacked", "NinoNullable<HoldsPacked>", "NinoNullable<PackedIntDateTime4>" };
#if ENABLE_IL2CPP
            var unexpected = wrong.Where(alike.Contains).ToList();
#else
            var unexpected = wrong;
#endif
            if (unexpected.Count != 0)
                Assert.Fail("Arrays laid out apart from compiled code: " + string.Join(", ", unexpected));
        }

        /// <summary>Writes each case, reads CoreCLR's bytes and writes them again: both CoreCLR's bytes.</summary>
        private sealed class LayoutCases : ILayoutCaseVisitor
        {
            public readonly List<string> Failed = new List<string>();
            public readonly List<string> SizeLines = new List<string>();

            /// <summary>The cases Nino refused (NotSupportedException): IL2CPP's generic structs it cannot lay out.</summary>
            public readonly List<string> Refused = new List<string>();

            public void Sizes<T>(string name) where T : struct =>
                SizeLines.Add($"{name} {Unsafe.SizeOf<T>()} (T? {Unsafe.SizeOf<T?>()})");

            public void Case<T>(string name, T value)
            {
                name = "layout " + name;
                try
                {
                    var expected = BclFixtures.FromHex(BclFixtures.Bytes[name]);
                    byte[] bytes = NinoSerializer.Serialize(value);
                    if (!bytes.SequenceEqual(expected))
                        Fail($"{name} writes {BitConverter.ToString(bytes)}, CoreCLR {BitConverter.ToString(expected)}");
                    byte[] again = NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(expected));
                    if (!again.SequenceEqual(expected))
                        Fail($"{name} reads back as {BitConverter.ToString(again)}, CoreCLR {BitConverter.ToString(expected)}");
                }
                catch (Exception e) when ((e.InnerException ?? e) is NotSupportedException n && n.Message.Contains("IL2CPP"))
                {
                    Refused.Add(name);
                    Debug.Log("NINO_LAYOUT_REFUSED|" + name);
                }
                catch (Exception e)
                {
                    Fail($"{name}: {e.GetType().Name}: {e.Message}");
                }
            }

            /// <summary>Logged at once, in its own line: a write past a value can crash the player before the end.</summary>
            private void Fail(string line)
            {
                Failed.Add(line);
                Debug.Log("NINO_LAYOUT_FAIL|" + line);
            }
        }

        [Test]
        public void UserRawFormatterIsBoundByItsMarks()
        {
            // Resolved at run time (Unity's compiler has no interceptors): bound to a raw long...
            var bytes = NinoSerializer.Serialize(new Cell<long> { Value = -7 });
            Assert.AreEqual("01-F9-FF-FF-FF-FF-FF-FF-FF", BitConverter.ToString(bytes));
            Assert.AreEqual(-7L, NinoDeserializer.Deserialize<Cell<long>>(bytes).Value);
            // ...and never to a value tuple, which is not raw: IL2CPP bound it before the generated registry
            // registered the [NinoRaw] mark, and wrote the tuple's memory in its own layout.
            Assert.Throws<InvalidOperationException>(() =>
                NinoSerializer.Serialize(new Cell<(byte, long, int)> { Value = (1, 2, 3) }));
        }

        [Test]
        public void VectorTIsRefusedAndFixedLanesReplaceIt()
        {
            // Vector<T> is 16 bytes here and 32 on x64 with AVX2: Nino refuses it, alone or inside (NINO029, suppressed
            // here to reach the run-time check).
#pragma warning disable NINO029
            Assert.Throws<NotSupportedException>(() => NinoSerializer.Serialize(new System.Numerics.Vector<int>(7)));
            Assert.Throws<NotSupportedException>(() =>
                NinoDeserializer.Deserialize<Around<System.Numerics.Vector<int>>[]>(new byte[64]));
#pragma warning restore NINO029
            // Its replacement writes CoreCLR x64's bytes (BclFixtures) whatever this CPU's Vector<float>.Count.
            Debug.Log($"Vector<float>.Count {System.Numerics.Vector<float>.Count}");
            var bytes = NinoSerializer.Serialize(LaneParticle.Doubled(3));
            Assert.AreEqual(BclFixtures.Bytes["LaneParticle"], BitConverter.ToString(bytes).Replace("-", ""));
            Assert.AreEqual(30f, NinoDeserializer.Deserialize<LaneParticle>(bytes).Lanes[15]);
        }

        /// <summary>Offsets of some tuples' items and of DateTimeOffset's fields on this runtime.</summary>
        private static string Layouts()
        {
            var a = default((int, long));
            var b = default((byte, short));
            var c = default((byte, long, int));
            var views = typeof(NinoSerializer).Assembly.GetType("Nino.Core.Internal.ValueViews");
            string Field(string name) => views?.GetField(name, BindingFlags.Static | BindingFlags.Public)?.GetValue(null)?.ToString();
            var marks = typeof(NinoSerializer).Assembly.GetType("Nino.Core.CompilerServices.NinoParameterMarks");
            return $"attributes of generic parameters visible: {marks?.GetField("Visible", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)}; " +
                   $"(int,long) {Unsafe.SizeOf<(int, long)>()}: {Offset(ref a, ref a.Item1)},{Offset(ref a, ref a.Item2)}; " +
                   $"(byte,short) {Unsafe.SizeOf<(byte, short)>()}: {Offset(ref b, ref b.Item1)},{Offset(ref b, ref b.Item2)}; " +
                   $"(byte,long,int) {Unsafe.SizeOf<(byte, long, int)>()}: {Offset(ref c, ref c.Item1)},{Offset(ref c, ref c.Item2)},{Offset(ref c, ref c.Item3)}; " +
                   $"KVP<int,long> {Unsafe.SizeOf<KeyValuePair<int, long>>()}; " +
                   $"DateTimeOffset {Unsafe.SizeOf<DateTimeOffset>()}: UTC at {Field("DateTimeOffsetUtc")}, minutes at {Field("DateTimeOffsetMinutes")} ({Field("DateTimeOffsetMinutesSize")} bytes); " +
                   $"Around<(int,long)> {Unsafe.SizeOf<Around<(int, long)>>()}";
        }

        private static long Offset<TS, TF>(ref TS s, ref TF f) =>
            (long)Unsafe.ByteOffset(ref Unsafe.As<TS, byte>(ref s), ref Unsafe.As<TF, byte>(ref f));

        private static string RuntimeName()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#else
            return "Mono";
#endif
        }
    }
}
