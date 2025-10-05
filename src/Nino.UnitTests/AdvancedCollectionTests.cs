using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class AdvancedCollectionTests
{
    [TestMethod]
    public void TestNewCollectionTypes()
    {
        // Test SortedSet<T>
        // 1. Primitive unmanaged
        SortedSet<int> sortedSetInt = new SortedSet<int> { 5, 3, 1, 4, 2 };
        byte[] bytes = NinoSerializer.Serialize(sortedSetInt);
        Assert.IsNotNull(bytes);
        SortedSet<int> sortedSetIntResult = NinoDeserializer.Deserialize<SortedSet<int>>(bytes);
        Assert.AreEqual(5, sortedSetIntResult.Count);
        Assert.IsTrue(sortedSetIntResult.SetEquals(sortedSetInt));

        // 2. TestStruct3 (unmanaged struct)
        SortedSet<TestStruct3> sortedSetStruct = new SortedSet<TestStruct3>()
        {
            new TestStruct3 { A = 1, B = 1.5f },
            new TestStruct3 { A = 2, B = 2.5f }
        };
        bytes = NinoSerializer.Serialize(sortedSetStruct);
        Assert.IsNotNull(bytes);
        SortedSet<TestStruct3> sortedSetStructResult = NinoDeserializer.Deserialize<SortedSet<TestStruct3>>(bytes);
        Assert.AreEqual(2, sortedSetStructResult.Count);

        // 3. Managed reference type
        SortedSet<string> sortedSetString = new SortedSet<string> { "delta", "alpha", "charlie", "bravo" };
        bytes = NinoSerializer.Serialize(sortedSetString);
        Assert.IsNotNull(bytes);
        SortedSet<string> sortedSetStringResult = NinoDeserializer.Deserialize<SortedSet<string>>(bytes);
        Assert.AreEqual(4, sortedSetStringResult.Count);
        Assert.IsTrue(sortedSetStringResult.SetEquals(sortedSetString));

        Console.WriteLine("SortedSet tests passed!");

        // Test PriorityQueue<TElement, TPriority>
        // 1. Primitive unmanaged
        PriorityQueue<int, int> priorityQueueInt = new PriorityQueue<int, int>();
        priorityQueueInt.Enqueue(10, 3);
        priorityQueueInt.Enqueue(20, 1);
        priorityQueueInt.Enqueue(30, 2);
        bytes = NinoSerializer.Serialize(priorityQueueInt);
        Assert.IsNotNull(bytes);
        PriorityQueue<int, int> priorityQueueIntResult = NinoDeserializer.Deserialize<PriorityQueue<int, int>>(bytes);
        Assert.AreEqual(3, priorityQueueIntResult.Count);
        Assert.AreEqual(20, priorityQueueIntResult.Dequeue()); // Priority 1
        Assert.AreEqual(30, priorityQueueIntResult.Dequeue()); // Priority 2
        Assert.AreEqual(10, priorityQueueIntResult.Dequeue()); // Priority 3

        // 2. String element with int priority (managed + unmanaged)
        PriorityQueue<string, int> priorityQueueStr = new PriorityQueue<string, int>();
        priorityQueueStr.Enqueue("low", 10);
        priorityQueueStr.Enqueue("high", 1);
        priorityQueueStr.Enqueue("medium", 5);
        bytes = NinoSerializer.Serialize(priorityQueueStr);
        Assert.IsNotNull(bytes);
        PriorityQueue<string, int> priorityQueueStrResult =
            NinoDeserializer.Deserialize<PriorityQueue<string, int>>(bytes);
        Assert.AreEqual(3, priorityQueueStrResult.Count);
        Assert.AreEqual("high", priorityQueueStrResult.Dequeue());

        // 3. TestStruct3 with byte priority (both unmanaged)
        PriorityQueue<TestStruct3, byte> priorityQueueStruct = new PriorityQueue<TestStruct3, byte>();
        priorityQueueStruct.Enqueue(new TestStruct3 { A = 1, B = 1.5f }, 3);
        priorityQueueStruct.Enqueue(new TestStruct3 { A = 2, B = 2.5f }, 1);
        bytes = NinoSerializer.Serialize(priorityQueueStruct);
        Assert.IsNotNull(bytes);
        PriorityQueue<TestStruct3, byte> priorityQueueStructResult =
            NinoDeserializer.Deserialize<PriorityQueue<TestStruct3, byte>>(bytes);
        Assert.AreEqual(2, priorityQueueStructResult.Count);

        Console.WriteLine("PriorityQueue tests passed!");

        // Test SortedDictionary<TKey, TValue>
        // 1. Primitive unmanaged key and value
        SortedDictionary<int, int> sortedDictInt = new SortedDictionary<int, int>
        {
            { 3, 30 },
            { 1, 10 },
            { 2, 20 }
        };
        bytes = NinoSerializer.Serialize(sortedDictInt);
        Assert.IsNotNull(bytes);
        SortedDictionary<int, int> sortedDictIntResult =
            NinoDeserializer.Deserialize<SortedDictionary<int, int>>(bytes);
        Assert.AreEqual(3, sortedDictIntResult.Count);
        Assert.AreEqual(10, sortedDictIntResult[1]);
        Assert.AreEqual(20, sortedDictIntResult[2]);
        Assert.AreEqual(30, sortedDictIntResult[3]);

        // 2. String key, managed value type
        SortedDictionary<string, TestStruct2> sortedDictMixed = new SortedDictionary<string, TestStruct2>
        {
            { "first", new TestStruct2 { A = 1, B = true, C = new TestStruct3 { A = 1, B = 1.5f } } },
            { "second", new TestStruct2 { A = 2, B = false, C = new TestStruct3 { A = 2, B = 2.5f } } }
        };
        bytes = NinoSerializer.Serialize(sortedDictMixed);
        Assert.IsNotNull(bytes);
        SortedDictionary<string, TestStruct2> sortedDictMixedResult =
            NinoDeserializer.Deserialize<SortedDictionary<string, TestStruct2>>(bytes);
        Assert.AreEqual(2, sortedDictMixedResult.Count);
        Assert.AreEqual(1, sortedDictMixedResult["first"].A);

        Console.WriteLine("SortedDictionary tests passed!");

        // Test SortedList<TKey, TValue>
        SortedList<int, string> sortedList = new SortedList<int, string>
        {
            { 3, "three" },
            { 1, "one" },
            { 2, "two" }
        };
        bytes = NinoSerializer.Serialize(sortedList);
        Assert.IsNotNull(bytes);
        SortedList<int, string> sortedListResult = NinoDeserializer.Deserialize<SortedList<int, string>>(bytes);
        Assert.AreEqual(3, sortedListResult.Count);
        Assert.AreEqual("one", sortedListResult[1]);

        Console.WriteLine("SortedList tests passed!");

        // Test ReadOnlyDictionary<TKey, TValue>
        Dictionary<string, int> innerDict = new Dictionary<string, int>
        {
            { "one", 1 },
            { "two", 2 },
            { "three", 3 }
        };
        System.Collections.ObjectModel.ReadOnlyDictionary<string, int> readOnlyDict =
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(innerDict);
        bytes = NinoSerializer.Serialize(readOnlyDict);
        Assert.IsNotNull(bytes);
        System.Collections.ObjectModel.ReadOnlyDictionary<string, int> readOnlyDictResult =
            NinoDeserializer.Deserialize<System.Collections.ObjectModel.ReadOnlyDictionary<string, int>>(bytes);
        Assert.AreEqual(3, readOnlyDictResult.Count);
        Assert.AreEqual(1, readOnlyDictResult["one"]);

        Console.WriteLine("ReadOnlyDictionary tests passed!");

        // Test ImmutableArray<T>
        // 1. Primitive unmanaged
        ImmutableArray<int> immutableArrayInt =
            ImmutableArray.Create(1, 2, 3, 4, 5);
        bytes = NinoSerializer.Serialize(immutableArrayInt);
        Assert.IsNotNull(bytes);
        ImmutableArray<int> immutableArrayIntResult =
            NinoDeserializer.Deserialize<ImmutableArray<int>>(bytes);
        Assert.AreEqual(5, immutableArrayIntResult.Length);
        Assert.AreEqual(1, immutableArrayIntResult[0]);
        Assert.AreEqual(5, immutableArrayIntResult[4]);

        // 2. Managed reference type
        ImmutableArray<string> immutableArrayStr =
            ImmutableArray.Create("one", "two", "three");
        bytes = NinoSerializer.Serialize(immutableArrayStr);
        Assert.IsNotNull(bytes);
        ImmutableArray<string> immutableArrayStrResult =
            NinoDeserializer.Deserialize<ImmutableArray<string>>(bytes);
        Assert.AreEqual(3, immutableArrayStrResult.Length);
        Assert.AreEqual("one", immutableArrayStrResult[0]);

        // 3. TestStruct3 (unmanaged struct)
        ImmutableArray<TestStruct3> immutableArrayStruct =
            ImmutableArray.Create(
                new TestStruct3 { A = 1, B = 1.5f },
                new TestStruct3 { A = 2, B = 2.5f });
        bytes = NinoSerializer.Serialize(immutableArrayStruct);
        Assert.IsNotNull(bytes);
        ImmutableArray<TestStruct3> immutableArrayStructResult =
            NinoDeserializer.Deserialize<ImmutableArray<TestStruct3>>(bytes);
        Assert.AreEqual(2, immutableArrayStructResult.Length);
        Assert.AreEqual((byte)1, immutableArrayStructResult[0].A);

        Console.WriteLine("ImmutableArray tests passed!");

        // Test ImmutableList<T>
        // 1. Primitive unmanaged
        ImmutableList<int> immutableListInt =
            ImmutableList.Create(10, 20, 30);
        bytes = NinoSerializer.Serialize(immutableListInt);
        Assert.IsNotNull(bytes);
        ImmutableList<int> immutableListIntResult =
            NinoDeserializer.Deserialize<ImmutableList<int>>(bytes);
        Assert.AreEqual(3, immutableListIntResult.Count);
        Assert.AreEqual(10, immutableListIntResult[0]);

        // 2. Managed value type (TestStruct2)
        ImmutableList<TestStruct2> immutableListStruct =
            ImmutableList.Create(
                new TestStruct2 { A = 1, B = true, C = new TestStruct3 { A = 1, B = 1.5f } });
        bytes = NinoSerializer.Serialize(immutableListStruct);
        Assert.IsNotNull(bytes);
        ImmutableList<TestStruct2> immutableListStructResult =
            NinoDeserializer.Deserialize<ImmutableList<TestStruct2>>(bytes);
        Assert.AreEqual(1, immutableListStructResult.Count);
        Assert.AreEqual(1, immutableListStructResult[0].A);

        Console.WriteLine("ImmutableList tests passed!");
    }

    [TestMethod]
    public void TestNewCollectionTypesComplex()
    {
        // Test nested generic types with new collections
        // 1. List of SortedSet
        List<SortedSet<int>> listOfSortedSets = new List<SortedSet<int>>
        {
            new SortedSet<int> { 3, 1, 2 },
            new SortedSet<int> { 6, 4, 5 }
        };
        byte[] bytes = NinoSerializer.Serialize(listOfSortedSets);
        Assert.IsNotNull(bytes);
        List<SortedSet<int>> listOfSortedSetsResult = NinoDeserializer.Deserialize<List<SortedSet<int>>>(bytes);
        Assert.AreEqual(2, listOfSortedSetsResult.Count);
        Assert.AreEqual(3, listOfSortedSetsResult[0].Count);

        // 2. Dictionary with ImmutableArray values
        Dictionary<string, ImmutableArray<int>> dictWithImmutableArrays =
            new Dictionary<string, ImmutableArray<int>>
            {
                { "first", ImmutableArray.Create(1, 2, 3) },
                { "second", ImmutableArray.Create(4, 5, 6) }
            };
        bytes = NinoSerializer.Serialize(dictWithImmutableArrays);
        Assert.IsNotNull(bytes);
        Dictionary<string, ImmutableArray<int>> dictWithImmutableArraysResult =
            NinoDeserializer.Deserialize<Dictionary<string, ImmutableArray<int>>>(bytes);
        Assert.AreEqual(2, dictWithImmutableArraysResult.Count);
        Assert.AreEqual(3, dictWithImmutableArraysResult["first"].Length);

        // 3. Jagged array with SortedDictionary
        SortedDictionary<int, string>[] jaggedSortedDict = new SortedDictionary<int, string>[]
        {
            new SortedDictionary<int, string> { { 1, "one" }, { 2, "two" } },
            new SortedDictionary<int, string> { { 3, "three" }, { 4, "four" } }
        };
        bytes = NinoSerializer.Serialize(jaggedSortedDict);
        Assert.IsNotNull(bytes);
        SortedDictionary<int, string>[] jaggedSortedDictResult =
            NinoDeserializer.Deserialize<SortedDictionary<int, string>[]>(bytes);
        Assert.AreEqual(2, jaggedSortedDictResult.Length);
        Assert.AreEqual("one", jaggedSortedDictResult[0][1]);

        // 4. Multi-dimensional array as type parameter in ImmutableList
        ImmutableList<int[,]> immutableListOf2DArrays =
            ImmutableList.Create(
                new int[,] { { 1, 2 }, { 3, 4 } },
                new int[,] { { 5, 6 }, { 7, 8 } }
            );
        bytes = NinoSerializer.Serialize(immutableListOf2DArrays);
        Assert.IsNotNull(bytes);
        ImmutableList<int[,]> immutableListOf2DArraysResult =
            NinoDeserializer.Deserialize<ImmutableList<int[,]>>(bytes);
        Assert.AreEqual(2, immutableListOf2DArraysResult.Count);
        Assert.AreEqual(1, immutableListOf2DArraysResult[0][0, 0]);
        Assert.AreEqual(8, immutableListOf2DArraysResult[1][1, 1]);

        // 5. PriorityQueue with nested generic type parameters
        PriorityQueue<List<string>, int> priorityQueueNested = new PriorityQueue<List<string>, int>();
        priorityQueueNested.Enqueue(new List<string> { "a", "b" }, 2);
        priorityQueueNested.Enqueue(new List<string> { "c", "d" }, 1);
        bytes = NinoSerializer.Serialize(priorityQueueNested);
        Assert.IsNotNull(bytes);
        PriorityQueue<List<string>, int> priorityQueueNestedResult =
            NinoDeserializer.Deserialize<PriorityQueue<List<string>, int>>(bytes);
        Assert.AreEqual(2, priorityQueueNestedResult.Count);
        List<string> firstItem = priorityQueueNestedResult.Dequeue();
        Assert.AreEqual(2, firstItem.Count);
        Assert.AreEqual("c", firstItem[0]); // Priority 1 comes first

        // 6. ReadOnlyDictionary with complex keys and values
        Dictionary<int, ImmutableArray<TestStruct3>> innerComplexDict =
            new Dictionary<int, ImmutableArray<TestStruct3>>
            {
                { 1, ImmutableArray.Create(new TestStruct3 { A = 1, B = 1.5f }) },
                { 2, ImmutableArray.Create(new TestStruct3 { A = 2, B = 2.5f }) }
            };
        System.Collections.ObjectModel.ReadOnlyDictionary<int, ImmutableArray<TestStruct3>>
            readOnlyComplexDict =
                new System.Collections.ObjectModel.ReadOnlyDictionary<int,
                    ImmutableArray<TestStruct3>>(innerComplexDict);
        bytes = NinoSerializer.Serialize(readOnlyComplexDict);
        Assert.IsNotNull(bytes);
        System.Collections.ObjectModel.ReadOnlyDictionary<int, ImmutableArray<TestStruct3>>
            readOnlyComplexDictResult =
                NinoDeserializer
                    .Deserialize<System.Collections.ObjectModel.ReadOnlyDictionary<int,
                        ImmutableArray<TestStruct3>>>(bytes);
        Assert.AreEqual(2, readOnlyComplexDictResult.Count);
        Assert.AreEqual((byte)1, readOnlyComplexDictResult[1][0].A);

        Console.WriteLine("Complex nested collection tests passed!");
    }
}
