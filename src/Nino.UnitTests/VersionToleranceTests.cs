using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class VersionToleranceTests
{
    [TestMethod]
    public void TestDeserializeOldData()
    {
        SaveData data = new SaveData
        {
            Id = 1,
            Name = "Test",
        };

        //from serialization old version of data
        /*
         * [NinoType(false)]
           public class SaveData
           {
               [NinoMember(1)] public int Id;
               [NinoMember(2)] public string Name;
           }
         */

        //print all const int in NinoTypeConst

        Console.WriteLine(string.Join(", ", NinoSerializer.Serialize(data)));
        // SaveData is sealed and version tolerant: null flag, body length, then the members.
        byte[] oldData =
        {
            1, // not null
            16, 0, 0, 0, // body length
            1, 0, 0, 0, // Id
            4, 0, 0, 0, 4, 0, 0, 0, 84, 101, 115, 116 // Name (UTF-8: char count, byte count, bytes)
        };
        //require symbol WEAK_VERSION_TOLERANCE to be defined
#if WEAK_VERSION_TOLERANCE
        SaveData result = NinoDeserializer.Deserialize<SaveData>(oldData);
        Assert.AreEqual(data.Id, result.Id);
        Assert.AreEqual(data.Name, result.Name);
        Assert.AreEqual(default, result.NewField1);
        Assert.AreEqual(default, result.NewField2);
#else
        //should throw out of range exception
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        {
            var _ = NinoDeserializer.Deserialize<SaveData>(oldData);
        });
#endif
    }

#if WEAK_VERSION_TOLERANCE
    [TestMethod]
    public void TestModifyListMemberDataStructure2()
    {
        // same data as above
        List<IListElementClass> list = new List<IListElementClass>
        {
            new ListElementClass
            {
                Id = 1,
                Name = "Test",
                CreateTime = new DateTime(2025, 2, 22)
            },
            new ListElementClass2Renamed
            {
                Id = 2,
                Name = "Test2",
                CreateTime = new DateTime(2025, 2, 22)
            }
        };
        var buf = NinoSerializer.Serialize(list);
        Console.WriteLine(string.Join(", ", buf));

        // The old data structure (without the Extra members), written by hand: count, then for each element
        // its type id and its length-prefixed body.
        var oldBuffer = new NinoArrayBufferWriter();
        var writer = new Writer(oldBuffer);
        writer.WriteLength(2);
        writer.Write(NinoTypeId.Compute("ListElementClass"));
        int body = writer.ReserveInt32();
        writer.Write(1);
        writer.Write("Test");
        writer.Write(new DateTime(2025, 2, 22));
        writer.PatchLength(body);
        writer.Write(NinoTypeId.Compute("ListElementClass2")); // [NinoFormerName]
        body = writer.ReserveInt32();
        writer.Write(2);
        writer.Write("Test2");
        writer.Write(new DateTime(2025, 2, 22));
        writer.PatchLength(body);
        writer.Flush();
        byte[] bytes = oldBuffer.ToArray();

        List<IListElementClass> result = NinoDeserializer.Deserialize<List<IListElementClass>>(bytes);
        Assert.AreEqual(list.Count, result.Count);
        foreach (var item in list)
        {
            switch (item)
            {
                case ListElementClass listElementClass:
                    Assert.IsTrue(result[0] is ListElementClass);
                    Assert.AreEqual(listElementClass.Id, ((ListElementClass)result[0]).Id);
                    Assert.AreEqual(listElementClass.Name, ((ListElementClass)result[0]).Name);
                    Assert.AreEqual(listElementClass.CreateTime, ((ListElementClass)result[0]).CreateTime);
                    Assert.AreEqual(listElementClass.Extra, false);
                    break;
                case ListElementClass2Renamed listElementClass2:
                    Assert.IsTrue(result[1] is ListElementClass2Renamed);
                    Assert.AreEqual(listElementClass2.Id, ((ListElementClass2Renamed)result[1]).Id);
                    Assert.AreEqual(listElementClass2.Name, ((ListElementClass2Renamed)result[1]).Name);
                    Assert.AreEqual(listElementClass2.CreateTime, ((ListElementClass2Renamed)result[1]).CreateTime);
                    Assert.AreEqual(listElementClass2.Extra, null);
                    break;
            }
        }
    }
#endif
}
