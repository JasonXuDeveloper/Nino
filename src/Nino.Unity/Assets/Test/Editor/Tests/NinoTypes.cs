using System;
using System.Collections.Generic;
using System.Diagnostics;
using Test.NinoGen;
using Nino.Test;
using NUnit.Framework;
using Debug = UnityEngine.Debug;

namespace Test.Editor.Tests
{
    public class NinoTypes
    {
        [Test]
        public void TestCommonClass()
        {
            //custom type
            PrimitiveTypeTest c = new PrimitiveTypeTest()
            {
                ni = null,
                v3 = UnityEngine.Vector3.one,
                m = UnityEngine.Matrix4x4.identity,
                qs = new List<UnityEngine.Quaternion>()
                {
                    new(100.99f, 299.31f, 45.99f, 0.5f),
                    new(100.99f, 299.31f, 45.99f, 0.5f),
                    new(100.99f, 299.31f, 45.99f, 0.5f)
                },
                dict = new Dictionary<string, int>()
                {
                    { "test1", 1 },
                    { "test2", 2 },
                    { "test3", 3 },
                    { "test4", 4 },
                },
                dict2 = new Dictionary<string, Data>()
                {
                    { "dict2.entry1", new Data() },
                    { "dict2.entry2", new Data() },
                    { "dict2.entry3", new Data() },
                },
                Dt = DateTime.Now
            };

            Debug.Log($"will serialize c: {c}");
            var bs = Serializer.Serialize(c);
            Debug.Log($"serialized to {bs.Length} bytes: {string.Join(",", bs)}");
            Debug.Log("will deserialize");
            Deserializer.Deserialize(bs, out PrimitiveTypeTest cc);
            Debug.Log($"deserialized as cc: {cc}");

            Assert.AreEqual(c.ToString(), cc.ToString());
        }

        [Test]
        public void TestTrivialClass()
        {
            Stopwatch sw = new Stopwatch();

            IncludeAllClassCodeGen codeGen = new IncludeAllClassCodeGen()
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            Debug.Log(
                "serialize an 'include all' class with code gen, this will not make the serialization result larger or slower, if and only if code gen occurs for an include all class");
            Debug.Log($"will serialize codeGen: {codeGen}");
            sw.Reset();
            sw.Start();
            var bs = codeGen.Serialize();
            sw.Stop();
            Debug.Log(
                $"serialized to {bs.Length} bytes in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {string.Join(",", bs)}");

            Debug.Log("will deserialize");
            sw.Reset();
            sw.Start();
            Deserializer.Deserialize(bs, out IncludeAllClassCodeGen codeGenR);
            sw.Stop();
            Debug.Log(
                $"deserialized as codeGenR in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {codeGenR}");

            NotIncludeAllClass d = new NotIncludeAllClass()
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            Debug.Log(
                "Now in comparison, we serialize a class with the same structure and same value");
            Debug.Log($"will serialize d in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {d}");
            sw.Reset();
            sw.Start();
            bs = d.Serialize();
            sw.Stop();
            Debug.Log($"serialized to {bs.Length} bytes: {string.Join(",", bs)}");

            Debug.Log("will deserialize");
            sw.Reset();
            sw.Start();
            Deserializer.Deserialize(bs, out NotIncludeAllClass dd);
            sw.Stop();
            Debug.Log($"deserialized as dd in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {dd}");

            Assert.AreEqual(codeGen.ToString(), codeGenR.ToString());
            Assert.AreEqual(d.ToString(), dd.ToString());
            Assert.AreEqual(codeGen.ToString(), dd.ToString());
        }

        [Test]
        public void TestComplexClass()
        {
            ComplexData data = new ComplexData();
            data.a = new int[3][];
            data.a[0] = new[] { 1, 2 };
            data.a[1] = new[] { 10, 20 };
            data.a[2] = new[] { 100, 200 };
            data.b = new List<int[]>()
            {
                new[] { 3, 5 },
                new[] { 7, 9 },
            };
            data.c = new[]
            {
                new List<int>() { 10, 11, 12 },
                new List<int>() { 13, 14, 15 },
            };
            data.d = new Dictionary<string, Dictionary<string, int>>()
            {
                {
                    "test1", new Dictionary<string, int>()
                    {
                        { "test1_1", 1 },
                        { "test1_2", 2 },
                    }
                }
            };
            data.e = new[]
            {
                new Dictionary<string, Dictionary<string, int[][]>>()
                {
                    {
                        "test2", new Dictionary<string, int[][]>()
                        {
                            { "test2_1", data.a },
                            { "test2_2", data.a },
                        }
                    },
                    {
                        "test3", new Dictionary<string, int[][]>()
                        {
                            { "test3_1", data.a },
                            { "test3_2", data.a },
                        }
                    }
                },
                new Dictionary<string, Dictionary<string, int[][]>>()
                {
                    {
                        "test4", new Dictionary<string, int[][]>()
                        {
                            { "test4_1", data.a },
                            { "test4_2", data.a },
                        }
                    },
                    {
                        "test5", new Dictionary<string, int[][]>()
                        {
                            { "test5_1", data.a },
                            { "test5_2", data.a },
                        }
                    }
                }
            };
            data.f = new[]
            {
                new[]
                {
                    new Data()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    },
                    new Data()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    }
                },
                new[]
                {
                    new Data()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    },
                    new Data()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    }
                }
            };
            data.g = new List<Data[]>()
            {
                new[]
                {
                    new Data()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    },
                    new Data()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    }
                },
                new[]
                {
                    new Data()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    },
                    new Data()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    }
                },
            };
            data.h = new[]
            {
                data.f,
                data.f
            };
            data.i = new[]
            {
                new List<Data>()
                {
                    new()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    }
                },
                new List<Data>()
                {
                    new()
                    {
                        x = short.MaxValue,
                        y = byte.MaxValue,
                        z = short.MaxValue,
                        f = 1234.56789f,
                        d = 66.66666666m,
                        db = 999.999999999999,
                        bo = true,
                        en = TestEnum.A,
                    }
                }
            };
            data.j = new[]
            {
                data.g,
                data.g,
            };
            Debug.Log(data);
            var buf = Serializer.Serialize(data);
            Debug.Log($"Serialized data: {buf.Length} bytes, {string.Join(",", buf)}");
            Deserializer.Deserialize(buf, out ComplexData result);
            Debug.Log($"Deserialized as: {result}");

            Assert.AreEqual(data.ToString(), result.ToString());
        }
    }
}