using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Paths that skip the general code for some element and collection types: [NinoType] struct elements through their
/// generated formatter, SortedDictionary, SortedList and ConcurrentDictionary through their own calls, and small
/// blocks copied inline. The bytes and the results must be those of the general path.
/// Each family is tested in its own class (ArrayFastPathTests, BlockFastPathTests, SortedListFastPathTests, ...),
/// so they run in parallel; this one checks that the runtime layouts behind them are recognized.
/// </summary>
[TestClass]
public class CollectionFastPathTests
{
    private sealed class Counting : System.Collections.ObjectModel.Collection<int>
    {
        public int Inserted;

        protected override void InsertItem(int index, int item)
        {
            Inserted++;
            base.InsertItem(index, item);
        }
    }

    /// <summary>
    /// A <c>Collection&lt;T&gt;</c> is written and read through the list it wraps, as a list is: the bytes of the
    /// list, a new one made around the list read, an existing one refilled in place (its list kept); one around
    /// another <c>IList</c> and a subclass go through their own members, which a subclass may override.
    /// </summary>
    [TestMethod]
    public void CollectionGoesThroughItsList()
    {
        void Each<T>(T[] items)
        {
            var list = items.ToList();
            var collection = new System.Collections.ObjectModel.Collection<T>(list);
            byte[] bytes = NinoSerializer.Serialize(collection);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(list), bytes, typeof(T).Name);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(new System.Collections.ObjectModel.Collection<T>(items)),
                "around an array");
            CollectionAssert.AreEqual(bytes,
                NinoSerializer.Serialize(new System.Collections.ObjectModel.ObservableCollection<T>(items)), "a subclass");

            CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<System.Collections.ObjectModel.Collection<T>>(bytes));
            // Into an existing one, longer and shorter: the same instance around the same list.
            foreach (int extra in new[] { 3, -1 })
            {
                var inner = items.Concat(items).Take(Math.Max(0, items.Length + extra)).ToList();
                var into = new System.Collections.ObjectModel.Collection<T>(inner);
                var same = into;
                NinoDeserializer.Deserialize(bytes, ref into);
                Assert.AreSame(same, into);
                CollectionAssert.AreEqual(items, into);
                CollectionAssert.AreEqual(items, inner);
            }

            // Around a list that is no List<T>: through Add.
            var linked = new System.Collections.ObjectModel.Collection<T>(new ListWrapper<T>());
            NinoDeserializer.Deserialize(bytes, ref linked);
            CollectionAssert.AreEqual(items, linked);
        }

        Each(Enumerable.Range(0, 300).ToArray());
        Each(Enumerable.Range(0, 300).Select(i => i % 4 == 0 ? (int?)null : i).ToArray());
        Each(Enumerable.Range(0, 70).Select(i => i % 5 == 0 ? null : "s" + i).ToArray());
        Each(Array.Empty<long>());

        System.Collections.ObjectModel.Collection<int> none = null;
        byte[] nothing = NinoSerializer.Serialize(none);
        Assert.IsNull(NinoDeserializer.Deserialize<System.Collections.ObjectModel.Collection<int>>(nothing));
        var existing = new System.Collections.ObjectModel.Collection<int> { 1 };
        NinoDeserializer.Deserialize(nothing, ref existing);
        Assert.IsNull(existing);

        // A subclass is filled through its own InsertItem.
        byte[] five = NinoSerializer.Serialize(new List<int> { 1, 2, 3, 4, 5 });
        var counting = NinoDeserializer.Deserialize<Counting>(five);
        Assert.AreEqual(5, counting.Inserted);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, counting);
    }

    /// <summary>An IList that is no List&lt;T&gt;, for a Collection around one.</summary>
    private sealed class ListWrapper<T> : IList<T>
    {
        private readonly List<T> _items = new();
        public T this[int index] { get => _items[index]; set => _items[index] = value; }
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public void Add(T item) => _items.Add(item);
        public void Clear() => _items.Clear();
        public bool Contains(T item) => _items.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
        public int IndexOf(T item) => _items.IndexOf(item);
        public void Insert(int index, T item) => _items.Insert(index, item);
        public bool Remove(T item) => _items.Remove(item);
        public void RemoveAt(int index) => _items.RemoveAt(index);
    }

    /// <summary>
    /// A <c>HashSet</c> of raw <c>T?</c> is written out of its entries and read into them where a slot is its value's
    /// memory: the bytes of the array of its elements in enumeration order, with stale memory in a slot's padding
    /// and absent value cleared; a set with a removed element, and slots that do not tile a vector (12 and 24
    /// bytes), go through a copy and write the same bytes.
    /// </summary>
    [TestMethod]
    public void SetsOfNullablesGoThroughTheirEntries()
    {
        void Each<T>(Func<int, T> make) where T : struct
        {
            foreach (int count in new[] { 1, 2, 9, 70, 1000 })
            {
                var set = new HashSet<T?>(Enumerable.Range(0, count).Select(i => i % 7 == 3 ? (T?)null : make(i)));
                byte[] bytes = NinoSerializer.Serialize(set);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(set.ToArray()), bytes, $"{typeof(T).Name} {count}");
                var read = NinoDeserializer.Deserialize<HashSet<T?>>(bytes);
                Assert.IsTrue(set.SetEquals(read) && read.Count == set.Count);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(read), "read back and written");
                var into = new HashSet<T?> { make(-5) };
                NinoDeserializer.Deserialize(bytes, ref into);
                Assert.IsTrue(set.SetEquals(into));

                // A free entry: no longer its first Count entries.
                set.Remove(set.First());
                CollectionAssert.AreEqual(NinoSerializer.Serialize(set.ToArray()), NinoSerializer.Serialize(set));
            }
        }

        Each(i => i * 3 - 1);
        Each(i => (long)i << 33);
        Each(i => (short)i);
        Each(i => (byte)i);
        Each(i => i + 0.5m);
        Each(i => (i, (long)i));
        Each(i => new DateTime(2020, 1, 1).AddHours(i));

        // A repeated element and a presence byte other than 0 or 1 are invalid data.
        byte[] twice = NinoSerializer.Serialize(new int?[] { 4, 4 });
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<HashSet<int?>>(twice));
        byte[] bad = NinoSerializer.Serialize(new int?[] { 4, 5 });
        bad[4] = 2;
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<HashSet<int?>>(bad));
    }

    [TestMethod]
    public void CollectionLayoutsAreRecognized()
    {
        // Each collection Nino reads and writes through its fields is checked against the runtime's layout at
        // startup, and its public API is used when the check fails. On .NET every check must pass, or the fast
        // paths are silently off.
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.List, "List<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.Stack, "Stack<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.Collection, "Collection<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.Queue, "Queue<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.SortedList, "SortedList<TKey, TValue>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.SortedSet, "SortedSet<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.SortedDictionary, "SortedDictionary<TKey, TValue>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.HashSet, "HashSet<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.Dictionary, "Dictionary<TKey, TValue>");
    }

#if NET9_0_OR_GREATER && !NINO_CORE_NETSTANDARD
    [TestMethod]
    public void LayoutsByNameMatchTheMirrors()
    {
        // The net10.0 build finds List<T>, Stack<T>, Queue<T>, SortedList<TKey, TValue> and LinkedList<T> by their
        // fields' names (UnsafeAccessor) first; every other build by the mirror classes. Both must give the same
        // offsets, so both read and write the same bytes (the netstandard test runs take the mirrors alone).
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.ByNameMatchesMirrors());
    }
#endif
}
