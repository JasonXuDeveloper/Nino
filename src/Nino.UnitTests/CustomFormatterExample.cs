using Nino.Core;

namespace Nino.UnitTests;
// Example showing the new high-performance singleton-based custom formatter system
// This eliminates runtime lookups, provides compile-time type safety, and uses cached singletons

/// <summary>
/// Custom type that needs special serialization
/// </summary>
public struct SpecialValue
{
    public int Value { get; set; }
    public string Label { get; set; }
}

/// <summary>
/// High-performance custom formatter for SpecialValue.
/// Inherits from NinoFormatter{T} and provides strongly-typed methods.
/// Singleton instance is automatically provided by the base class - no manual declaration needed!
/// </summary>
public class SpecialValueFormatter : NinoFormatter<SpecialValue>
{
    // No need to declare Instance - NinoFormatterInstance<SpecialValueFormatter, SpecialValue> handles it automatically!
    public override void Serialize(SpecialValue value, ref Writer writer)
    {
        // Custom logic: serialize as value + label length + label
        writer.Write(value.Value);
        writer.Write(value.Label?.Length ?? 0);
        if (!string.IsNullOrEmpty(value.Label))
        {
            writer.WriteUtf8(value.Label);
        }
    }

    public override void Deserialize(out SpecialValue value, ref Reader reader)
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

    public override void DeserializeRef(ref SpecialValue value, ref Reader reader)
    {
        Deserialize(out value, ref reader);
    }
}

/// <summary>
/// High-performance custom formatter for compact integer encoding.
/// Singleton is automatically managed by NinoFormatterInstance - no manual work required!
/// </summary>
public class CompactIntFormatter : NinoFormatter<int>
{
    // No need to declare Instance - NinoFormatterInstance<CompactIntFormatter, int> handles it automatically!
    public override void Serialize(int value, ref Writer writer)
    {
        // Simple variable-length integer encoding
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }

        writer.Write((byte)value);
    }

    public override void Deserialize(out int value, ref Reader reader)
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

    public override void DeserializeRef(ref int value, ref Reader reader)
    {
        Deserialize(out value, ref reader);
    }
}

/// <summary>
/// Example type using the new singleton-based custom formatters
/// </summary>
[NinoType]
public class ExampleWithCustomFormatters
{
    public int NormalField;

    // This field uses the new singleton-based formatter - maximum performance!
    // Cached singleton instance + direct method calls = zero overhead
    [NinoCustomFormatter(typeof(SpecialValueFormatter))]
    public SpecialValue CustomField;

    public string NormalString;

    // Another custom field with singleton formatter
    [NinoCustomFormatter(typeof(CompactIntFormatter))]
    public int CompactInt;
}

// Helper class for comparison testing (without custom formatters)
[NinoType]
public class ExampleForComparison
{
    public int NormalField;
    public SpecialValue StandardSpecialValue; // No custom formatter
    public string NormalString;
    public int StandardInt; // No custom formatter
}