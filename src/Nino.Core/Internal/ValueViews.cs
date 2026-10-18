using System;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>Where the fields of value types are on this runtime (constants once the class is initialized).</summary>
    internal static class ValueViews
    {
        /// <summary>
        /// Byte position of the UTC ticks inside a <see cref="DateTimeOffset"/> (its date, stored with no kind bits),
        /// 0 or 8; its offset in minutes (an int, or a short followed by padding) is at the other one. -1 when the
        /// layout is neither: .NET Framework, Mono and .NET 8 have the date first and a short, .NET 10 an int first.
        /// </summary>
        public static readonly int DateTimeOffsetTicks = FindDateTimeOffsetTicks();

        private static int FindDateTimeOffsetTicks()
        {
            try
            {
                if (DateTimeOffsetMatches(0))
                    return 0;
                if (DateTimeOffsetMatches(8))
                    return 8;
            }
            catch
            {
                // Not this layout.
            }

            return -1;
        }

        private static bool DateTimeOffsetMatches(int ticksAt)
        {
            int minutesAt = 8 - ticksAt;
            if (Unsafe.SizeOf<DateTimeOffset>() < Math.Max(ticksAt + 8, minutesAt + 4))
                return false;
            var value = new DateTimeOffset(637_000_000_000_000_000, new TimeSpan(5, 30, 0));
            ref byte bytes = ref Unsafe.As<DateTimeOffset, byte>(ref value);
            if (Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref bytes, ticksAt)) != value.UtcTicks ||
                Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref bytes, minutesAt)) != 330)
                return false;
            // Written as the formatter writes them, read back through the API.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, ticksAt), 600_000_000_000_000_000);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, minutesAt), -90);
            return value.UtcTicks == 600_000_000_000_000_000 && value.Offset == new TimeSpan(-1, -30, 0) &&
                   value.Ticks == 600_000_000_000_000_000 - 90 * TimeSpan.TicksPerMinute;
        }
    }
}
