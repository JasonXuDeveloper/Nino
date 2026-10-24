using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Empty collections skip the out-of-line element loops (NinoElements.Read, the dictionary, set and enumerable writes)
/// and build no immutable builder: the same bytes as before, and the same values back.
/// </summary>
[TestClass]
public class EmptyCollectionTests
{
    private static readonly byte[] EmptyBytes = NinoSerializer.Serialize(Array.Empty<int>());

    private static T Check<T>(T value) where T : class
    {
        var bytes = NinoSerializer.Serialize(value);
        CollectionAssert.AreEqual(EmptyBytes, bytes, typeof(T).Name);
        var back = NinoDeserializer.Deserialize<T>(bytes);
        Assert.IsNotNull(back, typeof(T).Name);
        CollectionAssert.AreEqual(EmptyBytes, NinoSerializer.Serialize(back), typeof(T).Name);
        return back;
    }

    [TestMethod]
    public void EmptyCollectionsRoundTrip()
    {
        Assert.AreEqual(0, Check(new List<InlineNote>()).Count);
        Assert.AreEqual(0, Check(new InlineNote[0]).Length);
        Assert.AreEqual(0, Check(new HashSet<string>()).Count);
        Assert.AreEqual(0, Check(new Dictionary<string, InlineNote>()).Count);
        Assert.AreEqual(0, Check(new Dictionary<int, int>()).Count);
#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD // PriorityQueue: .NET 6+ builds of Nino.Core only
        Assert.AreEqual(0, Check(new PriorityQueue<string, int>()).Count);
#endif
        Assert.AreEqual(0, Check<IList<InlineNote>>(new List<InlineNote>()).Count);
        Assert.AreEqual(0, Check<IReadOnlyCollection<string>>(new HashSet<string>()).Count);
        Assert.AreSame(ImmutableHashSet<string>.Empty, Check(ImmutableHashSet<string>.Empty));
        Assert.AreSame(ImmutableSortedSet<string>.Empty, Check(ImmutableSortedSet<string>.Empty));
        Assert.AreSame(ImmutableDictionary<string, InlineNote>.Empty, Check(ImmutableDictionary<string, InlineNote>.Empty));
        Assert.AreSame(ImmutableSortedDictionary<string, InlineNote>.Empty,
            Check(ImmutableSortedDictionary<string, InlineNote>.Empty));
    }

    [TestMethod]
    public void EmptyReadIntoExistingClearsIt()
    {
        var list = new List<InlineNote> { new() { Id = 1 } };
        NinoDeserializer.Deserialize(EmptyBytes, ref list);
        Assert.AreEqual(0, list.Count);
        var set = new HashSet<string> { "a" };
        NinoDeserializer.Deserialize(EmptyBytes, ref set);
        Assert.AreEqual(0, set.Count);
        var dictionary = new Dictionary<string, InlineNote> { ["a"] = new() };
        NinoDeserializer.Deserialize(EmptyBytes, ref dictionary);
        Assert.AreEqual(0, dictionary.Count);
    }
}
