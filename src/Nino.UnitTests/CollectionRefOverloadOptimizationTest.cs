using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.UnityMock;

namespace Nino.UnitTests
{
    [TestClass]
    public class CollectionRefOverloadOptimizationTest
    {
        [TestMethod]
        public void TestListRefOverloadIdentityPreservation()
        {
            // Test that List<T> ref overload preserves object identity (optimized path)
            var originalList = new List<TestClass>
            {
                new TestClass { A = 1, B = "first" },
                new TestClass { A = 2, B = "second" },
                new TestClass { A = 3, B = "third" }
            };

            // Store references to verify identity preservation
            var ref1 = originalList[0];
            var ref2 = originalList[1];
            var ref3 = originalList[2];

            // Test capacity behavior (should grow from 3 to accommodate potential growth)
            var initialCapacity = originalList.Capacity;
            Console.WriteLine($"Initial capacity: {initialCapacity}, Count: {originalList.Count}");

            // Serialize the list
            var bytes = NinoSerializer.Serialize(originalList);

            // Use ref overload to deserialize into existing list
            var reader = new Reader(bytes);
            NinoDeserializer.DeserializeRef(ref originalList, ref reader);

            var finalCapacity = originalList.Capacity;
            Console.WriteLine($"Final capacity: {finalCapacity}, Count: {originalList.Count}");

            // Verify data integrity
            Assert.AreEqual(3, originalList.Count);
            Assert.AreEqual(1, originalList[0].A);
            Assert.AreEqual("first", originalList[0].B);
            Assert.AreEqual(2, originalList[1].A);
            Assert.AreEqual("second", originalList[1].B);
            Assert.AreEqual(3, originalList[2].A);
            Assert.AreEqual("third", originalList[2].B);

            // CRITICAL: Verify that the object references are still the same (identity preserved)
            Console.WriteLine($"Original refs: {ref1.GetHashCode():X}, {ref2.GetHashCode():X}, {ref3.GetHashCode():X}");
            Console.WriteLine($"Current refs:  {originalList[0].GetHashCode():X}, {originalList[1].GetHashCode():X}, {originalList[2].GetHashCode():X}");
            
            Assert.AreSame(ref1, originalList[0], "List ref overload should preserve object identity");
            Assert.AreSame(ref2, originalList[1], "List ref overload should preserve object identity");
            Assert.AreSame(ref3, originalList[2], "List ref overload should preserve object identity");
        }

        [TestMethod]
        public void TestListRefOverloadShrinkAndGrow()
        {
            // Test the three-phase optimization: shrink then grow
            var originalList = new List<TestClass>
            {
                new TestClass { A = 1, B = "first" },
                new TestClass { A = 2, B = "second" },
                new TestClass { A = 3, B = "third" },
                new TestClass { A = 4, B = "fourth" },
                new TestClass { A = 5, B = "fifth" }
            };

            // Store references to first 3 elements
            var ref1 = originalList[0];
            var ref2 = originalList[1];
            var ref3 = originalList[2];

            // Phase 1: Shrink to 3 elements
            var smallerList = new List<TestClass>
            {
                new TestClass { A = 10, B = "updated_first" },
                new TestClass { A = 20, B = "updated_second" },
                new TestClass { A = 30, B = "updated_third" }
            };

            var bytes = NinoSerializer.Serialize(smallerList);
            var reader = new Reader(bytes);
            NinoDeserializer.DeserializeRef(ref originalList, ref reader);

            // Verify shrink worked correctly
            Assert.AreEqual(3, originalList.Count, "List should shrink from 5 to 3 elements");
            Assert.AreSame(ref1, originalList[0], "Object identity should be preserved during shrink");
            Assert.AreSame(ref2, originalList[1], "Object identity should be preserved during shrink");
            Assert.AreSame(ref3, originalList[2], "Object identity should be preserved during shrink");
            Assert.AreEqual(10, originalList[0].A, "Data should be updated during shrink");
            Assert.AreEqual("updated_first", originalList[0].B);

            // Phase 2: Grow to 6 elements
            var largerList = new List<TestClass>
            {
                new TestClass { A = 100, B = "grow_first" },
                new TestClass { A = 200, B = "grow_second" },
                new TestClass { A = 300, B = "grow_third" },
                new TestClass { A = 400, B = "grow_fourth" },
                new TestClass { A = 500, B = "grow_fifth" },
                new TestClass { A = 600, B = "grow_sixth" }
            };

            bytes = NinoSerializer.Serialize(largerList);
            reader = new Reader(bytes);
            NinoDeserializer.DeserializeRef(ref originalList, ref reader);

            // Verify grow worked correctly
            Assert.AreEqual(6, originalList.Count, "List should grow from 3 to 6 elements");
            Assert.AreSame(ref1, originalList[0], "Object identity should be preserved during growth");
            Assert.AreSame(ref2, originalList[1], "Object identity should be preserved during growth");
            Assert.AreSame(ref3, originalList[2], "Object identity should be preserved during growth");
            Assert.AreEqual(100, originalList[0].A, "Data should be updated during growth");
            Assert.AreEqual("grow_first", originalList[0].B);
            Assert.AreEqual(600, originalList[5].A, "New elements should be added correctly");
            Assert.AreEqual("grow_sixth", originalList[5].B);
        }

        [TestMethod]
        public void TestHashSetRefOverloadUsesNonOptimizedPath()
        {
            // Test that HashSet<T> uses the non-optimized path (clear-and-add)
            // since it doesn't have indexer and RemoveAt
            var originalSet = new HashSet<int> { 1, 2, 3, 4, 5 };
            var bytes = NinoSerializer.Serialize(originalSet);
            
            var refSet = new HashSet<int> { 10, 20 };
            var reader = new Reader(bytes);
            NinoDeserializer.DeserializeRef(ref refSet, ref reader);
            
            // Verify the HashSet was updated correctly using fallback approach
            Assert.AreEqual(5, refSet.Count);
            Assert.IsTrue(refSet.Contains(1));
            Assert.IsTrue(refSet.Contains(2));
            Assert.IsTrue(refSet.Contains(3));
            Assert.IsTrue(refSet.Contains(4));
            Assert.IsTrue(refSet.Contains(5));
            Assert.IsFalse(refSet.Contains(10));
            Assert.IsFalse(refSet.Contains(20));
        }
    }
}