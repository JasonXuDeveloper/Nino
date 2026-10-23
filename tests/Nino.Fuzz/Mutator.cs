using System;
using System.Collections.Generic;

namespace Nino.Fuzz
{
    /// <summary>
    /// Byte-level mutations aimed at Nino's format: lengths, counts and type ids are little-endian int32s, so most
    /// mutations write an interesting 32-bit value (or one relative to the bytes left) at a random offset.
    /// </summary>
    public sealed class Mutator
    {
        public const int MaxLength = 1 << 20;

        static readonly int[] Interesting =
        {
            0, 1, -1, -2, 2, 3, 4, 7, 8, 15, 16, 31, 32, 63, 64, 127, 128, 255, 256, 0x7FFF, 0x8000, 0xFFFF, 0x10000,
            int.MaxValue, int.MinValue, int.MinValue | 4, int.MinValue | 8, 0x40000000, 0x3FFFFFFF, 1 << 20, 1 << 24,
            -3, -4, -1000, 0x7FFFFFF0, unchecked((int)0x80000001), 0x01010101,
        };

        readonly Random _rng;

        public Mutator(Random rng) => _rng = rng;

        public byte[] Mutate(byte[] input, IReadOnlyList<byte[]> pool)
        {
            var d = new List<byte>(input);
            int rounds = _rng.Next(8) == 0 ? 4 + _rng.Next(12) : 1 + _rng.Next(3);
            for (int i = 0; i < rounds; i++) Once(d, pool);
            if (d.Count > MaxLength) d.RemoveRange(MaxLength, d.Count - MaxLength);
            return d.ToArray();
        }

        void Once(List<byte> d, IReadOnlyList<byte[]> pool)
        {
            if (d.Count == 0) d.Add((byte)_rng.Next(256));
            int pos = _rng.Next(d.Count);
            switch (_rng.Next(12))
            {
                case 0: d[pos] ^= (byte)(1 << _rng.Next(8)); break;
                case 1: d[pos] = (byte)_rng.Next(256); break;
                case 2:
                case 3:
                case 4:
                {
                    // An int32 at pos: an interesting value, or the number of bytes left give or take a few.
                    int v = _rng.Next(3) == 0 ? d.Count - pos - 4 + _rng.Next(-8, 9) : Interesting[_rng.Next(Interesting.Length)];
                    if (_rng.Next(4) == 0) v = ~v; // Nino writes some lengths complemented (strings: ~length).
                    for (int b = 0; b < 4 && pos + b < d.Count; b++) d[pos + b] = (byte)(v >> (8 * b));
                    break;
                }
                case 5: d.RemoveRange(pos, Math.Min(d.Count - pos, 1 + _rng.Next(_rng.Next(2) == 0 ? 8 : 64))); break;
                case 6:
                {
                    int n = 1 + _rng.Next(8);
                    var ins = new byte[n];
                    _rng.NextBytes(ins);
                    d.InsertRange(pos, ins);
                    break;
                }
                case 7: d.RemoveRange(pos, d.Count - pos); break; // truncate
                case 8:
                {
                    // Copy a chunk elsewhere.
                    int len = Math.Min(d.Count - pos, 1 + _rng.Next(32));
                    var chunk = d.GetRange(pos, len);
                    d.InsertRange(_rng.Next(d.Count + 1), chunk);
                    break;
                }
                case 9:
                {
                    // Repeat a chunk many times in place: deep nesting out of one level of it.
                    int len = Math.Min(d.Count - pos, 1 + _rng.Next(16));
                    var chunk = d.GetRange(pos, len);
                    int times = 2 + _rng.Next(_rng.Next(4) == 0 ? 2000 : 32);
                    for (int t = 0; t < times && d.Count + len <= MaxLength; t++) d.InsertRange(pos, chunk);
                    break;
                }
                case 10:
                {
                    // Splice in part of another input of the pool.
                    if (pool.Count == 0) break;
                    var other = pool[_rng.Next(pool.Count)];
                    if (other.Length == 0) break;
                    int from = _rng.Next(other.Length);
                    int len = Math.Min(other.Length - from, 1 + _rng.Next(256));
                    int cut = Math.Min(d.Count - pos, _rng.Next(len + 1));
                    d.RemoveRange(pos, cut);
                    d.InsertRange(pos, new ArraySegment<byte>(other, from, len));
                    break;
                }
                case 11:
                {
                    // A byte (presence flag, type id byte, bool) set to a boundary value.
                    byte[] bytes = { 0, 1, 2, 0x7F, 0x80, 0xFF, 0xFE };
                    d[pos] = bytes[_rng.Next(bytes.Length)];
                    break;
                }
            }
        }
    }
}
