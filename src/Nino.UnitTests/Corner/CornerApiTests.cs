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
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize((object)Payload()));
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
            AssertPayload((ApiPayload)NinoDeserializer.Deserialize(bytes, typeof(ApiPayload)));

            object existing = new ApiPayload();
            var before = existing;
            NinoDeserializer.Deserialize(bytes, typeof(ApiPayload), ref existing);
            Assert.AreSame(before, existing);
            AssertPayload((ApiPayload)existing);

            // An existing value of another type is not reused.
            object other = "not a payload";
            NinoDeserializer.Deserialize(bytes, typeof(ApiPayload), ref other);
            AssertPayload((ApiPayload)other);

            Assert.AreEqual(5, NinoDeserializer.Deserialize(NinoSerializer.Serialize(5), typeof(int)));
            Assert.AreEqual(5L, NinoDeserializer.Deserialize(NinoSerializer.Serialize((object)5L), typeof(long)));
            Assert.IsNull(NinoDeserializer.Deserialize(NinoSerializer.Serialize<string>(null), typeof(string)));
            Assert.AreEqual(9, ((RawPadded)NinoDeserializer.Deserialize(NinoSerializer.Serialize(new RawPadded { D = 9 }), typeof(RawPadded))).D);
            var shape = NinoDeserializer.Deserialize(NinoSerializer.Serialize<AbstractShape>(new SquareShape { Side = 3 }), typeof(AbstractShape));
            Assert.AreEqual(3, ((SquareShape)shape).Side);
            CollectionAssert.AreEqual(new[] { 1 }, (List<int>)NinoDeserializer.Deserialize(NinoSerializer.Serialize(new List<int> { 1 }), typeof(List<int>)));

            Assert.ThrowsException<ArgumentNullException>(() => NinoDeserializer.Deserialize(bytes, null));
            Assert.ThrowsException<ArgumentNullException>(() => NinoSerializer.Serialize((object)null));
        }

        [TestMethod]
        public void EmptyBufferThrows()
        {
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize<int>(Array.Empty<byte>()));
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize<RawPadded>(Array.Empty<byte>()));
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize<string>(Array.Empty<byte>()));
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize<ApiPayload>(Array.Empty<byte>()));
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize<VerOuterV1>(Array.Empty<byte>()));
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize<List<int>>(Array.Empty<byte>()));
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize<int[]>(Array.Empty<byte>()));
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize(Array.Empty<byte>(), typeof(ApiPayload)));
            var target = Payload();
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize(Array.Empty<byte>(), ref target));
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
                ExpectFailure<int[,]>(data, "int[,] " + length);
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
