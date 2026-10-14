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
            writer.AddRange(NinoSerializer.Serialize(1));
            writer.AddRange(NinoSerializer.Serialize("n"));
            writer.AddRange(NinoSerializer.Serialize(new List<int> { 1 }));
            writer.AddRange(NinoSerializer.Serialize(2.5));
            writer.AddRange(NinoSerializer.Serialize(7L));
            var body = NinoSerializer.Serialize(value);
            // Type id (and the length prefix under WEAK_VERSION_TOLERANCE) come first; then the members in order.
            var expected = writer.ToArray();
            CollectionAssert.AreEqual(expected, body[^expected.Length..]);
        }

        [TestMethod]
        public void TolerantChainReadsOlderData()
        {
            var value = new DelegTolerant3 { A = 1, B = "b", C = "c" };
            var result = NinoDeserializer.Deserialize<DelegTolerant3>(NinoSerializer.Serialize(value));
            Assert.AreEqual(1, result.A);
            Assert.AreEqual("b", result.B);
            Assert.AreEqual("c", result.C);

            // Data written as DelegTolerant2 read as DelegTolerant3 with a matching id would end early; emulate with
            // a body that stops after the level-2 members.
            var full = NinoSerializer.Serialize(value);
            var older = NinoSerializer.Serialize(new DelegTolerant2 { A = 5, B = "six" });
            // Rewrite the type id so the shorter body is read as DelegTolerant3.
            System.Array.Copy(full, 0, older, 0, 4);
            var partial = NinoDeserializer.Deserialize<DelegTolerant3>(older);
            Assert.AreEqual(5, partial.A);
            Assert.AreEqual("six", partial.B);
            Assert.IsNull(partial.C);
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
