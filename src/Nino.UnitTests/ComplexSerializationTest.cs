using System;
using System.Linq;
using Nino.Serialization;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#pragma warning disable 8618

namespace Nino.UnitTests
{
    [NinoSerialize]
    [CodeGenIgnore]
    public partial class ComplexData
    {
        [NinoMember(0)]
        public int[][] A;
        [NinoMember(1)]
        public List<int[]> B;
        [NinoMember(2)]
        public List<int>[] C;
        [NinoMember(3)]
        public Dictionary<string,Dictionary<string, int>> D;
        [NinoMember(4)]
        public Dictionary<string,Dictionary<string, int[][]>>[] E;
        [NinoMember(5)] 
        public Data[][] F;
        [NinoMember(6)]
        public List<Data[]> G;
        [NinoMember(7)]
        public Data[][][] H;
        [NinoMember(8)]
        public List<Data>[] I;
        [NinoMember(9)]
        public List<Data[]>[] J;
        public override string ToString()
        {
            return $"{string.Join(",", A.SelectMany(x => x).ToArray())},\n" +
                   $"{string.Join(",", B.SelectMany(x => x).ToArray())},\n" +
                   $"{string.Join(",", C.SelectMany(x => x).ToArray())},\n" +
                   $"{GetDictString(D)},\n" +
                   $"{string.Join(",\n", E.Select(GetDictString).ToArray())}\n" +
                   $"{string.Join(",\n", F.SelectMany(x => x).Select(x => x))}\n" +
                   $"{string.Join(",\n", G.SelectMany(x => x).Select(x => x))}\n" +
                   $"{string.Join(",\n", H.SelectMany(x => x).SelectMany(x => x).Select(x => x))}\n" +
                   $"{string.Join(",\n", I.SelectMany(x => x).Select(x => x))}\n" +
                   $"{string.Join(",\n", J.SelectMany(x => x).Select(x => x).SelectMany(x => x).Select(x => x))}\n";
        }

        private string GetDictString<TK, TV>(Dictionary<TK, Dictionary<TK, TV>> ddd)
        {
            return $"{string.Join(",", ddd.Keys.ToList())},\n" +
                   $"   {string.Join(",", ddd.Values.ToList().SelectMany(k => k.Keys))},\n" +
                   $"   {string.Join(",", ddd.Values.ToList().SelectMany(k => k.Values))}";
        }
    }

    [NinoSerialize]
    [CodeGenIgnore]
    public partial class Data
    {
        [NinoMember(1)] public int X;

        [NinoMember(2)] public short Y;

        [NinoMember(3)] public long Z;

        [NinoMember(4)] public float F;

        [NinoMember(5)] public decimal D;

        [NinoMember(6)] public double Db;

        [NinoMember(7)] public bool Bo;

        [NinoMember(8)] public TestEnum En;

        [NinoMember(9)] public string Name = "";

        public override string ToString()
        {
            return $"{X},{Y},{Z},{F},{D},{Db},{Bo},{En},{Name}";
        }
    }

    [NinoSerialize]
    [CodeGenIgnore]
    public partial class NestedData
    {
        [NinoMember(1)] [System.Runtime.Serialization.DataMember]
        public string Name = "";

        [NinoMember(2)] public Data[] Ps = Array.Empty<Data>();

        public override string ToString()
        {
            return $"{Name},{Ps[0]}";
        }
    }

    public enum TestEnum : byte
    {
        A = 1,
        B = 2
    }

    [TestClass]
    public partial class ComplexSerializationTest
    {
        [TestMethod]
        public void TestNonGenericNoCodeGen()
        {
            Data dt = new Data()
            {
                X = short.MaxValue,
                Y = byte.MaxValue,
                Z = short.MaxValue,
                F = 1234.56789f,
                D = 66.66666666m,
                Db = 999.999999999999,
                Bo = true,
                En = TestEnum.A,
                Name = "aasdfghjhgtrewqwerftg"
            };

            var buf = Serializer.Serialize((object)dt);
            var dt2 = Deserializer.Deserialize(typeof(Data), buf);
            Assert.IsTrue(dt.ToString() == dt2.ToString());
        }

        [TestMethod]
        public void TestNonGenericCodeGen()
        {
            A dt = new A()
            {
                Val = 1
            };

            var buf = Serializer.Serialize((object)dt);
            var dt2 = Deserializer.Deserialize(typeof(A), buf);
            Assert.IsTrue(dt.ToString() == dt2.ToString());
        }

        [TestMethod]
        public void TestNestedData()
        {
            //nested data
            Data[] dt = new Data[1000];
            for (int i = 0; i < dt.Length; i++)
            {
                dt[i] = new Data()
                {
                    X = short.MaxValue,
                    Y = byte.MaxValue,
                    Z = short.MaxValue,
                    F = 1234.56789f,
                    D = 66.66666666m,
                    Db = 999.999999999999,
                    Bo = true,
                    En = TestEnum.A,
                    Name = "aasdfghjhgtrewqwerftg"
                };
            }

            var nd = new NestedData()
            {
                Name = "Test",
                Ps = dt
            };

            var buf = Serializer.Serialize(nd);
            var nd2 = Deserializer.Deserialize<NestedData>(buf);
            Assert.AreEqual(nd.Name, nd2.Name);
            Assert.AreEqual(nd.Ps.Length, nd2.Ps.Length);
            for (int i = 0; i < nd.Ps.Length; i++)
            {
                Assert.AreEqual(nd.Ps[i].X, nd2.Ps[i].X);
                Assert.AreEqual(nd.Ps[i].Y, nd2.Ps[i].Y);
                Assert.AreEqual(nd.Ps[i].Z, nd2.Ps[i].Z);
                Assert.AreEqual(nd.Ps[i].F, nd2.Ps[i].F);
                Assert.AreEqual(nd.Ps[i].D, nd2.Ps[i].D);
                Assert.AreEqual(nd.Ps[i].Db, nd2.Ps[i].Db);
                Assert.AreEqual(nd.Ps[i].Bo, nd2.Ps[i].Bo);
                Assert.AreEqual(nd.Ps[i].En, nd2.Ps[i].En);
                Assert.AreEqual(nd.Ps[i].Name, nd2.Ps[i].Name);
            }
        }

        [TestMethod]
        public void TestComplexData()
        {
            ComplexData data = new ComplexData();
            data.A = new int[3][];
            data.A[0] = new[] { 1, 2 };
            data.A[1] = new[] { 10, 20 };
            data.A[2] = new[] { 100, 200 };
            data.B = new List<int[]>()
            {
                new[] { 3, 5 },
                new[] { 7, 9 },
            };
            data.C = new[]
            {
                new List<int>() { 10, 11, 12 },
                new List<int>() { 13, 14, 15 },
            };
            data.D = new Dictionary<string, Dictionary<string, int>>()
            {
                {
                    "test1", new Dictionary<string, int>()
                    {
                        { "test1_1", 1 },
                        { "test1_2", 2 },
                    }
                }
            };
            data.E = new[]
            {
                new Dictionary<string, Dictionary<string, int[][]>>()
                {
                    {
                        "test2", new Dictionary<string, int[][]>()
                        {
                            { "test2_1", data.A },
                            { "test2_2", data.A },
                        }
                    },
                    {
                        "test3", new Dictionary<string, int[][]>()
                        {
                            { "test3_1", data.A },
                            { "test3_2", data.A },
                        }
                    }
                },
                new Dictionary<string, Dictionary<string, int[][]>>()
                {
                    {
                        "test4", new Dictionary<string, int[][]>()
                        {
                            { "test4_1", data.A },
                            { "test4_2", data.A },
                        }
                    },
                    {
                        "test5", new Dictionary<string, int[][]>()
                        {
                            { "test5_1", data.A },
                            { "test5_2", data.A },
                        }
                    }
                }
            };
            data.F = new[]
            {
                new[]
                {
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    },
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    }
                },
                new[]
                {
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    },
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    }
                }
            };
            data.G = new List<Data[]>()
            {
                new[]
                {
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    },
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    }
                },
                new[]
                {
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    },
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    }
                },
            };
            data.H = new[]
            {
                data.F,
                data.F
            };
            data.I = new[]
            {
                new List<Data>()
                {
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    }
                },
                new List<Data>()
                {
                    new Data()
                    {
                        X = short.MaxValue,
                        Y = byte.MaxValue,
                        Z = short.MaxValue,
                        F = 1234.56789f,
                        D = 66.66666666m,
                        Db = 999.999999999999,
                        Bo = true,
                        En = TestEnum.A,
                        Name = "asdfhudjh"
                    }
                }
            };
            data.J = new[]
            {
                data.G,
                data.G,
            };
            var buf = Serializer.Serialize(data);
            var data2 = Deserializer.Deserialize<ComplexData>(buf);
            Assert.AreEqual(data.ToString(), data2.ToString());
        }
    }
}