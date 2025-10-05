using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class PrivateMemberAccessTests
{
    [TestMethod]
    public void TestPrivateAccess()
    {
        ProtectedShouldInclude protectedShouldInclude = new ProtectedShouldInclude()
        {
            Id = 991122
        };
        byte[] bytes = NinoSerializer.Serialize(protectedShouldInclude);
        ProtectedShouldInclude protectedShouldInclude2 =
            NinoDeserializer.Deserialize<ProtectedShouldInclude>(bytes);
        Assert.AreEqual(protectedShouldInclude.Id, protectedShouldInclude2.Id);

        ShouldIgnorePrivate data = new ShouldIgnorePrivate
        {
            Id = 1,
            Name = "Test",
            CreateTime = DateTime.Today
        };
        bytes = NinoSerializer.Serialize(data);
        ShouldIgnorePrivate shouldIgnorePrivate = NinoDeserializer.Deserialize<ShouldIgnorePrivate>(bytes);
        Assert.AreNotEqual(data.Id, shouldIgnorePrivate.Id);
        Assert.AreEqual(data.Name, shouldIgnorePrivate.Name);
        Assert.AreEqual(data.CreateTime, shouldIgnorePrivate.CreateTime);

        TestPrivateMemberClass pcls = new TestPrivateMemberClass();
        pcls.A = 1;

        bytes = NinoSerializer.Serialize(pcls);
        TestPrivateMemberClass pcls2 = NinoDeserializer.Deserialize<TestPrivateMemberClass>(bytes);
        Assert.AreEqual(pcls.A, pcls2.A);
        Assert.AreEqual(pcls.ReadonlyId, pcls2.ReadonlyId);

        RecordWithPrivateMember record = new RecordWithPrivateMember("Test");
        Assert.IsNotNull(record.Name);
        Assert.AreEqual("Test", record.Name);

        bytes = NinoSerializer.Serialize(record);
        RecordWithPrivateMember r1 = NinoDeserializer.Deserialize<RecordWithPrivateMember>(bytes);
        Assert.AreEqual(record.Name, r1.Name);
        Assert.AreEqual(record.ReadonlyId, r1.ReadonlyId);

        RecordWithPrivateMember2 record2 = new RecordWithPrivateMember2("Test");
        Assert.IsNotNull(record2.Name);
        Assert.AreEqual("Test", record2.Name);

        bytes = NinoSerializer.Serialize(record2);
        RecordWithPrivateMember2 r2 = NinoDeserializer.Deserialize<RecordWithPrivateMember2>(bytes);
        Assert.AreEqual(record2.Name, r2.Name);
        Assert.AreEqual(record2.ReadonlyId, r2.ReadonlyId);

        StructWithPrivateMember s = new StructWithPrivateMember
        {
            Id = 1,
        };
        s.SetName("Test");
        Assert.AreEqual("Test", s.GetName());

        bytes = NinoSerializer.Serialize(s);
        StructWithPrivateMember s2 = NinoDeserializer.Deserialize<StructWithPrivateMember>(bytes);
        Assert.AreEqual(s.Id, s2.Id);
        Assert.AreEqual(s.GetName(), s2.GetName());

        ClassWithPrivateMember<float> cls = new ClassWithPrivateMember<float>();
        cls.Flag = true;
        cls.List = new List<float>
        {
            1.1f,
            2.2f,
            3.3f
        };
        Assert.IsNotNull(cls.Name);

        bytes = NinoSerializer.Serialize(cls);
        ClassWithPrivateMember<float> result = NinoDeserializer.Deserialize<ClassWithPrivateMember<float>>(bytes);
        Assert.AreEqual(cls.Id, result.Id);
        //private field
        Assert.AreEqual(cls.Name, result.Name);
        //private property
        Assert.AreEqual(cls.Flag, result.Flag);
        //private generic field, list sequentially equal
        Assert.AreEqual(cls.List.Count, result.List.Count);
        for (int i = 0; i < cls.List.Count; i++)
        {
            Assert.AreEqual(cls.List[i], result.List[i]);
        }

        ClassWithPrivateMember<int> cls2 = new ClassWithPrivateMember<int>();
        cls2.Flag = false;
        cls2.List = new List<int>
        {
            3,
            2,
            1
        };
        Assert.IsNotNull(cls2.Name);

        bytes = NinoSerializer.Serialize(cls2);
        ClassWithPrivateMember<int> result2 = NinoDeserializer.Deserialize<ClassWithPrivateMember<int>>(bytes);
        Assert.AreEqual(cls2.Id, result2.Id);
        Assert.AreEqual(cls2.Name, result2.Name);
        Assert.AreEqual(cls2.Flag, result2.Flag);
        Assert.AreEqual(cls2.List.Count, result2.List.Count);
        for (int i = 0; i < cls2.List.Count; i++)
        {
            Assert.AreEqual(cls2.List[i], result2.List[i]);
        }

        Bindable<int> bindable = new Bindable<int>(1);
        bytes = NinoSerializer.Serialize(bindable);
        Bindable<int> bindable2 = NinoDeserializer.Deserialize<Bindable<int>>(bytes);
        Assert.AreEqual(bindable.Value, bindable2.Value);
    }
}
