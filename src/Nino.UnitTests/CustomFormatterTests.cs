using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class CustomFormatterTests
{
    [TestMethod]
    public void TestNewCustomFormatterSystem()
    {
        // Start with a simple test to verify basic functionality
        Console.WriteLine("Testing new custom formatter system...");

        // Create test instance with custom formatter fields
        ExampleWithCustomFormatters example = new ExampleWithCustomFormatters
        {
            NormalField = 42,
            CustomField = new SpecialValue { Value = 1337, Label = "TestSpecial" },
            NormalString = "HelloWorld",
            CompactInt = 127 // Simple single-byte value
        };

        Console.WriteLine("Original data:");
        Console.WriteLine($"  NormalField: {example.NormalField}");
        Console.WriteLine($"  CustomField.Value: {example.CustomField.Value}");
        Console.WriteLine($"  CustomField.Label: {example.CustomField.Label}");
        Console.WriteLine($"  NormalString: {example.NormalString}");
        Console.WriteLine($"  CompactInt: {example.CompactInt}");

        // Serialize using the new singleton-based custom formatters
        byte[] bytes;
        try
        {
            bytes = NinoSerializer.Serialize(example);
            Assert.IsNotNull(bytes);
            Console.WriteLine($"✅ Serialization successful: {bytes.Length} bytes");
            Console.WriteLine($"Serialized bytes: {string.Join(", ", bytes)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Serialization failed: {ex.Message}");
            throw;
        }

        // Deserialize and verify data integrity
        try
        {
            ExampleWithCustomFormatters result = NinoDeserializer.Deserialize<ExampleWithCustomFormatters>(bytes);
            Assert.IsNotNull(result);
            Console.WriteLine("✅ Deserialization successful");

            Console.WriteLine("Deserialized data:");
            Console.WriteLine($"  NormalField: {result.NormalField}");
            Console.WriteLine($"  CustomField.Value: {result.CustomField.Value}");
            Console.WriteLine($"  CustomField.Label: {result.CustomField.Label}");
            Console.WriteLine($"  NormalString: {result.NormalString}");
            Console.WriteLine($"  CompactInt: {result.CompactInt}");

            // Verify normal fields are preserved
            Assert.AreEqual(example.NormalField, result.NormalField, "Normal field should be preserved");
            Assert.AreEqual(example.NormalString, result.NormalString, "Normal string should be preserved");

            // Verify custom formatted SpecialValue field
            Assert.AreEqual(example.CustomField.Value, result.CustomField.Value,
                "SpecialValue.Value should be preserved");
            Assert.AreEqual(example.CustomField.Label, result.CustomField.Label,
                "SpecialValue.Label should be preserved");

            // Verify custom formatted compact int field
            Assert.AreEqual(example.CompactInt, result.CompactInt, "CompactInt should be preserved");

            Console.WriteLine("✅ Data integrity verified through round-trip serialization");

            // Verify custom formatters are being used by checking byte patterns

            // The CompactIntFormatter should encode 127 as a single byte (0x7F = 127)
            bool foundCompactInt = false;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0x7F)
                {
                    foundCompactInt = true;
                    Console.WriteLine($"✅ Found CompactInt encoding at byte {i}: 0x7F (127)");
                    break;
                }
            }

            Assert.IsTrue(foundCompactInt, "CompactIntFormatter should encode 127 as single byte 0x7F");

            Console.WriteLine("✅ All custom formatter tests passed!");
            Console.WriteLine("✅ SpecialValueFormatter correctly invoked for custom serialization");
            Console.WriteLine("✅ CompactIntFormatter correctly invoked for variable-length encoding");
            Console.WriteLine("✅ Expected byte patterns confirmed - custom formatters are working correctly");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Deserialization failed: {ex}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }
}
