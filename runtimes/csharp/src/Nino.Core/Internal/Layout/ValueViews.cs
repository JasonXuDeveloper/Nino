using System;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>Where the fields of value types are on this runtime (constants once the class is initialized).</summary>
    internal static class ValueViews
    {
        // Found through the public API alone (no reflection, so trimming and NativeAOT keep it): UTC ticks at, minutes
        // at, minutes size.
        private static readonly int[] DateTimeOffsetLayout = FindDateTimeOffset();

        /// <summary>
        /// Byte position of the UTC ticks inside a <see cref="DateTimeOffset"/> (its date, stored with no kind bits);
        /// -1 when not found. 64-bit .NET has its offset in minutes first (a <c>short</c> on .NET 8, an <c>int</c> on
        /// .NET 10) and the date at 8, .NET Framework and Mono the date first and a <c>short</c> at 8, 32-bit runtimes
        /// pack them into 12 bytes.
        /// </summary>
        public static readonly int DateTimeOffsetUtc = DateTimeOffsetLayout[0];

        /// <summary>Byte position of the offset in minutes inside a <see cref="DateTimeOffset"/>; -1 when not found.</summary>
        public static readonly int DateTimeOffsetMinutes = DateTimeOffsetLayout[1];

        /// <summary>Size of the offset in minutes: 2 (a <c>short</c>) or 4 (an <c>int</c>, .NET 9 and later).</summary>
        public static readonly int DateTimeOffsetMinutesSize = DateTimeOffsetLayout[2];

        /// <summary>
        /// <see cref="DateTimeOffsetUtc"/> where the value is 16 bytes with the date and the minutes at 0 and 8 (every
        /// 64-bit runtime), which <c>DateTimeOffsetFormatter</c> reads and writes directly; -1 elsewhere.
        /// </summary>
        public static readonly int DateTimeOffsetTicks =
            Unsafe.SizeOf<DateTimeOffset>() == 16 && (DateTimeOffsetUtc == 0 || DateTimeOffsetUtc == 8) &&
            DateTimeOffsetMinutes == 8 - DateTimeOffsetUtc
                ? DateTimeOffsetUtc
                : -1;

        private static int[] FindDateTimeOffset()
        {
            int size = Unsafe.SizeOf<DateTimeOffset>();
            for (int utcAt = 0; utcAt + 8 <= size; utcAt += 4)
                for (int minutesAt = 0; minutesAt + 2 <= size; minutesAt += 2)
                {
                    if (minutesAt + 2 > utcAt && minutesAt < utcAt + 8)
                        continue;
                    int minutesSize = MinutesSize(minutesAt, utcAt, size);
                    if (ObjectTypes.Passes(() => DateTimeOffsetMatches(utcAt, minutesAt, minutesSize)))
                        return new[] { utcAt, minutesAt, minutesSize };
                }

            return new[] { -1, -1, 0 };
        }

        private static bool DateTimeOffsetMatches(int utcAt, int minutesAt, int minutesSize)
        {
            var value = new DateTimeOffset(637_000_000_000_000_000, new TimeSpan(5, 30, 0));
            ref byte bytes = ref Unsafe.As<DateTimeOffset, byte>(ref value);
            if (Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref bytes, utcAt)) != value.UtcTicks ||
                Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref bytes, minutesAt)) != 330)
                return false;
            // Written into the fields, read back through the API.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, utcAt), 600_000_000_000_000_000);
            if (minutesSize == 4)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, minutesAt), -90);
            else
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, minutesAt), (short)-90);
            return value.UtcTicks == 600_000_000_000_000_000 && value.Offset == new TimeSpan(-1, -30, 0) &&
                   value.Ticks == 600_000_000_000_000_000 - 90 * TimeSpan.TicksPerMinute;
        }

        /// <summary>4 where the two bytes after the minutes belong to them (an int), else 2.</summary>
        private static int MinutesSize(int minutesAt, int utcAt, int size)
        {
            if (minutesAt + 4 > size || (minutesAt < utcAt && minutesAt + 4 > utcAt))
                return 2;
            var value = new DateTimeOffset(637_000_000_000_000_000, TimeSpan.Zero);
            ref byte bytes = ref Unsafe.As<DateTimeOffset, byte>(ref value);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, minutesAt), 0x10005);
            return value.Offset.Ticks == 0x10005 * TimeSpan.TicksPerMinute ? 4 : 2;
        }
    }
}
