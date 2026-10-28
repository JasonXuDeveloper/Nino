using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// Software prefetch of the objects a loop over references reaches next. The elements of an array or a list of
    /// classes are separate heap objects; once their order in the collection is not their order in memory (objects
    /// replaced, shuffled or sorted after they were allocated), each one is a cache miss the hardware prefetcher cannot
    /// predict, and the loop waits on each in turn. Prefetching the object <see cref="Far"/> elements ahead, and the
    /// objects its members point to once it is in the cache (<see cref="Near"/> ahead), overlaps those misses.
    /// <para>
    /// Only for collections of at least a threshold of objects of a generated class formatter
    /// (<see cref="CompilerServices.INinoPrefetchFormatter"/>), in a loop of their own out of line
    /// (<see cref="CompilerServices.NinoPrefetched"/>): where the objects are in the cache a prefetch per element costs
    /// about a third of a nanosecond (1,000 shapes serialized 14 to 15% slower). x64 on .NET 8+ (<c>prefetcht0</c>);
    /// on Arm64, which .NET gives no scalar prefetch (only SVE's, experimental in .NET 10), a load of the object's first
    /// byte instead. The netstandard builds (Unity, IL2CPP, .NET Framework) have no intrinsics: they prefetch through
    /// registered kernels that do (<see cref="NinoKernels.Prefetch"/>, Unity's Burst ones), the objects only, and
    /// otherwise keep the one loop.
    /// </para>
    /// </summary>
    internal static class NinoPrefetch
    {
        /// <summary>How many elements ahead the element's object is prefetched.</summary>
        public const int Far = 16;

        /// <summary>How many elements ahead the objects of the element's members are (its own object then in the cache).</summary>
        public const int Near = 8;

        /// <summary>
        /// The bytes the loads of <see cref="At"/> on Arm64 add up to, stored once per collection so the JIT keeps
        /// the loads. Never read.
        /// </summary>
        public static int Sink;

        /// <summary>Whether this process prefetches (a JIT-time constant).</summary>
        public static bool Supported
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => Sse.IsSupported || AdvSimd.Arm64.IsSupported;
#else
            get => false;
#endif
        }

        /// <summary>Whether <see cref="At"/> is a prefetch instruction (x64), not a load whose result is kept.</summary>
        public static bool Hints
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => Sse.IsSupported;
#else
            get => false;
#endif
        }

        /// <summary>
        /// Whether the collection loops of <typeparamref name="TFormatter"/>'s elements take the prefetching loop for
        /// <paramref name="count"/> of them: a platform that prefetches, a generated class formatter, and more of its
        /// elements than the last-level cache holds (<see cref="Of{TFormatter}.Threshold"/>). On .NET 8+ all of it
        /// but the length is a JIT-time constant once the threshold is set (one compare); where the first two are
        /// false nothing of this is compiled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Takes<TFormatter>(int count) where TFormatter : struct =>
#if NET8_0_OR_GREATER
            Supported && default(TFormatter) is CompilerServices.INinoPrefetchFormatter &&
            count > Of<TFormatter>.Threshold;
#else
            // Kernels that prefetch (NinoKernels.Prefetches; Unity's Burst ones), tested first: one load and a
            // compare without them. No `default(TFormatter) is ...`: IL2CPP does not fold it, and the cast would box.
            NinoKernelHook.Prefetches && count > Of<TFormatter>.Threshold;
#endif

        /// <summary>The formatter <typeparamref name="TFormatter"/> as <see cref="CompilerServices.INinoPrefetchFormatter"/>, for a collection <see cref="Takes"/> is true for.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompilerServices.INinoPrefetchFormatter Formatter<TFormatter>() where TFormatter : struct =>
#if NET8_0_OR_GREATER
            (CompilerServices.INinoPrefetchFormatter)default(TFormatter);
#else
            Of<TFormatter>.Formatter;
#endif

        /// <summary>
        /// The fewest elements a collection prefetches: those that fit in a quarter of the smallest L2, which a
        /// collection likely has in the cache whatever the machine (4,096 shapes of about 100 bytes were on par).
        /// </summary>
        private const int MinElements = 1024;

        /// <summary>
        /// <typeparamref name="TFormatter"/> boxed once as the interface (null for other formatters), and the most
        /// elements its collections hold without prefetching: the last-level cache over the element's bytes, at
        /// least <see cref="MinElements"/>; int.MaxValue for other formatters. Static readonly: the JIT takes both as
        /// constants once they are set.
        /// </summary>
        public static class Of<TFormatter> where TFormatter : struct
        {
            public static readonly CompilerServices.INinoPrefetchFormatter Formatter =
                default(TFormatter) as CompilerServices.INinoPrefetchFormatter;

            public static readonly int Threshold = Formatter == null
                ? int.MaxValue
                : (int)System.Math.Min(int.MaxValue - 1,
                    System.Math.Max(MinElements, CacheBytes / System.Math.Max(1, Formatter.PrefetchBytes)));
        }

        /// <summary>
        /// The bytes of this machine's last-level cache, read once: the largest cache level of the first CPU in
        /// /sys on Linux and Android, GetLogicalProcessorInformationEx on Windows (.NET 8+);
        /// <see cref="DefaultCacheBytes"/> where neither answers. On a CPU of several cache groups (AMD's CCXs) it is one group's.
        /// </summary>
        public static readonly long CacheBytes = LastLevelCache();

        /// <summary>
        /// The cache assumed where none is read: 32 MiB, as large as most desktop and server L3s (an AMD EPYC 7763
        /// CCX, a Ryzen), so that a machine whose cache is unknown prefetches only collections that likely miss it.
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

        /// <summary>
        /// Prefetches <paramref name="value"/>'s object unless it is null (a prefetch of address 0 walks the page
        /// tables for nothing; the slots of a new array are all null): prefetcht0 on x64, which never faults (the GC
        /// moving the object only makes it useless), or on Arm64 a load of its first byte, returned for
        /// <see cref="Sink"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int At(object value)
        {
#if NET8_0_OR_GREATER
            if (value == null)
                return 0;
            if (Sse.IsSupported)
            {
                Sse.Prefetch0((void*)Unsafe.As<object, nint>(ref value));
                return 0;
            }

            if (AdvSimd.Arm64.IsSupported)
                return ObjectFields.At<byte>(value, 0);
#endif
            return 0;
        }
    }
}
