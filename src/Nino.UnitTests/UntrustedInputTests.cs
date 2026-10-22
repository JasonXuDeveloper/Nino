using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>A struct that holds itself through a list: nested data cannot be deferred (a struct has no slot).</summary>
[NinoType]
public struct UntrustedStructNode
{
    public int V;
    public List<UntrustedStructNode> Kids;
}

/// <summary>
/// Data from an untrusted source: every malformed input throws InvalidOperationException with a message starting
/// "Nino:", never another exception, a crash, or a value holding memory the data did not write.
/// </summary>
[TestClass]
public class UntrustedInputTests
{
    private static void Invalid<T>(byte[] data)
    {
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<T>(data));
        StringAssert.StartsWith(e.Message, "Nino:");
    }

    private static byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    [TestMethod]
    public void DeepStructsThrowInsteadOfOverflowingTheStack()
    {
        // [V][count = 1] repeated: each struct holds the next, a million deep. Read on a 1 MB thread, which the
        // depth alone overflowed (the process crashed).
        var data = Enumerable.Repeat(Ints(7, 1), 1_000_000).SelectMany(b => b).ToArray();
        Exception thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                NinoDeserializer.Deserialize<UntrustedStructNode>(data);
            }
            catch (Exception e)
            {
                thrown = e;
            }
        }, 1 << 20);
        thread.Start();
        thread.Join();
        Assert.IsInstanceOfType<InvalidOperationException>(thrown);
        StringAssert.StartsWith(thrown.Message, "Nino:");

        // Nesting the writer accepts still round-trips.
        var node = new UntrustedStructNode { V = 0 };
        for (int i = 1; i < 100; i++)
            node = new UntrustedStructNode { V = i, Kids = new List<UntrustedStructNode> { node } };
        var back = NinoDeserializer.Deserialize<UntrustedStructNode>(NinoSerializer.Serialize(node));
        Assert.AreEqual(99, back.V);
    }

    [TestMethod]
    public void LengthsAreCheckedBeforeAllocating()
    {
        // A StringBuilder of 2^30 chars from 4 bytes.
        Invalid<StringBuilder>(Ints(0x3FFFFFFF));
        Invalid<StringBuilder>(Ints(-0x3FFFFFFF - 2));
        // Dimensions whose product overflows a long (to 0), or that the runtime cannot allocate even empty.
        Invalid<int[,,]>(Ints(1 << 22, 1 << 21, 1 << 21));
        Invalid<int[,,]>(Ints(0x4000, 0x2000, 0x2000));
        Invalid<int[,,]>(Ints(0x10000, 0x10000, 0));
        Assert.AreEqual(0, NinoDeserializer.Deserialize<int[,,]>(Ints(0, 0x10000, 0x10000)).Length);
    }

    [TestMethod]
    public void AFailedReadLeavesNoUninitializedMemory()
    {
        // 2,000 elements of 8 bytes claimed (16 KB, allocated uninitialized), 2,000 bytes given: rejected before
        // the array is made, so the existing value keeps no stale heap memory.
        var data = Ints(2000).Concat(new byte[2000]).ToArray();
        var list = new List<(int, int)>();
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize(data, ref list));
        Assert.IsTrue(list.All(x => x == default));
        var array = Array.Empty<(int, int)>();
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize(data, ref array));
        Assert.IsTrue(array.All(x => x == default));
        Invalid<Memory<long>>(data);
    }

    [TestMethod]
    public void InvalidValuesThrowAsInvalidData()
    {
        // A null key: [count 1][key: null string][value].
        var nullKey = Ints(1, -1, 5);
        Invalid<Dictionary<string, int>>(nullKey);
        Invalid<SortedDictionary<string, int>>(nullKey);
        Invalid<SortedList<string, int>>(nullKey);
        Invalid<System.Collections.Concurrent.ConcurrentDictionary<string, int>>(nullKey);
        Invalid<System.Collections.Immutable.ImmutableDictionary<string, int>>(nullKey);
        Invalid<IDictionary<string, int>>(nullKey);

        // Values the types' constructors reject.
        Invalid<Version>(Ints(1, -5, -1, -1));
        Invalid<System.Globalization.CultureInfo>(NinoSerializer.Serialize("a\u0000b!"));
        Invalid<TimeZoneInfo>(NinoSerializer.Serialize("No/Such_Zone")
            .Concat(NinoSerializer.Serialize((string)null)).ToArray());
        Invalid<TimeZoneInfo>(NinoSerializer.Serialize("Custom")
            .Concat(NinoSerializer.Serialize("not;serialized;rules")).ToArray());
        Invalid<Uri>(NinoSerializer.Serialize("http://[::1"));
        // A UTC time or offset out of range.
        Invalid<DateTimeOffset>(BitConverter.GetBytes(long.MaxValue).Concat(BitConverter.GetBytes((short)0)).ToArray());
    }
}
