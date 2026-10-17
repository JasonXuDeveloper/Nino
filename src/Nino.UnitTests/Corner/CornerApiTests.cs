using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Corner;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>A buffer writer that hands out spans no larger than asked for, in separate chunks.</summary>
    internal sealed class StingyBufferWriter : IBufferWriter<byte>
    {
        private readonly List<byte> _written = new List<byte>();
        private byte[] _current = Array.Empty<byte>();
        public int GetSpanCalls;

        public byte[] Written => _written.ToArray();

        public void Advance(int count) => _written.AddRange(_current.Take(count));

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            GetSpanCalls++;
            _current = new byte[Math.Max(sizeHint, 1)];
            return _current;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    [NinoType]
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
        private static ApiPayload Payload() => new ApiPayload
        {
            Id = 42, Name = "payload",
            Nodes = new List<SimpleNode> { new SimpleNode { Id = 1, Name = "n" }, null },
            Numbers = Enumerable.Range(0, 50).ToArray(),
            Shapes = new Dictionary<string, AbstractShape>
                { ["sq"] = new SquareShape { Side = 1 }, ["c"] = new SealedCircle { Radius = 2 }, ["null"] = null },
            Raw = new RawPadded { A = 1, B = 2, C = 3, D = 4 },
            Raws = new[] { new RawPadded { D = 5 } },
            Sealed = new VerOuterV1 { A = 7, Mids = new List<VerMidV1> { new VerMidV1 { M = 1 } } },
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

        /// <summary>A pool that records what it hands out and gets back (issue #198).</summary>
        private sealed class CountingPool : ArrayPool<byte>
        {
            public readonly List<byte[]> Rented = new List<byte[]>();
            public readonly List<byte[]> Returned = new List<byte[]>();

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
            NinoDeserializer.DeserializeBoxed(ref first, ref reader, typeof(AbstractShape));
            NinoDeserializer.DeserializeBoxed(ref second, ref reader, typeof(AbstractShape));
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
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<int>(Array.Empty<byte>()));
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<RawPadded>(Array.Empty<byte>()));
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<string>(Array.Empty<byte>()));
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<ApiPayload>(Array.Empty<byte>()));
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<VerOuterV1>(Array.Empty<byte>()));
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<List<int>>(Array.Empty<byte>()));
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<int[]>(Array.Empty<byte>()));
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.DeserializeBoxed(Array.Empty<byte>(), typeof(ApiPayload)));
            var target = Payload();
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize(Array.Empty<byte>(), ref target));
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
                ExpectFailure<string>(data, "string " + length);
                ExpectFailure<List<string>>(data, "List<string> " + length);
                ExpectFailure<Dictionary<int, int>>(data, "Dictionary " + length);
                ExpectFailure<RawPadded[]>(data, "raw array " + length);
                ExpectFailure<HashSet<long>>(data, "HashSet " + length);
                // Both lengths: a zero length makes any other length valid (an empty array).
                ExpectFailure<int[,]>(BitConverter.GetBytes(length).Concat(data).ToArray(), "int[,] " + length);
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
