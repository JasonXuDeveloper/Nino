using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class TupleAndKeyValuePairTests
{
    [TestMethod]
    public void TestTuple()
    {
        Tuple<int, string> tuple = new Tuple<int, string>(1, "Test");
        byte[] bytes = NinoSerializer.Serialize(tuple);
        Assert.IsNotNull(bytes);

        Tuple<int, string> result = NinoDeserializer.Deserialize<Tuple<int, string>>(bytes);
        Assert.AreEqual(tuple.Item1, result.Item1);
        Assert.AreEqual(tuple.Item2, result.Item2);

        Tuple<int, int> tuple2 = new Tuple<int, int>(1, 2);
        bytes = NinoSerializer.Serialize(tuple2);
        Assert.IsNotNull(bytes);

        Tuple<int, int> result2 = NinoDeserializer.Deserialize<Tuple<int, int>>(bytes);
        Assert.AreEqual(tuple2.Item1, result2.Item1);
        Assert.AreEqual(tuple2.Item2, result2.Item2);

        Tuple<string, Tuple<int, string>> tuple3 =
            new Tuple<string, Tuple<int, string>>("Test", new Tuple<int, string>(1, "Test"));
        bytes = NinoSerializer.Serialize(tuple3);
        Assert.IsNotNull(bytes);

        Tuple<string, Tuple<int, string>> result3 =
            NinoDeserializer.Deserialize<Tuple<string, Tuple<int, string>>>(bytes);
        Assert.AreEqual(tuple3.Item1, result3.Item1);
        Assert.AreEqual(tuple3.Item2.Item1, result3.Item2.Item1);
        Assert.AreEqual(tuple3.Item2.Item2, result3.Item2.Item2);

        Tuple<Data, Tuple<int, int>> tuple4 = new Tuple<Data, Tuple<int, int>>(new Data()
        {
            En = TestEnum.A,
            Name = "Test",
            X = 1,
            Y = 2
        }, new Tuple<int, int>(1, 2));
        bytes = NinoSerializer.Serialize(tuple4);
        Assert.IsNotNull(bytes);

        Tuple<Data, Tuple<int, int>> result4 = NinoDeserializer.Deserialize<Tuple<Data, Tuple<int, int>>>(bytes);
        Assert.AreEqual(tuple4.Item1.En, result4.Item1.En);
        Assert.AreEqual(tuple4.Item1.Name, result4.Item1.Name);
        Assert.AreEqual(tuple4.Item1.X, result4.Item1.X);
        Assert.AreEqual(tuple4.Item1.Y, result4.Item1.Y);
        Assert.AreEqual(tuple4.Item2.Item1, result4.Item2.Item1);
        Assert.AreEqual(tuple4.Item2.Item2, result4.Item2.Item2);

        List<(int a, int b)> lst1 = new List<(int a, int b)>
        {
            (1, 2),
            (3, 4)
        };
        bytes = NinoSerializer.Serialize(lst1);
        Assert.IsNotNull(bytes);
        List<(int aa, int bb)> result5 = NinoDeserializer.Deserialize<List<(int aa, int bb)>>(bytes);
        Assert.AreEqual(lst1.Count, result5.Count);
        for (int i = 0; i < lst1.Count; i++)
        {
            Assert.AreEqual(lst1[i].a, result5[i].aa);
            Assert.AreEqual(lst1[i].b, result5[i].bb);
        }
    }

    [TestMethod]
    public void TestValueTuple()
    {
        ValueTuple<int, string> tuple = new ValueTuple<int, string>(1, "Test");
        byte[] bytes = NinoSerializer.Serialize(tuple);
        Assert.IsNotNull(bytes);

        ValueTuple<int, string> result = NinoDeserializer.Deserialize<ValueTuple<int, string>>(bytes);
        Assert.AreEqual(tuple.Item1, result.Item1);
        Assert.AreEqual(tuple.Item2, result.Item2);

        ValueTuple<int, int> tuple2 = new ValueTuple<int, int>(1, 2);
        bytes = NinoSerializer.Serialize(tuple2);
        Assert.IsNotNull(bytes);

        ValueTuple<int, int> result2 = NinoDeserializer.Deserialize<ValueTuple<int, int>>(bytes);
        Assert.AreEqual(tuple2.Item1, result2.Item1);
        Assert.AreEqual(tuple2.Item2, result2.Item2);

        ValueTuple<string, ValueTuple<int, string>> tuple3 =
            new ValueTuple<string, ValueTuple<int, string>>("Test", new ValueTuple<int, string>(1, "Test"));
        bytes = NinoSerializer.Serialize(tuple3);
        Assert.IsNotNull(bytes);

        ValueTuple<string, ValueTuple<int, string>> result3 =
            NinoDeserializer.Deserialize<ValueTuple<string, ValueTuple<int, string>>>(bytes);
        Assert.AreEqual(tuple3.Item1, result3.Item1);
        Assert.AreEqual(tuple3.Item2.Item1, result3.Item2.Item1);
        Assert.AreEqual(tuple3.Item2.Item2, result3.Item2.Item2);

        ValueTuple<Data, ValueTuple<int, int>> tuple4 = new ValueTuple<Data, ValueTuple<int, int>>(new Data()
        {
            En = TestEnum.A,
            Name = "Test",
            X = 1,
            Y = 2
        }, new ValueTuple<int, int>(1, 2));
        bytes = NinoSerializer.Serialize(tuple4);
        Assert.IsNotNull(bytes);

        ValueTuple<Data, ValueTuple<int, int>> result4 =
            NinoDeserializer.Deserialize<ValueTuple<Data, ValueTuple<int, int>>>(bytes);
        Assert.AreEqual(tuple4.Item1.En, result4.Item1.En);
        Assert.AreEqual(tuple4.Item1.Name, result4.Item1.Name);
        Assert.AreEqual(tuple4.Item1.X, result4.Item1.X);
        Assert.AreEqual(tuple4.Item1.Y, result4.Item1.Y);
        Assert.AreEqual(tuple4.Item2.Item1, result4.Item2.Item1);
        Assert.AreEqual(tuple4.Item2.Item2, result4.Item2.Item2);

        (bool a, string b) val = (true, "1");
        bytes = NinoSerializer.Serialize(val);
        Assert.IsNotNull(bytes);

        (bool, string) result5 = NinoDeserializer.Deserialize<(bool, string)>(bytes);
        Assert.AreEqual(val.a, result5.Item1);
        Assert.AreEqual(val.b, result5.Item2);

        (bool, string) val2 = (false, "2");
        bytes = NinoSerializer.Serialize(val2);
        Assert.IsNotNull(bytes);

        (bool a, string b) result6 = NinoDeserializer.Deserialize<(bool a, string b)>(bytes);
        Assert.AreEqual(val2.Item1, result6.a);
        Assert.AreEqual(val2.Item2, result6.b);
    }

    [TestMethod]
    public void TestKvp()
    {
        KeyValuePair<int, long> kvp = new KeyValuePair<int, long>(1, 1234567890);
        byte[] bytes = NinoSerializer.Serialize(kvp);
        Assert.IsNotNull(bytes);
        Console.WriteLine(string.Join(", ", bytes));

        KeyValuePair<int, long> result = NinoDeserializer.Deserialize<KeyValuePair<int, long>>(bytes);
        Assert.AreEqual(kvp.Key, result.Key);
        Assert.AreEqual(kvp.Value, result.Value);

        KeyValuePair<int, string> kvp2 = new KeyValuePair<int, string>(1, "Test");
        bytes = NinoSerializer.Serialize(kvp2);
        Assert.IsNotNull(bytes);

        KeyValuePair<int, string> result2 = NinoDeserializer.Deserialize<KeyValuePair<int, string>>(bytes);
        Assert.AreEqual(kvp2.Key, result2.Key);
        Assert.AreEqual(kvp2.Value, result2.Value);

        KeyValuePair<int, KeyValuePair<int, long>> kvp3 =
            new KeyValuePair<int, KeyValuePair<int, long>>(1, new KeyValuePair<int, long>(2, 1234567890));
        bytes = NinoSerializer.Serialize(kvp3);
        Assert.IsNotNull(bytes);

        KeyValuePair<int, KeyValuePair<int, long>> result3 =
            NinoDeserializer.Deserialize<KeyValuePair<int, KeyValuePair<int, long>>>(bytes);
        Assert.AreEqual(kvp3.Key, result3.Key);
        Assert.AreEqual(kvp3.Value.Key, result3.Value.Key);

        KeyValuePair<string, KeyValuePair<bool, string>> kvp4 =
            new KeyValuePair<string, KeyValuePair<bool, string>>("Test111",
                new KeyValuePair<bool, string>(true, "Test"));
        bytes = NinoSerializer.Serialize(kvp4);
        Assert.IsNotNull(bytes);

        KeyValuePair<string, KeyValuePair<bool, string>> result4 =
            NinoDeserializer.Deserialize<KeyValuePair<string, KeyValuePair<bool, string>>>(bytes);
        Assert.AreEqual(kvp4.Key, result4.Key);
        Assert.AreEqual(kvp4.Value.Key, result4.Value.Key);
    }
}
