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

    [NinoType(versionTolerant: true)]
    public class DelegTolerant1
    {
        public int A;
    }

    [NinoType(versionTolerant: true)]
    public class DelegTolerant2 : DelegTolerant1
    {
        public string B;
    }

    [NinoType(versionTolerant: true)]
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
            NinoDeserializer.DeserializeRef(ref target, bytes);
            Assert.AreEqual("n", target.Name);
            Assert.AreEqual(7, target.E);
        }

        [TestMethod]
        public void MultiLevelBytesMatchInlineLayout()
        {
            var value = new DelegLevel3 { A = 1, Name = "n", Items = new List<int> { 1 }, D = 2.5, E = 7 };
            var writer = new List<byte>();
            // Under WEAK_VERSION_TOLERANCE each level is prefixed with its length.
            void Level(params byte[][] members)
            {
#if WEAK_VERSION_TOLERANCE
                int length = 0;
                foreach (var m in members)
                    length += m.Length;
                writer.AddRange(System.BitConverter.GetBytes(length));
#endif
                foreach (var m in members)
                    writer.AddRange(m);
            }

            Level(NinoSerializer.Serialize(1), NinoSerializer.Serialize("n"));
            Level(NinoSerializer.Serialize(new List<int> { 1 }));
            Level(NinoSerializer.Serialize(2.5), NinoSerializer.Serialize(7L));
            var body = NinoSerializer.Serialize(value);
            // The type id comes first; then the members in order.
            var expected = writer.ToArray();
            CollectionAssert.AreEqual(expected, body[^expected.Length..]);
        }

        // Each level of a tolerant hierarchy is delimited on its own: any level may gain or lose trailing members.
        // A level is given as the serialized bytes of its members.
        private static byte[] TolerantData(params byte[][][] levels)
        {
            var data = new List<byte>(System.BitConverter.GetBytes(NinoTypeId.Of(typeof(DelegTolerant3))));
            foreach (var level in levels)
            {
                int length = 0;
                foreach (var m in level)
                    length += m.Length;
                data.AddRange(System.BitConverter.GetBytes(length));
                foreach (var m in level)
                    data.AddRange(m);
            }

            return data.ToArray();
        }

        private static byte[][] Members(params byte[][] members) => members;

        [TestMethod]
        public void TolerantChainReadsOlderData()
        {
            var value = new DelegTolerant3 { A = 1, B = "b", C = "c" };
            var result = NinoDeserializer.Deserialize<DelegTolerant3>(NinoSerializer.Serialize(value));
            Assert.AreEqual(1, result.A);
            Assert.AreEqual("b", result.B);
            Assert.AreEqual("c", result.C);

            // Written before C existed: the leaf level is empty.
            var older = TolerantData(
                Members(NinoSerializer.Serialize(5)),
                Members(NinoSerializer.Serialize("six")),
                Members());
            var partial = NinoDeserializer.Deserialize<DelegTolerant3>(older);
            Assert.AreEqual(5, partial.A);
            Assert.AreEqual("six", partial.B);
            Assert.IsNull(partial.C);

            // Written by a newer version whose middle level gained a member: it is skipped, C still lines up.
            var newer = TolerantData(
                Members(NinoSerializer.Serialize(5)),
                Members(NinoSerializer.Serialize("six"), NinoSerializer.Serialize(123L)),
                Members(NinoSerializer.Serialize("seven")));
            var skipped = NinoDeserializer.Deserialize<DelegTolerant3>(newer);
            Assert.AreEqual(5, skipped.A);
            Assert.AreEqual("six", skipped.B);
            Assert.AreEqual("seven", skipped.C);

            // In place: a member missing from older data keeps its current value.
            var target = new DelegTolerant3 { C = "kept" };
            NinoDeserializer.DeserializeRef(ref target, older);
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
