using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary><c>BigInteger</c>: byte count, then its two's complement bytes, least significant first.</summary>
    public struct BigIntegerFormatter : INinoFormatter<BigInteger>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(BigInteger value, ref Writer writer)
        {
#if NETSTANDARD2_0
            var bytes = value.ToByteArray();
            writer.WriteLength(bytes.Length);
            writer.WriteRaw(bytes);
#else
            int count = value.GetByteCount();
            value.TryWriteBytes(MemoryMarshal.CreateSpan(ref writer.ReserveCounted(count, 1), count), out _);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref BigInteger value)
        {
            int count = reader.UnsafeRead<int>();
            if ((uint)count > (uint)reader.Remaining || NinoLimits.TooLong(count))
                NinoThrow.InvalidLength(count);
#if NETSTANDARD2_0
            if (count <= sizeof(int))
            {
                ref byte bytes = ref reader.UnsafeAdvance(count);
                int number = count switch
                {
                    1 => unchecked((sbyte)bytes),
                    2 => NinoUnsafe.Read<short>(ref bytes),
                    3 => NinoUnsafe.Read<ushort>(ref bytes) | (unchecked((sbyte)Unsafe.Add(ref bytes, 2)) << 16),
                    4 => NinoUnsafe.Read<int>(ref bytes),
                    _ => 0
                };
                value = new BigInteger(number);
                return;
            }

            value = new BigInteger(reader.ReadRaw(count).ToArray());
#else
            // The byte count was checked against the remaining data above.
            value = new BigInteger(MemoryMarshal.CreateReadOnlySpan(ref reader.UnsafeAdvance(count), count));
#endif
        }
    }
}
