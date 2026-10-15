using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    // Bases dispatch the subtypes of their assembly through generated code (the hierarchy's root holds the dispatch;
    // classes below it use the root's).

    [NinoType]
    public abstract class DispatchRoot
    {
        public int A;
    }

    [NinoType]
    public class DispatchMid : DispatchRoot
    {
        public string B;
    }

    [NinoType]
    public sealed class DispatchLeaf : DispatchMid
    {
        public long C;
    }

    [NinoType]
    public sealed class DispatchSibling : DispatchRoot
    {
        public double D;
    }

    [TestClass]
    public class DispatchTests
    {
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
            Assert.ThrowsException<InvalidOperationException>(() => NinoDeserializer.Deserialize<DispatchMid>(bytes));
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
