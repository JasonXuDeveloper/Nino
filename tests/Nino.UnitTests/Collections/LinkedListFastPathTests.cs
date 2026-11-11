using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using static Nino.UnitTests.Rt;

#nullable disable
namespace Nino.UnitTests;

public class NamedLinkedList : LinkedList<string>
{
}

/// <summary>
/// LinkedList walked node by node: the bytes of a list of its items, raw items at an unaligned offset, derived types.
/// </summary>
[TestClass]
public class LinkedListFastPathTests
{
    [TestMethod]
    public void LinkedListsAreWalkedNodeByNode()
    {
        foreach (int n in new[] { 0, 1, 2, 17, 31, 32, 33, 1000 })
        {
            var ints = new LinkedList<int>(Enumerable.Range(0, n).Select(i => i * 31));
            if (ints.Last is { } last)
            {
                ints.RemoveLast();
                ints.AddFirst(last);
            }
            var intBytes = NinoSerializer.Serialize(ints);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(ints.ToList()), intBytes);
            CollectionAssert.AreEqual(ints.ToList(), NinoDeserializer.Deserialize<LinkedList<int>>(intBytes).ToList());

            var padded = new LinkedList<PaddedItem>(Enumerable.Range(0, n)
                .Select(i => new PaddedItem { Id = i, Count = (short)i, Bound = i % 2 == 0 }));
            using var buffer = OffsetWriter();
            NinoSerializer.Serialize(padded, buffer);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(padded.ToArray()), buffer.WrittenSpan.Slice(1).ToArray());

            var names = new NamedLinkedList();
            foreach (var i in Enumerable.Range(0, n))
                names.AddLast(i % 4 == 0 ? null : "n" + i);
            var read = NinoDeserializer.Deserialize<NamedLinkedList>(NinoSerializer.Serialize(names));
            CollectionAssert.AreEqual(names.ToList(), read.ToList());
            NinoDeserializer.Deserialize(intBytes, ref ints);
            Assert.AreEqual(n, ints.Count);
        }
    }
}
