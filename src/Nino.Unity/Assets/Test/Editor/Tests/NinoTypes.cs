using System;
using System.Collections.Generic;
using Nino.Core;
using Nino.Test;
using NUnit.Framework;

namespace Test.Editor.Tests
{
    public class NinoTypes
    {
        private static Data SampleData() => new Data
        {
            x = short.MaxValue,
            y = byte.MaxValue,
            z = short.MaxValue,
            f = 1234.56789f,
            d = 66.66666666m,
            db = 999.999999999999,
            bo = true,
            en = TestEnum.A,
        };

        [Test]
        public void TestCommonClass()
        {
            var c = new PrimitiveTypeTest
            {
                ni = null,
                v3 = UnityEngine.Vector3.one,
                m = UnityEngine.Matrix4x4.identity,
                qs = new List<UnityEngine.Quaternion>
                {
                    new(100.99f, 299.31f, 45.99f, 0.5f),
                    new(100.99f, 299.31f, 45.99f, 0.5f),
                    new(100.99f, 299.31f, 45.99f, 0.5f)
                },
                dict = new Dictionary<string, int>
                {
                    { "test1", 1 },
                    { "test2", 2 },
                    { "test3", 3 },
                    { "test4", 4 },
                },
                dict2 = new Dictionary<string, Data>
                {
                    { "dict2.entry1", new Data() },
                    { "dict2.entry2", new Data() },
                    { "dict2.entry3", new Data() },
                },
                Dt = new DateTime(2025, 7, 19, 10, 25, 2, DateTimeKind.Utc)
            };

            var bytes = NinoSerializer.Serialize(c);
            var cc = NinoDeserializer.Deserialize<PrimitiveTypeTest>(bytes);

            Assert.AreEqual(c.ToString(), cc.ToString());
            // The private [NinoMember] round-trips too.
            Assert.AreEqual(c.Dt, cc.Dt);
        }

        [Test]
        public void TestTrivialClass()
        {
            var codeGen = new IncludeAllClassCodeGen
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            var bytes = NinoSerializer.Serialize(codeGen);
            var codeGenR = NinoDeserializer.Deserialize<IncludeAllClassCodeGen>(bytes);

            // The same members, selected with [NinoMember] instead of collected.
            var d = new NotIncludeAllClass
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            var dd = NinoDeserializer.Deserialize<NotIncludeAllClass>(NinoSerializer.Serialize(d));

            Assert.AreEqual(codeGen.ToString(), codeGenR.ToString());
            Assert.AreEqual(d.ToString(), dd.ToString());
            Assert.AreEqual(codeGen.ToString(), dd.ToString());
        }

        [Test]
        public void TestMixedData()
        {
            var key = new NotIncludeAllClass { a = 30, b = 20, c = 12938, d = 19283 };
            var data = new MixedData
            {
                a = 1,
                b = 2,
                c = 100,
                d = 500,
                e = 666,
                f = 99999999,
                g = 123456788765,
                h = 123456321,
                i = 6.6f,
                j = 9.9,
                k = 1.23456789012345m,
                l = true,
                m = 'a',
                n = "aksjdfhgheuwi",
                o = new List<int> { 1, 2, 3, 4, 5 },
                p = new List<NotIncludeAllClass> { key },
                q = new byte[] { 1, 2, 3 },
                r = new[] { key },
                s = new Dictionary<string, NotIncludeAllClass> { { "ks", key } },
                t = new Dictionary<NotIncludeAllClass, int> { { key, 39 } },
                u = new Dictionary<string, int> { { "usd", 7 } },
                v = new Dictionary<NotIncludeAllClass, NotIncludeAllClass> { { key, key } }
            };

            var result = NinoDeserializer.Deserialize<MixedData>(NinoSerializer.Serialize(data));

            Assert.AreEqual(data.ToString(), result.ToString());
        }

        [Test]
        public void TestComplexClass()
        {
            var data = new ComplexData();
            data.a = new int[3][];
            data.a[0] = new[] { 1, 2 };
            data.a[1] = new[] { 10, 20 };
            data.a[2] = new[] { 100, 200 };
            data.b = new List<int[]>
            {
                new[] { 3, 5 },
                new[] { 7, 9 },
            };
            data.c = new[]
            {
                new List<int> { 10, 11, 12 },
                new List<int> { 13, 14, 15 },
            };
            data.d = new Dictionary<string, Dictionary<string, int>>
            {
                {
                    "test1", new Dictionary<string, int>
                    {
                        { "test1_1", 1 },
                        { "test1_2", 2 },
                    }
                }
            };
            data.e = new[]
            {
                new Dictionary<string, Dictionary<string, int[][]>>
                {
                    {
                        "test2", new Dictionary<string, int[][]>
                        {
                            { "test2_1", data.a },
                            { "test2_2", data.a },
                        }
                    },
                    {
                        "test3", new Dictionary<string, int[][]>
                        {
                            { "test3_1", data.a },
                            { "test3_2", data.a },
                        }
                    }
                },
                new Dictionary<string, Dictionary<string, int[][]>>
                {
                    {
                        "test4", new Dictionary<string, int[][]>
                        {
                            { "test4_1", data.a },
                            { "test4_2", data.a },
                        }
                    },
                    {
                        "test5", new Dictionary<string, int[][]>
                        {
                            { "test5_1", data.a },
                            { "test5_2", data.a },
                        }
                    }
                }
            };
            data.f = new[]
            {
                new[] { SampleData(), SampleData() },
                new[] { SampleData(), SampleData() }
            };
            data.g = new List<Data[]>
            {
                new[] { SampleData(), SampleData() },
                new[] { SampleData(), SampleData() },
            };
            data.h = new[]
            {
                data.f,
                data.f
            };
            data.i = new[]
            {
                new List<Data> { SampleData() },
                new List<Data> { SampleData() }
            };
            data.j = new[]
            {
                data.g,
                data.g,
            };

            var result = NinoDeserializer.Deserialize<ComplexData>(NinoSerializer.Serialize(data));

            Assert.AreEqual(data.ToString(), result.ToString());
        }
    }
}
