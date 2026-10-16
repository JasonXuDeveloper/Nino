using System.Collections.Generic;
using Nino.Core;

// Deeply nested types without WEAK_VERSION_TOLERANCE (no regions): their deferred bodies follow the top-level
// object instead of a region's section.
namespace Nino.UnitTests.Subset.Deep
{
    [NinoType]
    public sealed class PlainLink
    {
        public int Value;
        public PlainLink Next;
    }

    [NinoType]
    public class PlainTree
    {
        public int Value;
        public List<PlainTree> Children;
    }

    [NinoType]
    public class PlainTreeLeaf : PlainTree
    {
        public string Tag;
    }

    [NinoType(trackReferences: true)]
    public sealed class PlainTracked
    {
        public int Value;
        public PlainTracked Next;
        public PlainTracked Prev;
    }

    [NinoType]
    public sealed class PlainRecordLike
    {
        public PlainRecordLike(int value, PlainRecordLike next)
        {
            Value = value;
            Next = next;
        }

        public int Value { get; }
        public PlainRecordLike Next { get; }
    }

    // A plain body holding a version-tolerant one: regions open below a deferral point.
    [NinoType]
    public sealed class PlainOuter
    {
        public int Value;
        public TolerantInner Inner;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class TolerantInner
    {
        public int Value;
        public PlainOuter Outer;
    }
}
