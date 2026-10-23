using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    public interface ISyncList<T> : IList<T>
    {
        void Set(IList<T> list);
    }

    // https://github.com/JasonXuDeveloper/Nino/issues/166
    [NinoType]
    public partial class SyncList<T> : List<T>, ISyncList<T>
    {
        [NinoIgnore] private string _fieldName;
        [NinoIgnore] private bool _hadInit;

        public SyncList()
        {
        }

        public SyncList(IEnumerable<T> collection) : base(collection)
        {
        }

        public void Init(string fieldName)
        {
            _hadInit = true;
            _fieldName = fieldName;
        }

        public bool HadInit => _hadInit;

        public void Set(IList<T> list)
        {
            Clear();
            AddRange(list);
        }
    }

    public class PlainDerivedList : List<string>
    {
    }

    public class DerivedDictionary<TKey, TValue> : Dictionary<TKey, TValue>
    {
    }

    [NinoType]
    public partial class SyncListHolder
    {
        public SyncList<long> Ids;
        public SyncList<string> Names;
        public PlainDerivedList Plain;
        public DerivedDictionary<int, string> Map;
    }

    [TestClass]
    public class CustomCollectionTests
    {
        [TestMethod]
        public void NinoTypeDerivedListKeepsItems()
        {
            var list = new SyncList<long> { 1, 2, 3 };
            var result = NinoDeserializer.Deserialize<SyncList<long>>(NinoSerializer.Serialize(list));
            CollectionAssert.AreEqual(list, result);
        }

        [TestMethod]
        public void DerivedCollectionsAsMembers()
        {
            var holder = new SyncListHolder
            {
                Ids = new SyncList<long> { 1, 2, 3 },
                Names = new SyncList<string> { "a", null, "c" },
                Plain = new PlainDerivedList { "x", "y" },
                Map = new DerivedDictionary<int, string> { [1] = "one", [2] = "two" },
            };
            var result = NinoDeserializer.Deserialize<SyncListHolder>(NinoSerializer.Serialize(holder));
            CollectionAssert.AreEqual(holder.Ids, result.Ids);
            CollectionAssert.AreEqual(holder.Names, result.Names);
            CollectionAssert.AreEqual(holder.Plain, result.Plain);
            CollectionAssert.AreEquivalent(holder.Map, result.Map);
            Assert.IsInstanceOfType(result.Ids, typeof(SyncList<long>));
        }

        [TestMethod]
        public void ReuseDeserializationReusesDerivedList()
        {
            var holder = new SyncListHolder { Ids = new SyncList<long> { 4, 5 } };
            var bytes = NinoSerializer.Serialize(holder);
            var existing = new SyncListHolder { Ids = new SyncList<long> { 9, 9, 9 } };
            existing.Ids.Init("ids");
            var ids = existing.Ids;
            NinoDeserializer.Deserialize(bytes, ref existing);
            Assert.AreSame(ids, existing.Ids);
            Assert.IsTrue(existing.Ids.HadInit);
            CollectionAssert.AreEqual(new long[] { 4, 5 }, existing.Ids);
        }
    }
}

namespace Nino.UnitTests
{
    [NinoType]
    public class JaggedArrays
    {
        public int[][] Jagged;
        public int[][,] JaggedOfMulti;
        public int[,][] MultiOfJagged;
        public (int, string)[] Tuples;
        public System.Collections.Generic.Dictionary<string, (int Id, float[] Values)> Named;
    }

    [TestClass]
    public class JaggedArrayTests
    {
        [TestMethod]
        public void JaggedAndMultiDimensionalCombinations()
        {
            var value = new JaggedArrays
            {
                Jagged = new[] { new[] { 1, 2 }, null, new int[0] },
                JaggedOfMulti = new[] { new int[,] { { 1, 2 }, { 3, 4 } }, null },
                MultiOfJagged = new int[,][] { { new[] { 5 }, null }, { new int[0], new[] { 6, 7 } } },
                Tuples = new[] { (1, "a"), (2, null) },
                Named = new System.Collections.Generic.Dictionary<string, (int, float[])> { ["x"] = (3, new[] { 1f }) },
            };
            var result = NinoDeserializer.Deserialize<JaggedArrays>(NinoSerializer.Serialize(value));
            CollectionAssert.AreEqual(value.Jagged[0], result.Jagged[0]);
            Assert.IsNull(result.Jagged[1]);
            Assert.AreEqual(0, result.Jagged[2].Length);
            CollectionAssert.AreEqual(value.JaggedOfMulti[0], result.JaggedOfMulti[0]);
            Assert.IsNull(result.JaggedOfMulti[1]);
            Assert.AreEqual(2, result.MultiOfJagged.GetLength(0));
            CollectionAssert.AreEqual(new[] { 6, 7 }, result.MultiOfJagged[1, 1]);
            Assert.IsNull(result.MultiOfJagged[0, 1]);
            CollectionAssert.AreEqual(value.Tuples, result.Tuples);
            Assert.AreEqual(3, result.Named["x"].Item1);
            CollectionAssert.AreEqual(new[] { 1f }, result.Named["x"].Item2);
        }
    }
}
