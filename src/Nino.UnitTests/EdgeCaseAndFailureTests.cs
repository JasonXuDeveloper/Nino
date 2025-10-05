using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class EdgeCaseAndFailureTests
{
    [TestMethod]
    public void TestBoxedSerializationFailures()
    {
        Console.WriteLine("Testing expected failures for boxed serialization of non-polymorphic types...");

        // === Test List<object> with SAME element types ===

        // Test 1: List<object> with all integers should fail
        var listWithAllInts = new List<object> { 1, 2, 3, 4, 5 };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(listWithAllInts);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "List<object> with all integers should fail");

        // Test 2: List<object> with all booleans should fail
        var listWithAllBools = new List<object> { true, false, true, false };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(listWithAllBools);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "List<object> with all booleans should fail");

        // Test 3: List<object> with all strings should fail
        var listWithAllStrings = new List<object> { "test1", "test2", "test3" };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(listWithAllStrings);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "List<object> with all strings should fail");

        // Test 4: List<object> with all float should fail
        var listWithAllFloats = new List<object> { 1.1f, 2.2f, 3.3f };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(listWithAllFloats);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "List<object> with all floats should fail");

        // Test 5: List<object> with all List<int> should fail
        var listWithAllLists = new List<object>
            { new List<int> { 1, 2 }, new List<int> { 3, 4 }, new List<int> { 5, 6 } };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(listWithAllLists);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "List<object> with all List<int> should fail");

        // Test 6: List<object> with all Dictionary<string, int> should fail
        var listWithAllDicts = new List<object>
        {
            new Dictionary<string, int> { { "a", 1 } },
            new Dictionary<string, int> { { "b", 2 } }
        };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(listWithAllDicts);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "List<object> with all Dictionary<string, int> should fail");

        // Test 7: List<object> with all int arrays should fail
        var listWithAllArrays = new List<object> { new int[] { 1, 2 }, new int[] { 3, 4 }, new int[] { 5, 6 } };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(listWithAllArrays);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "List<object> with all int arrays should fail");

        // === Test List<object> with MIXED element types ===

        // Test 8: Mixed List<object> with primitive types should fail
        var mixedPrimitives = new List<object> { 42, 3.14f, true, 'c', (byte)255 };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(mixedPrimitives);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "Mixed List<object> with primitive types should fail");

        // Test 9: Mixed List<object> with strings and primitives should fail
        var mixedStringsAndPrimitives = new List<object> { "test", 42, true };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(mixedStringsAndPrimitives);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "Mixed List<object> with strings and primitives should fail");

        // Test 10: Mixed List<object> with collections should fail
        var mixedCollections = new List<object>
            { new List<int> { 1 }, new Dictionary<string, int> { { "a", 1 } }, new int[] { 1, 2 } };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(mixedCollections);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "Mixed List<object> with collections should fail");

        // Test 11: Mixed List<object> with everything should fail
        var mixedEverything = new List<object> { 42, "test", new List<int> { 1 }, new int[] { 1 }, true, 3.14f };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(mixedEverything);
            NinoDeserializer.Deserialize<List<object>>(data);
        }, "Mixed List<object> with all non-polymorphic types should fail");

        // === Test Generic<object> with non-polymorphic types ===

        // Test 12: Generic<object> with string should fail
        var genericWithString = new Generic<object> { Val = "test string" };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(genericWithString);
            NinoDeserializer.Deserialize<Generic<object>>(data);
        }, "Generic<object> with string should fail");

        // Test 13: Generic<object> with int should fail
        var genericWithInt = new Generic<object> { Val = 42 };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(genericWithInt);
            NinoDeserializer.Deserialize<Generic<object>>(data);
        }, "Generic<object> with int should fail");

        // Test 14: Generic<object> with bool should fail
        var genericWithBool = new Generic<object> { Val = true };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(genericWithBool);
            NinoDeserializer.Deserialize<Generic<object>>(data);
        }, "Generic<object> with bool should fail");

        // Test 15: Generic<object> with float should fail
        var genericWithFloat = new Generic<object> { Val = 3.14f };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(genericWithFloat);
            NinoDeserializer.Deserialize<Generic<object>>(data);
        }, "Generic<object> with float should fail");

        // Test 16: Generic<object> with List<int> should fail
        var genericWithList = new Generic<object> { Val = new List<int> { 1, 2, 3 } };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(genericWithList);
            NinoDeserializer.Deserialize<Generic<object>>(data);
        }, "Generic<object> with List<int> should fail");

        // Test 17: Generic<object> with Dictionary<string, int> should fail
        var genericWithDict = new Generic<object> { Val = new Dictionary<string, int> { { "key", 123 } } };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(genericWithDict);
            NinoDeserializer.Deserialize<Generic<object>>(data);
        }, "Generic<object> with Dictionary<string, int> should fail");

        // Test 18: Generic<object> with int array should fail
        var genericWithArray = new Generic<object> { Val = new int[] { 1, 2, 3 } };
        Assert.ThrowsException<Exception>(() =>
        {
            var data = NinoSerializer.Serialize(genericWithArray);
            NinoDeserializer.Deserialize<Generic<object>>(data);
        }, "Generic<object> with int array should fail");

        Console.WriteLine("All boxed serialization failure tests passed - non-polymorphic types correctly rejected");
    }
}
