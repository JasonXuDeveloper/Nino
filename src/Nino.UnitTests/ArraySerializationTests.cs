using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class ArraySerializationTests
{
    [TestMethod]
    public void ArraySegmentBytes()
    {
        ArraySegment<byte> bytes = new ArraySegment<byte>(new byte[] { 1, 2, 3, 4, 5 });
        byte[] serialized = NinoSerializer.Serialize(bytes);
        Assert.IsNotNull(serialized);
        ArraySegment<byte> deserialized = NinoDeserializer.Deserialize<ArraySegment<byte>>(serialized);
        Assert.AreEqual(bytes.Count, deserialized.Count);
        for (int i = 0; i < bytes.Count; i++)
        {
            Assert.AreEqual(bytes.Array[bytes.Offset + i], deserialized.Array[deserialized.Offset + i]);
        }
    }

    [TestMethod]
    public void TestMultiDimensionalArrays()
    {
        // Test 1: 2D array of primitive type (int)
        int[,] int2D = new int[,]
        {
            { 1, 2, 3 },
            { 4, 5, 6 }
        };
        byte[] bytes = NinoSerializer.Serialize(int2D);
        Assert.IsNotNull(bytes);
        Console.WriteLine($"Serialized int[,]: {string.Join(", ", bytes)}");

        int[,] int2DResult = NinoDeserializer.Deserialize<int[,]>(bytes);
        Assert.AreEqual(2, int2DResult.GetLength(0));
        Assert.AreEqual(3, int2DResult.GetLength(1));
        Assert.AreEqual(1, int2DResult[0, 0]);
        Assert.AreEqual(2, int2DResult[0, 1]);
        Assert.AreEqual(3, int2DResult[0, 2]);
        Assert.AreEqual(4, int2DResult[1, 0]);
        Assert.AreEqual(5, int2DResult[1, 1]);
        Assert.AreEqual(6, int2DResult[1, 2]);

        // Test 2: 3D array of primitive type (float)
        float[,,] float3D = new float[,,]
        {
            {
                { 1.1f, 2.2f },
                { 3.3f, 4.4f }
            },
            {
                { 5.5f, 6.6f },
                { 7.7f, 8.8f }
            }
        };
        bytes = NinoSerializer.Serialize(float3D);
        Assert.IsNotNull(bytes);

        float[,,] float3DResult = NinoDeserializer.Deserialize<float[,,]>(bytes);
        Assert.AreEqual(2, float3DResult.GetLength(0));
        Assert.AreEqual(2, float3DResult.GetLength(1));
        Assert.AreEqual(2, float3DResult.GetLength(2));
        Assert.AreEqual(1.1f, float3DResult[0, 0, 0]);
        Assert.AreEqual(2.2f, float3DResult[0, 0, 1]);
        Assert.AreEqual(8.8f, float3DResult[1, 1, 1]);

        // Test 3: 2D array of user-defined struct (TestStruct)
        TestStruct[,] struct2D = new TestStruct[,]
        {
            {
                new TestStruct { A = 1, B = "First" },
                new TestStruct { A = 2, B = "Second" }
            },
            {
                new TestStruct { A = 3, B = "Third" },
                new TestStruct { A = 4, B = "Fourth" }
            }
        };
        bytes = NinoSerializer.Serialize(struct2D);
        Assert.IsNotNull(bytes);

        TestStruct[,] struct2DResult = NinoDeserializer.Deserialize<TestStruct[,]>(bytes);
        Assert.AreEqual(2, struct2DResult.GetLength(0));
        Assert.AreEqual(2, struct2DResult.GetLength(1));
        Assert.AreEqual(1, struct2DResult[0, 0].A);
        Assert.AreEqual("First", struct2DResult[0, 0].B);
        Assert.AreEqual(4, struct2DResult[1, 1].A);
        Assert.AreEqual("Fourth", struct2DResult[1, 1].B);

        // Test 4: 2D array of reference type (TestClass)
        TestClass[,] class2D = new TestClass[,]
        {
            {
                new TestClass { A = 10, B = "A" },
                new TestClass { A = 20, B = "B" }
            },
            {
                new TestClass { A = 30, B = "C" },
                new TestClass { A = 40, B = "D" }
            }
        };
        bytes = NinoSerializer.Serialize(class2D);
        Assert.IsNotNull(bytes);

        TestClass[,] class2DResult = NinoDeserializer.Deserialize<TestClass[,]>(bytes);
        Assert.AreEqual(2, class2DResult.GetLength(0));
        Assert.AreEqual(2, class2DResult.GetLength(1));
        Assert.AreEqual(10, class2DResult[0, 0].A);
        Assert.AreEqual("A", class2DResult[0, 0].B);
        Assert.AreEqual(40, class2DResult[1, 1].A);
        Assert.AreEqual("D", class2DResult[1, 1].B);

        // Test 5: 4D array of bool (testing higher dimensions)
        bool[,,,] bool4D = new bool[2, 2, 2, 2];
        bool4D[0, 0, 0, 0] = true;
        bool4D[1, 1, 1, 1] = true;
        bytes = NinoSerializer.Serialize(bool4D);
        Assert.IsNotNull(bytes);

        bool[,,,] bool4DResult = NinoDeserializer.Deserialize<bool[,,,]>(bytes);
        Assert.AreEqual(2, bool4DResult.GetLength(0));
        Assert.AreEqual(2, bool4DResult.GetLength(1));
        Assert.AreEqual(2, bool4DResult.GetLength(2));
        Assert.AreEqual(2, bool4DResult.GetLength(3));
        Assert.IsTrue(bool4DResult[0, 0, 0, 0]);
        Assert.IsTrue(bool4DResult[1, 1, 1, 1]);
        Assert.IsFalse(bool4DResult[0, 1, 0, 0]);

        // Test 6: Mixed - jagged array containing multi-dimensional arrays
        int[][,] jaggedWith2D = new int[][,]
        {
            new int[,] { { 1, 2 }, { 3, 4 } },
            new int[,] { { 5, 6 }, { 7, 8 } }
        };
        bytes = NinoSerializer.Serialize(jaggedWith2D);
        Assert.IsNotNull(bytes);

        int[][,] jaggedWith2DResult = NinoDeserializer.Deserialize<int[][,]>(bytes);
        Assert.AreEqual(2, jaggedWith2DResult.Length);
        Assert.AreEqual(2, jaggedWith2DResult[0].GetLength(0));
        Assert.AreEqual(2, jaggedWith2DResult[0].GetLength(1));
        Assert.AreEqual(1, jaggedWith2DResult[0][0, 0]);
        Assert.AreEqual(4, jaggedWith2DResult[0][1, 1]);
        Assert.AreEqual(5, jaggedWith2DResult[1][0, 0]);
        Assert.AreEqual(8, jaggedWith2DResult[1][1, 1]);

        // Test 7: Multi-dimensional array as type parameter in generic
        List<int[,]> listOf2DArrays = new List<int[,]>
        {
            new int[,] { { 1, 2 }, { 3, 4 } },
            new int[,] { { 5, 6 }, { 7, 8 } }
        };
        bytes = NinoSerializer.Serialize(listOf2DArrays);
        Assert.IsNotNull(bytes);

        List<int[,]> listOf2DArraysResult = NinoDeserializer.Deserialize<List<int[,]>>(bytes);
        Assert.AreEqual(2, listOf2DArraysResult.Count);
        Assert.AreEqual(1, listOf2DArraysResult[0][0, 0]);
        Assert.AreEqual(4, listOf2DArraysResult[0][1, 1]);
        Assert.AreEqual(5, listOf2DArraysResult[1][0, 0]);
        Assert.AreEqual(8, listOf2DArraysResult[1][1, 1]);

        // Test 8: Dictionary with multi-dimensional array values
        Dictionary<string, int[,]> dictWith2DArrays = new Dictionary<string, int[,]>
        {
            { "first", new int[,] { { 1, 2 }, { 3, 4 } } },
            { "second", new int[,] { { 5, 6 }, { 7, 8 } } }
        };
        bytes = NinoSerializer.Serialize(dictWith2DArrays);
        Assert.IsNotNull(bytes);

        Dictionary<string, int[,]> dictWith2DArraysResult =
            NinoDeserializer.Deserialize<Dictionary<string, int[,]>>(bytes);
        Assert.AreEqual(2, dictWith2DArraysResult.Count);
        Assert.AreEqual(1, dictWith2DArraysResult["first"][0, 0]);
        Assert.AreEqual(4, dictWith2DArraysResult["first"][1, 1]);
        Assert.AreEqual(5, dictWith2DArraysResult["second"][0, 0]);
        Assert.AreEqual(8, dictWith2DArraysResult["second"][1, 1]);

        // Test 9: Null multi-dimensional array
        int[,] nullArray = null;
        bytes = NinoSerializer.Serialize(nullArray);
        Assert.IsNotNull(bytes);

        int[,] nullArrayResult = NinoDeserializer.Deserialize<int[,]>(bytes);
        Assert.IsNull(nullArrayResult);

        // Test 10: Empty multi-dimensional array (0 elements)
        int[,] emptyArray = new int[0, 0];
        bytes = NinoSerializer.Serialize(emptyArray);
        Assert.IsNotNull(bytes);

        int[,] emptyArrayResult = NinoDeserializer.Deserialize<int[,]>(bytes);
        Assert.AreEqual(0, emptyArrayResult.GetLength(0));
        Assert.AreEqual(0, emptyArrayResult.GetLength(1));

        Console.WriteLine("Multi-dimensional array tests passed!");
    }
}
