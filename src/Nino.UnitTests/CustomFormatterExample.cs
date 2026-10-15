using Nino.Core;

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

    [NinoCustomFormatter(typeof(SpecialValueFormatter))]
    public SpecialValue CustomField;

    public string NormalString;

    [NinoCustomFormatter(typeof(CompactIntFormatter))]
    public int CompactInt;
}

[NinoType]
public class ExampleWithStructFormatter
{
    [NinoCustomFormatter(typeof(ZigZagLongFormatter))]
    public long Small;

    [NinoCustomFormatter(typeof(ZigZagLongFormatter))]
    public long Negative { get; set; }

    public int After;
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