using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class RefOverloadTests
{
    [TestMethod]
    public void TestRefOverload()
    {
        List<int> list = new List<int> { 1, 2, 3, 4, 5 };
        byte[] bytes = NinoSerializer.Serialize(list);
        Assert.IsNotNull(bytes);

        List<int> temp = new List<int> { 0, 0 };
        NinoDeserializer.Deserialize(bytes, ref temp);
        Assert.AreEqual(5, temp.Count);
        Assert.AreEqual(1, temp[0]);
        Assert.AreEqual(2, temp[1]);
        Assert.AreEqual(3, temp[2]);
        Assert.AreEqual(4, temp[3]);
        Assert.AreEqual(5, temp[4]);

        List<string> strs = new List<string> { "Hello", "World" };
        bytes = NinoSerializer.Serialize(strs);
        Assert.IsNotNull(bytes);

        List<string> tempStrs = new List<string> { "Test", "Test2", "Test3" };
        NinoDeserializer.Deserialize(bytes, ref tempStrs);
        Assert.AreEqual(2, tempStrs.Count);
        Assert.AreEqual("Hello", tempStrs[0]);
        Assert.AreEqual("World", tempStrs[1]);

        HierarchicalSub2 sub2 = new HierarchicalSub2
        {
            F = new List<int>() { 1, 2, 3 }
        };
        bytes = NinoSerializer.Serialize(sub2);
        Assert.IsNotNull(bytes);
        HierarchicalSub2 tempSub2 = new HierarchicalSub2();
        NinoDeserializer.Deserialize(bytes, ref tempSub2);
        Assert.IsNotNull(tempSub2.F);
        Assert.AreEqual(3, tempSub2.F.Count);
        Assert.AreEqual(1, tempSub2.F[0]);
        Assert.AreEqual(2, tempSub2.F[1]);
        Assert.AreEqual(3, tempSub2.F[2]);

        TestClass3 testClass3 = new TestClass3
        {
            A = 1,
            B = "Test",
            M = new TestClass3()
            {
                A = 2
            }
        };
        bytes = NinoSerializer.Serialize(testClass3);
        Assert.IsNotNull(bytes);
        Console.WriteLine(string.Join(", ", bytes));

        TestClass3 tempTestClass3 = new TestClass3();
        NinoDeserializer.Deserialize(bytes, ref tempTestClass3);
        Assert.AreEqual(testClass3.A, tempTestClass3.A);
        Assert.AreEqual(testClass3.B, tempTestClass3.B);
        Assert.IsNotNull(tempTestClass3.M);
        Assert.AreEqual(testClass3.M.A, tempTestClass3.M.A);

        Data dt = new()
        {
            X = 10,
            En = TestEnum.B,
            Name = "Hello"
        };
        bytes = NinoSerializer.Serialize(dt);
        Assert.IsNotNull(bytes);

        dt = new()
        {
            X = 1,
            Y = 2,
            En = TestEnum.A,
            Name = "World"
        };
        NinoDeserializer.Deserialize(bytes, ref dt);
        Assert.AreEqual(10, dt.X);
        Assert.AreEqual(0, dt.Y); // Y should not be changed
        Assert.AreEqual(TestEnum.B, dt.En);
        Assert.AreEqual("Hello", dt.Name);

        dt = new()
        {
            X = 100,
            En = TestEnum.B,
            Name = "Nino"
        };
        object boxed = dt;
        NinoDeserializer.Deserialize(bytes, typeof(Data), ref boxed);
        Assert.IsTrue(boxed is Data);
        var data = (Data)boxed;
        Assert.AreEqual(10, data.X);
        Assert.AreEqual(0, data.Y); // Y should not be changed
        Assert.AreEqual(TestEnum.B, data.En);
        Assert.AreEqual("Hello", data.Name);
    }

    [TestMethod]
    public void TestRefOverloadObjectIdentityPreservation()
    {
        // Test Array ref overload preserves object identity
        var originalArray = new TestClass[]
        {
            new TestClass { A = 1, B = "first" },
            new TestClass { A = 2, B = "second" },
            new TestClass { A = 3, B = "third" }
        };

        // Store references to verify identity preservation
        var ref1 = originalArray[0];
        var ref2 = originalArray[1];
        var ref3 = originalArray[2];

        // Serialize the array
        var bytes = NinoSerializer.Serialize(originalArray);

        // Modify the existing objects' data to verify they get overwritten (not replaced)
        ref1.A = 999;
        ref1.B = "modified1";
        ref2.A = 888;
        ref2.B = "modified2";
        ref3.A = 777;
        ref3.B = "modified3";

        // Use ref overload to deserialize into existing array
        NinoDeserializer.Deserialize(bytes, ref originalArray);

        // Verify data integrity
        Assert.AreEqual(3, originalArray.Length, "Array length should be preserved");
        Assert.AreEqual(1, originalArray[0].A, "First element A should be 1");
        Assert.AreEqual("first", originalArray[0].B, "First element B should be 'first'");
        Assert.AreEqual(2, originalArray[1].A, "Second element A should be 2");
        Assert.AreEqual("second", originalArray[1].B, "Second element B should be 'second'");
        Assert.AreEqual(3, originalArray[2].A, "Third element A should be 3");
        Assert.AreEqual("third", originalArray[2].B, "Third element B should be 'third'");

        // CRITICAL: Verify that the object references are still the same (identity preserved)
        Assert.AreSame(ref1, originalArray[0],
            "Array ref overload should preserve object identity in existing elements");
        Assert.AreSame(ref2, originalArray[1],
            "Array ref overload should preserve object identity in existing elements");
        Assert.AreSame(ref3, originalArray[2],
            "Array ref overload should preserve object identity in existing elements");

        // CRITICAL: Verify that the original reference objects were updated with deserialized data (not replaced)
        Assert.AreEqual(1, ref1.A, "Original reference should have updated data from deserialization");
        Assert.AreEqual("first", ref1.B, "Original reference should have updated data from deserialization");
        Assert.AreEqual(2, ref2.A, "Original reference should have updated data from deserialization");
        Assert.AreEqual("second", ref2.B, "Original reference should have updated data from deserialization");
        Assert.AreEqual(3, ref3.A, "Original reference should have updated data from deserialization");
        Assert.AreEqual("third", ref3.B, "Original reference should have updated data from deserialization");

        Console.WriteLine("Array ref overload preserves object identity - PASSED");
    }

    [TestMethod]
    public void TestRefOverloadArrayShrinkPreservesObjects()
    {
        // Test Array ref overload preserves object identity when shrinking array
        var originalArray = new TestClass[]
        {
            new TestClass { A = 1, B = "first" },
            new TestClass { A = 2, B = "second" },
            new TestClass { A = 3, B = "third" },
            new TestClass { A = 4, B = "fourth" },
            new TestClass { A = 5, B = "fifth" }
        };

        // Store references to the first 3 elements (which will remain after shrinking)
        var ref1 = originalArray[0];
        var ref2 = originalArray[1];
        var ref3 = originalArray[2];
        // Also store references to elements that will be removed (4th and 5th)
        var ref4 = originalArray[3];
        var ref5 = originalArray[4];

        // Create a smaller array to serialize (only first 3 elements)
        var smallerArray = new TestClass[]
        {
            new TestClass { A = 10, B = "updated_first" },
            new TestClass { A = 20, B = "updated_second" },
            new TestClass { A = 30, B = "updated_third" }
        };

        // Serialize the smaller array
        var bytes = NinoSerializer.Serialize(smallerArray);

        // Modify the existing objects' data in the larger array to verify they get overwritten (not replaced)
        ref1.A = 999;
        ref1.B = "modified1";
        ref2.A = 888;
        ref2.B = "modified2";
        ref3.A = 777;
        ref3.B = "modified3";
        ref4.A = 666;
        ref4.B = "modified4";
        ref5.A = 555;
        ref5.B = "modified5";

        // Use ref overload to deserialize into existing larger array (should shrink to 3 elements)
        NinoDeserializer.Deserialize(bytes, ref originalArray);

        // Verify array was resized correctly
        Assert.AreEqual(3, originalArray.Length, "Array should be shrunk to 3 elements");

        // Verify data integrity for remaining elements
        Assert.AreEqual(10, originalArray[0].A, "First element A should be 10");
        Assert.AreEqual("updated_first", originalArray[0].B, "First element B should be 'updated_first'");
        Assert.AreEqual(20, originalArray[1].A, "Second element A should be 20");
        Assert.AreEqual("updated_second", originalArray[1].B, "Second element B should be 'updated_second'");
        Assert.AreEqual(30, originalArray[2].A, "Third element A should be 30");
        Assert.AreEqual("updated_third", originalArray[2].B, "Third element B should be 'updated_third'");

        // CRITICAL: Verify that the object references are still the same for remaining elements (identity preserved)
        Assert.AreSame(ref1, originalArray[0],
            "Array ref overload should preserve object identity for remaining elements when shrinking");
        Assert.AreSame(ref2, originalArray[1],
            "Array ref overload should preserve object identity for remaining elements when shrinking");
        Assert.AreSame(ref3, originalArray[2],
            "Array ref overload should preserve object identity for remaining elements when shrinking");

        // CRITICAL: Verify that the original reference objects were updated with deserialized data (not replaced)
        Assert.AreEqual(10, ref1.A, "Original reference should have updated data from deserialization");
        Assert.AreEqual("updated_first", ref1.B, "Original reference should have updated data from deserialization");
        Assert.AreEqual(20, ref2.A, "Original reference should have updated data from deserialization");
        Assert.AreEqual("updated_second", ref2.B, "Original reference should have updated data from deserialization");
        Assert.AreEqual(30, ref3.A, "Original reference should have updated data from deserialization");
        Assert.AreEqual("updated_third", ref3.B, "Original reference should have updated data from deserialization");

        // Verify that the removed elements' references still exist and retain their modified values
        // (They should still exist as objects, just not be part of the array anymore)
        Assert.AreEqual(666, ref4.A, "Removed element reference should still exist with modified data");
        Assert.AreEqual("modified4", ref4.B, "Removed element reference should still exist with modified data");
        Assert.AreEqual(555, ref5.A, "Removed element reference should still exist with modified data");
        Assert.AreEqual("modified5", ref5.B, "Removed element reference should still exist with modified data");

        Console.WriteLine("Array ref overload shrinking preserves object identity for remaining elements - PASSED");
    }

    [TestMethod]
    public void TestMultiDimensionalArrayRefOverload()
    {
        // Test ref overload for 2D arrays
        int[,] int2D = new int[,]
        {
            { 1, 2, 3 },
            { 4, 5, 6 }
        };
        byte[] bytes = NinoSerializer.Serialize(int2D);

        int[,] existingArray = new int[2, 3];
        NinoDeserializer.Deserialize(bytes, ref existingArray);

        Assert.AreEqual(2, existingArray.GetLength(0));
        Assert.AreEqual(3, existingArray.GetLength(1));
        Assert.AreEqual(1, existingArray[0, 0]);
        Assert.AreEqual(6, existingArray[1, 2]);

        // Test ref overload with different dimensions (should recreate array)
        int[,] differentSize = new int[3, 4];
        NinoDeserializer.Deserialize(bytes, ref differentSize);

        Assert.AreEqual(2, differentSize.GetLength(0));
        Assert.AreEqual(3, differentSize.GetLength(1));
        Assert.AreEqual(1, differentSize[0, 0]);
        Assert.AreEqual(6, differentSize[1, 2]);

        // Test ref overload with same dimensions (should reuse array)
        int[,] sameSize = new int[2, 3];
        var sameSizeRef = sameSize;
        NinoDeserializer.Deserialize(bytes, ref sameSize);

        Assert.AreSame(sameSizeRef, sameSize, "Should reuse array when dimensions match");
        Assert.AreEqual(1, sameSize[0, 0]);
        Assert.AreEqual(6, sameSize[1, 2]);

        // Test ref overload with null (should create new array)
        int[,] nullArray = null;
        NinoDeserializer.Deserialize(bytes, ref nullArray);

        Assert.IsNotNull(nullArray);
        Assert.AreEqual(2, nullArray.GetLength(0));
        Assert.AreEqual(3, nullArray.GetLength(1));
        Assert.AreEqual(1, nullArray[0, 0]);
        Assert.AreEqual(6, nullArray[1, 2]);

        Console.WriteLine("Multi-dimensional array ref overload tests passed!");
    }

    [TestMethod]
    public void TestNewCollectionTypesRefOverload()
    {
        // Test ref overload for SortedSet
        SortedSet<int> sortedSet = new SortedSet<int> { 1, 2, 3 };
        byte[] bytes = NinoSerializer.Serialize(sortedSet);

        SortedSet<int> existingSortedSet = new SortedSet<int> { 10, 20 };
        NinoDeserializer.Deserialize(bytes, ref existingSortedSet);
        Assert.AreEqual(3, existingSortedSet.Count);
        Assert.IsTrue(existingSortedSet.Contains(1));
        Assert.IsFalse(existingSortedSet.Contains(10)); // Should be cleared

#if NET6_0_OR_GREATER
        // Test ref overload for PriorityQueue
        PriorityQueue<string, int> priorityQueue = new PriorityQueue<string, int>();
        priorityQueue.Enqueue("first", 1);
        priorityQueue.Enqueue("second", 2);
        bytes = NinoSerializer.Serialize(priorityQueue);

        PriorityQueue<string, int> existingPriorityQueue = new PriorityQueue<string, int>();
        existingPriorityQueue.Enqueue("old", 99);
        NinoDeserializer.Deserialize(bytes, ref existingPriorityQueue);
        Assert.AreEqual(2, existingPriorityQueue.Count);
        Assert.AreEqual("first", existingPriorityQueue.Dequeue());
#endif

        // Test ref overload for SortedDictionary
        SortedDictionary<int, string> sortedDict = new SortedDictionary<int, string>
        {
            { 1, "one" },
            { 2, "two" }
        };
        bytes = NinoSerializer.Serialize(sortedDict);

        SortedDictionary<int, string> existingSortedDict = new SortedDictionary<int, string>
        {
            { 99, "old" }
        };
        NinoDeserializer.Deserialize(bytes, ref existingSortedDict);
        Assert.AreEqual(2, existingSortedDict.Count);
        Assert.AreEqual("one", existingSortedDict[1]);
        Assert.IsFalse(existingSortedDict.ContainsKey(99));

        // Test ref overload for ImmutableArray (should create new)
        ImmutableArray<int> immutableArray =
            ImmutableArray.Create(1, 2, 3);
        bytes = NinoSerializer.Serialize(immutableArray);

        ImmutableArray<int> existingImmutableArray =
            ImmutableArray.Create(10, 20);
        NinoDeserializer.Deserialize(bytes, ref existingImmutableArray);
        Assert.AreEqual(3, existingImmutableArray.Length);
        Assert.AreEqual(1, existingImmutableArray[0]);

        // Test ref overload for ImmutableList (should create new)
        ImmutableList<string> immutableList =
            ImmutableList.Create("a", "b", "c");
        bytes = NinoSerializer.Serialize(immutableList);

        ImmutableList<string> existingImmutableList =
            ImmutableList.Create("x", "y");
        NinoDeserializer.Deserialize(bytes, ref existingImmutableList);
        Assert.AreEqual(3, existingImmutableList.Count);
        Assert.AreEqual("a", existingImmutableList[0]);

        // Test ref overload for ReadOnlyDictionary (should create new)
        Dictionary<string, int> innerDict = new Dictionary<string, int> { { "one", 1 } };
        System.Collections.ObjectModel.ReadOnlyDictionary<string, int> readOnlyDict =
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(innerDict);
        bytes = NinoSerializer.Serialize(readOnlyDict);

        System.Collections.ObjectModel.ReadOnlyDictionary<string, int> existingReadOnlyDict =
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>
                { { "old", 99 } });
        NinoDeserializer.Deserialize(bytes, ref existingReadOnlyDict);
        Assert.AreEqual(1, existingReadOnlyDict.Count);
        Assert.AreEqual(1, existingReadOnlyDict["one"]);

        Console.WriteLine("Ref overload tests for new collections passed!");
    }
}
