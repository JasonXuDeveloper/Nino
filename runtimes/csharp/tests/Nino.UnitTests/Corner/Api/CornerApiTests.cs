using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Corner;

#nullable disable
namespace Nino.UnitTests
{
    /// <summary>A buffer writer that hands out spans no larger than asked for, in separate chunks.</summary>
    internal sealed class StingyBufferWriter : IBufferWriter<byte>
    {
        private readonly List<byte> _written = new();
        private byte[] _current = Array.Empty<byte>();
        public int GetSpanCalls;
        public Action OnGetSpan;
        /// <summary>Most bytes a span holds, whatever was asked for (a writer that hands out fixed chunks).</summary>
        public int MaxChunk = int.MaxValue;

        public byte[] Written => _written.ToArray();

        public void Advance(int count) => _written.AddRange(_current.Take(count));

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            GetSpanCalls++;
            OnGetSpan?.Invoke();
            _current = new byte[Math.Max(Math.Min(sizeHint, MaxChunk), 1)];
            return _current;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class ApiPayload
    {
        public int Id;
        public string Name;
        public List<SimpleNode> Nodes;
        public int[] Numbers;
        public Dictionary<string, AbstractShape> Shapes;
        public RawPadded Raw;
        public RawPadded[] Raws;
        public VerOuterV1 Sealed;
    }

    [TestClass]
    public class CornerApiTests
    {
        private static ApiPayload Payload() => new()
        {
            Id = 42, Name = "payload",
            Nodes = new List<SimpleNode> { new() { Id = 1, Name = "n" }, null },
            Numbers = Enumerable.Range(0, 50).ToArray(),
            Shapes = new Dictionary<string, AbstractShape>
                { ["sq"] = new SquareShape { Side = 1 }, ["c"] = new SealedCircle { Radius = 2 }, ["null"] = null },
            Raw = new RawPadded { A = 1, B = 2, C = 3, D = 4 },
            Raws = new[] { new RawPadded { D = 5 } },
            Sealed = new VerOuterV1 { A = 7, Mids = new List<VerMidV1> { new() { M = 1 } } },
        };

        private static void AssertPayload(ApiPayload p)
        {
            Assert.AreEqual(42, p.Id);
            Assert.AreEqual("payload", p.Name);
            Assert.AreEqual("n", p.Nodes[0].Name);
            Assert.IsNull(p.Nodes[1]);
            Assert.AreEqual(49, p.Numbers[49]);
            Assert.AreEqual(2, ((SealedCircle)p.Shapes["c"]).Radius);
            Assert.IsNull(p.Shapes["null"]);
            Assert.AreEqual(4, p.Raw.D);
            Assert.AreEqual(5, p.Raws[0].D);
            Assert.AreEqual(1, p.Sealed.Mids[0].M);
        }

        [TestMethod]
        public void BufferWritersOfEveryKind()
        {
            var expected = NinoSerializer.Serialize(Payload());

            var stingy = new StingyBufferWriter();
            NinoSerializer.Serialize(Payload(), stingy);
            CollectionAssert.AreEqual(expected, stingy.Written);

            // A writer that hands out less than asked for gets the payload in chunks.
            var chunked = new StingyBufferWriter { MaxChunk = 7 };
            NinoSerializer.Serialize(Payload(), chunked);
            CollectionAssert.AreEqual(expected, chunked.Written);

            // Raw values and raw arrays take their own paths.
            var raw = new StingyBufferWriter();
            NinoSerializer.Serialize(new RawPadded { D = 9 }, raw);
            NinoSerializer.Serialize(new[] { 1, 2, 3 }, raw);
            NinoSerializer.Serialize(new[] { new RawPadded { A = 1 } }, raw);
            NinoSerializer.Serialize("tail", raw);
            var rawBytes = raw.Written;
            var reader = new Reader(rawBytes);
            Assert.AreEqual(9, NinoDeserializer.Deserialize<RawPadded>(ref reader).D);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, NinoDeserializer.Deserialize<int[]>(ref reader));
            Assert.AreEqual(1, NinoDeserializer.Deserialize<RawPadded[]>(ref reader)[0].A);
            Assert.AreEqual("tail", NinoDeserializer.Deserialize<string>(ref reader));

            // A NinoArrayBufferWriter is written in place; several values back to back, then reused.
            using var nino = new NinoArrayBufferWriter(1);
            NinoSerializer.Serialize(Payload(), nino);
            NinoSerializer.Serialize(Payload(), nino);
            Assert.AreEqual(expected.Length * 2, nino.WrittenCount);
            var both = nino.WrittenSpan.ToArray();
            var r2 = new Reader(both);
            AssertPayload(NinoDeserializer.Deserialize<ApiPayload>(ref r2));
            AssertPayload(NinoDeserializer.Deserialize<ApiPayload>(ref r2));
            nino.ResetWrittenCount();
            NinoSerializer.Serialize(Payload(), nino);
            CollectionAssert.AreEqual(expected, nino.ToArray());

            // Boxed overloads.
            var boxedWriter = new StingyBufferWriter();
            NinoSerializer.SerializeBoxed(Payload(), boxedWriter, typeof(ApiPayload));
            CollectionAssert.AreEqual(expected, boxedWriter.Written);
            CollectionAssert.AreEqual(expected, NinoSerializer.SerializeBoxed(Payload(), typeof(ApiPayload)));
        }

        [TestMethod]
        public void RawListKeepsItsLengthWhenTheDestinationGrowsIt()
        {
            var values = new List<int>(1) { 42 };
            var buffer = new StingyBufferWriter { OnGetSpan = () => values.Add(99) };

            NinoSerializer.Serialize(values, buffer);

            Assert.AreEqual(1, buffer.GetSpanCalls);
            CollectionAssert.AreEqual(new[] { 42 }, NinoDeserializer.Deserialize<int[]>(buffer.Written));
        }

        [TestMethod]
        public void RawSlicesCanWriteOverTheirBackingArray()
        {
            Check(static bytes => new Memory<byte>(bytes, 2, 7));
            Check(static bytes => new ReadOnlyMemory<byte>(bytes, 2, 7));
            Check(static bytes => new ArraySegment<byte>(bytes, 2, 7));
            Check(static bytes => new ReadOnlySequence<byte>(bytes, 2, 7));

            static void Check<T>(Func<byte[], T> slice)
            {
                byte[] bytes = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
                T value = slice(bytes);
                byte[] expected = NinoSerializer.Serialize(value);
                var output = new AliasedBufferWriter(bytes);
                NinoSerializer.Serialize(value, output);
                CollectionAssert.AreEqual(expected, bytes.AsSpan(0, output.Written).ToArray());
            }
        }

        // Past 64 bytes the disjoint copy is a forward vector loop, which a destination overlapping the source
        // (here 2 bytes after it, or 6 before) would overwrite before reading: these sizes must take the overlap-safe
        // move.
        [TestMethod]
        [DataRow(100, 2)]
        [DataRow(1000, 2)]
        [DataRow(5000, 2)]
        [DataRow(1000, -6)]
        [DataRow(5000, -6)]
        public void LongRawSlicesCanWriteOverTheirBackingArray(int count, int shift)
        {
            Check(count, shift, static (ints, n) => new Memory<int>(ints, 3, n));
            Check(count, shift, static (ints, n) => new ReadOnlyMemory<int>(ints, 3, n));
            Check(count, shift, static (ints, n) => new ArraySegment<int>(ints, 3, n));
            Check(count, shift, static (ints, n) => new ReadOnlySequence<int>(ints, 3, n));

            static void Check<T>(int n, int shift, Func<int[], int, T> slice)
            {
                var ints = Enumerable.Range(0, n + 4).Select(i => i * 0x01010101).ToArray();
                T value = slice(ints, n);
                byte[] expected = NinoSerializer.Serialize(value);
                // The slice starts at byte 12; the body follows the 4-byte length, at start + 4 = 12 + shift.
                int start = 8 + shift;
                var output = new AliasedIntWriter(ints, start);
                NinoSerializer.Serialize(value, output);
                Assert.AreEqual(expected.Length, output.Written);
                CollectionAssert.AreEqual(expected,
                    MemoryMarshal.AsBytes(ints.AsSpan()).Slice(start, output.Written).ToArray());
            }
        }

        private sealed class AliasedIntWriter : IBufferWriter<byte>
        {
            private readonly int[] _ints;
            private readonly int _start;
            public int Written { get; private set; }

            public AliasedIntWriter(int[] ints, int start) => (_ints, _start) = (ints, start);
            public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();
            public Span<byte> GetSpan(int sizeHint = 0) => MemoryMarshal.AsBytes(_ints.AsSpan()).Slice(_start + Written);
            public void Advance(int count) => Written += count;
        }

        private sealed class AliasedBufferWriter : IBufferWriter<byte>
        {
            private readonly byte[] _bytes;
            public int Written { get; private set; }

            public AliasedBufferWriter(byte[] bytes) => _bytes = bytes;
            public Memory<byte> GetMemory(int sizeHint = 0) => _bytes.AsMemory(Written);
            public Span<byte> GetSpan(int sizeHint = 0) => _bytes.AsSpan(Written);
            public void Advance(int count) => Written += count;
        }

        [TestMethod]
        public void CustomMemoryManagerIsReadBeforeCallingTheDestination()
        {
            Check(static manager => manager.Memory);
            Check(static manager => new ReadOnlySequence<byte>(manager.Memory));

            static void Check<T>(Func<ArrayMemoryManager, T> view)
            {
                using var manager = new ArrayMemoryManager();
                T value = view(manager);
                byte[] expected = NinoSerializer.Serialize(value);
                var output = new StingyBufferWriter { OnGetSpan = () => manager.GetSpan()[0] = 99 };

                NinoSerializer.Serialize(value, output);

                CollectionAssert.AreEqual(expected, output.Written);
            }
        }

        private enum SliceCode : short { Low = -31, High = 300 }

        [TestMethod]
        public void RawSlicesPreserveNonIntWireLayouts()
        {
            Check(new[] { true, false, true });
            Check(new[] { SliceCode.Low, SliceCode.High });
            Check(new[] { new RawPadded { A = 1, D = 9 }, new RawPadded { A = 2, D = -7 } });

            static void Check<T>(T[] values) where T : unmanaged
            {
                byte[] expected = NinoSerializer.Serialize(values);
                CheckOutput(new Memory<T>(values), expected);
                CheckOutput(new ReadOnlyMemory<T>(values), expected);
                CheckOutput(new ArraySegment<T>(values), expected);
                CheckOutput(new ReadOnlySequence<T>(values), expected);
            }

            static void CheckOutput<T>(T value, byte[] expected)
            {
                var output = new ArrayBufferWriter<byte>(expected.Length + 1);
                output.GetSpan(expected.Length + 1).Fill(0xCC);
                output.Advance(1);
                NinoSerializer.Serialize(value, output);
                Assert.AreEqual((byte)0xCC, output.WrittenSpan[0]);
                CollectionAssert.AreEqual(expected, output.WrittenSpan.Slice(1).ToArray());
            }
        }

        private sealed class ArrayMemoryManager : MemoryManager<byte>
        {
            private readonly byte[] _bytes = { 1, 2, 3 };

            public override Span<byte> GetSpan() => _bytes;
            public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
            public override void Unpin() { }
            protected override void Dispose(bool disposing) { }

            protected override bool TryGetArray(out ArraySegment<byte> segment)
            {
                segment = new ArraySegment<byte>(_bytes);
                return true;
            }
        }

        /// <summary>A pool that records what it hands out and gets back (issue #198).</summary>
        private sealed class CountingPool : ArrayPool<byte>
        {
            public readonly List<byte[]> Rented = new();
            public readonly List<byte[]> Returned = new();

            public override byte[] Rent(int minimumLength)
            {
                var array = new byte[minimumLength];
                Rented.Add(array);
                return array;
            }

            public override void Return(byte[] array, bool clearArray = false) => Returned.Add(array);
        }

        [TestMethod]
        public void BufferWriterUsesTheGivenPool()
        {
            var expected = NinoSerializer.Serialize(Payload());
            var pool = new CountingPool();
            var nino = new NinoArrayBufferWriter(1, pool);
            Assert.AreEqual(1, pool.Rented.Count);

            // Serializing grows the buffer: every array comes from the pool, and each replaced one goes back to it.
            NinoSerializer.Serialize(Payload(), nino);
            CollectionAssert.AreEqual(expected, nino.ToArray());
            Assert.IsTrue(pool.Rented.Count > 1);
            CollectionAssert.AreEqual(pool.Rented.Take(pool.Rented.Count - 1).ToList(), pool.Returned);

            var last = pool.Rented[pool.Rented.Count - 1];
            nino.Dispose();
            Assert.AreSame(last, pool.Returned[pool.Returned.Count - 1]);
            Assert.AreEqual(pool.Rented.Count, pool.Returned.Count);

            // Capacity 0 rents nothing until the first write.
            var empty = new CountingPool();
            using (var lazy = new NinoArrayBufferWriter(0, empty))
            {
                Assert.AreEqual(0, empty.Rented.Count);
                NinoSerializer.Serialize(Payload(), lazy);
                CollectionAssert.AreEqual(expected, lazy.ToArray());
            }
            Assert.AreEqual(empty.Rented.Count, empty.Returned.Count);

            Assert.ThrowsExactly<ArgumentNullException>(() => new NinoArrayBufferWriter(16, null));
        }

        [TestMethod]
        public void SpanOverloadsAtUnalignedOffsets()
        {
            var bytes = NinoSerializer.Serialize(Payload());
            for (int offset = 1; offset < 8; offset++)
            {
                var big = new byte[bytes.Length + offset + 5];
                bytes.CopyTo(big, offset);
                var span = new ReadOnlySpan<byte>(big, offset, bytes.Length);
                AssertPayload(NinoDeserializer.Deserialize<ApiPayload>(span));

                var target = new ApiPayload { Nodes = new List<SimpleNode>() };
                var nodes = target.Nodes;
                NinoDeserializer.Deserialize(span, ref target);
                Assert.AreSame(nodes, target.Nodes);
                AssertPayload(target);

                var rawBytes = NinoSerializer.Serialize(new[] { new RawPadded { D = offset } });
                var rawBig = new byte[rawBytes.Length + offset];
                rawBytes.CopyTo(rawBig, offset);
                Assert.AreEqual(offset, NinoDeserializer.Deserialize<RawPadded[]>(rawBig.AsSpan(offset))[0].D);
            }

            // Trailing bytes after a complete value are ignored.
            var padded = bytes.Concat(new byte[] { 1, 2, 3 }).ToArray();
            AssertPayload(NinoDeserializer.Deserialize<ApiPayload>(padded));
        }

        [TestMethod]
        public void NonGenericDeserialize()
        {
            var bytes = NinoSerializer.Serialize(Payload());
            AssertPayload((ApiPayload)NinoDeserializer.DeserializeBoxed(bytes, typeof(ApiPayload)));

            object existing = new ApiPayload();
            var before = existing;
            NinoDeserializer.DeserializeBoxed(bytes, ref existing, typeof(ApiPayload));
            Assert.AreSame(before, existing);
            AssertPayload((ApiPayload)existing);

            // An existing value of another type is not reused.
            object other = "not a payload";
            NinoDeserializer.DeserializeBoxed(bytes, ref other, typeof(ApiPayload));
            AssertPayload((ApiPayload)other);

            Assert.AreEqual(5, NinoDeserializer.DeserializeBoxed(NinoSerializer.Serialize(5), typeof(int)));
            Assert.AreEqual(5L, NinoDeserializer.DeserializeBoxed(NinoSerializer.SerializeBoxed(5L, typeof(long)), typeof(long)));
            Assert.IsNull(NinoDeserializer.DeserializeBoxed(NinoSerializer.Serialize<string>(null), typeof(string)));
            Assert.AreEqual(9, ((RawPadded)NinoDeserializer.DeserializeBoxed(NinoSerializer.Serialize(new RawPadded { D = 9 }), typeof(RawPadded))).D);
            var shape = NinoDeserializer.DeserializeBoxed(NinoSerializer.Serialize<AbstractShape>(new SquareShape { Side = 3 }), typeof(AbstractShape));
            Assert.AreEqual(3, ((SquareShape)shape).Side);
            CollectionAssert.AreEqual(new[] { 1 }, (List<int>)NinoDeserializer.DeserializeBoxed(NinoSerializer.Serialize(new List<int> { 1 }), typeof(List<int>)));

            Assert.ThrowsExactly<ArgumentNullException>(() => NinoDeserializer.DeserializeBoxed(bytes, null));
            Assert.ThrowsExactly<ArgumentNullException>(() => NinoSerializer.SerializeBoxed(Payload(), null));
        }

        [TestMethod]
        public void BoxedRoundTrips()
        {
            // SerializeBoxed(value, type) writes exactly what Serialize<T> writes with T = type.
            object payload = Payload();
            var bytes = NinoSerializer.SerializeBoxed(payload, typeof(ApiPayload));
            CollectionAssert.AreEqual(NinoSerializer.Serialize((ApiPayload)payload), bytes);
            AssertPayload((ApiPayload)NinoDeserializer.DeserializeBoxed(bytes, typeof(ApiPayload)));

            // Polymorphic: a subtype boxed as its base keeps its runtime type.
            object square = new SquareShape { Name = "sq", Side = 4 };
            var shapeBytes = NinoSerializer.SerializeBoxed(square, typeof(AbstractShape));
            CollectionAssert.AreEqual(NinoSerializer.Serialize<AbstractShape>((SquareShape)square), shapeBytes);
            var shape = NinoDeserializer.DeserializeBoxed(shapeBytes, typeof(AbstractShape));
            Assert.IsInstanceOfType(shape, typeof(SquareShape));
            Assert.AreEqual("sq", ((SquareShape)shape).Name);
            Assert.AreEqual(4, ((SquareShape)shape).Side);

            // As object: the type id is written, the value comes back as its runtime type.
            var objectBytes = NinoSerializer.SerializeBoxed(square, typeof(object));
            Assert.IsInstanceOfType(NinoDeserializer.DeserializeBoxed(objectBytes, typeof(object)), typeof(SquareShape));

            // Value types, nullables and collections.
            Assert.AreEqual(42, NinoDeserializer.DeserializeBoxed(NinoSerializer.SerializeBoxed(42, typeof(int)), typeof(int)));
            Assert.AreEqual(7, NinoDeserializer.DeserializeBoxed(NinoSerializer.SerializeBoxed(7, typeof(int?)), typeof(int?)));
            Assert.AreEqual(9, ((RawPadded)NinoDeserializer.DeserializeBoxed(
                NinoSerializer.SerializeBoxed(new RawPadded { D = 9 }, typeof(RawPadded)), typeof(RawPadded))).D);
            CollectionAssert.AreEqual(new[] { 1, 2 }, (List<int>)NinoDeserializer.DeserializeBoxed(
                NinoSerializer.SerializeBoxed(new List<int> { 1, 2 }, typeof(List<int>)), typeof(List<int>)));

            // Null, for every type that can be null.
            foreach (var type in new[] { typeof(ApiPayload), typeof(AbstractShape), typeof(object), typeof(string), typeof(int?), typeof(List<int>) })
            {
                var nullBytes = NinoSerializer.SerializeBoxed(null, type);
                Assert.IsNull(NinoDeserializer.DeserializeBoxed(nullBytes, type), type.Name);
                object existing = "replaced by null";
                NinoDeserializer.DeserializeBoxed(nullBytes, ref existing, type);
                Assert.IsNull(existing, type.Name);
            }
            CollectionAssert.AreEqual(NinoSerializer.Serialize<ApiPayload>(null), NinoSerializer.SerializeBoxed(null, typeof(ApiPayload)));

            // Writer and reader overloads.
            var writerBuffer = new NinoArrayBufferWriter();
            var writer = new Writer(writerBuffer);
            NinoSerializer.SerializeBoxed(square, ref writer, typeof(AbstractShape));
            NinoSerializer.SerializeBoxed(null, ref writer, typeof(AbstractShape));
            writer.Flush();
            var reader = new Reader(writerBuffer.WrittenSpan);
            object first = null, second = new SquareShape();
            NinoDeserializer.DeserializeBoxed(ref reader, ref first, typeof(AbstractShape));
            NinoDeserializer.DeserializeBoxed(ref reader, ref second, typeof(AbstractShape));
            Assert.AreEqual(4, ((SquareShape)first).Side);
            Assert.IsNull(second);

            Assert.ThrowsExactly<ArgumentNullException>(() => NinoSerializer.SerializeBoxed(payload, (Type)null));
        }

        [TestMethod]
        public void WriterValueOverloadsKeepTheirEncodings()
        {
            // Write<T>(in T) and Write<T>(T?) both accept a nullable: the nullable one must still win (a has-value
            // byte, then the value), and an in argument read from a field writes the same bytes as a local.
            int? none = null, five = 5;
            var holder = new Tuple<Guid>(Guid.NewGuid());
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.Write(none);
            writer.Write(five);
            writer.Write(7);
            writer.Write(holder.Item1);
            writer.Flush();
            Assert.AreEqual(1 + 5 + 4 + 16, buffer.WrittenCount);

            var reader = new Reader(buffer.WrittenSpan);
            reader.Read(out int? first);
            reader.Read(out int? second);
            Assert.IsNull(first);
            Assert.AreEqual(5, second);
            Assert.AreEqual(7, reader.Read<int>());
            Assert.AreEqual(holder.Item1, reader.Read<Guid>());
        }

        [TestMethod]
        public void EmptyBufferThrows()
        {
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<int>(Array.Empty<byte>()));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<RawPadded>(Array.Empty<byte>()));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<string>(Array.Empty<byte>()));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<ApiPayload>(Array.Empty<byte>()));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<VerOuterV1>(Array.Empty<byte>()));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<List<int>>(Array.Empty<byte>()));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<int[]>(Array.Empty<byte>()));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.DeserializeBoxed(Array.Empty<byte>(), typeof(ApiPayload)));
            var target = Payload();
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(Array.Empty<byte>(), ref target));
        }

        private static void ExpectFailure<T>(byte[] data, string what)
        {
            var task = Task.Run(() =>
            {
                try
                {
                    NinoDeserializer.Deserialize<T>(data);
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
            });
            Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(5)), what + ": hang");
            Assert.IsNotNull(task.Result, what + ": no exception");
            Assert.IsInstanceOfType(task.Result, typeof(InvalidOperationException), what + ": " + task.Result);
        }

        [TestMethod]
        public void EveryTruncationThrowsAClearException()
        {
            var bytes = NinoSerializer.Serialize(Payload());
            for (int length = 0; length < bytes.Length; length++)
                ExpectFailure<ApiPayload>(bytes.Take(length).ToArray(), "payload cut at " + length);

            var list = NinoSerializer.Serialize(new List<string> { "a", null, "ccc" });
            for (int length = 0; length < list.Length; length++)
                ExpectFailure<List<string>>(list.Take(length).ToArray(), "list cut at " + length);

            var keyed = NinoSerializer.Serialize(new KeyedVerLeafV1 { A = 1, B = "b", D = new List<int> { 1 } });
            for (int length = 0; length < keyed.Length; length++)
                ExpectFailure<KeyedVerLeafV1>(keyed.Take(length).ToArray(), "keyed cut at " + length);

            var tracked = new RefNode { Id = 1 };
            tracked.Self = tracked;
            var trackedBytes = NinoSerializer.Serialize(tracked);
            for (int length = 0; length < trackedBytes.Length; length++)
                ExpectFailure<RefNode>(trackedBytes.Take(length).ToArray(), "tracked cut at " + length);
        }

        [TestMethod]
        public void HugeLengthsAreRejectedBeforeAllocating()
        {
            foreach (var length in new[] { int.MaxValue, int.MaxValue / 2, 1 << 28, -2, int.MinValue })
            {
                var data = BitConverter.GetBytes(length).Concat(new byte[16]).ToArray();
                ExpectFailure<List<int>>(data, "List<int> " + length);
                ExpectFailure<int[]>(data, "int[] " + length);
                ExpectFailure<List<string>>(data, "List<string> " + length);
                ExpectFailure<Dictionary<int, int>>(data, "Dictionary " + length);
                ExpectFailure<RawPadded[]>(data, "raw array " + length);
                ExpectFailure<HashSet<long>>(data, "HashSet " + length);
                // Both lengths: a zero length makes any other length valid (an empty array).
                ExpectFailure<int[,]>(BitConverter.GetBytes(length).Concat(data).ToArray(), "int[,] " + length);
            }

            // String headers: the largest of either form, and 2^28 chars.
            foreach (var header in new[] { uint.MaxValue, uint.MaxValue - 1, 1u << 29, (1u << 29) + 1 })
            {
                var data = StringEncodingTests.Header(header).Concat(new byte[16]).ToArray();
                ExpectFailure<string>(data, "string " + header);
                ExpectFailure<System.Text.StringBuilder>(data, "StringBuilder " + header);
                ExpectFailure<List<string>>(new byte[] { 1, 0, 0, 0 }.Concat(data).ToArray(), "List<string> " + header);
            }
        }

        [TestMethod]
        public void CorruptedBytesNeverHangOrCrash()
        {
            var bytes = NinoSerializer.Serialize(Payload());
            var random = new Random(42);
            var unexpected = new List<string>();
            for (int i = 0; i < bytes.Length * 3; i++)
            {
                var corrupt = (byte[])bytes.Clone();
                int at = i % bytes.Length;
                corrupt[at] = i < bytes.Length ? (byte)0xFF : (byte)random.Next(256);
                var task = Task.Run(() =>
                {
                    try
                    {
                        NinoDeserializer.Deserialize<ApiPayload>(corrupt);
                        return null;
                    }
                    catch (Exception e)
                    {
                        return e;
                    }
                });
                Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(5)), "hang with byte " + at + " corrupted");
                // Either a value or Nino's own InvalidOperationException; anything else is a missing check.
                if (task.Result != null && task.Result is not InvalidOperationException)
                    unexpected.Add($"byte {at} = {corrupt[at]}: {task.Result.GetType().Name}: {task.Result.Message}");
            }

            Assert.AreEqual(0, unexpected.Count, string.Join("\n", unexpected.Take(10)));
        }
    }
}
