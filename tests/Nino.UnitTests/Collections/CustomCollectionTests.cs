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
