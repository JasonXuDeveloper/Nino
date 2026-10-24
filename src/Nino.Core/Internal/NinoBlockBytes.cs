#if NET8_0_OR_GREATER
using System;

namespace Nino.Core
{
    /// <summary>
    /// The bytes of a value and of its block matched one to one, from a block map
    /// (<see cref="INinoBlockFormatter{T}.BlockMap"/>: memory offset, block offset, size triples). Built once per type,
    /// off the hot paths: every shuffle mask, permute index and lane plan of <see cref="NinoBlock{T, TFormatter}"/>
    /// and <see cref="NinoLanes"/> is read from it.
    /// </summary>
    internal sealed class NinoBlockBytes
    {
        /// <summary>The size of a block and of a value in memory.</summary>
        public readonly int Size, MemorySize;

        /// <summary>For each byte of a block, the byte of the value's memory it holds (-1: none).</summary>
        private readonly int[] _memoryOf;

        /// <summary>For each byte of a value's memory, the byte of its block that holds it (-1: padding).</summary>
        private readonly int[] _wireOf;

        public NinoBlockBytes(int[] map, int size, int memorySize)
        {
            Size = size;
            MemorySize = memorySize;
            _memoryOf = new int[size];
            _wireOf = new int[memorySize];
            _memoryOf.AsSpan().Fill(-1);
            _wireOf.AsSpan().Fill(-1);
            for (int i = 0; i < map.Length; i += 3)
            for (int b = 0; b < map[i + 2]; b++)
            {
                _memoryOf[map[i + 1] + b] = map[i] + b;
                _wireOf[map[i] + b] = map[i + 1] + b;
            }
        }

        /// <summary>
        /// The byte of consecutive values' memory that byte <paramref name="wire"/> of their consecutive blocks holds
        /// (-1: none).
        /// </summary>
        public int MemoryAt(int wire)
        {
            int memory = _memoryOf[wire % Size];
            return memory < 0 ? -1 : wire / Size * MemorySize + memory;
        }

        /// <summary>The byte of consecutive blocks that holds byte <paramref name="memory"/> of their values (-1: padding).</summary>
        public int WireAt(int memory)
        {
            int wire = _wireOf[memory % MemorySize];
            return wire < 0 ? -1 : memory / MemorySize * Size + wire;
        }

        /// <summary>
        /// For each byte <paramref name="toWire"/> writes (blocks: <see cref="MemoryAt"/>; memory: <see cref="WireAt"/>)
        /// of the first <paramref name="values"/> values, the byte it comes from, as an index of a byte shuffle or
        /// permute (0x80: none, a zero), in <paramref name="length"/> bytes.
        /// </summary>
        public byte[] Indices(bool toWire, int values, int length)
        {
            var indices = new byte[length];
            indices.AsSpan().Fill(0x80);
            int bytes = values * (toWire ? Size : MemorySize);
            for (int at = 0; at < bytes; at++)
            {
                int from = toWire ? MemoryAt(at) : WireAt(at);
                if (from >= 0)
                    indices[at] = (byte)from;
            }

            return indices;
        }
    }
}
#endif
