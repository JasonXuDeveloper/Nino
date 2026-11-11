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
    [TestMethod]
    public void CollectionLayoutsAreRecognized()
    {
        // Each collection Nino reads and writes through its fields is checked against the runtime's layout at
        // startup, and its public API is used when the check fails. On .NET every check must pass, or the fast
        // paths are silently off.
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.List, "List<T>");
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.Stack, "Stack<T>");
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
