using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner
{
    [NinoType(trackReferences: true)]
    public class RefNode
    {
        public int Id;
        public RefNode Self;
        public RefNode Other;
        public List<RefNode> Children;
        public Dictionary<int, RefNode> Map;
        public RefNode[] Array;
    }

    [NinoType(trackReferences: true)]
    public abstract class RefAnimal
    {
        public string Name;
        public RefAnimal Friend;
    }

    [NinoType]
    public class RefDog : RefAnimal
    {
        public int Bark;
    }

    [NinoType]
    public sealed class RefCat : RefAnimal
    {
        public RefDog Enemy;
    }

    [NinoType(trackReferences: true)]
    public sealed class RefSealedLeaf
    {
        public string Value;
    }

    [NinoType]
    public class RefZoo
    {
        public RefAnimal A;
        public RefAnimal B;
        public List<RefAnimal> All;
        public object Boxed;
        public RefSealedLeaf Leaf1;
        public RefSealedLeaf Leaf2;
        public List<RefSealedLeaf> Leaves;
    }

    [NinoType(trackReferences: true)]
    public class RefGeneric<T>
    {
        public T Value;
        public RefGeneric<T> Next;
    }

    [NinoType]
    public interface IRefPeer
    {
    }

    // Tracked, reached through declared types that are not tracked (object, an interface).
    [NinoType(trackReferences: true)]
    public class RefPeerNode : IRefPeer
    {
        public int Id;
        public object Payload;
        public IRefPeer Peer;
    }

    [TestClass]
    public class CornerReferenceTests
    {
        [TestMethod]
        public void SelfReferenceAndTwoNodeCycle()
        {
            var a = new RefNode { Id = 1 };
            a.Self = a;
            var b = new RefNode { Id = 2, Other = a };
            a.Other = b;

            var back = Rt.RoundTrip(a);
            Assert.AreSame(back, back.Self);
            Assert.AreEqual(2, back.Other.Id);
            Assert.AreSame(back, back.Other.Other);
            Assert.IsNull(back.Other.Self);
        }

        [TestMethod]
        public void SharedReferencesInsideCollections()
        {
            var shared = new RefNode { Id = 7 };
            var root = new RefNode
            {
                Id = 1,
                Children = new List<RefNode> { shared, shared, null, shared },
                Map = new Dictionary<int, RefNode> { [1] = shared, [2] = null },
                Array = new[] { shared },
                Other = shared,
            };
            shared.Children = new List<RefNode> { root };

            var back = Rt.RoundTrip(root);
            var s = back.Other;
            Assert.AreEqual(7, s.Id);
            Assert.AreSame(s, back.Children[0]);
            Assert.AreSame(s, back.Children[1]);
            Assert.IsNull(back.Children[2]);
            Assert.AreSame(s, back.Children[3]);
            Assert.AreSame(s, back.Map[1]);
            Assert.IsNull(back.Map[2]);
            Assert.AreSame(s, back.Array[0]);
            Assert.AreSame(back, s.Children[0]);

            // A top-level collection of tracked objects shares references too.
            var list = Rt.RoundTrip(new List<RefNode> { shared, shared });
            Assert.AreSame(list[0], list[1]);
        }

        [TestMethod]
        public void ReferencesAcrossPolymorphicMembersAndUntrackedHolders()
        {
            var dog = new RefDog { Name = "d", Bark = 3 };
            var cat = new RefCat { Name = "c", Enemy = dog, Friend = dog };
            dog.Friend = cat;
            var leaf = new RefSealedLeaf { Value = "v" };
            var zoo = new RefZoo
            {
                A = dog, B = cat, All = new List<RefAnimal> { cat, dog, cat }, Boxed = dog,
                Leaf1 = leaf, Leaf2 = leaf, Leaves = new List<RefSealedLeaf> { leaf, null },
            };

            var back = Rt.RoundTrip(zoo);
            var d = (RefDog)back.A;
            var c = (RefCat)back.B;
            Assert.AreSame(c, d.Friend);
            Assert.AreSame(d, c.Enemy);
            Assert.AreSame(d, c.Friend);
            Assert.AreSame(c, back.All[0]);
            Assert.AreSame(d, back.All[1]);
            Assert.AreSame(c, back.All[2]);
            Assert.AreSame(back.Leaf1, back.Leaf2);
            Assert.AreSame(back.Leaf1, back.Leaves[0]);
            Assert.AreEqual("v", back.Leaf1.Value);
        }

        [TestMethod]
        public void TrackedInstanceInAnObjectSlotStaysShared()
        {
            var dog = new RefDog { Name = "d" };
            var back = Rt.RoundTrip(new RefZoo { A = dog, Boxed = dog });
            Assert.AreSame(back.A, back.Boxed);

            var list = Rt.RoundTrip(new List<object> { dog, dog });
            Assert.AreSame(list[0], list[1]);
        }

        [TestMethod]
        public void CyclesThroughObjectAndInterfaceSlots()
        {
            var a = new RefPeerNode { Id = 1 };
            var b = new RefPeerNode { Id = 2, Peer = a, Payload = a };
            a.Payload = a;
            a.Peer = b;

            // The same graph from every declared type: the root's own header differs, the identities do not.
            foreach (var back in new[]
                     {
                         Rt.RoundTrip(a), (RefPeerNode)Rt.RoundTrip<object>(a), (RefPeerNode)Rt.RoundTrip<IRefPeer>(a),
                     })
            {
                Assert.AreEqual(1, back.Id);
                Assert.AreSame(back, back.Payload);
                var peer = (RefPeerNode)back.Peer;
                Assert.AreEqual(2, peer.Id);
                Assert.AreSame(back, peer.Peer);
                Assert.AreSame(back, peer.Payload);
            }

            // A back reference through an object slot is the type id and the index.
            var once = NinoSerializer.Serialize(new List<object> { b });
            var twice = NinoSerializer.Serialize(new List<object> { b, b });
            Assert.AreEqual(once.Length + 8, twice.Length);

            // Tracked and untracked declared types share one table.
            var dog = new RefDog { Name = "d" };
            var zoo = Rt.RoundTrip(new RefZoo { Boxed = dog, A = dog, All = new List<RefAnimal> { dog } });
            Assert.AreSame(zoo.Boxed, zoo.A);
            Assert.AreSame(zoo.A, zoo.All[0]);

            // A tracked generic type in an object slot.
            var g = new RefGeneric<int> { Value = 3 };
            g.Next = g;
            var objects = Rt.RoundTrip(new List<object> { g, g });
            Assert.AreSame(objects[0], objects[1]);
            Assert.AreSame(objects[0], ((RefGeneric<int>)objects[0]).Next);
        }

        [TestMethod]
        public void BackReferenceOfAnotherTypeInAnObjectSlotIsRejected()
        {
            var dog = new RefDog { Name = "d" };
            var bytes = NinoSerializer.Serialize(new List<object> { dog, dog });
            // The second element is a back reference: RefDog's type id and index 1. Claim another tracked type.
            int second = bytes.Length - 8;
            Assert.AreEqual(NinoTypeId.Of(typeof(RefDog)), System.BitConverter.ToInt32(bytes, second));
            Assert.AreEqual(1, System.BitConverter.ToInt32(bytes, second + 4));
            System.BitConverter.GetBytes(NinoTypeId.Of(typeof(RefPeerNode))).CopyTo(bytes, second);
            Assert.ThrowsExactly<System.InvalidOperationException>(() => NinoDeserializer.Deserialize<List<object>>(bytes));
        }

        [TestMethod]
        public void TrackedGenericTypeCycle()
        {
            var a = new RefGeneric<string> { Value = "a" };
            var b = new RefGeneric<string> { Value = "b", Next = a };
            a.Next = b;
            var back = Rt.RoundTrip(a);
            Assert.AreEqual("b", back.Next.Value);
            Assert.AreSame(back, back.Next.Next);
        }

        [TestMethod]
        public void ReuseDeserializationWithTrackingAndCycles()
        {
            var shared = new RefNode { Id = 5 };
            var source = new RefNode { Id = 1, Other = shared, Children = new List<RefNode> { shared, shared } };
            source.Self = source;
            var bytes = NinoSerializer.Serialize(source);

            var existingOther = new RefNode();
            var existingChildren = new List<RefNode> { new RefNode(), new RefNode(), new RefNode() };
            var target = new RefNode { Other = existingOther, Children = existingChildren };
            var before = target;
            NinoDeserializer.Deserialize(bytes, ref target);

            Assert.AreSame(before, target);
            Assert.AreSame(target, target.Self);
            Assert.AreSame(existingOther, target.Other, "new object read into the existing instance");
            Assert.AreEqual(5, target.Other.Id);
            Assert.AreSame(existingChildren, target.Children);
            Assert.AreEqual(2, target.Children.Count);
            Assert.IsTrue(target.Children.All(c => ReferenceEquals(c, target.Other)));

            // Deserializing the same data again keeps the graph stable.
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreSame(before, target);
            Assert.AreSame(target, target.Self);
            Assert.IsTrue(target.Children.All(c => ReferenceEquals(c, target.Other)));
        }

        [TestMethod]
        public void BackReferencesAreNotRecycled()
        {
            var recycled = new List<RefNode>();
            NinoSettings.SetRecycler<RefNode>(recycled.Add);
            try
            {
                var shared = new RefNode { Id = 9 };
                var bytes = NinoSerializer.Serialize(new RefNode { Other = shared, Self = shared });
                var a = new RefNode();
                var b = new RefNode();
                var target = new RefNode { Other = a, Self = b };
                NinoDeserializer.Deserialize(bytes, ref target);
                // Self comes first in member order: the new object is read into b; Other is then a back reference.
                Assert.AreSame(b, target.Self);
                Assert.AreSame(b, target.Other);
                Assert.AreEqual(9, b.Id);
                // a was replaced by a back reference: per the design it is not recycled (it may be shared).
                Assert.AreEqual(0, recycled.Count);
            }
            finally
            {
                NinoSettings.SetRecycler<RefNode>(null);
            }
        }

        [TestMethod]
        public void SeparateCallsDoNotShareReferences()
        {
            var shared = new RefNode { Id = 1 };
            var first = Rt.RoundTrip(new RefNode { Other = shared });
            var second = Rt.RoundTrip(new RefNode { Other = shared });
            Assert.AreNotSame(first.Other, second.Other);
            // Same bytes for the same graph across calls (no leftover state in the writer).
            CollectionAssert.AreEqual(NinoSerializer.Serialize(new RefNode { Other = shared, Self = shared }),
                NinoSerializer.Serialize(new RefNode { Other = shared, Self = shared }));
        }

        [TestMethod]
        public void LargeChainDoesNotOverflow()
        {
            // Long linked chains are recursive in Nino; keep a depth a game save could have.
            var head = new RefNode { Id = 0 };
            var node = head;
            for (int i = 1; i < 500; i++)
            {
                node.Other = new RefNode { Id = i, Self = head };
                node = node.Other;
            }

            var back = Rt.RoundTrip(head);
            int count = 0;
            for (var n = back; n != null; n = n.Other)
            {
                Assert.AreEqual(count, n.Id);
                if (count > 0)
                    Assert.AreSame(back, n.Self);
                count++;
            }

            Assert.AreEqual(500, count);
        }
    }
}
