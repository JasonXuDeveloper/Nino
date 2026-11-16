using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using static Nino.UnitTests.Rt;
using static Nino.UnitTests.CollectionFastPathFixtures;

#if NET8_0_OR_GREATER && !NINO_CORE_NETSTANDARD
#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// PriorityQueue written as raw (element, priority) pairs in heap order and read back without changing insertion order.
/// </summary>
[TestClass]
public class PriorityQueueFastPathTests
{
    [TestMethod]
    public void PriorityQueueWritesRawPairsInHeapOrder()
    {
        Check(i => i * 7, i => -13 * i, Comparer<int>.Create((a, b) => b.CompareTo(a)));
        Check(i => new Subset.Corner.RawPadded { A = (byte)i, B = -i, C = (short)i, D = 7L * i },
            i => -13 * i, Comparer<int>.Default);
        Check(i => new ReverseIndex(i), i => (ushort)(13 * i),
            Comparer<ushort>.Create((a, b) => b.CompareTo(a)));
        Check(i => (Int8Key)i, i => new ReverseIndex(i * 13), Comparer<ReverseIndex>.Default);

        static void Check<T, TPriority>(Func<int, T> create, Func<int, TPriority> priorityOf,
            IComparer<TPriority> comparer)
        {
            var queue = new PriorityQueue<T, TPriority>(32, comparer);
            for (int i = 0; i < 19; i++)
                queue.Enqueue(create(i), priorityOf(i));

            using var expected = new NinoArrayBufferWriter();
            NinoSerializer.Serialize(queue.Count, expected);
            foreach (var (element, priority) in queue.UnorderedItems)
            {
                NinoSerializer.Serialize(element, expected);
                NinoSerializer.Serialize(priority, expected);
            }

            using var actual = OffsetWriter();
            NinoSerializer.Serialize(queue, actual);
            CollectionAssert.AreEqual(expected.WrittenSpan.ToArray(), actual.WrittenSpan.Slice(1).ToArray());
        }
    }

    [TestMethod]
    [DataRow(19)]
    [DataRow(34)]
    public void PriorityQueueReadsRawHeapWithoutChangingInsertionOrder(int count)
    {
        foreach (int unorderedIndex in new[] { -1, 3, 8, count - 1 })
        foreach (bool customComparer in new[] { false, true })
        foreach (int capacity in new[] { 1, 64 })
        {
            var comparer = customComparer ? Comparer<int>.Create((a, b) => b.CompareTo(a)) : Comparer<int>.Default;
            var expected = new PriorityQueue<int, int>(capacity, comparer);
            var actual = new PriorityQueue<int, int>(capacity, comparer);
            actual.Enqueue(-1, 0);
            using var buffer = OffsetWriter();
            NinoSerializer.Serialize(count, buffer);
            for (int i = 0; i < count; i++)
            {
                int priority = i == unorderedIndex ? int.MinValue : i / 3;
                expected.Enqueue(i, priority);
                NinoSerializer.Serialize(i, buffer);
                NinoSerializer.Serialize(priority, buffer);
            }

            var original = actual;
            NinoDeserializer.Deserialize(buffer.WrittenSpan.Slice(1), ref actual);
            Assert.AreSame(original, actual);
            Assert.AreEqual(expected.EnsureCapacity(0), actual.EnsureCapacity(0));
            CollectionAssert.AreEqual(expected.UnorderedItems.ToList(), actual.UnorderedItems.ToList());
            while (expected.TryDequeue(out int element, out int priority))
            {
                Assert.IsTrue(actual.TryDequeue(out int actualElement, out int actualPriority));
                Assert.AreEqual((element, priority), (actualElement, actualPriority));
            }
        }
    }
}
#endif
