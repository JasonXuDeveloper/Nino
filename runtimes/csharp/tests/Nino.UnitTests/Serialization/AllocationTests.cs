using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset;

#nullable disable
namespace Nino.UnitTests;

public enum AllocColor
{
    Red,
    Green,
    Blue,
}

[Flags]
public enum AllocBits : byte
{
    None = 0,
    A = 1,
    B = 2,
}

[NinoType(format: NinoFormat.VersionTolerant)]
public struct AllocStruct
{
    public int A;
    public long? B;
    public AllocColor C;
}

/// <summary>A struct with a reference: its <c>T?</c> is never raw (a presence byte and the members).</summary>
[NinoType(format: NinoFormat.VersionTolerant)]
public struct AllocRefStruct
{
    public string S;
    public long A;
    public long B;
}

/// <summary>A struct with a reference that fits 16 bytes: its <c>T?</c> took another path in a read over present
/// values than <see cref="AllocRefStruct"/>'s on .NET 11.</summary>
[NinoType(format: NinoFormat.VersionTolerant)]
public struct AllocSmallRefStruct
{
    public int A;
    public string S;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public struct AllocGenericStruct<T>
{
    public T Value;
    public int Tag;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class AllocGenericClass<T>
{
    public T Value;
    public int Tag;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class AllocFlat
{
    public int A;
    public long B;
    public double C;
    public AllocColor D;
    public int? E;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public abstract class AllocBase
{
    public int X;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class AllocSubA : AllocBase
{
    public long Y;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class AllocSubB : AllocBase
{
    public double Z;
    public int? W;
}

/// <summary>Every shape below as a member at once.</summary>
[NinoType(format: NinoFormat.VersionTolerant)]
public class AllocHolder
{
    public int? A;
    public NinoNullable<long> B;
    public AllocColor C;
    public AllocBits D;
    public SubsetRawStruct E;
    public (int, long?) F;
    public KeyValuePair<int, int?> G;
    public AllocGenericStruct<int?> H;
    public int?[] I;
    public List<SubsetRawStruct> J;
    public HashSet<int?> K;
    public Dictionary<int, int?> L;
    public AllocRefStruct? M;
    public AllocBase N;
}

/// <summary>
/// Nino boxes nothing on a read or a write (the owner's rule; <c>optimize</c>, "Never box"): a write into a buffer
/// that has room allocates nothing, a read allocates only the objects it returns (the same bytes as building them by
/// hand), and a read into an existing value of the same shape allocates nothing. Each shape is called at a call site of
/// its own type, so the interceptors are taken where the build has them. The fewest bytes of any round count: the
/// first rounds run Tier0 code, which keeps boxes the optimizer removes, until the methods are compiled again
/// (#449: <c>HashSet&lt;int?&gt;</c> boxed every element in a read, one allocation each).
/// </summary>
[TestClass]
public class AllocationTests
{
    private const int Calls = 64;
    private const int Rounds = 80;

    private static readonly NinoArrayBufferWriter Buffer = new(1 << 16);

    private static class Sink<T>
    {
        public static T Value;
    }

    private delegate void ReadInto<T>(byte[] bytes, ref T value);

    /// <summary>
    /// The fewest bytes <paramref name="action"/> allocated per call in any round, stopping at the first round that
    /// allocated <paramref name="expected"/> or fewer; the rounds are apart, so that the runtime compiles the methods
    /// at their final tier meanwhile.
    /// </summary>
    private static long PerCall(Action action, long expected = 0)
    {
        long best = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Calls; i++)
                action();
            best = Math.Min(best, (GC.GetAllocatedBytesForCurrentThread() - before) / Calls);
            if (best <= expected)
                return best;
            Thread.Sleep(15);
        }

        return best;
    }

    /// <summary>What building the read's result by hand allocates.</summary>
    private static long Built(Func<object> build) => PerCall(() => Sink<object>.Value = build(), -1);

    private static readonly List<string> Failures = new();

    private static void Check(string what, long actual, long expected)
    {
        if (actual != expected)
            Failures.Add($"{what}: {actual} bytes per call, expected {expected}");
    }

    /// <summary>
    /// Writes <paramref name="value"/> (0 bytes), reads it as a new value (what <paramref name="build"/> allocates, or
    /// nothing for a value type) and into the value read before (nothing).
    /// </summary>
    private static void Shape<T>(string name, T value, Action<T> write, Func<byte[], T> read, ReadInto<T> readInto,
        Func<object> build = null, bool runtimeBoxesReadInto = false)
    {
        Buffer.ResetWrittenCount();
        write(value);
        byte[] bytes = Buffer.WrittenSpan.ToArray();
        Check(name + " write", PerCall(() =>
        {
            Buffer.ResetWrittenCount();
            write(value);
        }), 0);

        long expected = typeof(T).IsValueType ? 0 : Built(build);
        Check(name + " read", PerCall(() => Sink<T>.Value = read(bytes), expected), expected);

        if (runtimeBoxesReadInto)
            return;
        T target = read(bytes);
        Check(name + " read into", PerCall(() => readInto(bytes, ref target)), 0);
    }

    /// <summary>
    /// Reads <paramref name="a"/>'s bytes and <paramref name="b"/>'s bytes in turn into one value: a present value
    /// replaced by null and back (a discarded <c>T?</c> was boxed to be tested for null).
    /// </summary>
    private static void Alternating<T>(string name, T a, T b, T target, Action<T> write, ReadInto<T> readInto)
    {
        Buffer.ResetWrittenCount();
        write(a);
        byte[] first = Buffer.WrittenSpan.ToArray();
        Buffer.ResetWrittenCount();
        write(b);
        byte[] second = Buffer.WrittenSpan.ToArray();
        Check(name + " read into, alternating", PerCall(() =>
        {
            readInto(first, ref target);
            readInto(second, ref target);
        }), 0);
    }

    private static int?[] Nullables(int count)
    {
        var values = new int?[count];
        for (int i = 0; i < count; i++)
            values[i] = i % 3 == 0 ? null : i;
        return values;
    }

    [TestMethod]
    public void ValuesAllocateNothing()
    {
        Failures.Clear();
        Shape("int", 42, v => NinoSerializer.Serialize(v, Buffer), b => NinoDeserializer.Deserialize<int>(b),
            (byte[] b, ref int v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("long?", (long?)42, v => NinoSerializer.Serialize(v, Buffer), b => NinoDeserializer.Deserialize<long?>(b),
            (byte[] b, ref long? v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("NinoNullable<long>", new NinoNullable<long>(42), v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<NinoNullable<long>>(b),
            (byte[] b, ref NinoNullable<long> v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("enum", AllocColor.Blue, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<AllocColor>(b),
            (byte[] b, ref AllocColor v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("flags", AllocBits.A | AllocBits.B, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<AllocBits>(b),
            (byte[] b, ref AllocBits v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("enum?", (AllocColor?)AllocColor.Green, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<AllocColor?>(b),
            (byte[] b, ref AllocColor? v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("raw struct", new SubsetRawStruct { X = 1, W = 2, Y = 3 }, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<SubsetRawStruct>(b),
            (byte[] b, ref SubsetRawStruct v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("raw struct?", (SubsetRawStruct?)new SubsetRawStruct { X = 1 }, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<SubsetRawStruct?>(b),
            (byte[] b, ref SubsetRawStruct? v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("struct", new AllocStruct { A = 1, B = 2, C = AllocColor.Red }, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<AllocStruct>(b),
            (byte[] b, ref AllocStruct v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("struct with a reference?", (AllocRefStruct?)new AllocRefStruct { A = 1 },
            v => NinoSerializer.Serialize(v, Buffer), b => NinoDeserializer.Deserialize<AllocRefStruct?>(b),
            (byte[] b, ref AllocRefStruct? v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("generic struct of int?", new AllocGenericStruct<int?> { Value = 1, Tag = 2 },
            v => NinoSerializer.Serialize(v, Buffer), b => NinoDeserializer.Deserialize<AllocGenericStruct<int?>>(b),
            (byte[] b, ref AllocGenericStruct<int?> v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("generic struct of an enum", new AllocGenericStruct<AllocColor> { Value = AllocColor.Blue },
            v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<AllocGenericStruct<AllocColor>>(b),
            (byte[] b, ref AllocGenericStruct<AllocColor> v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("value tuple", (1, (long?)2), v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<(int, long?)>(b),
            (byte[] b, ref (int, long?) v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("value tuple of T?, enum, struct", ((int?)1, AllocColor.Red, new SubsetRawStruct { X = 1 }),
            v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<(int?, AllocColor, SubsetRawStruct)>(b),
            (byte[] b, ref (int?, AllocColor, SubsetRawStruct) v) => NinoDeserializer.Deserialize(b, ref v));
        Shape("KeyValuePair", new KeyValuePair<int, int?>(1, 2), v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<KeyValuePair<int, int?>>(b),
            (byte[] b, ref KeyValuePair<int, int?> v) => NinoDeserializer.Deserialize(b, ref v));
        Assert.AreEqual(0, Failures.Count, string.Join("\n", Failures));
    }

    [TestMethod]
    public void CollectionsAllocateTheirStorageOnly()
    {
        Failures.Clear();
        // As many as the benchmark's collections.
        const int n = 1000;
        var nullables = Nullables(n);
        var raws = new List<SubsetRawStruct>();
        var tuples = new List<(int, long?)>();
        var pairs = new KeyValuePair<int, int?>[n];
        var set = new HashSet<int?>();
        var longs = new HashSet<long?>();
        var map = new Dictionary<int, int?>();
        var keyed = new Dictionary<int?, int>();
        var wrapped = new NinoNullable<long>[n];
        for (int i = 0; i < n; i++)
        {
            raws.Add(new SubsetRawStruct { X = i, Y = i * 3 });
            tuples.Add((i, i % 2 == 0 ? null : i));
            pairs[i] = new KeyValuePair<int, int?>(i, nullables[i]);
            set.Add(nullables[i]);
            longs.Add(i % 4 == 0 ? null : i);
            map[i] = nullables[i];
            keyed[i] = i;
            wrapped[i] = i % 3 == 0 ? default : new NinoNullable<long>(i);
        }

        Shape("int?[]", nullables, v => NinoSerializer.Serialize(v, Buffer), b => NinoDeserializer.Deserialize<int?[]>(b),
            (byte[] b, ref int?[] v) => NinoDeserializer.Deserialize(b, ref v), () => new int?[n]);
        Shape("NinoNullable<long>[]", wrapped, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<NinoNullable<long>[]>(b),
            (byte[] b, ref NinoNullable<long>[] v) => NinoDeserializer.Deserialize(b, ref v),
            () => new NinoNullable<long>[n]);
        Shape("List<int?>", new List<int?>(nullables), v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<List<int?>>(b),
            (byte[] b, ref List<int?> v) => NinoDeserializer.Deserialize(b, ref v), () => new List<int?>(n));
        Shape("List<raw struct>", raws, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<List<SubsetRawStruct>>(b),
            (byte[] b, ref List<SubsetRawStruct> v) => NinoDeserializer.Deserialize(b, ref v),
            () => new List<SubsetRawStruct>(n));
        Shape("List<(int, long?)>", tuples, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<List<(int, long?)>>(b),
            (byte[] b, ref List<(int, long?)> v) => NinoDeserializer.Deserialize(b, ref v),
            () => new List<(int, long?)>(n));
        Shape("KeyValuePair[]", pairs, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<KeyValuePair<int, int?>[]>(b),
            (byte[] b, ref KeyValuePair<int, int?>[] v) => NinoDeserializer.Deserialize(b, ref v),
            () => new KeyValuePair<int, int?>[n]);
        Shape("HashSet<int?>", set, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<HashSet<int?>>(b),
            (byte[] b, ref HashSet<int?> v) => NinoDeserializer.Deserialize(b, ref v),
            () => new HashSet<int?>(set.Count));
        // The benchmark's set (CollectionsTest.NullableHashSetInts): on .NET 11 its read boxed every value (#449).
        var sevens = new HashSet<int?>();
        for (int i = 0; i < n; i++)
            sevens.Add(i == 0 ? null : i * 7);
        Shape("HashSet<int?> of sevens", sevens, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<HashSet<int?>>(b),
            (byte[] b, ref HashSet<int?> v) => NinoDeserializer.Deserialize(b, ref v), () => new HashSet<int?>(n));
        Shape("HashSet<long?>", longs, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<HashSet<long?>>(b),
            (byte[] b, ref HashSet<long?> v) => NinoDeserializer.Deserialize(b, ref v),
            () => new HashSet<long?>(longs.Count));
        Shape("Dictionary<int, int?>", map, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<Dictionary<int, int?>>(b),
            (byte[] b, ref Dictionary<int, int?> v) => NinoDeserializer.Deserialize(b, ref v),
            () => new Dictionary<int, int?>(n));
        Shape("Dictionary<int?, int>", keyed, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<Dictionary<int?, int>>(b),
            (byte[] b, ref Dictionary<int?, int> v) => NinoDeserializer.Deserialize(b, ref v),
            () => new Dictionary<int?, int>(n),
            // .NET 11.0's Dictionary boxes a T? key in every lookup and insert inlined into the caller (its
            // `key == null`): the read into an existing dictionary looks each key up, 24 bytes per key.
            runtimeBoxesReadInto: Environment.Version.Major >= 11);
        Assert.AreEqual(0, Failures.Count, string.Join("\n", Failures));
    }

    [TestMethod]
    public void ObjectsAllocateThemselvesOnly()
    {
        Failures.Clear();
        var subs = new List<AllocBase>();
        for (int i = 0; i < 50; i++)
        {
            subs.Add(new AllocSubA { X = i, Y = i });
            subs.Add(new AllocSubB { X = i, Z = i, W = i % 2 == 0 ? null : i });
        }

        Shape("flat class", new AllocFlat { A = 1, B = 2, C = 3, D = AllocColor.Blue, E = 5 },
            v => NinoSerializer.Serialize(v, Buffer), b => NinoDeserializer.Deserialize<AllocFlat>(b),
            (byte[] b, ref AllocFlat v) => NinoDeserializer.Deserialize(b, ref v), () => new AllocFlat());
        Shape("generic class of int?", new AllocGenericClass<int?> { Value = 1, Tag = 2 },
            v => NinoSerializer.Serialize(v, Buffer), b => NinoDeserializer.Deserialize<AllocGenericClass<int?>>(b),
            (byte[] b, ref AllocGenericClass<int?> v) => NinoDeserializer.Deserialize(b, ref v),
            () => new AllocGenericClass<int?>());
        Shape("generic class of a struct", new AllocGenericClass<SubsetRawStruct> { Tag = 2 },
            v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<AllocGenericClass<SubsetRawStruct>>(b),
            (byte[] b, ref AllocGenericClass<SubsetRawStruct> v) => NinoDeserializer.Deserialize(b, ref v),
            () => new AllocGenericClass<SubsetRawStruct>());
        Shape("polymorphic base", (AllocBase)new AllocSubB { X = 1, Z = 2, W = 3 },
            v => NinoSerializer.Serialize(v, Buffer), b => NinoDeserializer.Deserialize<AllocBase>(b),
            (byte[] b, ref AllocBase v) => NinoDeserializer.Deserialize(b, ref v), () => new AllocSubB());
        Shape("List<polymorphic base>", subs, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<List<AllocBase>>(b),
            (byte[] b, ref List<AllocBase> v) => NinoDeserializer.Deserialize(b, ref v), () =>
            {
                var list = new List<AllocBase>(subs.Count);
                for (int i = 0; i < subs.Count; i += 2)
                {
                    list.Add(new AllocSubA());
                    list.Add(new AllocSubB());
                }

                return list;
            });

        var nullables = Nullables(10);
        var holder = new AllocHolder
        {
            A = 5, B = new NinoNullable<long>(7), C = AllocColor.Blue, D = AllocBits.A | AllocBits.B,
            E = new SubsetRawStruct { X = 1 }, F = (1, 2), G = new KeyValuePair<int, int?>(1, null),
            H = new AllocGenericStruct<int?> { Value = 3 }, I = nullables,
            J = new List<SubsetRawStruct> { new() { X = 1 }, new() { X = 2 } }, K = new HashSet<int?>(nullables),
            L = new Dictionary<int, int?> { [1] = 1, [2] = null }, M = new AllocRefStruct { A = 1 },
            N = new AllocSubA { X = 1 },
        };
        Shape("class of every shape", holder, v => NinoSerializer.Serialize(v, Buffer),
            b => NinoDeserializer.Deserialize<AllocHolder>(b),
            (byte[] b, ref AllocHolder v) => NinoDeserializer.Deserialize(b, ref v), () => new AllocHolder
            {
                I = new int?[nullables.Length], J = new List<SubsetRawStruct>(2),
                K = new HashSet<int?>(holder.K.Count), L = new Dictionary<int, int?>(2), N = new AllocSubA(),
            });
        Assert.AreEqual(0, Failures.Count, string.Join("\n", Failures));
    }

    [TestMethod]
    public void NullsOverValuesAllocateNothing()
    {
        Failures.Clear();
        var present = new AllocRefStruct?[] { new AllocRefStruct { A = 1, S = "a" }, new AllocRefStruct { A = 2 } };
        var absent = new AllocRefStruct?[2];
        Alternating("struct with a reference?", (AllocRefStruct?)present[0], null, null,
            v => NinoSerializer.Serialize(v, Buffer),
            (byte[] b, ref AllocRefStruct? v) => NinoDeserializer.Deserialize(b, ref v));
        Alternating("struct with a reference?[]", present, absent, new AllocRefStruct?[2],
            v => NinoSerializer.Serialize(v, Buffer),
            (byte[] b, ref AllocRefStruct?[] v) => NinoDeserializer.Deserialize(b, ref v));
        Alternating("List<struct with a reference?>", new List<AllocRefStruct?>(present),
            new List<AllocRefStruct?>(absent), new List<AllocRefStruct?>(absent),
            v => NinoSerializer.Serialize(v, Buffer),
            (byte[] b, ref List<AllocRefStruct?> v) => NinoDeserializer.Deserialize(b, ref v));
        Alternating("generic struct of a struct with a reference?",
            new AllocGenericStruct<AllocRefStruct?> { Value = present[0] }, default, default,
            v => NinoSerializer.Serialize(v, Buffer),
            (byte[] b, ref AllocGenericStruct<AllocRefStruct?> v) => NinoDeserializer.Deserialize(b, ref v));
        // On .NET 11, cc8f4132 boxed each of these values a null replaced (32 bytes each).
        var small = new AllocSmallRefStruct?[] { new AllocSmallRefStruct { A = 1, S = "a" }, new AllocSmallRefStruct { A = 2 } };
        Alternating("small struct with a reference?[]", small, new AllocSmallRefStruct?[2], new AllocSmallRefStruct?[2],
            v => NinoSerializer.Serialize(v, Buffer),
            (byte[] b, ref AllocSmallRefStruct?[] v) => NinoDeserializer.Deserialize(b, ref v));
        Alternating("List<small struct with a reference?>", new List<AllocSmallRefStruct?>(small),
            new List<AllocSmallRefStruct?> { null, null }, new List<AllocSmallRefStruct?> { null, null },
            v => NinoSerializer.Serialize(v, Buffer),
            (byte[] b, ref List<AllocSmallRefStruct?> v) => NinoDeserializer.Deserialize(b, ref v));
        Alternating("int?[]", Nullables(10), new int?[10], new int?[10], v => NinoSerializer.Serialize(v, Buffer),
            (byte[] b, ref int?[] v) => NinoDeserializer.Deserialize(b, ref v));
        Alternating("Dictionary<int, struct with a reference?>",
            new Dictionary<int, AllocRefStruct?> { [1] = present[0] },
            new Dictionary<int, AllocRefStruct?> { [1] = null }, new Dictionary<int, AllocRefStruct?>(),
            v => NinoSerializer.Serialize(v, Buffer),
            (byte[] b, ref Dictionary<int, AllocRefStruct?> v) => NinoDeserializer.Deserialize(b, ref v));
        Assert.AreEqual(0, Failures.Count, string.Join("\n", Failures));
    }
}
