using Nino.Shared.Util;
using Nino.Serialization;
using System.Collections.Generic;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Test10
    {
        private const string SerializationTest10 = "Nino/Test/Serialization/Test10 - Complex Data";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest10, priority=10)]
#endif
        public static void Main()
        {
            ComplexData data = new ComplexData();
            data.a = new int[3][];
            data.a[0] = new int[2] { 1, 2 };
            data.a[1] = new int[2] { 10, 20 };
            data.a[2] = new int[2] { 100, 200 };
            data.b = new List<int[]>()
            {
                new int[] { 3, 5 },
                new int[] { 7, 9 },
            };
            data.c = new List<int>[]
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
            data.e = new Dictionary<string, Dictionary<string, int[][]>>[]
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
            data.f = new Data[][]
            {
                new Data[]
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
                        name = "asdfhudjh"
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
                        name = "asdfhudjh"
                    }
                },
                new Data[]
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
                        name = "asdfhudjh"
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
                        name = "asdfhudjh"
                    }
                }
            };
            data.g = new List<Data[]>()
            {
                new Data[]
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
                        name = "asdfhudjh"
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
                        name = "asdfhudjh"
                    }
                },
                new Data[]
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
                        name = "asdfhudjh"
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
                        name = "asdfhudjh"
                    }
                },
            };
            data.h = new Data[][][]
            {
                data.f,
                data.f
            };
            data.i = new List<Data>[]
            {
                new List<Data>()
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
                        name = "asdfhudjh"
                    }
                },
                new List<Data>()
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
                        name = "asdfhudjh"
                    }
                }
            };
            data.j = new List<Data[]>[]
            {
                data.g,
                data.g,
            };
            Logger.D(data);
            var buf = Serializer.Serialize(data);
            Logger.D($"Serialized data: {buf.Length} bytes, {string.Join(",",buf)}");
            Logger.D($"Deserialized as: {Deserializer.Deserialize<ComplexData>(buf)}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod