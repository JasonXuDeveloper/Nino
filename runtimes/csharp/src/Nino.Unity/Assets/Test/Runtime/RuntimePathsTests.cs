using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nino.Core;
using NUnit.Framework;
using UnityEngine;

namespace Test.Runtime
{
    /// <summary>
    /// The paths through the runtime's own collections (Nino.Core's CollectionViews, ObjectTypes.Retype,
    /// SortedSetNodes, ReadLinked, ConcurrentTables): each declares the layouts it knows, checks them at startup against
    /// an instance with known contents and falls back to the public API when none matches. These tests assert that the
    /// fast paths are taken on this runtime (Mono in the editor, IL2CPP in a player), not only that the results are
    /// right. Run in play mode and in an IL2CPP player (unity.yml), and in edit mode through
    /// Test.Editor.Tests.RuntimePathsInEditMode.
    /// </summary>
    public class RuntimePathsTests
    {
        private static object Static(string type, string field)
        {
            var t = typeof(NinoSerializer).Assembly.GetType(type, true);
            var f = t.GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(f, $"{type}.{field}");
            return f.GetValue(null);
        }

        [Test]
        public void FastPathsAreTaken()
        {
            var flags = new List<(string Name, bool On)>
            {
                ("ObjectTypes.Usable", (bool)Static("Nino.Core.Internal.ObjectTypes", "Usable")),
                ("ConcurrentTables.Chains", (bool)Static("Nino.Core.Internal.ConcurrentTables", "Chains")),
                ("ConcurrentTables.ReferenceChains",
                    (bool)Static("Nino.Core.Internal.ConcurrentTables", "ReferenceChains")),
                ("ConcurrentTables.Builds", (bool)Static("Nino.Core.Internal.ConcurrentTables", "Builds")),
                ("SortedSetNodes.Layout", (int)Static("Nino.Core.Internal.SortedSetNodes", "Layout") != 0),
            };
            foreach (var view in new[]
                     {
                         "List", "Stack", "Queue", "SortedList", "SortedSet", "SortedDictionary", "LinkedList",
                         "HashSet", "Dictionary"
                     })
                flags.Add(("CollectionViews." + view, (bool)Static("Nino.Core.Internal.CollectionViews", view)));

            Debug.Log($"Nino runtime paths ({Application.platform}, {RuntimeName()}): " +
                      string.Join(", ", flags.Select(f => $"{f.Name}={(f.On ? "fast" : "public API")}")));
            CollectionAssert.IsEmpty(flags.Where(f => !f.On).Select(f => f.Name).ToArray(),
                "paths that fell back to the public API");
        }

        private static string RuntimeName()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#else
            return "Mono";
#endif
        }

        // sameBytes: written back as read (not for a ConcurrentDictionary, whose order is its buckets').
        private static T RoundTrip<T>(T value, bool sameBytes = true)
        {
            byte[] bytes = NinoSerializer.Serialize(value);
            T back = NinoDeserializer.Deserialize<T>(bytes);
            // A collection built without the public API is collected and moved like any other.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (sameBytes)
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back), typeof(T).Name);
            return back;
        }

        // GetType out of line: the JIT or IL2CPP may fold it to the type allocated (ObjectTypes.Retype).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Type TypeOf(object value) => value.GetType();

        [Test]
        public void ListsStacksAndQueues()
        {
            var list = RoundTrip(Enumerable.Range(0, 100).ToList());
            CollectionAssert.AreEqual(Enumerable.Range(0, 100).ToArray(), list);
            list.Add(100);
            Assert.AreEqual(101, list.Count);

            var strings = RoundTrip(Enumerable.Range(0, 50).Select(i => "s" + i).ToList());
            Assert.AreEqual("s49", strings[49]);

            var stack = RoundTrip(new Stack<int>(Enumerable.Range(0, 40)));
            CollectionAssert.AreEqual(Enumerable.Range(0, 40).Reverse().ToArray(), stack.ToArray());
            stack.Push(99);
            Assert.AreEqual(99, stack.Pop());

            var queue = new Queue<string>();
            for (int i = 0; i < 30; i++)
                queue.Enqueue("q" + i);
            queue.Dequeue(); // a head past the start of the array
            var back = RoundTrip(queue);
            CollectionAssert.AreEqual(queue.ToArray(), back.ToArray());
            back.Enqueue("last");
            Assert.AreEqual("q1", back.Dequeue());
        }

        [Test]
        public void HashSetsAndDictionaries()
        {
            var set = RoundTrip(new HashSet<int>(Enumerable.Range(0, 200).Select(i => i * 7)));
            Assert.AreEqual(200, set.Count);
            Assert.IsTrue(set.Contains(7 * 199));
            Assert.IsTrue(set.Add(-1));
            Assert.IsTrue(set.Remove(0));

            var map = RoundTrip(Enumerable.Range(0, 200).ToDictionary(i => "k" + i, i => i));
            Assert.AreEqual(150, map["k150"]);
            map.Add("new", -1);
            Assert.IsTrue(map.Remove("k0"));
            Assert.AreEqual(200, map.Count);
        }

        [Test]
        public void SortedCollections()
        {
            var sortedSet = RoundTrip(new SortedSet<int>(Enumerable.Range(0, 300).Select(i => (i * 37) % 1000)));
            Assert.AreEqual(typeof(SortedSet<int>), TypeOf(sortedSet));
            CollectionAssert.AreEqual(Enumerable.Range(0, 300).Select(i => (i * 37) % 1000).OrderBy(i => i).ToArray(),
                sortedSet.ToArray());
            // The tree built by Nino is a valid red-black tree: adding and removing rebalance it.
            for (int i = 0; i < 100; i++)
                Assert.IsTrue(sortedSet.Add(1000 + i));
            for (int i = 0; i < 100; i++)
                Assert.IsTrue(sortedSet.Remove((i * 37) % 1000));
            Assert.AreEqual(300, sortedSet.Count);
            Assert.AreEqual(sortedSet.Min, sortedSet.First());

            var strings = RoundTrip(new SortedSet<string>(Enumerable.Range(0, 64).Select(i => "v" + i)));
            Assert.IsTrue(strings.Contains("v63"));

            var sortedMap = RoundTrip(new SortedDictionary<int, string>(Enumerable.Range(0, 120)
                .ToDictionary(i => 120 - i, i => "x" + i)));
            Assert.AreEqual("x0", sortedMap[120]);
            sortedMap.Add(0, "zero");
            Assert.AreEqual(0, sortedMap.Keys.First());

            var sortedList = RoundTrip(new SortedList<int, int>(Enumerable.Range(0, 80).ToDictionary(i => i * 2, i => i)));
            Assert.AreEqual(40, sortedList[80]);
            sortedList.Add(1, 1);
            Assert.AreEqual(1, sortedList.IndexOfKey(1));
        }

        [Test]
        public void LinkedLists()
        {
            var linked = RoundTrip(new LinkedList<string>(Enumerable.Range(0, 60).Select(i => "n" + i)));
            Assert.AreEqual(60, linked.Count);
            Assert.AreEqual("n0", linked.First.Value);
            Assert.AreEqual("n59", linked.Last.Value);
            Assert.AreSame(linked, linked.First.List);
            linked.AddLast("end");
            linked.RemoveFirst();
            Assert.AreEqual("n1", linked.First.Value);
            CollectionAssert.AreEqual(Enumerable.Range(1, 59).Select(i => "n" + i).Append("end").ToArray(),
                linked.ToArray());
        }

        [Test]
        public void ConcurrentDictionaries()
        {
            var ints = RoundTrip(new ConcurrentDictionary<int, int>(Enumerable.Range(0, 500)
                .Select(i => new KeyValuePair<int, int>(i, -i))), false);
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 500).Select(i => new KeyValuePair<int, int>(i, -i)).ToArray(),
                ints.ToArray());
            Assert.AreEqual(typeof(ConcurrentDictionary<int, int>), TypeOf(ints));
            Assert.AreEqual(500, ints.Count);
            Assert.IsTrue(ints.TryGetValue(499, out int value) && value == -499);
            Assert.IsTrue(ints.TryAdd(1000, 1));
            Assert.IsTrue(ints.TryRemove(0, out _));
            Assert.AreEqual(500, ints.Count);

            var strings = RoundTrip(new ConcurrentDictionary<string, string>(Enumerable.Range(0, 100)
                .Select(i => new KeyValuePair<string, string>("k" + i, "v" + i))), false);
            Assert.AreEqual(100, strings.Count);
            Assert.AreEqual("v42", strings["k42"]);
            strings["k42"] = "changed";
            Assert.AreEqual("changed", strings["k42"]);
        }
    }
}
