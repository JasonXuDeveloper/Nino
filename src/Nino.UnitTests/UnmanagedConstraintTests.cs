using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// A generic [NinoType] whose generated formatter carries the type's C# <c>unmanaged</c> constraint. It must bind for
/// every unmanaged type, including ones Nino does not write as raw memory (only <c>[NinoRaw]</c> parameters of the
/// raw formatters require that).
/// </summary>
[NinoType]
public sealed class UnmanagedBox<T> where T : unmanaged
{
    public T Value;
    public T[] Values;
    public List<T> List;
}

/// <summary>Unmanaged for C#, but written member by member (version tolerant), so not raw.</summary>
[NinoType(format: NinoFormat.VersionTolerant)]
public struct TolerantPoint
{
    public int X;
    public int Y;
}

[NinoType]
public sealed class UnmanagedBoxHolder
{
    public UnmanagedBox<(int, long)> Tuple;
    public UnmanagedBox<TolerantPoint> Point;
    public UnmanagedBox<int> Int;
}

// Same shape as RuntimeBox<T>, with the member type known to the generator.
[NinoType] public sealed class TupleBoxHolder { public UnmanagedBox<(int, long)> Value; }
[NinoType] public sealed class PointBoxHolder { public UnmanagedBox<TolerantPoint> Value; }

[TestClass]
public class UnmanagedConstraintTests
{
    private static UnmanagedBox<T> Box<T>(T a, T b) where T : unmanaged =>
        new UnmanagedBox<T> { Value = a, Values = new[] { a, b }, List = new List<T> { b } };

    private static void AssertBox<T>(UnmanagedBox<T> expected, UnmanagedBox<T> actual) where T : unmanaged
    {
        Assert.AreEqual(expected.Value, actual.Value);
        CollectionAssert.AreEqual(expected.Values, actual.Values);
        CollectionAssert.AreEqual(expected.List, actual.List);
    }

    [TestMethod]
    public void GeneratedPath()
    {
        var holder = new UnmanagedBoxHolder
        {
            Tuple = Box((1, 2L), (3, 4L)),
            Point = Box(new TolerantPoint { X = 1, Y = 2 }, new TolerantPoint { X = 3, Y = 4 }),
            Int = Box(5, 6),
        };
        var back = NinoDeserializer.Deserialize<UnmanagedBoxHolder>(NinoSerializer.Serialize(holder));
        AssertBox(holder.Tuple, back.Tuple);
        AssertBox(holder.Point, back.Point);
        AssertBox(holder.Int, back.Int);
    }

    [TestMethod]
    public void RuntimePathWritesTheSameBytes()
    {
        var tuple = Box((1, 2L), (3, 4L));
        var point = Box(new TolerantPoint { X = 1, Y = 2 }, new TolerantPoint { X = 3, Y = 4 });

        // RuntimeBox<T>'s member is bound by the runtime resolver, through the generated registration.
        var runtimeTuple = NinoSerializer.Serialize(new RuntimeBox<UnmanagedBox<(int, long)>> { Value = tuple });
        var runtimePoint = NinoSerializer.Serialize(new RuntimeBox<UnmanagedBox<TolerantPoint>> { Value = point });
        AssertBox(tuple, NinoDeserializer.Deserialize<RuntimeBox<UnmanagedBox<(int, long)>>>(runtimeTuple).Value);
        AssertBox(point, NinoDeserializer.Deserialize<RuntimeBox<UnmanagedBox<TolerantPoint>>>(runtimePoint).Value);

        // The generated formatter (named directly) and the runtime-bound one write the same bytes.
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new TupleBoxHolder { Value = tuple }), runtimeTuple);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new PointBoxHolder { Value = point }), runtimePoint);
    }

    [TestMethod]
    public void TopLevel()
    {
        var tuple = Box((7, 8L), (9, 10L));
        AssertBox(tuple, NinoDeserializer.Deserialize<UnmanagedBox<(int, long)>>(NinoSerializer.Serialize(tuple)));
        var point = Box(new TolerantPoint { X = 7 }, new TolerantPoint { Y = 8 });
        AssertBox(point, NinoDeserializer.Deserialize<UnmanagedBox<TolerantPoint>>(NinoSerializer.Serialize(point)));
    }
}
