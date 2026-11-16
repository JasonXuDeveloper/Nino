using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

// What the attributes of an interface member do. A type implementing the member implicitly writes its own member, with
// its own attributes: the interface's [NinoUtf8], [NinoMemberFormatter] and [NinoIgnore] do not reach it (NINO033),
// while its [NinoMember(index)] collects and places it. A type implementing it explicitly has no member of its own to
// write: the interface's member is written, through the interface, with the interface's attributes (no NINO033).

#pragma warning disable NINO033
[NinoType]
public interface IAttributedMembers
{
    [NinoIgnore] int Skipped { get; set; }
    [NinoMemberFormatter(typeof(CompactIntFormatter))] int Compact { get; set; }
    [NinoUtf8] string Name { get; set; }
}

public sealed class ImplicitAttributedMembers : IAttributedMembers
{
    public int Skipped { get; set; }
    public int Compact { get; set; }
    public string Name { get; set; }
}

public struct ImplicitAttributedMembersStruct : IAttributedMembers
{
    public int Skipped { get; set; }
    public int Compact { get; set; }
    public string Name { get; set; }
}
#pragma warning restore NINO033

[NinoType(autoCollect: false)]
public interface IIndexedMembers
{
    [NinoMember(1)] int Second { get; set; }
    [NinoMember(0)] int First { get; set; }
}

public sealed class ImplicitIndexedMembers : IIndexedMembers
{
    public int Second { get; set; }
    public int First { get; set; }
}

[NinoType]
public interface IExplicitMembers
{
    [NinoIgnore] int Skipped { get; set; }
    [NinoMemberFormatter(typeof(CompactIntFormatter))] int Compact { get; set; }
    [NinoUtf8] string Name { get; set; }
}

public sealed class ExplicitMembers : IExplicitMembers
{
    int IExplicitMembers.Skipped { get; set; }
    int IExplicitMembers.Compact { get; set; }
    string IExplicitMembers.Name { get; set; }
    public int Own { get; set; }
}

public struct ExplicitMembersStruct : IExplicitMembers
{
    int IExplicitMembers.Skipped { get; set; }
    int IExplicitMembers.Compact { get; set; }
    string IExplicitMembers.Name { get; set; }
    public int Own { get; set; }
}

// Re-created through its constructor: the explicit members are assigned after it.
public sealed record ExplicitMembersRecord(int Own) : IExplicitMembers
{
    int IExplicitMembers.Skipped { get; set; }
    int IExplicitMembers.Compact { get; set; }
    string IExplicitMembers.Name { get; set; }
}

public record struct ExplicitMembersRecordStruct(int Own) : IExplicitMembers
{
    int IExplicitMembers.Skipped { get; set; }
    int IExplicitMembers.Compact { get; set; }
    string IExplicitMembers.Name { get; set; }
}

[NinoType(autoCollect: false)]
public interface IIndexedExplicitMembers
{
    [NinoMember(1)] int Second { get; set; }
    [NinoMember(0)] int First { get; set; }
}

public struct ImplicitIndexedStruct : IIndexedExplicitMembers
{
    public int Second { get; set; }
    public int First { get; set; }
}

public struct ExplicitIndexedStruct : IIndexedExplicitMembers
{
    int IIndexedExplicitMembers.Second { get; set; }
    int IIndexedExplicitMembers.First { get; set; }
}

[NinoType(format: NinoFormat.Keyed)]
public interface IKeyedExplicitMembers
{
    int Number { get; set; }
    [NinoUtf8] string Text { get; set; }
}

public struct ImplicitKeyedStruct : IKeyedExplicitMembers
{
    public int Number { get; set; }
    [NinoUtf8] public string Text { get; set; }
}

// One member explicit, one implicit (with its own attribute).
public struct ExplicitKeyedStruct : IKeyedExplicitMembers
{
    int IKeyedExplicitMembers.Number { get; set; }
    [NinoUtf8] public string Text { get; set; }
}

public sealed class ExplicitKeyedClass : IKeyedExplicitMembers
{
    int IKeyedExplicitMembers.Number { get; set; }
    string IKeyedExplicitMembers.Text { get; set; }
}

// Of fixed size and a block (a pair is not raw): written by WriteFixed, collections of it in blocks.
[NinoType]
public interface IFixedExplicitMembers
{
    (int, int) Pair { get; set; }
    long Id { get; set; }
}

public struct ImplicitFixedStruct : IFixedExplicitMembers
{
    public (int, int) Pair { get; set; }
    public long Id { get; set; }
}

public struct ExplicitFixedStruct : IFixedExplicitMembers
{
    (int, int) IFixedExplicitMembers.Pair { get; set; }
    long IFixedExplicitMembers.Id { get; set; }
}

[NinoType]
public interface IExplicitBox<T>
{
    T Item { get; set; }
}

public struct ExplicitIntBox : IExplicitBox<int>
{
    int IExplicitBox<int>.Item { get; set; }
}

public struct ExplicitBox<T> : IExplicitBox<T>
{
    T IExplicitBox<T>.Item { get; set; }
}

[TestClass]
public class InterfaceMemberAttributeTests
{
    private const string Text = "héllo";
    private const int Value = 300;

    private static byte[] Utf16(string value)
    {
        using var buffer = new NinoArrayBufferWriter();
        var writer = new Writer(buffer);
        writer.Write(value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Utf8(string value)
    {
        using var buffer = new NinoArrayBufferWriter();
        var writer = new Writer(buffer);
        writer.WriteUtf8(value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static bool Contains(byte[] bytes, byte[] part)
    {
        for (int i = 0; i + part.Length <= bytes.Length; i++)
        {
            if (bytes.AsSpan(i, part.Length).SequenceEqual(part))
                return true;
        }

        return false;
    }

    [TestMethod]
    public void ImplicitImplementationsWriteTheirOwnAttributes()
    {
        // The interface's attributes do not reach the implementing member: written in UTF-16, as a plain int, and
        // not ignored.
        foreach (IAttributedMembers value in new IAttributedMembers[]
                 {
                     new ImplicitAttributedMembers { Skipped = 7, Compact = Value, Name = Text },
                     new ImplicitAttributedMembersStruct { Skipped = 7, Compact = Value, Name = Text },
                 })
        {
            var bytes = NinoSerializer.Serialize(value);
            string hex = Rt.ToHex(bytes);
            Assert.IsTrue(Contains(bytes, Utf16(Text)), hex);
            Assert.IsFalse(Contains(bytes, Utf8(Text)), hex);
            Assert.IsTrue(Contains(bytes, BitConverter.GetBytes(Value)), hex);

            var read = NinoDeserializer.Deserialize<IAttributedMembers>(bytes);
            Assert.AreEqual(value.GetType(), read.GetType());
            Assert.AreEqual(7, read.Skipped);
            Assert.AreEqual(Value, read.Compact);
            Assert.AreEqual(Text, read.Name);
        }
    }

    [TestMethod]
    public void InterfaceIndicesPlaceImplicitImplementations()
    {
        // [NinoMember(index)] on the interface collects the implementing member, which has none, in index order.
        var bytes = NinoSerializer.Serialize<IIndexedMembers>(new ImplicitIndexedMembers { First = 1, Second = 2 });
        var first = BitConverter.GetBytes(1).Concat(BitConverter.GetBytes(2)).ToArray();
        Assert.IsTrue(Contains(bytes, first), Rt.ToHex(bytes));

        var read = NinoDeserializer.Deserialize<IIndexedMembers>(bytes);
        Assert.AreEqual(1, read.First);
        Assert.AreEqual(2, read.Second);
    }

    private static IExplicitMembers Fill(IExplicitMembers value)
    {
        value.Skipped = 7;
        value.Compact = Value;
        value.Name = Text;
        return value;
    }

    [TestMethod]
    public void ExplicitImplementationsWriteTheInterfaceAttributes()
    {
        // [NinoMemberFormatter] writes 300 in two bytes, [NinoUtf8] the name in UTF-8, [NinoIgnore] skips the member;
        // the type's own member follows the interface's.
        var body = new byte[] { 0xAC, 0x02 }.Concat(Utf8(Text)).Concat(BitConverter.GetBytes(5)).ToArray();
        foreach (var value in new[]
                 {
                     Fill(new ExplicitMembers { Own = 5 }),
                     Fill(new ExplicitMembersStruct { Own = 5 }),
                     Fill(new ExplicitMembersRecord(5)),
                     Fill(new ExplicitMembersRecordStruct(5)),
                 })
        {
            var bytes = NinoSerializer.Serialize(value);
            string hex = Rt.ToHex(bytes);
            Assert.IsTrue(bytes.AsSpan().EndsWith(body), hex);
            Assert.IsFalse(Contains(bytes, Utf16(Text)), hex);

            var read = NinoDeserializer.Deserialize<IExplicitMembers>(bytes);
            Assert.AreEqual(value.GetType(), read.GetType());
            Assert.AreEqual(0, read.Skipped);
            Assert.AreEqual(Value, read.Compact);
            Assert.AreEqual(Text, read.Name);
        }

        // Into an existing struct, and as the concrete types.
        var existing = new ExplicitMembersStruct { Own = 1 };
        var structBytes = NinoSerializer.Serialize((ExplicitMembersStruct)Fill(new ExplicitMembersStruct { Own = 5 }));
        NinoDeserializer.Deserialize(structBytes, ref existing);
        Assert.AreEqual(5, existing.Own);
        Assert.AreEqual(Text, ((IExplicitMembers)existing).Name);
        var record = NinoDeserializer.Deserialize<ExplicitMembersRecord>(
            NinoSerializer.Serialize((ExplicitMembersRecord)Fill(new ExplicitMembersRecord(5))));
        Assert.AreEqual(5, record.Own);
        Assert.AreEqual(Value, ((IExplicitMembers)record).Compact);
    }

    [TestMethod]
    public void ExplicitImplementationsKeepTheLayout()
    {
        // The same bytes as an implicit implementation: interface indices order the members.
        var implicitBytes = NinoSerializer.Serialize(new ImplicitIndexedStruct { First = 1, Second = 2 });
        IIndexedExplicitMembers explicitValue = new ExplicitIndexedStruct();
        explicitValue.First = 1;
        explicitValue.Second = 2;
        var explicitBytes = NinoSerializer.Serialize((ExplicitIndexedStruct)explicitValue);
        CollectionAssert.AreEqual(implicitBytes, explicitBytes);
        IIndexedExplicitMembers read = NinoDeserializer.Deserialize<ExplicitIndexedStruct>(implicitBytes);
        Assert.AreEqual(1, read.First);
        Assert.AreEqual(2, read.Second);

        // Keyed: the same keys, so either type reads the other's bytes.
        var keyed = NinoSerializer.Serialize(new ImplicitKeyedStruct { Number = Value, Text = Text });
        IKeyedExplicitMembers explicitKeyed = new ExplicitKeyedStruct();
        explicitKeyed.Number = Value;
        explicitKeyed.Text = Text;
        CollectionAssert.AreEqual(keyed, NinoSerializer.Serialize((ExplicitKeyedStruct)explicitKeyed));
        IKeyedExplicitMembers fromImplicit = NinoDeserializer.Deserialize<ExplicitKeyedStruct>(keyed);
        Assert.AreEqual(Value, fromImplicit.Number);
        Assert.AreEqual(Text, fromImplicit.Text);

        IKeyedExplicitMembers keyedClass = new ExplicitKeyedClass();
        keyedClass.Number = Value;
        keyedClass.Text = Text;
        var classBytes = NinoSerializer.Serialize(keyedClass);
        Assert.IsTrue(Contains(classBytes, Utf8(Text)), Rt.ToHex(classBytes));
        var readClass = NinoDeserializer.Deserialize<IKeyedExplicitMembers>(classBytes);
        Assert.IsInstanceOfType<ExplicitKeyedClass>(readClass);
        Assert.AreEqual(Value, readClass.Number);
        Assert.AreEqual(Text, readClass.Text);
    }

    [TestMethod]
    public void ExplicitImplementationsOfFixedSize()
    {
        var implicitValues = new List<ImplicitFixedStruct>();
        var explicitValues = new List<ExplicitFixedStruct>();
        for (int i = 0; i < 20; i++)
        {
            implicitValues.Add(new ImplicitFixedStruct { Pair = (i, -i), Id = i * 1000L });
            IFixedExplicitMembers value = new ExplicitFixedStruct();
            value.Pair = (i, -i);
            value.Id = i * 1000L;
            explicitValues.Add((ExplicitFixedStruct)value);
        }

        var bytes = NinoSerializer.Serialize(explicitValues);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(implicitValues), bytes);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(implicitValues[3]), NinoSerializer.Serialize(explicitValues[3]));
        var read = NinoDeserializer.Deserialize<List<ExplicitFixedStruct>>(bytes);
        Assert.AreEqual(20, read.Count);
        for (int i = 0; i < read.Count; i++)
        {
            IFixedExplicitMembers value = read[i];
            Assert.AreEqual((i, -i), value.Pair);
            Assert.AreEqual(i * 1000L, value.Id);
        }
    }

    [TestMethod]
    public void ExplicitImplementationsOfGenericInterfaces()
    {
        IExplicitBox<int> box = new ExplicitIntBox();
        box.Item = Value;
        IExplicitBox<int> read = NinoDeserializer.Deserialize<ExplicitIntBox>(NinoSerializer.Serialize((ExplicitIntBox)box));
        Assert.AreEqual(Value, read.Item);

        IExplicitBox<string> generic = new ExplicitBox<string>();
        generic.Item = Text;
        IExplicitBox<string> readGeneric =
            NinoDeserializer.Deserialize<ExplicitBox<string>>(NinoSerializer.Serialize((ExplicitBox<string>)generic));
        Assert.AreEqual(Text, readGeneric.Item);
    }
}
