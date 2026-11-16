using System;
using System.Buffers.Binary;

namespace Nino.Codegen.Inspect
{
    /// <summary>Data that is not what Nino writes: where the reader stopped, and why (the check .NET makes there).</summary>
    internal sealed class InspectException : Exception
    {
        public InspectException(int offset, string message) : base(message) => Offset = offset;

        /// <summary>The offset in the input where the failing value starts.</summary>
        public int Offset { get; }
    }

    /// <summary>
    /// Reads the input forward, never past <see cref="End"/>: the end of the data, or of the region (a version-tolerant
    /// body, a keyed entry, a deferred body's entry) being read. Offsets are the input's.
    /// </summary>
    internal sealed class WireReader
    {
        private readonly byte[] _data;

        public WireReader(byte[] data, int start)
        {
            _data = data;
            Pos = start;
            End = data.Length;
        }

        public int Pos { get; set; }

        /// <summary>Where the current region ends; the input's length outside regions.</summary>
        public int End { get; set; }

        public int Length => _data.Length;

        public int Remaining => End - Pos;

        public ReadOnlySpan<byte> Data => _data;

        public InspectException Fail(string message) => Fail(Pos, message);

        public static InspectException Fail(int offset, string message) => new(offset, message);

        /// <summary>The next <paramref name="count"/> bytes, which must lie inside the current region.</summary>
        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining)
                throw Fail(End == _data.Length
                    ? $"unexpected end of data: {count} byte(s) needed, {Remaining} left"
                    : $"runs past the end of its region at {End}: {count} byte(s) needed, {Remaining} left");
            var span = new ReadOnlySpan<byte>(_data, Pos, count);
            Pos += count;
            return span;
        }

        public byte U8() => Take(1)[0];

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        /// <summary>A length or count: -1 (null) or 0 and more, for elements of at least <paramref name="minimum"/> bytes.</summary>
        public int Count(int minimum, bool nullable = true)
        {
            int at = Pos;
            int count = I32();
            if (count == -1 && nullable)
                return -1;
            if (count < 0)
                throw Fail(at, $"invalid length {count}");
            if ((long)count * Math.Max(minimum, 1) > Remaining)
                throw Fail(at, $"invalid length {count}: {(long)count * Math.Max(minimum, 1)} byte(s) or more " +
                               $"claimed, {Remaining} left");
            return count;
        }

        /// <summary>A string header (wire-format spec §2.3.1): minimal, below 2^32.</summary>
        public uint Header()
        {
            int at = Pos;
            byte first = U8();
            uint value;
            switch (first)
            {
                case < 0x80:
                    return first;
                case 0x80:
                    value = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
                    if (value < 0x80)
                        throw Fail(at, $"string header not minimal ({value} in 3 bytes)");
                    return value;
                case 0x81:
                    value = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
                    if (value < 0x10000)
                        throw Fail(at, $"string header not minimal ({value} in 5 bytes)");
                    return value;
                default:
                    throw Fail(at, $"invalid string header marker 0x{first:X2}");
            }
        }
    }
}
