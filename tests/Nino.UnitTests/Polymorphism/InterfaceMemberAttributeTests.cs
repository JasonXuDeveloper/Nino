using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

// What the attributes of an interface member do. A type implementing the member writes its own member, with its own
// attributes: the interface's [NinoUtf8], [NinoMemberFormatter] and [NinoIgnore] do not reach it (NINO033), while its
// [NinoMember(index)] collects and places it.

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
}
