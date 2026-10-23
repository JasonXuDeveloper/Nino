using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Corner;

#nullable disable
namespace Nino.UnitTests.Corner
{
    // Two versions of a nested schema (this assembly defines WEAK_VERSION_TOLERANCE, so every type is tolerant).
    // Sealed classes carry no type id, so V1 data can be read as V2 and back.
    [NinoType]
    public sealed class VerOuterV1
    {
        public int A;
        public VerMidV1 Mid;
        public List<VerMidV1> Mids;
        public string Tail;
    }

    [NinoType]
    public sealed class VerOuterV2
    {
        public int A;
        public VerMidV2 Mid;
        public List<VerMidV2> Mids;
        public string Tail;
        public string AddedOuter = "initial";
        public List<int> AddedOuterList;
    }

    [NinoType]
    public sealed class VerMidV1
    {
        public int M;
        public VerLeafV1 Leaf;
    }

    [NinoType]
    public sealed class VerMidV2
    {
        public int M;
        public VerLeafV2 Leaf;
        public long AddedMid;
    }

    [NinoType]
    public struct VerLeafV1
    {
        public string L;
    }

    [NinoType]
    public struct VerLeafV2
    {
        public string L;
        public double AddedLeaf;
        public int[] AddedArray;
    }

    // Appending to a base class. A tolerant object is one region with every level's members root first, so a member
    // appended to a base lands before the derived members and shifts them: that is not a compatible change.
    // Same-typed members keep the outcome deterministic.
    [NinoType]
    public class VerBaseV1
    {
        public int A;
    }

    [NinoType]
    public sealed class VerSealedLeafV1 : VerBaseV1
    {
        public int B;
    }

    [NinoType]
    public class VerBaseV2
    {
        public int A;
        public int AddedInBase;
    }

    [NinoType]
    public sealed class VerSealedLeafV2 : VerBaseV2
    {
        public int B;
    }

    // Polymorphic leaf that gains members (read with the type id rewritten, as a renamed build would see it).
    [NinoType]
    public abstract class VerPolyBase
    {
        public int Id;
    }

    [NinoType]
    public class VerPolyLeafV1 : VerPolyBase
    {
        public string Name;
    }

    [NinoType]
    public class VerPolyLeafV2 : VerPolyBase
    {
        public string Name;
        public List<string> Added;
    }

    // Keyed hierarchy where the base gains a member and the leaf reorders.
    [NinoType(format: NinoFormat.Keyed)]
    public class KeyedVerBaseV1
    {
        public int A;
        public string B;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedVerLeafV1 : KeyedVerBaseV1
    {
        public long C;
        public List<int> D;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public class KeyedVerBaseV2
    {
        public string B;
        public double NewInBase = 1.5;
        public int A;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedVerLeafV2 : KeyedVerBaseV2
    {
        public List<int> D;
        public long C;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedOuter
    {
        public KeyedVerLeafV1 Inner;
        public int After;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedOuterV2
    {
        public int After;
        public KeyedVerLeafV2 Inner;
    }

    [NinoType]
    public sealed class WeakTwinOfPlainSealed
    {
        public int A;
        public string B;
    }

    [TestClass]
    public class CornerVersioningTests
    {
        private static TTo Convert<TFrom, TTo>(TFrom value) =>
            NinoDeserializer.Deserialize<TTo>(NinoSerializer.Serialize(value));

        private static VerOuterV1 OuterV1() => new VerOuterV1
        {
            A = 1,
            Mid = new VerMidV1 { M = 2, Leaf = new VerLeafV1 { L = "leaf" } },
            Mids = new List<VerMidV1> { new VerMidV1 { M = 3 }, null, new VerMidV1 { M = 4, Leaf = new VerLeafV1 { L = "x" } } },
            Tail = "tail",
        };

        [TestMethod]
        public void OldDataReadByNewTypesAtEveryDepth()
        {
            var v2 = Convert<VerOuterV1, VerOuterV2>(OuterV1());
            Assert.AreEqual(1, v2.A);
            Assert.AreEqual(2, v2.Mid.M);
            Assert.AreEqual("leaf", v2.Mid.Leaf.L);
            Assert.AreEqual(0, v2.Mid.Leaf.AddedLeaf);
            Assert.IsNull(v2.Mid.Leaf.AddedArray);
            Assert.AreEqual(0, v2.Mid.AddedMid);
            Assert.AreEqual(3, v2.Mids.Count);
            Assert.AreEqual(3, v2.Mids[0].M);
            Assert.IsNull(v2.Mids[1]);
            Assert.AreEqual("x", v2.Mids[2].Leaf.L);
            Assert.AreEqual("tail", v2.Tail);
            // A member missing from old data keeps what construction gave it.
            Assert.AreEqual("initial", v2.AddedOuter);
            Assert.IsNull(v2.AddedOuterList);
        }

        [TestMethod]
        public void OldDataIntoAnExistingNewInstance()
        {
            var target = new VerOuterV2
            {
                AddedOuter = "kept?", AddedOuterList = new List<int> { 1 },
                Mid = new VerMidV2 { AddedMid = 77 },
            };
            var mid = target.Mid;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(OuterV1()), ref target);
            Assert.AreSame(mid, target.Mid);
            Assert.AreEqual(2, target.Mid.M);
            Assert.AreEqual("tail", target.Tail);
            // Pin the documented rule for members missing from the data, in place.
            Assert.AreEqual("kept?", target.AddedOuter);
            Assert.AreEqual(77, target.Mid.AddedMid);
        }

        [TestMethod]
        public void NewDataReadByOldTypesSkipsTrailingMembers()
        {
            var v2 = new VerOuterV2
            {
                A = 1,
                Mid = new VerMidV2 { M = 2, AddedMid = 5, Leaf = new VerLeafV2 { L = "l", AddedLeaf = 1.5, AddedArray = new[] { 1, 2 } } },
                Mids = new List<VerMidV2> { new VerMidV2 { M = 3, AddedMid = 9 } },
                Tail = "tail", AddedOuter = "new", AddedOuterList = new List<int> { 1, 2, 3 },
            };
            var v1 = Convert<VerOuterV2, VerOuterV1>(v2);
            Assert.AreEqual(1, v1.A);
            Assert.AreEqual(2, v1.Mid.M);
            Assert.AreEqual("l", v1.Mid.Leaf.L);
            Assert.AreEqual(3, v1.Mids.Single().M);
            Assert.AreEqual("tail", v1.Tail);

            // A list of new-version elements read as old.
            var list = Convert<List<VerMidV2>, List<VerMidV1>>(v2.Mids);
            Assert.AreEqual(3, list.Single().M);
        }

        [TestMethod]
        public void PolymorphicLeafGainsMembers()
        {
            var old = NinoSerializer.Serialize<VerPolyBase>(new VerPolyLeafV1 { Id = 1, Name = "n" });
            BitConverter.GetBytes(NinoTypeId.Of(typeof(VerPolyLeafV2))).CopyTo(old, 0);
            var read = (VerPolyLeafV2)NinoDeserializer.Deserialize<VerPolyBase>(old);
            Assert.AreEqual(1, read.Id);
            Assert.AreEqual("n", read.Name);
            Assert.IsNull(read.Added);

            var newer = NinoSerializer.Serialize<VerPolyBase>(new VerPolyLeafV2 { Id = 2, Name = "m", Added = new List<string> { "a" } });
            BitConverter.GetBytes(NinoTypeId.Of(typeof(VerPolyLeafV1))).CopyTo(newer, 0);
            var back = (VerPolyLeafV1)NinoDeserializer.Deserialize<VerPolyBase>(newer);
            Assert.AreEqual(2, back.Id);
            Assert.AreEqual("m", back.Name);
        }

        [TestMethod]
        public void MemberAppendedToABaseClass()
        {
            // Only appending to the most derived type is tolerated. With one region per object the bytes are the
            // members root first (A, [AddedInBase], B), so the member appended to the base takes B's place.
            var v1Bytes = NinoSerializer.Serialize(new VerSealedLeafV1 { A = 1, B = 7 });
            var v2 = NinoDeserializer.Deserialize<VerSealedLeafV2>(v1Bytes);
            Assert.AreEqual(1, v2.A);
            Assert.AreEqual(7, v2.AddedInBase); // B's bytes
            Assert.AreEqual(0, v2.B); // the region ended

            var v2Bytes = NinoSerializer.Serialize(new VerSealedLeafV2 { A = 1, AddedInBase = 2, B = 7 });
            var v1 = NinoDeserializer.Deserialize<VerSealedLeafV1>(v2Bytes);
            Assert.AreEqual(1, v1.A);
            Assert.AreEqual(2, v1.B); // AddedInBase's bytes; the trailing B is skipped
#if WEAK_VERSION_TOLERANCE
            // A sealed class: the not-null flag, then one length for the whole body, then the members root first.
            // (Before: 01 | 08000000 A AddedInBase | 04000000 B, one length per level.)
            var expected = new List<byte> { 1 };
            expected.AddRange(BitConverter.GetBytes(12));
            expected.AddRange(BitConverter.GetBytes(1));
            expected.AddRange(BitConverter.GetBytes(2));
            expected.AddRange(BitConverter.GetBytes(7));
            CollectionAssert.AreEqual(expected.ToArray(), v2Bytes);
#endif
        }

        [TestMethod]
        public void KeyedHierarchyBaseGainsMemberAndMembersReorder()
        {
            var v1 = new KeyedVerLeafV1 { A = 1, B = "b", C = 3, D = new List<int> { 4 } };
            var v2 = Convert<KeyedVerLeafV1, KeyedVerLeafV2>(v1);
            Assert.AreEqual(1, v2.A);
            Assert.AreEqual("b", v2.B);
            Assert.AreEqual(3, v2.C);
            CollectionAssert.AreEqual(v1.D, v2.D);
            Assert.AreEqual(1.5, v2.NewInBase);

            var back = Convert<KeyedVerLeafV2, KeyedVerLeafV1>(new KeyedVerLeafV2 { A = 5, B = "x", C = 6, NewInBase = 9 });
            Assert.AreEqual(5, back.A);
            Assert.AreEqual("x", back.B);
            Assert.AreEqual(6, back.C);
            Assert.IsNull(back.D);

            // Nested keyed objects, reordered at both levels.
            var outer = Convert<KeyedOuter, KeyedOuterV2>(new KeyedOuter { Inner = v1, After = 8 });
            Assert.AreEqual(8, outer.After);
            Assert.AreEqual(3, outer.Inner.C);

            // In place: members missing from the data keep their current value.
            var target = new KeyedVerLeafV2 { NewInBase = 42, D = new List<int> { 1, 2, 3 } };
            var d = target.D;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(v1), ref target);
            Assert.AreEqual(42, target.NewInBase);
            Assert.AreSame(d, target.D);
            CollectionAssert.AreEqual(v1.D, target.D);
        }

        [TestMethod]
        public void WeakVersionToleranceIsPerAssembly()
        {
            // Subset does not define WEAK_VERSION_TOLERANCE: no body length. This assembly does: 4 more bytes.
            var subset = NinoSerializer.Serialize(new PlainSealed { A = 1, B = "b" });
            var weak = NinoSerializer.Serialize(new WeakTwinOfPlainSealed { A = 1, B = "b" });
            Assert.AreEqual(subset.Length + 4, weak.Length);
            Assert.AreEqual(subset.Length - 1, BitConverter.ToInt32(weak, 1));
            CollectionAssert.AreEqual(subset.Skip(1).ToArray(), weak.Skip(5).ToArray());

            // An explicitly tolerant type of that assembly has the length either way.
            var tolerant = NinoSerializer.Serialize(new ExplicitTolerantSealed { A = 1, B = "b" });
            CollectionAssert.AreEqual(weak, tolerant);

            // A non-tolerant type of another assembly nested in a tolerant one of this one.
            var holder = Rt.RoundTrip(new List<PlainSealed> { new PlainSealed { A = 3, B = null } });
            Assert.AreEqual(3, holder[0].A);
            Assert.IsNull(holder[0].B);
        }
    }
}
