using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class RecordSerializationTests
{
    [TestMethod]
    public void TestRecords()
    {
        SimpleRecord record = new SimpleRecord
        {
            Id = 1,
            Name = "Test",
            CreateTime = DateTime.Today
        };

        byte[] bytes = NinoSerializer.Serialize(record);
        Assert.IsNotNull(bytes);

        SimpleRecord result = NinoDeserializer.Deserialize<SimpleRecord>(bytes);
        Assert.AreEqual(record, result);

        SimpleRecord2 record2 = new SimpleRecord2(1, "Test", DateTime.Today);
        bytes = NinoSerializer.Serialize(record2);
        Assert.IsNotNull(bytes);

        SimpleRecord2 result2 = NinoDeserializer.Deserialize<SimpleRecord2>(bytes);
        Assert.AreEqual(record2, result2);

        SimpleRecord3 record3 = new SimpleRecord3(1, "Test", DateTime.Today)
        {
            Flag = true,
            Ignored = 999
        };
        bytes = NinoSerializer.Serialize(record3);
        Assert.IsNotNull(bytes);

        SimpleRecord3 result3 = NinoDeserializer.Deserialize<SimpleRecord3>(bytes);
        Assert.AreEqual(result3.Ignored, 0);
        result3.Ignored = 999;
        Assert.AreEqual(record3, result3);

        SimpleRecord4 record4 = new SimpleRecord4(1, "Test", DateTime.Today)
        {
            Flag = true,
            ShouldNotIgnore = 1234
        };
        bytes = NinoSerializer.Serialize(record4);
        Assert.IsNotNull(bytes);

        SimpleRecord4 result4 = NinoDeserializer.Deserialize<SimpleRecord4>(bytes);
        Assert.AreEqual(record4.ShouldNotIgnore, result4.ShouldNotIgnore);
        Assert.AreEqual(result4.Flag, false);
        result4.Flag = true;
        Assert.AreEqual(record4, result4);

        SimpleRecord5 record5 = new SimpleRecord5(1, "Test", DateTime.Today)
        {
            Flag = true,
            ShouldNotIgnore = 1234
        };

        bytes = NinoSerializer.Serialize(record5);
        Assert.IsNotNull(bytes);

        SimpleRecord5 result5 = NinoDeserializer.Deserialize<SimpleRecord5>(bytes);
        Assert.AreEqual(record5.ShouldNotIgnore, result5.ShouldNotIgnore);

        SimpleRecord6<int> record6 = new SimpleRecord6<int>(1, 1234);
        bytes = NinoSerializer.Serialize(record6);
        Assert.IsNotNull(bytes);

        SimpleRecord6<int> result6 = NinoDeserializer.Deserialize<SimpleRecord6<int>>(bytes);
        Assert.AreEqual(record6, result6);
    }

    [TestMethod]
    public void TestRecordStruct()
    {
        SimpleRecordStruct record = new SimpleRecordStruct
        {
            Id = 1,
            Name = "Test",
            CreateTime = DateTime.Today
        };

        byte[] bytes = NinoSerializer.Serialize(record);
        Assert.IsNotNull(bytes);

        SimpleRecordStruct result = NinoDeserializer.Deserialize<SimpleRecordStruct>(bytes);
        Assert.AreEqual(record, result);

        SimpleRecordStruct2 record2 = new SimpleRecordStruct2(1, DateTime.Today);
        bytes = NinoSerializer.Serialize(record2);

        SimpleRecordStruct2 result2 = NinoDeserializer.Deserialize<SimpleRecordStruct2>(bytes);
        Assert.AreEqual(record2, result2);

        SimpleRecordStruct2<int> record3 = new SimpleRecordStruct2<int>(1, 1234);
        bytes = NinoSerializer.Serialize(record3);
        Assert.IsNotNull(bytes);

        SimpleRecordStruct2<int> result3 = NinoDeserializer.Deserialize<SimpleRecordStruct2<int>>(bytes);
        Assert.AreEqual(record3, result3);

        SimpleRecordStruct2<string> record4 = new SimpleRecordStruct2<string>(1, "Test");
        bytes = NinoSerializer.Serialize(record4);

        SimpleRecordStruct2<string> result4 = NinoDeserializer.Deserialize<SimpleRecordStruct2<string>>(bytes);
        Assert.AreEqual(record4, result4);
    }
}
