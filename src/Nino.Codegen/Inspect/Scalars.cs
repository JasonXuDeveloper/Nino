using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Nino.Codegen.Model;

namespace Nino.Codegen.Inspect
{
    /// <summary>
    /// The fixed-size values of the wire format (wire-format spec §3) as text and JSON, with the checks a reader makes
    /// on them in untrusted mode (§9.3, §9.10): <c>bool</c> 0 or 1, <c>decimal</c> flags and scale, <c>DateTime</c>,
    /// <c>DateOnly</c>, <c>TimeOnly</c> and <c>DateTimeOffset</c> ranges.
    /// </summary>
    internal static class Scalars
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>A primitive of <paramref name="kind"/> at <paramref name="at"/> in <paramref name="data"/>.</summary>
        public static (string Text, string Json) Primitive(ReadOnlySpan<byte> data, int at, PrimitiveKind kind)
        {
            var b = data.Slice(at);
            switch (kind)
            {
                case PrimitiveKind.Bool:
                    if (b[0] > 1)
                        throw WireReader.Fail(at, $"invalid bool byte {b[0]}");
                    return b[0] == 1 ? ("true", "true") : ("false", "false");
                case PrimitiveKind.I8:
                    return Integer((sbyte)b[0]);
                case PrimitiveKind.U8:
                    return Integer(b[0]);
                case PrimitiveKind.I16:
                    return Integer(BinaryPrimitives.ReadInt16LittleEndian(b));
                case PrimitiveKind.U16:
                    return Integer(BinaryPrimitives.ReadUInt16LittleEndian(b));
                case PrimitiveKind.I32:
                    return Integer(BinaryPrimitives.ReadInt32LittleEndian(b));
                case PrimitiveKind.U32:
                    return Integer(BinaryPrimitives.ReadUInt32LittleEndian(b));
                case PrimitiveKind.I64:
                case PrimitiveKind.NInt:
                    return Integer(BinaryPrimitives.ReadInt64LittleEndian(b));
                case PrimitiveKind.U64:
                case PrimitiveKind.NUInt:
                {
                    ulong value = BinaryPrimitives.ReadUInt64LittleEndian(b);
                    string text = value.ToString(Invariant);
                    return (text, InspectNode.JsonInteger(text, value <= 9007199254740992UL));
                }
                case PrimitiveKind.F16:
                    return Float((double)BinaryPrimitives.ReadHalfLittleEndian(b),
                        BinaryPrimitives.ReadHalfLittleEndian(b).ToString(Invariant));
                case PrimitiveKind.F32:
                {
                    float value = BinaryPrimitives.ReadSingleLittleEndian(b);
                    return Float(value, value.ToString("R", Invariant));
                }
                case PrimitiveKind.F64:
                {
                    double value = BinaryPrimitives.ReadDoubleLittleEndian(b);
                    return Float(value, value.ToString("R", Invariant));
                }
                case PrimitiveKind.Char:
                {
                    char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(b);
                    string code = "U+" + ((int)c).ToString("X4", Invariant);
                    return (char.IsSurrogate(c) || char.IsControl(c) ? code : $"'{c}' ({code})",
                        ((int)c).ToString(Invariant));
                }
                case PrimitiveKind.Decimal:
                    return Decimal(b, at);
                case PrimitiveKind.Guid:
                {
                    string text = new Guid(b.Slice(0, 16)).ToString();
                    return (text, InspectNode.JsonString(text));
                }
                case PrimitiveKind.DateTime:
                    return DateTime(BinaryPrimitives.ReadUInt64LittleEndian(b), at);
                case PrimitiveKind.TimeSpan:
                {
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(b);
                    return (TimeSpan.FromTicks(ticks).ToString("c", Invariant), Integer(ticks).Item2);
                }
                case PrimitiveKind.DateOnly:
                {
                    int day = BinaryPrimitives.ReadInt32LittleEndian(b);
                    if (day < 0 || day > DateOnly.MaxValue.DayNumber)
                        throw WireReader.Fail(at, $"invalid DateOnly day number {day}");
                    return (DateOnly.FromDayNumber(day).ToString("yyyy-MM-dd", Invariant), day.ToString(Invariant));
                }
                case PrimitiveKind.TimeOnly:
                {
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(b);
                    if (ticks < 0 || ticks >= TimeSpan.TicksPerDay)
                        throw WireReader.Fail(at, $"invalid TimeOnly ticks {ticks}");
                    return (new TimeOnly(ticks).ToString("HH:mm:ss.fffffff", Invariant), Integer(ticks).Item2);
                }
                case PrimitiveKind.I128:
                {
                    var value = new Int128(BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(8)),
                        BinaryPrimitives.ReadUInt64LittleEndian(b));
                    string text = value.ToString(Invariant);
                    return (text, InspectNode.JsonInteger(text, value >= -9007199254740992 && value <= 9007199254740992));
                }
                case PrimitiveKind.U128:
                {
                    var value = new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(8)),
                        BinaryPrimitives.ReadUInt64LittleEndian(b));
                    string text = value.ToString(Invariant);
                    return (text, InspectNode.JsonInteger(text, value <= 9007199254740992));
                }
                default: // Rune
                {
                    int value = BinaryPrimitives.ReadInt32LittleEndian(b);
                    string code = "U+" + value.ToString("X4", Invariant);
                    return (Rune.IsValid(value) && !Rune.IsControl(new Rune(value)) ? $"'{new Rune(value)}' ({code})" : code,
                        value.ToString(Invariant));
                }
            }
        }

        public static (string, string) Integer(long value)
        {
            string text = value.ToString(Invariant);
            return (text, InspectNode.JsonInteger(text, InspectNode.Exact(value)));
        }

        /// <summary>A float as JSON: a number when finite (the shortest text that reads back), else a string.</summary>
        private static (string, string) Float(double value, string text) =>
            (text, double.IsFinite(value) ? text : InspectNode.JsonString(text));

        private static (string, string) Decimal(ReadOnlySpan<byte> b, int at)
        {
            int flags = BinaryPrimitives.ReadInt32LittleEndian(b);
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(4));
            ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(8));
            int scale = (flags >> 16) & 0xFF;
            if ((flags & 0x7F00FFFF) != 0 || scale > 28)
                throw WireReader.Fail(at, $"invalid decimal flags 0x{flags:X8} (scale {scale})");
            var value = new decimal((int)lo, (int)(lo >> 32), (int)hi, flags < 0, (byte)scale);
            string text = value.ToString(Invariant);
            return (text, InspectNode.JsonString(text));
        }

        private static readonly string[] Kinds = { "Unspecified", "Utc", "Local", "Local" };

        private static (string, string) DateTime(ulong raw, int at)
        {
            long ticks = (long)(raw & 0x3FFFFFFFFFFFFFFFUL);
            int kind = (int)(raw >> 62);
            if (ticks > System.DateTime.MaxValue.Ticks)
                throw WireReader.Fail(at, $"invalid DateTime ticks {ticks}, past DateTime.MaxValue");
            string text = new System.DateTime(ticks).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", Invariant) + " " +
                          Kinds[kind] + (kind == 3 ? " (repeated hour)" : "");
            return (text, $"{{\"ticks\":{Integer(ticks).Item2},\"kind\":\"{Kinds[kind]}\"}}");
        }

        /// <summary>A <c>DateTimeOffset</c>: UTC ticks and an offset in minutes, checked as §3.4 says.</summary>
        public static (string, string) DateTimeOffset(long utcTicks, int offsetMinutes, int at)
        {
            long local = utcTicks + offsetMinutes * TimeSpan.TicksPerMinute;
            if (offsetMinutes is < -840 or > 840 || utcTicks < 0 || utcTicks > System.DateTime.MaxValue.Ticks ||
                local < 0 || local > System.DateTime.MaxValue.Ticks)
                throw WireReader.Fail(at, $"invalid DateTimeOffset (UTC ticks {utcTicks}, offset {offsetMinutes} minutes)");
            var value = new System.DateTimeOffset(new System.DateTime(local), TimeSpan.FromMinutes(offsetMinutes));
            return (value.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", Invariant),
                $"{{\"utcTicks\":{Integer(utcTicks).Item2},\"offsetMinutes\":{offsetMinutes.ToString(Invariant)}}}");
        }

        public static (string, string) BigInteger(ReadOnlySpan<byte> bytes)
        {
            string text = new BigInteger(bytes).ToString(Invariant);
            return (text, InspectNode.JsonString(text));
        }

        /// <summary>Bytes as lowercase hex, the text shortened past <paramref name="shown"/> bytes.</summary>
        public static (string Text, string Json) Hex(ReadOnlySpan<byte> bytes, int shown = 64)
        {
            string all = Convert.ToHexString(bytes).ToLowerInvariant();
            string text = bytes.Length <= shown
                ? Spaced(all)
                : Spaced(all.Substring(0, shown * 2)) + $" ... ({bytes.Length} bytes)";
            return (text, "\"" + all + "\"");
        }

        private static string Spaced(string hex) =>
            hex.Length == 0 ? "(none)" : string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }
}
