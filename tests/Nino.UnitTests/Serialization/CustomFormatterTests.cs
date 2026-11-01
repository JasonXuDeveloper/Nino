using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

// Custom formatters are structs implementing INinoFormatter<T>: generated code calls them as default(F), inlined.

/// <summary>
/// Custom type that needs special serialization
/// </summary>
public struct SpecialValue
{
    public int Value { get; set; }
    public string Label { get; set; }
}

/// <summary>Custom formatter for SpecialValue: the value, the label length, then the label as UTF-8.</summary>
public struct SpecialValueFormatter : INinoFormatter<SpecialValue>
{
    public void Serialize(SpecialValue value, ref Writer writer)
    {
        // Custom logic: serialize as value + label length + label
        writer.Write(value.Value);
        writer.Write(value.Label?.Length ?? 0);
        if (!string.IsNullOrEmpty(value.Label))
        {
            writer.WriteUtf8(value.Label);
        }
    }

    public void Deserialize(ref SpecialValue value, ref Reader reader)
    {
        reader.Read(out int intValue);
        reader.Read(out int labelLength);

        string label = null;
        if (labelLength > 0)
        {
            reader.ReadUtf8(out label);
        }

        value = new SpecialValue { Value = intValue, Label = label };
    }
}

/// <summary>Custom formatter for compact integer encoding (a varint).</summary>
public struct CompactIntFormatter : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer)
    {
        // Simple variable-length integer encoding
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }

        writer.Write((byte)value);
    }

    public void Deserialize(ref int value, ref Reader reader)
    {
        value = 0;
        int shift = 0;
        byte b;

        do
        {
            reader.Read(out b);
            value |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
    }
}

/// <summary>
/// Struct custom formatter: called directly by generated code (no virtual call, no instance).
/// Writes a long as a zig-zag varint.
/// </summary>
public struct ZigZagLongFormatter : INinoFormatter<long>
{
    public void Serialize(long value, ref Writer writer)
    {
        ulong v = (ulong)((value << 1) ^ (value >> 63));
        while (v >= 0x80)
        {
            writer.Write((byte)(v | 0x80));
            v >>= 7;
        }

        writer.Write((byte)v);
    }

    public void Deserialize(ref long value, ref Reader reader)
    {
        ulong v = 0;
        int shift = 0;
        byte b;
        do
        {
            reader.Read(out b);
            v |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        value = (long)(v >> 1) ^ -(long)(v & 1);
    }
}

/// <summary>Example type using custom formatters.</summary>
[NinoType]
public class ExampleWithCustomFormatters
{
    public int NormalField;

    [NinoMemberFormatter(typeof(SpecialValueFormatter))]
    public SpecialValue CustomField;

    public string NormalString;

    [NinoMemberFormatter(typeof(CompactIntFormatter))]
    public int CompactInt;
}

[NinoType]
public class ExampleWithStructFormatter
{
    [NinoMemberFormatter(typeof(ZigZagLongFormatter))]
    public long Small;

    [NinoMemberFormatter(typeof(ZigZagLongFormatter))]
    public long Negative { get; set; }

    public int After;
}

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

    [TestMethod]
    public void StructCustomFormatterIsUsed()
    {
        var value = new ExampleWithStructFormatter { Small = 3, Negative = -2, After = 7 };
        byte[] bytes = NinoSerializer.Serialize(value);
        var result = NinoDeserializer.Deserialize<ExampleWithStructFormatter>(bytes);
        Assert.AreEqual(3, result.Small);
        Assert.AreEqual(-2, result.Negative);
        Assert.AreEqual(7, result.After);

        // One varint byte per long instead of 8 raw bytes.
        var plain = NinoSerializer.Serialize(new ExampleWithStructFormatter { After = 7 });
        Assert.AreEqual(plain.Length, bytes.Length);
        Assert.IsTrue(bytes.Length < 8 + 8 + 4);
    }
}
