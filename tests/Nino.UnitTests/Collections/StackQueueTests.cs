using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Stack and Queue are read and written through their backing arrays (StackView, QueueView), and collections of raw
/// elements behind an interface as one block: the bytes must stay those of the element-by-element encoding (a List of
/// the same elements, in order), and the collections must work normally afterward.
/// </summary>
[TestClass]
public class StackQueueTests
{
    [TestMethod]
    public void LayoutsAreRecognized()
    {
        Assert.IsTrue(CollectionViews.Stack);
        Assert.IsTrue(CollectionViews.Queue);
    }

    /// <summary>A queue whose elements wrap around the end of its backing array: 3, 4, 5, 6.</summary>
    private static Queue<T> Wrapped<T>(System.Func<int, T> item)
    {
        var queue = new Queue<T>(4);
        for (int i = 1; i <= 4; i++)
            queue.Enqueue(item(i));
        queue.Dequeue();
        queue.Dequeue();
        queue.Enqueue(item(5));
        queue.Enqueue(item(6));
        return queue;
    }

    [TestMethod]
    public void BytesAreThoseOfAListInOrder()
    {
        var stack = new Stack<int>(Enumerable.Range(1, 5));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<int> { 1, 2, 3, 4, 5 }),
            NinoSerializer.Serialize(stack));

        CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<int> { 3, 4, 5, 6 }),
            NinoSerializer.Serialize(Wrapped(i => i)));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<string> { "3", "4", "5", "6" }),
            NinoSerializer.Serialize(Wrapped(i => i.ToString())));
        // int? has its own List encoding (fixed stride): compared with a queue that does not wrap.
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new Queue<int?>(new int?[] { 3, null, 5, null })),
            NinoSerializer.Serialize(Wrapped(i => i % 2 == 0 ? null : (int?)i)));

        CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<int>()), NinoSerializer.Serialize(new Stack<int>()));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<int>()), NinoSerializer.Serialize(new Queue<int>()));
    }

    [TestMethod]
    public void InterfacesOfRawElementsKeepTheirBytes()
    {
        var points = Enumerable.Range(0, 50).Select(i => new Vector3(i, i + 1, i + 2)).ToList();
        IList<Vector3> list = points;
        IEnumerable<Vector3> array = points.ToArray();
        IReadOnlyCollection<int> numbers = new Queue<int>(Enumerable.Range(0, 10));
        byte[] expected = NinoSerializer.Serialize(points);
        CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(list));
        CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(array));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(Enumerable.Range(0, 10).ToList()),
            NinoSerializer.Serialize(numbers));

        CollectionAssert.AreEqual(points, NinoDeserializer.Deserialize<IList<Vector3>>(expected).ToList());
        CollectionAssert.AreEqual(points, NinoDeserializer.Deserialize<IEnumerable<Vector3>>(expected).ToList());
        CollectionAssert.AreEqual(points, NinoDeserializer.Deserialize<Vector3[]>(expected));
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<IList<Vector3>>(expected.Take(expected.Length - 1).ToArray()));
    }

    [TestMethod]
    public void ReadCollectionsWorkAfterward()
    {
        var queue = NinoDeserializer.Deserialize<Queue<int>>(NinoSerializer.Serialize(Wrapped(i => i)));
        CollectionAssert.AreEqual(new[] { 3, 4, 5, 6 }, queue.ToArray());
        // The backing array is exactly full (the tail wrapped to 0): growing and the ring stay correct.
        queue.Enqueue(7);
        Assert.AreEqual(3, queue.Dequeue());
        queue.Enqueue(8);
        CollectionAssert.AreEqual(new[] { 4, 5, 6, 7, 8 }, queue.ToArray());

        var stack = NinoDeserializer.Deserialize<Stack<string>>(
            NinoSerializer.Serialize(new Stack<string>(new[] { "a", null, "c" })));
        Assert.AreEqual(3, stack.Count);
        Assert.AreEqual("c", stack.Pop());
        stack.Push("d");
        CollectionAssert.AreEqual(new[] { "d", null, "a" }, stack.ToArray());

        var empty = NinoDeserializer.Deserialize<Queue<int>>(NinoSerializer.Serialize(new Queue<int>()));
        empty.Enqueue(1);
        Assert.AreEqual(1, empty.Dequeue());
        Assert.IsNull(NinoDeserializer.Deserialize<Stack<int>>(NinoSerializer.Serialize((Stack<int>)null)));
    }

    [TestMethod]
    public void ExistingCollectionsAreRefilled()
    {
        // Larger than the existing capacity, from an empty stack (whose backing array is the shared empty one).
        var stack = new Stack<int>();
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Stack<int>(Enumerable.Range(0, 100))), ref stack);
        CollectionAssert.AreEqual(Enumerable.Range(0, 100).Reverse().ToArray(), stack.ToArray());
        Assert.AreEqual(0, new Stack<int>().Count); // the shared empty array was not written

        // Fewer elements into a wrapped queue: the old ones are dropped.
        var queue = Wrapped(i => i);
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Queue<int>(new[] { 9, 8 })), ref queue);
        CollectionAssert.AreEqual(new[] { 9, 8 }, queue.ToArray());
        queue.Enqueue(7);
        CollectionAssert.AreEqual(new[] { 9, 8, 7 }, queue.ToArray());

        // Non-raw unmanaged elements: slots the old contents left behind do not leak into the new ones.
        var nullable = Wrapped(i => (int?)(i * 10));
        nullable.Dequeue();
        nullable.Dequeue();
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Queue<int?>(new int?[] { null, 1, null })),
            ref nullable);
        CollectionAssert.AreEqual(new int?[] { null, 1, null }, nullable.ToArray());

        var words = new Stack<string>(new[] { "x", "y", "z", "w" });
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Stack<string>(new[] { "a" })), ref words);
        CollectionAssert.AreEqual(new[] { "a" }, words.ToArray());
    }
}
