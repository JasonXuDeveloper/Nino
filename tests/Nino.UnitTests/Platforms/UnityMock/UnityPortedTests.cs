using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nino.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nino.UnitTests.UnityMock
{
    [TestClass]
    public class UnityPortedTests
    {
        [TestMethod]
        public void TestSimpleVector3()
        {
            Vector3 v = Vector3.one;
            Console.WriteLine($"Serializing: {v}");
            
            var bs = NinoSerializer.Serialize(v);
            Console.WriteLine($"Serialized to {bs.Length} bytes: {string.Join(",", bs)}");
            
            var result = NinoDeserializer.Deserialize<Vector3>(bs);
            Console.WriteLine($"Deserialized: {result}");
            
            Assert.AreEqual(v.ToString(), result.ToString());
        }

        [TestMethod]
        public void TestSimpleData()
        {
            Data d = new Data { x = 5, y = 10, z = 15, f = 1.5f, d = 2.5m, db = 3.5, bo = true, en = TestEnum.A };
            Console.WriteLine($"Serializing: {d}");
            
            var bs = NinoSerializer.Serialize(d);
            Console.WriteLine($"Serialized to {bs.Length} bytes: {string.Join(",", bs)}");
            
            var result = NinoDeserializer.Deserialize<Data>(bs);
            Console.WriteLine($"Deserialized: {result}");
            
            Assert.AreEqual(d.ToString(), result.ToString());
        }
        [TestMethod]
        public void TestCommonClass()
        {
            //custom type
            var fixedDateTime = new DateTime(2025, 7, 19, 10, 25, 2, DateTimeKind.Utc);
            PrimitiveTypeTest c = new PrimitiveTypeTest()
            {
                ni = null,
                v3 = Vector3.one,
                m = Matrix4x4.identity,
                qs = new List<Quaternion>()
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
                Dt = fixedDateTime
            };

            Console.WriteLine($"will serialize c: {c}");
            var bs = NinoSerializer.Serialize(c);
            Console.WriteLine($"serialized to {bs.Length} bytes: {string.Join(",", bs)}");

            Console.WriteLine("will deserialize");
            try
            {
                PrimitiveTypeTest cc = NinoDeserializer.Deserialize<PrimitiveTypeTest>(bs);
                Console.WriteLine($"deserialized as cc: {cc}");
                Assert.AreEqual(c.ToString(), cc.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Deserialization failed: {ex}");
                throw;
            }
        }

        [TestMethod]
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
            Console.WriteLine(
                "serialize an 'include all' class with code gen, this will not make the serialization result larger or slower, if and only if code gen occurs for an include all class");
            Console.WriteLine($"will serialize codeGen: {codeGen}");
            sw.Reset();
            sw.Start();
            var bs = NinoSerializer.Serialize(codeGen);
            sw.Stop();
            Console.WriteLine(
                $"serialized to {bs.Length} bytes in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {string.Join(",", bs)}");

            Console.WriteLine("will deserialize");
            sw.Reset();
            sw.Start();
            var codeGenR = NinoDeserializer.Deserialize<IncludeAllClassCodeGen>(bs);
            sw.Stop();
            Console.WriteLine(
                $"deserialized as codeGenR in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {codeGenR}");

            NotIncludeAllClass d = new NotIncludeAllClass()
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            Console.WriteLine(
                "Now in comparison, we serialize a class with the same structure and same value");
            Console.WriteLine($"will serialize d in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {d}");
            sw.Reset();
            sw.Start();
            bs = NinoSerializer.Serialize(d);
            sw.Stop();
            Console.WriteLine($"serialized to {bs.Length} bytes: {string.Join(",", bs)}");

            Console.WriteLine("will deserialize");
            sw.Reset();
            sw.Start();
            var dd = NinoDeserializer.Deserialize<NotIncludeAllClass>(bs);
            sw.Stop();
            Console.WriteLine($"deserialized as dd in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {dd}");

            Assert.AreEqual(codeGen.ToString(), codeGenR.ToString());
            Assert.AreEqual(d.ToString(), dd.ToString());
            Assert.AreEqual(codeGen.ToString(), dd.ToString());
        }
    }
}