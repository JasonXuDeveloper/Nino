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
    /// <summary>
    /// <c>BitArray</c>: bit count (-1 = null), then the bits, eight to a byte, the first in the lowest bit.
    /// On .NET 10 and later a read reuses an existing array of the same length.
    /// </summary>
    public struct BitArrayFormatter : INinoFormatter<BitArray>
    {
        public void Serialize(BitArray value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            int count = value.Length;
#if NET10_0_OR_GREATER
            // The public view is already in wire order, but can expose dirty padding in the final byte.
            var source = CollectionsMarshal.AsBytes(value);
            int size = source.Length;
            ref byte block = ref writer.Reserve(NinoWire.LengthSize + size);
            NinoUnsafe.Write(ref block, count);
            ref byte payload = ref Unsafe.Add(ref block, NinoWire.LengthSize);
            if (size <= 2)
            {
                // A complete last byte shifts by zero. A partial one keeps only its low bits.
                int shift = -count & 7;
                if (size == 2)
                {
                    ushort word = NinoUnsafe.Read<ushort>(ref MemoryMarshal.GetReference(source));
                    NinoUnsafe.Write(ref payload, (ushort)(word & (ushort.MaxValue >> shift)));
                }
                else if (size != 0)
                    payload = (byte)(MemoryMarshal.GetReference(source) & (byte.MaxValue >> shift));

                return;
            }

            var target = MemoryMarshal.CreateSpan(ref payload, size);
            ref byte first = ref MemoryMarshal.GetReference(source);
            if (NinoSimd.HasArm64 && size > 64)
            {
                if (size <= NinoUnsafe.MaxArm64Short)
                    NinoUnsafe.CopyArm64Short(ref payload, ref first, (uint)size);
                else
                    NinoUnsafe.CopyOver64(ref payload, ref first, (uint)size);
            }
            else
                NinoUnsafe.Copy(ref payload, ref first, (uint)size);
            MaskTail(target, count);
#else
            int size = (int)(((uint)count + 7) / 8);
            var buffer = writer.ReserveArray(NinoWire.LengthSize + size, out int offset);
            NinoUnsafe.Write(ref buffer[offset], count);
            value.CopyTo(buffer, offset + NinoWire.LengthSize);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref BitArray value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, 0, out int count))
                return;

            var bytes = reader.ReadRaw((int)(((uint)count + 7) / 8));
#if NET10_0_OR_GREATER
            value = ReadBits(bytes, count, value);
#else
            // The last byte may hold bits past the count: Length cuts them off.
            value = new BitArray(bytes.ToArray()) { Length = count };
#endif
        }

#if NET10_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static BitArray ReadBits(ReadOnlySpan<byte> bytes, int count, BitArray current)
        {
            bool reuse = current != null && current.Length == count;
            var bits = reuse ? current : new BitArray(count);
            var target = CollectionsMarshal.AsBytes(bits);
            if (bytes.Length > 2)
            {
                if (bytes.Length > 64 && !(reuse && bytes.Overlaps(target)))
                {
                    ref byte destination = ref MemoryMarshal.GetReference(target);
                    ref byte source = ref MemoryMarshal.GetReference(bytes);
                    uint size = (uint)bytes.Length;
                    if (NinoUnsafe.IsArm64Short(size))
                        NinoUnsafe.CopyArm64Short(ref destination, ref source, size);
                    else
                        NinoUnsafe.CopyOver64(ref destination, ref source, size);
                }
                else
                    bytes.CopyTo(target);
            }
            else if (bytes.Length == 2)
                NinoUnsafe.Write(ref MemoryMarshal.GetReference(target),
                    NinoUnsafe.Read<ushort>(ref MemoryMarshal.GetReference(bytes)));
            else if (!bytes.IsEmpty)
                MemoryMarshal.GetReference(target) = MemoryMarshal.GetReference(bytes);

            if (reuse)
                bits.Length = count; // Also clears unused bits and invalidates existing enumerators.
            else
                MaskTail(target, count);
            return bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MaskTail(Span<byte> bytes, int count)
        {
            int tail = count & 7;
            if (tail != 0)
                bytes[bytes.Length - 1] &= (byte)((1 << tail) - 1);
        }
#endif
    }
}
