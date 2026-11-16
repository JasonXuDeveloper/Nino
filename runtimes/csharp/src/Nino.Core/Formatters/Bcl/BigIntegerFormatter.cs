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
            int count = value.GetByteCount();
            value.TryWriteBytes(MemoryMarshal.CreateSpan(ref writer.ReserveCounted(count, 1), count), out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref BigInteger value)
        {
            int count = reader.UnsafeRead<int>();
            if ((uint)count > (uint)reader.Remaining || NinoLimits.TooLong(count))
                NinoThrow.InvalidLength(count);
            // The byte count was checked against the remaining data above.
            value = new BigInteger(MemoryMarshal.CreateReadOnlySpan(ref reader.UnsafeAdvance(count), count));
        }
    }
}
