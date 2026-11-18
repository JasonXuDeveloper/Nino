using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using Nino.UnitTests;

// A user's formatter with nothing but Serialize and Deserialize, bound as the element of every collection.
[assembly: NinoFormatter(typeof(LabelFormatter))]

namespace Nino.UnitTests;

/// <summary>A value Nino has no formatter for: its code, then its text.</summary>
public readonly record struct Label(string Text, int Code);

/// <summary>Writes a <see cref="Label"/> as a <c>(int, string)</c> is written. No capability members.</summary>
public struct LabelFormatter : INinoFormatter<Label>
{
    public void Serialize(Label value, ref Writer writer)
    {
        writer.Write(value.Code);
        writer.Write(value.Text);
    }

    public void Deserialize(ref Reader reader, ref Label value)
    {
        reader.Read(out int code);
        reader.Read(out string text);
        value = new Label(text, code);
    }
}

[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class LabelCollections
{
    public List<Label> List;
    public Label[] Array;
    public HashSet<Label> Set;
    public Dictionary<Label, Label> Map;
    public LinkedList<Label> Linked;
    public Queue<Label> Queue;
    public Stack<Label> Stack;
    public IList<Label> Interface;
    public ImmutableArray<Label> Immutable;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class TupleCollections
{
    public List<(int, string)> List;
    public (int, string)[] Array;
    public HashSet<(int, string)> Set;
    public Dictionary<(int, string), (int, string)> Map;
    public LinkedList<(int, string)> Linked;
    public Queue<(int, string)> Queue;
    public Stack<(int, string)> Stack;
    public IList<(int, string)> Interface;
    public ImmutableArray<(int, string)> Immutable;
}

/// <summary>Six bytes on the wire (X, then Y), eight in memory: its padding is never written.</summary>
public struct Cell
{
    public int X;
    public short Y;
}

/// <summary>A user's formatter of <see cref="Cell"/> with the block members: collections move cells as blocks.</summary>
public struct CellBlockFormatter : INinoFormatter<Cell>
{
    public void Serialize(Cell value, ref Writer writer) => WriteFixed(in value, ref writer.Reserve(6));

    public void Deserialize(ref Reader reader, ref Cell value) => ReadBlock(ref value, ref reader.ReadBlock(6));

    public int FixedSize => 6;

    public void WriteFixed(in Cell value, ref byte destination)
    {
        Unsafe.WriteUnaligned(ref destination, value.X);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 4), value.Y);
    }

    public NinoBlockMap BlockMap()
    {
        var cell = default(Cell);
        return new NinoBlockMap().Raw<int>(NinoBlocks.Offset(ref cell, ref cell.X), 0)
            .Raw<short>(NinoBlocks.Offset(ref cell, ref cell.Y), 4);
    }

    public void ReadBlock(ref Cell value, ref byte source)
    {
        value.X = Unsafe.ReadUnaligned<int>(ref source);
        value.Y = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref source, 4));
    }
}

/// <summary>The same bytes as <see cref="CellBlockFormatter"/>, value by value: no capability members.</summary>
public struct CellPlainFormatter : INinoFormatter<Cell>
{
    public void Serialize(Cell value, ref Writer writer)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    public void Deserialize(ref Reader reader, ref Cell value)
    {
        reader.Read(out value.X);
        reader.Read(out value.Y);
    }
}

/// <summary>
/// Users' formatters as the elements of collections: one with only Serialize and Deserialize (every capability of
/// <see cref="INinoFormatter{T}"/> its default) and one with the block members, each writing the bytes of the type it
/// stands in for.
/// </summary>
[TestClass]
public class ElementFormatterTests
{
    private static readonly Label[] Labels = { new("a", 1), new("bc", -2), new("", 3), new(null, 4) };

    private static readonly Cell[] Cells = Enumerable.Range(0, 37)
        .Select(i => new Cell { X = i * 1_000_003, Y = (short)(i - 18) }).ToArray();

    [TestMethod]
    public void PlainFormatterHasNoCapabilities()
    {
        Assert.AreEqual(-1, NinoBlockPlan<Label, LabelFormatter>.Size);
        Assert.AreEqual(-1, NinoSlots<Label, LabelFormatter>.Stride);
        Assert.AreEqual(-1, NinoBlockPlan<Cell, CellPlainFormatter>.Size);
        Assert.AreEqual(-1, NinoSlots<Cell, CellPlainFormatter>.FixedStride);
        Assert.AreEqual(6, NinoBlockPlan<Cell, CellBlockFormatter>.Size);
        Assert.AreEqual(6, NinoSlots<Cell, CellBlockFormatter>.FixedStride);
    }

    [TestMethod]
    public void PlainFormatterAsElementOfEveryCollection()
    {
        var value = new LabelCollections
        {
            List = Labels.ToList(),
            Array = Labels,
            Set = new HashSet<Label>(Labels),
            Map = Labels.ToDictionary(x => x, x => new Label(x.Text + "!", -x.Code)),
            Linked = new LinkedList<Label>(Labels),
            Queue = new Queue<Label>(Labels),
            Stack = new Stack<Label>(Labels),
            Interface = Labels.ToList(),
            Immutable = Labels.ToImmutableArray(),
        };
        static (int, string) T(Label x) => (x.Code, x.Text);
        var tuples = new TupleCollections
        {
            List = Labels.Select(T).ToList(),
            Array = Labels.Select(T).ToArray(),
            Set = new HashSet<(int, string)>(Labels.Select(T)),
            Map = value.Map.ToDictionary(x => T(x.Key), x => T(x.Value)),
            Linked = new LinkedList<(int, string)>(Labels.Select(T)),
            Queue = new Queue<(int, string)>(Labels.Select(T)),
            Stack = new Stack<(int, string)>(Labels.Select(T)),
            Interface = Labels.Select(T).ToList(),
            Immutable = Labels.Select(T).ToImmutableArray(),
        };

        byte[] bytes = NinoSerializer.Serialize(value);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(tuples), bytes);

        var read = NinoDeserializer.Deserialize<LabelCollections>(bytes);
        CollectionAssert.AreEqual(value.List, read.List);
        CollectionAssert.AreEqual(value.Array, read.Array);
        Assert.IsTrue(value.Set.SetEquals(read.Set));
        CollectionAssert.AreEquivalent(value.Map.ToList(), read.Map.ToList());
        CollectionAssert.AreEqual(value.Linked.ToList(), read.Linked.ToList());
        CollectionAssert.AreEqual(value.Queue.ToList(), read.Queue.ToList());
        CollectionAssert.AreEqual(value.Stack.ToList(), read.Stack.ToList());
        CollectionAssert.AreEqual(value.Interface.ToList(), read.Interface.ToList());
        CollectionAssert.AreEqual(value.Immutable.ToList(), read.Immutable.ToList());

        // Resolved at run time rather than named by generated code.
        CollectionAssert.AreEqual(NinoSerializer.Serialize(tuples.List), NinoSerializer.Serialize(value.List));
        CollectionAssert.AreEqual(value.List,
            NinoDeserializer.Deserialize<List<Label>>(NinoSerializer.Serialize(value.List)));
    }

    [TestMethod]
    public void BlockFormatterAsElementOfEveryCollection()
    {
        var pairs = Cells.Select(c => (c.X, c.Y)).ToArray();
        RoundTrip<List<Cell>, ListFormatter<List<Cell>, Cell, CellBlockFormatter>,
            ListFormatter<List<Cell>, Cell, CellPlainFormatter>>(Cells.ToList(),
            NinoSerializer.Serialize(pairs.ToList()), x => x);
        RoundTrip<Cell[], ArrayFormatter<Cell, CellBlockFormatter>, ArrayFormatter<Cell, CellPlainFormatter>>(
            Cells, NinoSerializer.Serialize(pairs), x => x);
        RoundTrip<HashSet<Cell>, HashSetFormatter<HashSet<Cell>, Cell, CellBlockFormatter>,
            HashSetFormatter<HashSet<Cell>, Cell, CellPlainFormatter>>(new HashSet<Cell>(Cells),
            NinoSerializer.Serialize(new HashSet<(int, short)>(pairs)), x => x);
        RoundTrip<Dictionary<int, Cell>, DictionaryFormatter<int, Cell, UnmanagedFormatter<int>, CellBlockFormatter>,
            DictionaryFormatter<int, Cell, UnmanagedFormatter<int>, CellPlainFormatter>>(
            Cells.ToDictionary(c => c.X), NinoSerializer.Serialize(pairs.ToDictionary(c => c.X)), x => x.Values);
        RoundTrip<LinkedList<Cell>, CollectionFormatter<LinkedList<Cell>, Cell, CellBlockFormatter>,
            CollectionFormatter<LinkedList<Cell>, Cell, CellPlainFormatter>>(new LinkedList<Cell>(Cells),
            NinoSerializer.Serialize(new LinkedList<(int, short)>(pairs)), x => x);
        RoundTrip<Queue<Cell>, QueueFormatter<Cell, CellBlockFormatter>, QueueFormatter<Cell, CellPlainFormatter>>(
            new Queue<Cell>(Cells), NinoSerializer.Serialize(new Queue<(int, short)>(pairs)), x => x);
        RoundTrip<Stack<Cell>, StackFormatter<Cell, CellBlockFormatter>, StackFormatter<Cell, CellPlainFormatter>>(
            new Stack<Cell>(Cells), NinoSerializer.Serialize(new Stack<(int, short)>(pairs)), x => x);
    }

    /// <summary>
    /// <paramref name="value"/> written by the block formatter and the plain one: <paramref name="bytes"/> (the same
    /// elements as <c>(int, short)</c>) both times, and read back by both.
    /// </summary>
    private static void RoundTrip<T, TBlock, TPlain>(T value, byte[] bytes, System.Func<T, IEnumerable<Cell>> elements)
        where TBlock : struct, INinoFormatter<T>
        where TPlain : struct, INinoFormatter<T>
    {
        CollectionAssert.AreEqual(bytes, NinoDirect.Serialize<T, TBlock>(value), typeof(TBlock).Name);
        CollectionAssert.AreEqual(bytes, NinoDirect.Serialize<T, TPlain>(value), typeof(TPlain).Name);
        CollectionAssert.AreEqual(elements(value).ToList(), elements(NinoDirect.Deserialize<T, TBlock>(bytes)).ToList());
        CollectionAssert.AreEqual(elements(value).ToList(), elements(NinoDirect.Deserialize<T, TPlain>(bytes)).ToList());
    }
}
