using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    // Bases dispatch the subtypes of their assembly through generated code (the hierarchy's root holds the dispatch;
    // classes below it use the root's).

    [NinoType(format: NinoFormat.VersionTolerant)]
    public abstract class DispatchRoot
    {
        public int A;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class DispatchMid : DispatchRoot
    {
        public string B;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchLeaf : DispatchMid
    {
        public long C;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchSibling : DispatchRoot
    {
        public double D;
    }

    /// <summary>A subtype holding a list of the base: nested bodies, deep enough to take the deferred path.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchHolder : DispatchRoot
    {
        public List<DispatchRoot> Kids;
    }

    [TestClass]
    public class DispatchTests
    {
        [TestMethod]
        public void ElementLoopWritesWhatEachElementWritesAlone()
        {
            DispatchRoot[] values =
            {
                new DispatchSibling { A = 1, D = 2.5 }, null, new DispatchLeaf { A = 3, B = "leaf", C = 4 },
                new DispatchMid { A = 5, B = "mid" }, new DispatchHolder { A = 6, Kids = new List<DispatchRoot> { null } },
            };
            var expected = new List<byte>(BitConverter.GetBytes(values.Length));
            foreach (var value in values)
                expected.AddRange(NinoSerializer.Serialize(value));

            CollectionAssert.AreEqual(expected.ToArray(), NinoSerializer.Serialize(values));
            CollectionAssert.AreEqual(expected.ToArray(), NinoSerializer.Serialize(values.ToList()));
        }

        [TestMethod]
        public void DeepElementLoopsRoundTrip()
        {
            // Past NinoDepth.Inline open bodies, the loop writes each element through the dispatch's deep path.
            DispatchRoot node = new DispatchSibling { A = -1, D = 1 };
            for (int i = 0; i < 300; i++)
                node = new DispatchHolder { A = i, Kids = new List<DispatchRoot> { node, null, new DispatchMid { B = "m" + i } } };

            var bytes = NinoSerializer.Serialize(node);
            var back = NinoDeserializer.Deserialize<DispatchRoot>(bytes);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back));
            int depth = 0;
            while (back is DispatchHolder holder)
            {
                Assert.IsNull(holder.Kids[1]);
                Assert.AreEqual("m" + holder.A, ((DispatchMid)holder.Kids[2]).B);
                back = holder.Kids[0];
                depth++;
            }

            Assert.AreEqual(300, depth);
            Assert.AreEqual(1d, ((DispatchSibling)back).D);
        }

        [TestMethod]
        public void EveryLevelRoundTrips()
        {
            DispatchRoot[] values =
            {
                new DispatchMid { A = 1, B = "m" }, new DispatchLeaf { A = 2, B = "l", C = 3 },
                new DispatchSibling { A = 4, D = 5.5 }, null,
            };
            var back = NinoDeserializer.Deserialize<DispatchRoot[]>(NinoSerializer.Serialize(values));
            Assert.AreEqual("m", ((DispatchMid)back[0]).B);
            Assert.AreEqual(3, ((DispatchLeaf)back[1]).C);
            Assert.AreEqual(5.5, ((DispatchSibling)back[2]).D);
            Assert.IsNull(back[3]);

            // Through the middle class: its own type and its subtype.
            DispatchMid mid = new DispatchLeaf { A = 7, B = "x", C = 8 };
            var midBack = NinoDeserializer.Deserialize<DispatchMid>(NinoSerializer.Serialize(mid));
            Assert.AreEqual(8, ((DispatchLeaf)midBack).C);
            midBack = NinoDeserializer.Deserialize<DispatchMid>(NinoSerializer.Serialize(new DispatchMid { B = "own" }));
            Assert.AreEqual(typeof(DispatchMid), midBack.GetType());
        }

        [TestMethod]
        public void SiblingTypeInAMiddleSlotIsRejected()
        {
            var bytes = NinoSerializer.Serialize<DispatchRoot>(new DispatchSibling { D = 1 });
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<DispatchMid>(bytes));
        }

        [TestMethod]
        public void InstanceOfAnotherSubtypeIsReplaced()
        {
            DispatchRoot target = new DispatchSibling { D = 9 };
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<DispatchRoot>(new DispatchLeaf { C = 6 }), ref target);
            Assert.AreEqual(6, ((DispatchLeaf)target).C);

            // The same subtype is reused in place.
            var same = target;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<DispatchRoot>(new DispatchLeaf { C = 10 }), ref target);
            Assert.AreSame(same, target);
            Assert.AreEqual(10, ((DispatchLeaf)target).C);
        }
    }
}
