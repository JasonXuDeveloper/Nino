using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    // Values converted field by field where the runtime lays a type out differently.
    internal static partial class NinoLayout
    {
        /// <summary>
        /// Copies a value between its memory layout and its wire layout using <see cref="Info.Map"/> and
        /// <see cref="Info.Resized"/>. A wire integer that does not fit its narrower field in memory (an <c>nint</c>
        /// past 32 bits on a 32-bit runtime) is invalid data there.
        /// </summary>
        internal static void Convert(Info info, ref byte memory, ref byte wire, bool toWire)
        {
            var map = info.Map;
            if (toWire)
                Unsafe.InitBlockUnaligned(ref wire, 0, (uint)info.Size);
            for (int i = 0; i < map.Length; i += 3)
            {
                ref byte m = ref Unsafe.Add(ref memory, map[i]);
                ref byte w = ref Unsafe.Add(ref wire, map[i + 1]);
                if (toWire)
                    Unsafe.CopyBlockUnaligned(ref w, ref m, (uint)map[i + 2]);
                else
                    Unsafe.CopyBlockUnaligned(ref m, ref w, (uint)map[i + 2]);
            }

            if (info.Resized != null)
                ConvertResized(info.Resized, ref memory, ref wire, toWire);
            // After every field is in place (a DateTimeOffset's minutes are resized), as NinoMask clears them.
            if (toWire && info.Absent != null)
                ClearAbsent(info.Absent, ref wire);
        }

        private static void ConvertResized(int[] resized, ref byte memory, ref byte wire, bool toWire)
        {
            for (int i = 0; i < resized.Length; i += 5)
            {
                ref byte m = ref Unsafe.Add(ref memory, resized[i]);
                ref byte w = ref Unsafe.Add(ref wire, resized[i + 2]);
                int memorySize = resized[i + 1], wireSize = resized[i + 3];
                if (resized[i + 4] == 0)
                {
                    // Unsigned integers (nuint on 32-bit): their low bytes, zero-extended on the wire (cleared above);
                    // a wire value with bytes set past this runtime's field is invalid data.
                    if (toWire)
                    {
                        Unsafe.CopyBlockUnaligned(ref w, ref m, (uint)memorySize);
                        continue;
                    }

                    if (!Zero(ref Unsafe.Add(ref w, memorySize), wireSize - memorySize))
                        NinoThrow.InvalidData("an integer too large for this runtime's field.");
                    Unsafe.CopyBlockUnaligned(ref m, ref w, (uint)memorySize);
                    continue;
                }

                if (toWire)
                {
                    Store(ref w, wireSize, Load(ref m, memorySize));
                    continue;
                }

                long value = Load(ref w, wireSize);
                if (Load(ref Unsafe.As<long, byte>(ref value), memorySize) != value)
                    NinoThrow.InvalidData("an integer too large for this runtime's field.");
                Store(ref m, memorySize, value);
            }
        }

        /// <summary>Whether the <paramref name="length"/> bytes at <paramref name="at"/> are all zero.</summary>
        private static bool Zero(ref byte at, int length)
        {
            for (int i = 0; i < length; i++)
                if (Unsafe.Add(ref at, i) != 0)
                    return false;
            return true;
        }

        /// <summary>The little-endian signed integer of <paramref name="size"/> bytes (2, 4 or 8) at <paramref name="at"/>.</summary>
        private static long Load(ref byte at, int size) => size switch
        {
            2 => Unsafe.ReadUnaligned<short>(ref at),
            4 => Unsafe.ReadUnaligned<int>(ref at),
            _ => Unsafe.ReadUnaligned<long>(ref at),
        };

        private static void Store(ref byte at, int size, long value)
        {
            switch (size)
            {
                case 2:
                    Unsafe.WriteUnaligned(ref at, (short)value);
                    break;
                case 4:
                    Unsafe.WriteUnaligned(ref at, (int)value);
                    break;
                default:
                    Unsafe.WriteUnaligned(ref at, value);
                    break;
            }
        }
    }
}
