using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

public enum GoldenKind : byte
{
    None,
    First,
    Second
}

[NinoType]
public struct GoldenPoint
{
    public int X;
    public int Y;
}

[NinoType(format: NinoFormat.Positional)]
public abstract class GoldenShape
{
    public string Name;
}

[NinoType]
public sealed class GoldenCircle : GoldenShape
{
    public float Radius;
}

[NinoType]
public sealed class GoldenRect : GoldenShape
{
    public GoldenPoint Min;
    public GoldenPoint Max;
}

[NinoType]
public sealed class GoldenData
{
    public bool Flag;
    public byte B;
    public short S;
    public int I;
    public long L;
    public double D;
    public decimal M;
    public char C;
    public GoldenKind Kind;
    public int? NullableSet;
    public int? NullableNull;
    public string Ascii;
    public string Latin1;
    public string Utf16;
    [NinoUtf8] public string Utf8;
    public string NullString;
    public int[] Ints;
    public List<string> Strings;
    public Dictionary<int, string> Map;
    public int[,] Grid;
    public (int, string) Tuple;
    public KeyValuePair<string, int> Pair;
    public List<GoldenShape> Shapes;
    public GoldenShape NullShape;
    public object Boxed;
}

/// <summary>
/// Pins the wire format. Every Nino.Core build (netstandard2.0, netstandard2.1, net8.0, net10.0) must produce and
/// read exactly these bytes; CI runs this suite against each of them.
/// </summary>
[TestClass]
public class GoldenBytesTests
{
    private static GoldenData Create() => new GoldenData
    {
        Flag = true,
        B = 0xAB,
        S = -2,
        I = 0x12345678,
        L = long.MinValue,
        D = 1.5,
        M = 12.34m,
        C = '中',
        Kind = GoldenKind.Second,
        NullableSet = 7,
        NullableNull = null,
        Ascii = "Nino",
        Latin1 = "Café",
        Utf16 = "中文",
        Utf8 = "ニノ😀",
        NullString = null,
        Ints = new[] { 1, 2, 3 },
        Strings = new List<string> { "a", null, "" },
        Map = new Dictionary<int, string> { { 1, "x" }, { 2, "y" } },
        Grid = new[,] { { 1, 2, 3 }, { 4, 5, 6 } },
        Tuple = (9, "t"),
        Pair = new KeyValuePair<string, int>("k", 5),
        Shapes = new List<GoldenShape>
        {
            new GoldenCircle { Name = "c", Radius = 2f },
            new GoldenRect { Name = "r", Min = new GoldenPoint { X = 1, Y = 2 }, Max = new GoldenPoint { X = 3, Y = 4 } },
            null
        },
        NullShape = null,
        Boxed = new GoldenPoint { X = 5, Y = 6 },
    };

    // Generated once and checked in; regenerate only for an intentional format change (last: a string of Latin-1
    // chars is written one byte per char, under the header ~length; before that, a [NinoUtf8] string carries its
    // byte count after its char count; before that, a version-tolerant object carries one length for its
    // whole body instead of one per inheritance level; this suite builds with WEAK_VERSION_TOLERANCE, so GoldenCircle
    // and GoldenRect are tolerant).
    private const string Expected =
        "011A01000001ABFEFF785634120000000000000080000000000000F83F0000020000000000D2040000000000002D4E02010700000000FBFFFFFF4E696E6FFBFFFFFF436166E9020000002D4E8765040000000A000000E3838BE3838EF09F9880FFFFFFFF0300000001000000020000000300000003000000FEFFFFFF61FFFFFFFF000000000200000001000000FEFFFFFF7802000000FEFFFFFF79020000000300000001000000020000000300000004000000050000000600000009000000FEFFFFFF74FEFFFFFF6B050000000300000099034A6809000000FEFFFFFF63000000409B0A31BF1D000000FEFFFFFF7208000000010000000200000008000000030000000400000000000000000000001F0A15F5080000000500000006000000";

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    [TestMethod]
    public void SerializedBytesMatchGolden()
    {
        var bytes = NinoSerializer.Serialize(Create());
        Console.WriteLine(Hex(bytes));
        Assert.AreEqual(Expected, Hex(bytes));
    }

    [TestMethod]
    public void GoldenBytesDeserialize()
    {
        var bytes = new byte[Expected.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(Expected.Substring(i * 2, 2), 16);

        var expected = Create();
        var value = NinoDeserializer.Deserialize<GoldenData>(bytes);
        Assert.AreEqual(expected.Flag, value.Flag);
        Assert.AreEqual(expected.B, value.B);
        Assert.AreEqual(expected.S, value.S);
        Assert.AreEqual(expected.I, value.I);
        Assert.AreEqual(expected.L, value.L);
        Assert.AreEqual(expected.D, value.D);
        Assert.AreEqual(expected.M, value.M);
        Assert.AreEqual(expected.C, value.C);
        Assert.AreEqual(expected.Kind, value.Kind);
        Assert.AreEqual(expected.NullableSet, value.NullableSet);
        Assert.IsNull(value.NullableNull);
        Assert.AreEqual(expected.Ascii, value.Ascii);
        Assert.AreEqual(expected.Latin1, value.Latin1);
        Assert.AreEqual(expected.Utf16, value.Utf16);
        Assert.AreEqual(expected.Utf8, value.Utf8);
        Assert.IsNull(value.NullString);
        CollectionAssert.AreEqual(expected.Ints, value.Ints);
        CollectionAssert.AreEqual(expected.Strings, value.Strings);
        CollectionAssert.AreEquivalent(expected.Map, value.Map);
        CollectionAssert.AreEqual(expected.Grid, value.Grid);
        Assert.AreEqual(expected.Tuple, value.Tuple);
        Assert.AreEqual(expected.Pair, value.Pair);
        Assert.AreEqual(3, value.Shapes.Count);
        Assert.AreEqual(2f, ((GoldenCircle)value.Shapes[0]).Radius);
        Assert.AreEqual("c", value.Shapes[0].Name);
        var rect = (GoldenRect)value.Shapes[1];
        Assert.AreEqual("r", rect.Name);
        Assert.AreEqual(4, rect.Max.Y);
        Assert.IsNull(value.Shapes[2]);
        Assert.IsNull(value.NullShape);
        Assert.AreEqual(expected.Boxed, value.Boxed);
    }
}
