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
        byte[] oldData =
        {
            151, 164, 134, 105, 1, 0, 0, 0, 128, 0, 0, 4, 84, 101, 115, 116
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
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
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

        // serialized old data structure
        byte[] bytes = new byte[]
        {
            128, 0, 0, 2, 32, 0, 0, 0, 125, 234, 9, 159, 1, 0, 0, 0, 128, 0, 0, 4, 84, 0, 101, 0, 115, 0, 116, 0, 0,
            64, 172, 217, 211, 82, 221, 136, 34, 0, 0, 0, 75, 83, 158, 19, 2, 0, 0, 0, 128, 0, 0, 5, 84, 0, 101, 0,
            115, 0, 116, 0, 50, 0, 0, 64, 172, 217, 211, 82, 221, 136
        };

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
