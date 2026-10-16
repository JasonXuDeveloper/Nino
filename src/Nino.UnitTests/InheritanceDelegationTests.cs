using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    // Derived formatters delegate inherited members to the base formatter when the base members are reachable
    // (and no raw block straddles the boundary); the bytes must be the same as writing every member inline.

    [NinoType]
    public class DelegLevel1
    {
        public int A;
        public string Name;
    }

    [NinoType]
    public class DelegLevel2 : DelegLevel1
    {
        public List<int> Items;
    }

    [NinoType]
    public class DelegLevel3 : DelegLevel2
    {
        public double D;
        public long E;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class DelegTolerant1
    {
        public int A;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class DelegTolerant2 : DelegTolerant1
    {
        public string B;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class DelegTolerant3 : DelegTolerant2
    {
        public string C;
    }

    // Private base members are not part of the derived layout, so the derived member list is not an extension of
    // the base list: the derived formatter must write its members inline instead of delegating.
    [NinoType(containNonPublicMembers: true)]
    public partial class DelegPrivateBase
    {
        private int _secret;
        public int Visible;

        public int GetSecret() => _secret;
        public void SetSecret(int value) => _secret = value;
    }

    [NinoType(containNonPublicMembers: true)]
    public partial class DelegPrivateDerived : DelegPrivateBase
    {
        public int Extra;
    }

    [TestClass]
    public class InheritanceDelegationTests
    {
        [TestMethod]
        public void MultiLevelRoundTrip()
        {
            var value = new DelegLevel3 { A = 1, Name = "n", Items = new List<int> { 1, 2, 3 }, D = 2.5, E = 7 };
            var bytes = NinoSerializer.Serialize(value);
            var result = NinoDeserializer.Deserialize<DelegLevel3>(bytes);
            Assert.AreEqual(1, result.A);
            Assert.AreEqual("n", result.Name);
            CollectionAssert.AreEqual(value.Items, result.Items);
            Assert.AreEqual(2.5, result.D);
            Assert.AreEqual(7, result.E);

            // Through the base type (polymorphic dispatch to the most derived formatter).
            DelegLevel1 asBase = value;
            var poly = NinoDeserializer.Deserialize<DelegLevel1>(NinoSerializer.Serialize(asBase));
            Assert.IsInstanceOfType(poly, typeof(DelegLevel3));
            Assert.AreEqual(7, ((DelegLevel3)poly).E);

            // In-place reuse keeps the instance.
            var target = new DelegLevel3();
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreEqual("n", target.Name);
            Assert.AreEqual(7, target.E);
        }

        [TestMethod]
        public void MultiLevelBytesMatchInlineLayout()
        {
            var value = new DelegLevel3 { A = 1, Name = "n", Items = new List<int> { 1 }, D = 2.5, E = 7 };
            var writer = new List<byte>();
            byte[][] members =
            {
                NinoSerializer.Serialize(1), NinoSerializer.Serialize("n"),
                NinoSerializer.Serialize(new List<int> { 1 }),
                NinoSerializer.Serialize(2.5), NinoSerializer.Serialize(7L)
            };
#if WEAK_VERSION_TOLERANCE
            // Under WEAK_VERSION_TOLERANCE the whole body (every level, root first) is prefixed with one length.
            int length = 0;
            foreach (var m in members)
                length += m.Length;
            writer.AddRange(System.BitConverter.GetBytes(length));
#endif
            foreach (var m in members)
                writer.AddRange(m);

            var body = NinoSerializer.Serialize(value);
            // The type id comes first; then the members in order.
            var expected = writer.ToArray();
            CollectionAssert.AreEqual(expected, body[^expected.Length..]);
        }

        // A tolerant object is one region: its type id, one length, then the members of every level, root first.
        private static byte[] TolerantData(params byte[][] members)
        {
            var data = new List<byte>(System.BitConverter.GetBytes(NinoTypeId.Of(typeof(DelegTolerant3))));
            int length = 0;
            foreach (var m in members)
                length += m.Length;
            data.AddRange(System.BitConverter.GetBytes(length));
            foreach (var m in members)
                data.AddRange(m);
            return data.ToArray();
        }

        [TestMethod]
        public void TolerantChainReadsOlderData()
        {
            var value = new DelegTolerant3 { A = 1, B = "b", C = "c" };
            var bytes = NinoSerializer.Serialize(value);
            CollectionAssert.AreEqual(
                TolerantData(NinoSerializer.Serialize(1), NinoSerializer.Serialize("b"), NinoSerializer.Serialize("c")),
                bytes);
            var result = NinoDeserializer.Deserialize<DelegTolerant3>(bytes);
            Assert.AreEqual(1, result.A);
            Assert.AreEqual("b", result.B);
            Assert.AreEqual("c", result.C);

            // Written before C existed: the region ends after B.
            var older = TolerantData(NinoSerializer.Serialize(5), NinoSerializer.Serialize("six"));
            var partial = NinoDeserializer.Deserialize<DelegTolerant3>(older);
            Assert.AreEqual(5, partial.A);
            Assert.AreEqual("six", partial.B);
            Assert.IsNull(partial.C);

            // Written by a newer version whose leaf gained a trailing member: it is skipped.
            var newer = TolerantData(NinoSerializer.Serialize(5), NinoSerializer.Serialize("six"),
                NinoSerializer.Serialize("seven"), NinoSerializer.Serialize(123L));
            var skipped = NinoDeserializer.Deserialize<DelegTolerant3>(newer);
            Assert.AreEqual(5, skipped.A);
            Assert.AreEqual("six", skipped.B);
            Assert.AreEqual("seven", skipped.C);

            // Even older data that ends inside the base: every later member is missing.
            var oldest = TolerantData(NinoSerializer.Serialize(5));
            var baseOnly = NinoDeserializer.Deserialize<DelegTolerant3>(oldest);
            Assert.AreEqual(5, baseOnly.A);
            Assert.IsNull(baseOnly.B);
            Assert.IsNull(baseOnly.C);

            // In place: a member missing from older data keeps its current value.
            var target = new DelegTolerant3 { C = "kept" };
            NinoDeserializer.Deserialize(older, ref target);
            Assert.AreEqual("six", target.B);
            Assert.AreEqual("kept", target.C);
        }

        [TestMethod]
        public void PrivateBaseMembersFallBackToInline()
        {
            var value = new DelegPrivateDerived { Visible = 5, Extra = 6 };
            value.SetSecret(4);
            var result = NinoDeserializer.Deserialize<DelegPrivateDerived>(NinoSerializer.Serialize(value));
            Assert.AreEqual(0, result.GetSecret());
            Assert.AreEqual(5, result.Visible);
            Assert.AreEqual(6, result.Extra);

            var baseValue = new DelegPrivateBase { Visible = 5 };
            baseValue.SetSecret(4);
            var baseResult = NinoDeserializer.Deserialize<DelegPrivateBase>(NinoSerializer.Serialize(baseValue));
            Assert.AreEqual(4, baseResult.GetSecret());
            Assert.AreEqual(5, baseResult.Visible);
        }
    }
}
