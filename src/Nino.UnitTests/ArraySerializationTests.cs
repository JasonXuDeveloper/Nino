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

    [TestMethod]
    public void TestJaggedArrays()
    {
        // Test 1: Simple 2D jagged array of primitive type (int)
        int[][] int2DJagged = new int[][]
        {
            new int[] { 1, 2, 3 },
            new int[] { 4, 5 },
            new int[] { 6, 7, 8, 9 }
        };
        byte[] bytes = NinoSerializer.Serialize(int2DJagged);
        Assert.IsNotNull(bytes);
        Console.WriteLine($"Serialized int[][]: {string.Join(", ", bytes)}");

        int[][] int2DJaggedResult = NinoDeserializer.Deserialize<int[][]>(bytes);
        Assert.AreEqual(3, int2DJaggedResult.Length);
        Assert.AreEqual(3, int2DJaggedResult[0].Length);
        Assert.AreEqual(2, int2DJaggedResult[1].Length);
        Assert.AreEqual(4, int2DJaggedResult[2].Length);
        Assert.AreEqual(1, int2DJaggedResult[0][0]);
        Assert.AreEqual(5, int2DJaggedResult[1][1]);
        Assert.AreEqual(9, int2DJaggedResult[2][3]);

        // Test 2: Jagged array of bytes
        byte[][] byteJagged = new byte[][]
        {
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5 }
        };
        bytes = NinoSerializer.Serialize(byteJagged);
        Assert.IsNotNull(bytes);

        byte[][] byteJaggedResult = NinoDeserializer.Deserialize<byte[][]>(bytes);
        Assert.AreEqual(2, byteJaggedResult.Length);
        Assert.AreEqual(3, byteJaggedResult[0].Length);
        Assert.AreEqual(2, byteJaggedResult[1].Length);
        Assert.AreEqual(1, byteJaggedResult[0][0]);
        Assert.AreEqual(5, byteJaggedResult[1][1]);

        // Test 3: Jagged array of strings
        string[][] stringJagged = new string[][]
        {
            new string[] { "a", "b", "c" },
            new string[] { "d", "e" }
        };
        bytes = NinoSerializer.Serialize(stringJagged);
        Assert.IsNotNull(bytes);

        string[][] stringJaggedResult = NinoDeserializer.Deserialize<string[][]>(bytes);
        Assert.AreEqual(2, stringJaggedResult.Length);
        Assert.AreEqual(3, stringJaggedResult[0].Length);
        Assert.AreEqual("a", stringJaggedResult[0][0]);
        Assert.AreEqual("e", stringJaggedResult[1][1]);

        // Test 4: Triple-nested jagged array
        int[][][] int3DJagged = new int[][][]
        {
            new int[][]
            {
                new int[] { 1, 2 },
                new int[] { 3 }
            },
            new int[][]
            {
                new int[] { 4, 5, 6 }
            }
        };
        bytes = NinoSerializer.Serialize(int3DJagged);
        Assert.IsNotNull(bytes);

        int[][][] int3DJaggedResult = NinoDeserializer.Deserialize<int[][][]>(bytes);
        Assert.AreEqual(2, int3DJaggedResult.Length);
        Assert.AreEqual(2, int3DJaggedResult[0].Length);
        Assert.AreEqual(2, int3DJaggedResult[0][0].Length);
        Assert.AreEqual(1, int3DJaggedResult[0][0][0]);
        Assert.AreEqual(3, int3DJaggedResult[0][1][0]);
        Assert.AreEqual(6, int3DJaggedResult[1][0][2]);

        // Test 5: Jagged array of user-defined struct
        TestStruct[][] structJagged = new TestStruct[][]
        {
            new TestStruct[]
            {
                new TestStruct { A = 1, B = "First" },
                new TestStruct { A = 2, B = "Second" }
            },
            new TestStruct[]
            {
                new TestStruct { A = 3, B = "Third" }
            }
        };
        bytes = NinoSerializer.Serialize(structJagged);
        Assert.IsNotNull(bytes);

        TestStruct[][] structJaggedResult = NinoDeserializer.Deserialize<TestStruct[][]>(bytes);
        Assert.AreEqual(2, structJaggedResult.Length);
        Assert.AreEqual(2, structJaggedResult[0].Length);
        Assert.AreEqual(1, structJaggedResult[1].Length);
        Assert.AreEqual(1, structJaggedResult[0][0].A);
        Assert.AreEqual("Second", structJaggedResult[0][1].B);
        Assert.AreEqual(3, structJaggedResult[1][0].A);

        // Test 6: Jagged array of reference type (TestClass)
        TestClass[][] classJagged = new TestClass[][]
        {
            new TestClass[]
            {
                new TestClass { A = 10, B = "A" },
                new TestClass { A = 20, B = "B" }
            },
            new TestClass[]
            {
                new TestClass { A = 30, B = "C" }
            }
        };
        bytes = NinoSerializer.Serialize(classJagged);
        Assert.IsNotNull(bytes);

        TestClass[][] classJaggedResult = NinoDeserializer.Deserialize<TestClass[][]>(bytes);
        Assert.AreEqual(2, classJaggedResult.Length);
        Assert.AreEqual(2, classJaggedResult[0].Length);
        Assert.AreEqual(1, classJaggedResult[1].Length);
        Assert.AreEqual(10, classJaggedResult[0][0].A);
        Assert.AreEqual("B", classJaggedResult[0][1].B);
        Assert.AreEqual(30, classJaggedResult[1][0].A);

        // Test 7: Jagged array with null inner arrays
        int[][] jaggedWithNulls = new int[][]
        {
            new int[] { 1, 2 },
            null,
            new int[] { 3 }
        };
        bytes = NinoSerializer.Serialize(jaggedWithNulls);
        Assert.IsNotNull(bytes);

        int[][] jaggedWithNullsResult = NinoDeserializer.Deserialize<int[][]>(bytes);
        Assert.AreEqual(3, jaggedWithNullsResult.Length);
        Assert.IsNotNull(jaggedWithNullsResult[0]);
        Assert.IsNull(jaggedWithNullsResult[1]);
        Assert.IsNotNull(jaggedWithNullsResult[2]);
        Assert.AreEqual(2, jaggedWithNullsResult[0][1]);

        // Test 8: Null jagged array
        int[][] nullJagged = null;
        bytes = NinoSerializer.Serialize(nullJagged);
        Assert.IsNotNull(bytes);

        int[][] nullJaggedResult = NinoDeserializer.Deserialize<int[][]>(bytes);
        Assert.IsNull(nullJaggedResult);

        // Test 9: Empty jagged array
        int[][] emptyJagged = new int[0][];
        bytes = NinoSerializer.Serialize(emptyJagged);
        Assert.IsNotNull(bytes);

        int[][] emptyJaggedResult = NinoDeserializer.Deserialize<int[][]>(bytes);
        Assert.AreEqual(0, emptyJaggedResult.Length);

        // Test 10: Jagged array with empty inner arrays
        int[][] jaggedWithEmpty = new int[][]
        {
            new int[0],
            new int[] { 1 },
            new int[0]
        };
        bytes = NinoSerializer.Serialize(jaggedWithEmpty);
        Assert.IsNotNull(bytes);

        int[][] jaggedWithEmptyResult = NinoDeserializer.Deserialize<int[][]>(bytes);
        Assert.AreEqual(3, jaggedWithEmptyResult.Length);
        Assert.AreEqual(0, jaggedWithEmptyResult[0].Length);
        Assert.AreEqual(1, jaggedWithEmptyResult[1].Length);
        Assert.AreEqual(0, jaggedWithEmptyResult[2].Length);
        Assert.AreEqual(1, jaggedWithEmptyResult[1][0]);

        // Test 11: List of jagged arrays
        List<int[][]> listOfJagged = new List<int[][]>
        {
            new int[][] { new int[] { 1, 2 }, new int[] { 3 } },
            new int[][] { new int[] { 4, 5, 6 } }
        };
        bytes = NinoSerializer.Serialize(listOfJagged);
        Assert.IsNotNull(bytes);

        List<int[][]> listOfJaggedResult = NinoDeserializer.Deserialize<List<int[][]>>(bytes);
        Assert.AreEqual(2, listOfJaggedResult.Count);
        Assert.AreEqual(2, listOfJaggedResult[0].Length);
        Assert.AreEqual(1, listOfJaggedResult[1].Length);
        Assert.AreEqual(2, listOfJaggedResult[0][0][1]);
        Assert.AreEqual(6, listOfJaggedResult[1][0][2]);

        // Test 12: Dictionary with jagged array values
        Dictionary<string, byte[][]> dictWithJagged = new Dictionary<string, byte[][]>
        {
            { "first", new byte[][] { new byte[] { 1, 2 }, new byte[] { 3 } } },
            { "second", new byte[][] { new byte[] { 4, 5, 6 } } }
        };
        bytes = NinoSerializer.Serialize(dictWithJagged);
        Assert.IsNotNull(bytes);

        Dictionary<string, byte[][]> dictWithJaggedResult =
            NinoDeserializer.Deserialize<Dictionary<string, byte[][]>>(bytes);
        Assert.AreEqual(2, dictWithJaggedResult.Count);
        Assert.AreEqual(2, dictWithJaggedResult["first"].Length);
        Assert.AreEqual(2, dictWithJaggedResult["first"][0][1]);
        Assert.AreEqual(6, dictWithJaggedResult["second"][0][2]);

        Console.WriteLine("Jagged array tests passed!");
    }

    [TestMethod]
    public void TestJaggedArraysInCollections()
    {
        // Test 1: Queue<byte[][]>
        var queueWithJagged = new System.Collections.Generic.Queue<byte[][]>();
        queueWithJagged.Enqueue(new byte[][] { new byte[] { 1, 2 }, new byte[] { 3 } });
        queueWithJagged.Enqueue(new byte[][] { new byte[] { 4, 5, 6 } });

        byte[] bytes = NinoSerializer.Serialize(queueWithJagged);
        Assert.IsNotNull(bytes);

        var queueResult = NinoDeserializer.Deserialize<System.Collections.Generic.Queue<byte[][]>>(bytes);
        Assert.AreEqual(2, queueResult.Count);

        var first = queueResult.Dequeue();
        Assert.AreEqual(2, first.Length);
        Assert.AreEqual(2, first[0].Length);
        Assert.AreEqual(1, first[0][0]);

        var second = queueResult.Dequeue();
        Assert.AreEqual(1, second.Length);
        Assert.AreEqual(3, second[0].Length);
        Assert.AreEqual(6, second[0][2]);

        // Test 2: Stack<int[][]>
        var stackWithJagged = new System.Collections.Generic.Stack<int[][]>();
        stackWithJagged.Push(new int[][] { new int[] { 1, 2 }, new int[] { 3 } });
        stackWithJagged.Push(new int[][] { new int[] { 4, 5, 6 } });

        bytes = NinoSerializer.Serialize(stackWithJagged);
        Assert.IsNotNull(bytes);

        var stackResult = NinoDeserializer.Deserialize<System.Collections.Generic.Stack<int[][]>>(bytes);
        Assert.AreEqual(2, stackResult.Count);

        var firstStack = stackResult.Pop();
        Assert.AreEqual(1, firstStack.Length);
        Assert.AreEqual(3, firstStack[0].Length);
        Assert.AreEqual(6, firstStack[0][2]);

        Console.WriteLine("Jagged arrays in collections tests passed!");
    }

    [TestMethod]
    public void TestComplexMixedArraysInCollections()
    {
        // Test 1: Queue<int[][][,]> - Triple-nested jagged with 2D arrays at the end
        var queueComplex1 = new System.Collections.Generic.Queue<int[][][,]>();
        queueComplex1.Enqueue(new int[][][,]
        {
            new int[][,]
            {
                new int[,] { { 1, 2 }, { 3, 4 } },
                new int[,] { { 5, 6 }, { 7, 8 } }
            }
        });
        queueComplex1.Enqueue(new int[][][,]
        {
            new int[][,]
            {
                new int[,] { { 9, 10 }, { 11, 12 } }
            }
        });

        byte[] bytes = NinoSerializer.Serialize(queueComplex1);
        Assert.IsNotNull(bytes);

        var queueResult1 = NinoDeserializer.Deserialize<System.Collections.Generic.Queue<int[][][,]>>(bytes);
        Assert.AreEqual(2, queueResult1.Count);

        var first1 = queueResult1.Dequeue();
        Assert.AreEqual(1, first1.Length);
        Assert.AreEqual(2, first1[0].Length);
        Assert.AreEqual(2, first1[0][0][0, 1]);  // Row 0, Column 1 = 2
        Assert.AreEqual(4, first1[0][0][1, 1]);  // Row 1, Column 1 = 4

        var second1 = queueResult1.Dequeue();
        Assert.AreEqual(12, second1[0][0][1, 1]);

        // Test 2: Stack<byte[][,]> - Jagged array with 2D arrays
        var stackComplex = new System.Collections.Generic.Stack<byte[][,]>();
        stackComplex.Push(new byte[][,]
        {
            new byte[,] { { 1, 2, 3 }, { 4, 5, 6 } },
            new byte[,] { { 7, 8, 9 }, { 10, 11, 12 } }
        });
        stackComplex.Push(new byte[][,]
        {
            new byte[,] { { 13, 14 }, { 15, 16 } }
        });

        bytes = NinoSerializer.Serialize(stackComplex);
        Assert.IsNotNull(bytes);

        var stackResult = NinoDeserializer.Deserialize<System.Collections.Generic.Stack<byte[][,]>>(bytes);
        Assert.AreEqual(2, stackResult.Count);

        var firstStack = stackResult.Pop();
        Assert.AreEqual(1, firstStack.Length);
        Assert.AreEqual(16, firstStack[0][1, 1]);

        // Test 3: List<int[,][]> - 2D array with jagged arrays as elements
        var listComplex = new System.Collections.Generic.List<int[,][]>();
        listComplex.Add(new int[,][]
        {
            { new int[] { 1, 2 }, new int[] { 3 } },
            { new int[] { 4, 5, 6 }, new int[] { 7 } }
        });
        listComplex.Add(new int[,][]
        {
            { new int[] { 8 }, new int[] { 9, 10 } }
        });

        bytes = NinoSerializer.Serialize(listComplex);
        Assert.IsNotNull(bytes);

        var listResult = NinoDeserializer.Deserialize<System.Collections.Generic.List<int[,][]>>(bytes);
        Assert.AreEqual(2, listResult.Count);
        Assert.AreEqual(2, listResult[0].GetLength(0));
        Assert.AreEqual(2, listResult[0].GetLength(1));
        Assert.AreEqual(2, listResult[0][0, 0].Length);
        Assert.AreEqual(1, listResult[0][0, 0][0]);
        Assert.AreEqual(6, listResult[0][1, 0][2]);

        // Test 4: Dictionary<string, float[][,][,,]> - Very complex nested structure
        var dictComplex = new System.Collections.Generic.Dictionary<string, float[][,][,,]>();
        dictComplex["key1"] = new float[][,][,,]
        {
            new float[,][,,]
            {
                {
                    new float[,,] { { { 1.1f, 1.2f }, { 1.3f, 1.4f } } },
                    new float[,,] { { { 2.1f, 2.2f }, { 2.3f, 2.4f } } }
                }
            }
        };
        dictComplex["key2"] = new float[][,][,,]
        {
            new float[,][,,]
            {
                {
                    new float[,,] { { { 3.1f, 3.2f }, { 3.3f, 3.4f } } }
                }
            }
        };

        bytes = NinoSerializer.Serialize(dictComplex);
        Assert.IsNotNull(bytes);

        var dictResult = NinoDeserializer.Deserialize<System.Collections.Generic.Dictionary<string, float[][,][,,]>>(bytes);
        Assert.AreEqual(2, dictResult.Count);
        Assert.AreEqual(1.1f, dictResult["key1"][0][0, 0][0, 0, 0]);
        Assert.AreEqual(2.4f, dictResult["key1"][0][0, 1][0, 1, 1]);
        Assert.AreEqual(3.4f, dictResult["key2"][0][0, 0][0, 1, 1]);

        // Test 5: Queue<string[,][][]> - 2D array with double-nested jagged
        var queueComplex2 = new System.Collections.Generic.Queue<string[,][][]>();
        queueComplex2.Enqueue(new string[,][][]
        {
            {
                new string[][] { new string[] { "a", "b" }, new string[] { "c" } },
                new string[][] { new string[] { "d" } }
            }
        });

        bytes = NinoSerializer.Serialize(queueComplex2);
        Assert.IsNotNull(bytes);

        var queueResult2 = NinoDeserializer.Deserialize<System.Collections.Generic.Queue<string[,][][]>>(bytes);
        Assert.AreEqual(1, queueResult2.Count);

        var item = queueResult2.Dequeue();
        Assert.AreEqual(1, item.GetLength(0));
        Assert.AreEqual(2, item.GetLength(1));
        Assert.AreEqual("b", item[0, 0][0][1]);
        Assert.AreEqual("d", item[0, 1][0][0]);

        // Test 6: Stack<int[][,][,,]> - Jagged with 2D then 3D arrays
        var stackComplex2 = new System.Collections.Generic.Stack<int[][,][,,]>();
        stackComplex2.Push(new int[][,][,,]
        {
            new int[,][,,]
            {
                {
                    new int[,,] { { { 1, 2 }, { 3, 4 } } },
                    new int[,,] { { { 5, 6 }, { 7, 8 } } }
                }
            }
        });

        bytes = NinoSerializer.Serialize(stackComplex2);
        Assert.IsNotNull(bytes);

        var stackResult2 = NinoDeserializer.Deserialize<System.Collections.Generic.Stack<int[][,][,,]>>(bytes);
        Assert.AreEqual(1, stackResult2.Count);

        var stackItem = stackResult2.Pop();
        Assert.AreEqual(1, stackItem.Length);
        Assert.AreEqual(8, stackItem[0][0, 1][0, 1, 1]);

        // Test 7: List<byte[][][,]> with null elements
        var listWithNulls = new System.Collections.Generic.List<byte[][][,]>();
        listWithNulls.Add(new byte[][][,]
        {
            new byte[][,] { new byte[,] { { 1, 2 } } }
        });
        listWithNulls.Add(null);
        listWithNulls.Add(new byte[][][,]
        {
            new byte[][,] { new byte[,] { { 3, 4 } } }
        });

        bytes = NinoSerializer.Serialize(listWithNulls);
        Assert.IsNotNull(bytes);

        var listNullsResult = NinoDeserializer.Deserialize<System.Collections.Generic.List<byte[][][,]>>(bytes);
        Assert.AreEqual(3, listNullsResult.Count);
        Assert.IsNotNull(listNullsResult[0]);
        Assert.IsNull(listNullsResult[1]);
        Assert.IsNotNull(listNullsResult[2]);
        Assert.AreEqual(2, listNullsResult[0][0][0][0, 1]);

        // Test 8: Dictionary with complex mixed arrays as both key wrapper and value
        var dictMixed = new System.Collections.Generic.Dictionary<int, int[][,]>();
        dictMixed[1] = new int[][,]
        {
            new int[,] { { 10, 20 }, { 30, 40 } },
            new int[,] { { 50, 60 }, { 70, 80 } }
        };
        dictMixed[2] = new int[][,]
        {
            new int[,] { { 90, 100 } }
        };

        bytes = NinoSerializer.Serialize(dictMixed);
        Assert.IsNotNull(bytes);

        var dictMixedResult = NinoDeserializer.Deserialize<System.Collections.Generic.Dictionary<int, int[][,]>>(bytes);
        Assert.AreEqual(2, dictMixedResult.Count);
        Assert.AreEqual(40, dictMixedResult[1][0][1, 1]);
        Assert.AreEqual(80, dictMixedResult[1][1][1, 1]);
        Assert.AreEqual(100, dictMixedResult[2][0][0, 1]);

        Console.WriteLine("Complex mixed arrays in collections tests passed!");
    }

    [TestMethod]
    public void TestArraySegmentWithComplexArrays()
    {
        Console.WriteLine("Testing ArraySegment with complex array types...");

        // Test 1: ArraySegment<byte[]> - Simple jagged array
        var jaggedArray1 = new byte[][]
        {
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5 },
            new byte[] { 6, 7, 8, 9 }
        };
        var segment1 = new System.ArraySegment<byte[]>(jaggedArray1);

        byte[] bytes = NinoSerializer.Serialize(segment1);
        Assert.IsNotNull(bytes);

        var segmentResult1 = NinoDeserializer.Deserialize<System.ArraySegment<byte[]>>(bytes);
        Assert.AreEqual(3, segmentResult1.Count);
        Assert.AreEqual(3, segmentResult1[0].Length);
        Assert.AreEqual(2, segmentResult1[1].Length);
        Assert.AreEqual(4, segmentResult1[2].Length);
        Assert.AreEqual(1, segmentResult1[0][0]);
        Assert.AreEqual(5, segmentResult1[1][1]);
        Assert.AreEqual(9, segmentResult1[2][3]);

        // Test 2: ArraySegment<int[][]> - Double jagged
        var doubleJagged = new int[][][]
        {
            new int[][]
            {
                new int[] { 1, 2 },
                new int[] { 3, 4, 5 }
            },
            new int[][]
            {
                new int[] { 6 }
            }
        };
        var segment2 = new System.ArraySegment<int[][]>(doubleJagged);

        bytes = NinoSerializer.Serialize(segment2);
        Assert.IsNotNull(bytes);

        var segmentResult2 = NinoDeserializer.Deserialize<System.ArraySegment<int[][]>>(bytes);
        Assert.AreEqual(2, segmentResult2.Count);
        Assert.AreEqual(2, segmentResult2[0].Length);
        Assert.AreEqual(1, segmentResult2[1].Length);
        Assert.AreEqual(2, segmentResult2[0][0][1]);
        Assert.AreEqual(5, segmentResult2[0][1][2]);
        Assert.AreEqual(6, segmentResult2[1][0][0]);

        // Test 3: ArraySegment with offset and count (non-full segment)
        var jaggedArray2 = new int[][]
        {
            new int[] { 1, 2 },
            new int[] { 3, 4 },
            new int[] { 5, 6 },
            new int[] { 7, 8 }
        };
        var segment3 = new System.ArraySegment<int[]>(jaggedArray2, 1, 2); // Only middle two elements

        bytes = NinoSerializer.Serialize(segment3);
        Assert.IsNotNull(bytes);

        var segmentResult3 = NinoDeserializer.Deserialize<System.ArraySegment<int[]>>(bytes);
        Assert.AreEqual(2, segmentResult3.Count);
        Assert.AreEqual(3, segmentResult3[0][0]);
        Assert.AreEqual(4, segmentResult3[0][1]);
        Assert.AreEqual(5, segmentResult3[1][0]);
        Assert.AreEqual(6, segmentResult3[1][1]);

        Console.WriteLine("ArraySegment with complex array types tests passed!");
    }

#if NET6_0_OR_GREATER
    [TestMethod]
    public void TestPriorityQueueWithComplexArrays()
    {
        Console.WriteLine("Testing PriorityQueue with complex array types (.NET 6.0+)...");

        // Test 1: PriorityQueue with jagged array elements
        var pq1 = new System.Collections.Generic.PriorityQueue<byte[][], int>();
        pq1.Enqueue(new byte[][] { new byte[] { 1, 2 }, new byte[] { 3, 4, 5 } }, 1);
        pq1.Enqueue(new byte[][] { new byte[] { 6 } }, 2);
        pq1.Enqueue(new byte[][] { new byte[] { 7, 8 }, new byte[] { 9 } }, 3);

        byte[] bytes = NinoSerializer.Serialize(pq1);
        Assert.IsNotNull(bytes);

        var pqResult1 = NinoDeserializer.Deserialize<System.Collections.Generic.PriorityQueue<byte[][], int>>(bytes);
        Assert.AreEqual(3, pqResult1.Count);

        var first = pqResult1.Dequeue();
        Assert.AreEqual(2, first.Length);
        Assert.AreEqual(1, first[0][0]);
        Assert.AreEqual(5, first[1][2]);

        // Test 2: PriorityQueue with triple-jagged arrays
        var pq2 = new System.Collections.Generic.PriorityQueue<int[][][], int>();
        pq2.Enqueue(new int[][][]
        {
            new int[][] { new int[] { 1, 2 }, new int[] { 3 } }
        }, 10);
        pq2.Enqueue(new int[][][]
        {
            new int[][] { new int[] { 4, 5, 6 } }
        }, 20);

        bytes = NinoSerializer.Serialize(pq2);
        Assert.IsNotNull(bytes);

        var pqResult2 = NinoDeserializer.Deserialize<System.Collections.Generic.PriorityQueue<int[][][], int>>(bytes);
        Assert.AreEqual(2, pqResult2.Count);

        var firstElement = pqResult2.Dequeue();
        Assert.AreEqual(1, firstElement.Length);
        Assert.AreEqual(2, firstElement[0].Length);
        Assert.AreEqual(1, firstElement[0][0][0]);
        Assert.AreEqual(2, firstElement[0][0][1]);
        Assert.AreEqual(3, firstElement[0][1][0]);

        // Test 3: PriorityQueue with 2D arrays
        var pq3 = new System.Collections.Generic.PriorityQueue<int[,], int>();
        pq3.Enqueue(new int[,] { { 1, 2, 3 }, { 4, 5, 6 } }, 1);
        pq3.Enqueue(new int[,] { { 7, 8 }, { 9, 10 } }, 2);

        bytes = NinoSerializer.Serialize(pq3);
        Assert.IsNotNull(bytes);

        var pqResult3 = NinoDeserializer.Deserialize<System.Collections.Generic.PriorityQueue<int[,], int>>(bytes);
        Assert.AreEqual(2, pqResult3.Count);

        var first2D = pqResult3.Dequeue();
        Assert.AreEqual(2, first2D.GetLength(0));
        Assert.AreEqual(3, first2D.GetLength(1));
        Assert.AreEqual(1, first2D[0, 0]);
        Assert.AreEqual(6, first2D[1, 2]);

        // Test 4: PriorityQueue with 3D arrays
        var pq4 = new System.Collections.Generic.PriorityQueue<int[,,], int>();
        pq4.Enqueue(new int[,,]
        {
            { { 1, 2 }, { 3, 4 } },
            { { 5, 6 }, { 7, 8 } }
        }, 10);

        bytes = NinoSerializer.Serialize(pq4);
        Assert.IsNotNull(bytes);

        var pqResult4 = NinoDeserializer.Deserialize<System.Collections.Generic.PriorityQueue<int[,,], int>>(bytes);
        Assert.AreEqual(1, pqResult4.Count);

        var first3D = pqResult4.Dequeue();
        Assert.AreEqual(2, first3D.GetLength(0));
        Assert.AreEqual(2, first3D.GetLength(1));
        Assert.AreEqual(2, first3D.GetLength(2));
        Assert.AreEqual(1, first3D[0, 0, 0]);
        Assert.AreEqual(8, first3D[1, 1, 1]);

        // Test 5: PriorityQueue with mixed jagged/multi-dim arrays (int[][,])
        var pq5 = new System.Collections.Generic.PriorityQueue<int[][,], int>();
        pq5.Enqueue(new int[][,]
        {
            new int[,] { { 1, 2 }, { 3, 4 } },
            new int[,] { { 5, 6 }, { 7, 8 } }
        }, 1);

        bytes = NinoSerializer.Serialize(pq5);
        Assert.IsNotNull(bytes);

        var pqResult5 = NinoDeserializer.Deserialize<System.Collections.Generic.PriorityQueue<int[][,], int>>(bytes);
        Assert.AreEqual(1, pqResult5.Count);

        var firstMixed = pqResult5.Dequeue();
        Assert.AreEqual(2, firstMixed.Length);
        Assert.AreEqual(4, firstMixed[0][1, 1]);
        Assert.AreEqual(8, firstMixed[1][1, 1]);

        // Test 6: PriorityQueue with mixed multi-dim/jagged arrays (int[,][])
        var pq6 = new System.Collections.Generic.PriorityQueue<int[,][], string>();
        pq6.Enqueue(new int[,][]
        {
            { new int[] { 1, 2 }, new int[] { 3 } },
            { new int[] { 4, 5, 6 }, new int[] { 7, 8 } }
        }, "low");

        bytes = NinoSerializer.Serialize(pq6);
        Assert.IsNotNull(bytes);

        var pqResult6 = NinoDeserializer.Deserialize<System.Collections.Generic.PriorityQueue<int[,][], string>>(bytes);
        Assert.AreEqual(1, pqResult6.Count);

        var firstMixed2 = pqResult6.Dequeue();
        Assert.AreEqual(2, firstMixed2.GetLength(0));
        Assert.AreEqual(2, firstMixed2.GetLength(1));
        Assert.AreEqual(1, firstMixed2[0, 0][0]);
        Assert.AreEqual(3, firstMixed2[0, 1][0]);
        Assert.AreEqual(6, firstMixed2[1, 0][2]);
        Assert.AreEqual(8, firstMixed2[1, 1][1]);

        Console.WriteLine("PriorityQueue with complex array types tests passed!");
    }
#endif
}
