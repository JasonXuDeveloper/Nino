using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// Software prefetch of the objects a collection loop reaches next. The elements of an array or a list of classes
    /// are separate heap objects; once their order in the collection is not their order in memory (objects replaced,
    /// shuffled or sorted after they were allocated), each one is a cache miss the hardware prefetcher cannot predict,
    /// and the loop waits on each in turn.
    /// <para>
    /// A collection of a generated class formatter's objects (<see cref="INinoPrefetchFormatter"/>) longer than
    /// <see cref="Of{TFormatter}.Threshold"/> whose first objects are not in allocation order (<see cref="Scattered"/>)
    /// is walked by the ordinary element loop in runs of <see cref="Run"/>: before each run, <see cref="Ahead"/> (out of
    /// line, keyed by the formatter) prefetches the objects two runs ahead and the objects the members of the next run
    /// point to. The writer or reader is never passed to it: it stays in the loop's registers and no frame grows.
    /// For every other formatter the loop's test (the platform, then <c>default(TFormatter) is
    /// INinoPrefetchFormatter</c>) folds to false while the JIT imports it, and its code is the loop without runs.
    /// </para>
    /// <para>
    /// x64 on .NET 8+ prefetches with <c>prefetcht0</c>; Arm64, which .NET gives no scalar prefetch (only SVE's), loads
    /// the object's first bytes instead and sums them into <see cref="Sink"/>. The netstandard builds (Unity, IL2CPP,
    /// .NET Framework) do not prefetch.
    /// </para>
    /// </summary>
    internal static class NinoPrefetch
    {
        /// <summary>The elements of a run, one call of <see cref="Ahead"/> each.</summary>
        public const int Run = 16;

        /// <summary>
        /// The fewest elements a collection prefetches: those that fit in a quarter of the smallest L2, which a
        /// collection likely has in the cache whatever the machine.
        /// </summary>
        private const int MinElements = 1024;

        /// <summary>
        /// The share of the last-level cache a collection's elements may take without prefetching: a quarter. Objects
        /// scattered over a set that fits the cache still miss it (and the TLB), as other data competes for it.
        /// </summary>
        private const int CacheShare = 4;

        /// <summary>
        /// The largest distance between consecutive objects of the sample <see cref="Scattered"/> takes for objects in
        /// allocation order, which the hardware prefetcher follows.
        /// </summary>
        private const int OrderedStride = 1024;

        /// <summary>The elements <see cref="Scattered"/> samples.</summary>
        private const int Sample = 17;

        /// <summary>
        /// What the loads of <see cref="At"/> on Arm64 add up to, stored once per run so the JIT keeps them. Never read.
        /// </summary>
        public static int Sink;

        /// <summary>
        /// Whether the collection of <paramref name="count"/> elements at <paramref name="first"/> of a formatter that
        /// implements <see cref="INinoPrefetchFormatter"/> (tested before, with the platform, where the JIT folds both
        /// while it imports the loop) is walked in runs that prefetch: more elements than
        /// <see cref="Of{TFormatter}.Threshold"/> (a static readonly field), and objects not in allocation order
        /// (<see cref="Scattered"/>, out of line, once per collection).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Takes<TFormatter>(ref object first, int count) where TFormatter : struct =>
            count > Of<TFormatter>.Threshold && Scattered(ref first);

        /// <summary>
        /// Before the run of <see cref="Run"/> elements at <paramref name="at"/>, <paramref name="left"/> elements
        /// left: prefetches the objects of the run after the next one and, through the formatter, the objects the
        /// members of the next run point to (those objects were prefetched a run earlier). Out of line: the loop
        /// calls it once per run, with nothing of its own in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Ahead<TFormatter>(ref object at, int left) where TFormatter : struct
        {
            int sum = 0;
            int far = Math.Min(left - 2 * Run, Run);
            ref object item = ref Unsafe.Add(ref at, 2 * Run);
            for (int i = 0; i < far; i++)
                sum += At(Unsafe.Add(ref item, i));
            int near = Math.Min(left - Run, Run);
            if (near > 0)
                sum += ((INinoPrefetchFormatter)default(TFormatter)).PrefetchMembers(ref Unsafe.Add(ref at, Run),
                    near);
#if NET8_0_OR_GREATER
            if (!Sse.IsSupported)
                Sink = sum;
#endif
        }

        /// <summary>
        /// Whether the first <see cref="Sample"/> objects at <paramref name="first"/> are not in allocation order. In
        /// allocation order, most consecutive objects follow each other in memory less than
        /// <see cref="OrderedStride"/> bytes apart, which the hardware prefetcher already follows (a prefetch per
        /// element there only adds work: 1,000,000 shapes in allocation order were written 8 % slower on EPYC 9V74
        /// with one); most, not all, as a thread allocates in chunks (8 KB), and the next chunk may lie anywhere: the
        /// first shape of the benchmark's array lay 132 KB after the second. Shuffled, a step that short is
        /// improbable. A null among them (a new array being read) is not prefetched either.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Scattered(ref object first)
        {
            nint previous = Address(first);
            if (previous == 0)
                return false;
            int ordered = 0;
            for (int i = 1; i < Sample; i++)
            {
                nint next = Address(Unsafe.Add(ref first, i));
                if (next == 0)
                    return false;
                if (next - previous > 0 && next - previous <= OrderedStride)
                    ordered++;
                previous = next;
            }

            return ordered < (Sample - 1) / 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nint Address(object value) => Unsafe.As<object, nint>(ref value);

        /// <summary>
        /// Prefetches <paramref name="value"/>'s object unless it is null: prefetcht0 on x64, which never faults (the GC
        /// moving the object only makes it useless), or on Arm64 a load of its first byte (through the reference, which
        /// the GC updates), returned for <see cref="Sink"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int At(object value)
        {
#if NET8_0_OR_GREATER
            if (value == null)
                return 0;
            if (Sse.IsSupported)
            {
                Sse.Prefetch0((void*)Address(value));
                return 0;
            }

            if (AdvSimd.Arm64.IsSupported)
                return ObjectFields.At<byte>(value, 0);
#endif
            return 0;
        }

        /// <summary>
        /// The most elements a collection of <typeparamref name="TFormatter"/>'s objects holds without prefetching:
        /// a <see cref="CacheShare"/> of the last-level cache over the element's bytes
        /// (<see cref="INinoPrefetchFormatter.PrefetchBytes"/>), at least <see cref="MinElements"/>. Static readonly:
        /// the JIT takes it as a constant once it is set. Read only for formatters that implement the interface.
        /// </summary>
        public static class Of<TFormatter> where TFormatter : struct
        {
            public static readonly int Threshold = default(TFormatter) is INinoPrefetchFormatter formatter
                ? (int)Math.Min(int.MaxValue - 1,
                    Math.Max(MinElements, CacheBytes / CacheShare / Math.Max(1, formatter.PrefetchBytes)))
                : int.MaxValue;
        }

        /// <summary>
        /// The bytes of this machine's last-level cache, read once: the largest cache level of the first CPU in /sys
        /// on Linux and Android, GetLogicalProcessorInformationEx on Windows (.NET 8+); <see cref="DefaultCacheBytes"/>
        /// where neither answers. On a CPU of several cache groups (AMD's CCXs) it is one group's.
        /// </summary>
        public static readonly long CacheBytes = LastLevelCache();

        /// <summary>
        /// The cache assumed where none is read: 32 MiB, as large as most desktop and server L3s (an AMD EPYC 7763
        /// CCX), so that a machine whose cache is unknown prefetches only collections that likely miss it.
        /// </summary>
        private const long DefaultCacheBytes = 32L << 20;

        private static long LastLevelCache()
        {
            long bytes = 0;
            try
            {
#if NET8_0_OR_GREATER
                if (OperatingSystem.IsWindows())
                    bytes = WindowsCache();
                else
#endif
                if (Directory.Exists(SysCache))
                    bytes = LinuxCache();
            }
            catch (Exception)
            {
                // No access, an unexpected format: the default.
            }

            return bytes > 0 ? bytes : DefaultCacheBytes;
        }

        private const string SysCache = "/sys/devices/system/cpu/cpu0/cache";

        /// <summary>The size of the highest level among /sys/devices/system/cpu/cpu0/cache/index*/ ("32768K").</summary>
        private static long LinuxCache()
        {
            int top = 0;
            long bytes = 0;
            foreach (var index in Directory.GetDirectories(SysCache, "index*"))
            {
                string levelFile = Path.Combine(index, "level"), sizeFile = Path.Combine(index, "size");
                if (!File.Exists(levelFile) || !File.Exists(sizeFile) ||
                    !int.TryParse(File.ReadAllText(levelFile).Trim(), out int level) || level < top)
                    continue;
                string size = File.ReadAllText(sizeFile).Trim();
                long scale = 1;
                if (size.EndsWith("K", StringComparison.OrdinalIgnoreCase))
                    scale = 1L << 10;
                else if (size.EndsWith("M", StringComparison.OrdinalIgnoreCase))
                    scale = 1L << 20;
                if (scale != 1)
                    size = size.Substring(0, size.Length - 1);
                if (!long.TryParse(size, out long value) || value <= 0)
                    continue;
                if (level > top || value * scale > bytes)
                    bytes = value * scale;
                top = level;
            }

            return bytes;
        }

#if NET8_0_OR_GREATER
        // .NET 8+ only: the netstandard builds run under IL2CPP too, where a P/Invoke of a library the player does not
        // have can fail its build (WebGL).
        [DllImport("kernel32", SetLastError = true)]
        private static extern unsafe bool GetLogicalProcessorInformationEx(int relationship, byte* buffer,
            ref uint length);

        /// <summary>
        /// The largest cache among the SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records of RelationCache (2): each
        /// record its relationship and size, then CACHE_RELATIONSHIP (level at 8, size at 12).
        /// </summary>
        private static unsafe long WindowsCache()
        {
            const int relationCache = 2;
            uint length = 0;
            GetLogicalProcessorInformationEx(relationCache, null, ref length);
            if (length == 0 || length > 1 << 20)
                return 0;
            byte[] buffer = new byte[length];
            long bytes = 0;
            int top = 0;
            fixed (byte* start = buffer)
            {
                if (!GetLogicalProcessorInformationEx(relationCache, start, ref length))
                    return 0;
                for (uint at = 0; at + 16 <= length;)
                {
                    uint size = *(uint*)(start + at + 4);
                    if (size < 16 || at + size > length)
                        break;
                    if (*(int*)(start + at) == relationCache)
                    {
                        int level = start[at + 8];
                        long cache = *(uint*)(start + at + 12);
                        if (level > top || level == top && cache > bytes)
                            (top, bytes) = (level, cache);
                    }

                    at += size;
                }
            }

            return bytes;
        }
#endif
    }
}
