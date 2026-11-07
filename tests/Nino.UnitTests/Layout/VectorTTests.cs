using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.UnitTests.Subset;

namespace Nino.UnitTests
{
    /// <summary>
    /// <c>Vector&lt;T&gt;</c> is as wide as the CPU's vectors (16 bytes on Arm64, Mono, IL2CPP and x64 without AVX2, 32
    /// with AVX2, 64 with <c>DOTNET_MaxVectorTBitWidth=512</c>), so no bytes of it read on every host: Nino serializes no
    /// type that is or holds one. The analyzer reports NINO029 (suppressed here to reach the run-time checks), and the
    /// runtime refuses it when the formatter or layout is first resolved. The replacement (<see cref="Particle"/>)
    /// writes the same bytes on every CPU: run these tests with <c>DOTNET_EnableAVX2=0</c> and
    /// <c>DOTNET_MaxVectorTBitWidth=512</c> too.
    /// </summary>
    [TestClass]
    public class VectorTTests
    {
#pragma warning disable NINO029
        private static void Refused<T>(T value)
        {
            var refusal = Assert.ThrowsExactly<NotSupportedException>(() => NinoSerializer.Serialize(value));
            StringAssert.Contains(refusal.Message, "System.Numerics.Vector`1");
            StringAssert.Contains(refusal.Message, "[NinoIgnore]");
            // Reading is refused before a byte is looked at: 16 and 32 bytes alike.
            Assert.ThrowsExactly<NotSupportedException>(() => NinoDeserializer.Deserialize<T>(new byte[16]));
            Assert.ThrowsExactly<NotSupportedException>(() => NinoDeserializer.Deserialize<T>(new byte[64]));
            T into = value;
            Assert.ThrowsExactly<NotSupportedException>(() => NinoDeserializer.Deserialize(new byte[32], ref into));
            Assert.ThrowsExactly<NotSupportedException>(() => NinoSerializer.SerializeBoxed(value, typeof(T)));
            Assert.IsFalse(NinoSerializer.IsSupported(typeof(T)), typeof(T).ToString());
            // Again: still refused (the formatter is never published).
            Assert.ThrowsExactly<NotSupportedException>(() => NinoSerializer.Serialize(value));
        }

        [TestMethod]
        public void VectorTAloneOrInsideAnythingIsRefused()
        {
            var vector = new Vector<int>(7);
            Refused(vector);
            Refused(new[] { vector, vector });
            Refused(new List<Vector<int>> { vector });
            Refused((Vector<int>?)vector);
            Refused((1, vector));
            Refused(new KeyValuePair<int, Vector<float>>(1, default));
            Refused(new Dictionary<int, Vector<long>> { [1] = default });
            Refused(new Around<Vector<int>> { A = 1, B = vector, C = -1 });
            Refused(new[] { new Around<Around<Vector<byte>>>() });
            // In a base type or the IEnumerable<T> of a collection that names no vector itself.
            Refused(new VectorList { vector });
            Refused(new Dictionary<int, VectorList> { [1] = new VectorList() });
            Refused(new VectorBag());
        }

        /// <summary>A collection whose base type holds the vectors (NINO029, not NINO007).</summary>
        public class VectorList : List<Vector<int>>
        {
        }

        /// <summary>A collection whose IEnumerable&lt;T&gt; holds the vectors.</summary>
        public class VectorBag : IEnumerable<Vector<float>>
        {
            public IEnumerator<Vector<float>> GetEnumerator() =>
                ((IEnumerable<Vector<float>>)Array.Empty<Vector<float>>()).GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// A formatter used without the resolver: <see cref="UnmanagedFormatter{T}"/> made by hand, or the
        /// <see cref="Writer"/> and <see cref="Reader"/> value methods, refuse the type as the resolver does, with the same
        /// <see cref="NotSupportedException"/> (it was a <see cref="NullReferenceException"/> from the layout of no size).
        /// </summary>
        private static void RefusedByHand<T>(T value) where T : unmanaged
        {
            var buffer = new NinoArrayBufferWriter();
            var refusal = Assert.ThrowsExactly<NotSupportedException>(() =>
            {
                var writer = new Writer(buffer);
                new UnmanagedFormatter<T>().Serialize(value, ref writer);
            });
            StringAssert.Contains(refusal.Message, "System.Numerics.Vector`1");
            Assert.ThrowsExactly<NotSupportedException>(() =>
            {
                var writer = new Writer(buffer);
                writer.Write(value);
            });
            Assert.ThrowsExactly<NotSupportedException>(() =>
            {
                var reader = new Reader(new byte[64]);
                T into = default;
                new UnmanagedFormatter<T>().Deserialize(ref reader, ref into);
            });
            Assert.ThrowsExactly<NotSupportedException>(() =>
            {
                var reader = new Reader(new byte[64]);
                reader.Read<T>();
            });
        }

        [TestMethod]
        public void FormattersMadeByHandRefuseIt()
        {
            RefusedByHand(new Vector<int>(7));
            RefusedByHand(new Around<Vector<long>> { A = 1, C = -1 });
            RefusedByHand(new Around<Around<Vector<byte>>>());
            // Every other type keeps its bytes through the same calls.
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            new UnmanagedFormatter<Around<Vector4>>().Serialize(new Around<Vector4> { A = 1, B = Vector4.One, C = -1 },
                ref writer);
            writer.Flush();
            var reader = new Reader(buffer.WrittenSpan);
            Around<Vector4> back = default;
            new UnmanagedFormatter<Around<Vector4>>().Deserialize(ref reader, ref back);
            Assert.AreEqual(Vector4.One, back.B);
        }

        [TestMethod]
        public void LayoutsDoNotCopyIt()
        {
            // The raw paths ask NinoLayout, which never copies a Vector<T> as it lies in memory: neither canonical
            // (so no formatter is raw) nor sized. It does not throw: Mono and IL2CPP run NinoLayout<T>'s initializer
            // whenever a method naming it is compiled.
            var layout = NinoLayout.Get(typeof(Around<Vector<int>>), Unsafe.SizeOf<Around<Vector<int>>>());
            Assert.IsFalse(layout.Canonical);
            Assert.AreEqual(0, layout.Size);
            Assert.IsFalse(NinoLayout<Vector<byte>>.IsCanonical);
            Assert.AreEqual(typeof(Vector<int>), NinoReflection.HostSizedVectorIn(typeof(List<Around<Vector<int>>>[])));
            Assert.IsNull(NinoReflection.HostSizedVectorIn(typeof(Around<Vector4>)));
            Assert.IsNull(NinoReflection.HostSizedVectorIn(typeof(Particle)));
        }
#pragma warning restore NINO029

#if NET8_0_OR_GREATER
        [TestMethod]
        public void FixedSizeVectorsStaySerializable()
        {
            var value = new Around<System.Runtime.Intrinsics.Vector128<int>>
                { A = 1, B = System.Runtime.Intrinsics.Vector128.Create(1, -2, 3, -4), C = -1 };
            Assert.AreEqual(value.B, NinoDeserializer.Deserialize<Around<System.Runtime.Intrinsics.Vector128<int>>>(
                NinoSerializer.Serialize(value)).B);
            Assert.IsNull(NinoReflection.HostSizedVectorIn(typeof(System.Runtime.Intrinsics.Vector256<float>[])));
        }
#endif

        [TestMethod]
        public void FixedLanesWriteTheSameBytesOnEveryCpu()
        {
            var particle = new Particle { Id = 3 };
            for (int i = 0; i < Particle.LaneCount; i++)
                particle.Lanes[i] = i;
            // 16 floats split into whole vectors whatever Vector<float>.Count is (4, 8 or 16).
            Assert.AreEqual(Particle.LaneCount / Vector<float>.Count, particle.Vectors.Length);
            particle.Scale(0.5f);
            particle.Head += Vector<float>.One;
            for (int i = 0; i < Particle.LaneCount; i++)
                Assert.AreEqual(i * 0.5f + (i < Vector<float>.Count ? 1 : 0), particle.Lanes[i]);

            // Not null, Id, then the array: its length and 16 floats. The same 73 bytes on every CPU.
            var bytes = NinoSerializer.Serialize(new Particle { Id = 3, Lanes = Lanes(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15) });
            CollectionAssert.AreEqual(Rt.FromHex(
                "01 03000000 10000000" +
                "00000000 0000803F 00000040 00004040 00008040 0000A040 0000C040 0000E040" +
                "00000041 00001041 00002041 00003041 00004041 00005041 00006041 00007041"), bytes);
            var back = NinoDeserializer.Deserialize<Particle>(bytes);
            Assert.AreEqual(3, back.Id);
            CollectionAssert.AreEqual(Lanes(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15), back.Lanes);
            Assert.AreEqual(new Vector<float>(back.Lanes), back.Head);
        }

        private static float[] Lanes(params float[] lanes) => lanes;
    }
}
