using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

// 回归样本：开放泛型类型 + 非泛型 [NinoType] 基类。
// 修复前 FormatterEmitter 会走静态分派分支，拼出 `.__WriteSubtype(...)` /
// `AsBaseSlot<TSub, >` 之类的非法代码（CS1525 / CS1031 / CS1513），整个工程编不过。
[NinoType(false)]
public abstract class GenericPolyBase
{
    [NinoMember(1)] public int Id;
}

[NinoType(false)]
public abstract class GenericPolyBox<T> : GenericPolyBase
{
    [NinoMember(2)] public T Value;
}

[NinoType(false)]
public sealed class GenericPolyIntBox : GenericPolyBox<int>
{
}

// 对照组：结构完全一样但非泛型，确认改动没有影响原有静态分派路径
[NinoType(false)]
public abstract class NonGenericPolyBase
{
    [NinoMember(1)] public int Id;
}

[NinoType(false)]
public sealed class NonGenericPolyLeaf : NonGenericPolyBase
{
    [NinoMember(2)] public int Value;
}

[TestClass]
public class GenericPolymorphicFormatterTests
{
    /// <summary>
    /// 通过基类引用序列化/反序列化开放泛型子类实例：类型与字段值都必须还原。
    /// </summary>
    [TestMethod]
    public void TestOpenGenericDerivedRoundTripThroughBase()
    {
        var source = new GenericPolyIntBox
        {
            Id = 7,
            Value = 42
        };

        byte[] bytes = NinoSerializer.Serialize<GenericPolyBase>(source);
        Assert.IsNotNull(bytes);
        Assert.IsTrue(bytes.Length > 0, "payload must not be empty");

        var restored = NinoDeserializer.Deserialize<GenericPolyBase>(bytes);
        Assert.IsNotNull(restored);
        Assert.IsInstanceOfType(restored, typeof(GenericPolyIntBox), "runtime type must survive round trip");
        Assert.AreEqual(7, restored.Id);

        var box = (GenericPolyIntBox)restored;
        Assert.AreEqual(42, box.Value);
    }

    /// <summary>
    /// 再序列化必须逐字节相等（格式稳定，且没有多写/少写字段）。
    /// </summary>
    [TestMethod]
    public void TestOpenGenericDerivedRoundTripIsByteStable()
    {
        var source = new GenericPolyIntBox { Id = 1, Value = 2 };

        byte[] first = NinoSerializer.Serialize<GenericPolyBase>(source);
        var restored = NinoDeserializer.Deserialize<GenericPolyBase>(first);
        byte[] second = NinoSerializer.Serialize<GenericPolyBase>(restored);

        CollectionAssert.AreEqual(first, second);
    }

    /// <summary>
    /// null 仍按 NullTypeId 编码，反序列化回来必须是 null。
    /// </summary>
    [TestMethod]
    public void TestOpenGenericDerivedNullRoundTrip()
    {
        byte[] bytes = NinoSerializer.Serialize<GenericPolyBase>(null);
        Assert.IsNotNull(bytes);

        var restored = NinoDeserializer.Deserialize<GenericPolyBase>(bytes);
        Assert.IsNull(restored);
    }

    /// <summary>
    /// 对照组：非泛型多态类型继续走原有路径，行为不变。
    /// </summary>
    [TestMethod]
    public void TestNonGenericPolymorphicUnaffected()
    {
        var source = new NonGenericPolyLeaf { Id = 3, Value = 4 };

        byte[] bytes = NinoSerializer.Serialize<NonGenericPolyBase>(source);
        var restored = NinoDeserializer.Deserialize<NonGenericPolyBase>(bytes);

        Assert.IsNotNull(restored);
        Assert.IsInstanceOfType(restored, typeof(NonGenericPolyLeaf));
        Assert.AreEqual(3, restored.Id);
        Assert.AreEqual(4, ((NonGenericPolyLeaf)restored).Value);
    }
}
