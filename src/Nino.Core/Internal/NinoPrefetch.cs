using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
    /// Only top-level calls prefetch: generated interceptors call <see cref="NinoDirect"/>'s <c>...Prefetched</c>
    /// methods for an array or a list of a class whose formatter implements <see cref="INinoPrefetchFormatter"/>, and
    /// the collection's formatter (<see cref="INinoPrefetchTopFormatter{T}"/>) writes it, or reads into its existing
    /// objects, here when it is longer than <see cref="Of{TFormatter}.Threshold"/> and its first objects are not in
    /// allocation order (<see cref="Takes{T,TFormatter}"/>): the ordinary element loop with its own writer or reader,
    /// in runs of <see cref="Run"/>, before each of which <see cref="Ahead"/> (out of line, keyed by the formatter)
    /// prefetches the objects two runs ahead and the objects the members of the next run point to. Collections nested
    /// in a value never do: a test in the shared element loops cost every short collection of such a class (CommonTypes
    /// Orders and Tree, one short list per object, 2-7 % slower), and a collection that outgrows the cache is
    /// almost always the value written or read.
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
        /// Whether a collection of <paramref name="length"/> elements of <typeparamref name="TFormatter"/> may prefetch:
        /// more than <see cref="Of{TFormatter}.Threshold"/>, a static readonly field (int.MaxValue where nothing
        /// prefetches). The test the top-level calls inline: one compare against a constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Long<TFormatter>(int length) where TFormatter : struct => length > Of<TFormatter>.Threshold;

        /// <summary>
        /// Whether the collection <paramref name="values"/> prefetches: <see cref="Long{TFormatter}"/>, and objects
        /// not in allocation order (<see cref="Scattered"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Takes<T, TFormatter>(ReadOnlySpan<T> values) where TFormatter : struct, INinoFormatter<T> =>
            Long<TFormatter>(values.Length) &&
            Scattered(ref Unsafe.As<T, object>(ref MemoryMarshal.GetReference(values)));

        /// <summary>
        /// Writes the array <paramref name="values"/> (<see cref="Long{TFormatter}"/>) as a whole payload at the end of
        /// <paramref name="buffer"/> in runs and returns true, or returns false (nothing written) when its objects are
        /// in allocation order: the bytes of <see cref="ArrayFormatter{T,TFormatter}"/> with a new writer. Out of line,
        /// as everything past the inlined length test, so the top-level call around it compiles as before.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Write<T, TFormatter>(T[] values, NinoArrayBufferWriter buffer)
            where TFormatter : struct, INinoFormatter<T> =>
            Write<T, TFormatter>(new ReadOnlySpan<T>(values), buffer);

        /// <summary>The list <paramref name="values"/> as <see cref="Write{T,TFormatter}(T[], NinoArrayBufferWriter)"/> writes an array.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Write<T, TFormatter>(List<T> values, NinoArrayBufferWriter buffer)
            where TFormatter : struct, INinoFormatter<T> =>
            Write<T, TFormatter>(ListAccess.AsSpan<T>(values), buffer);

        /// <summary>
        /// Writes <paramref name="values"/> at the end of <paramref name="buffer"/> as a whole payload, with a writer of
        /// its own: its length, then its elements in runs. The bytes of
        /// <see cref="NinoCollections.WriteSpan{T,TFormatter}"/> with a new writer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Write<T, TFormatter>(ReadOnlySpan<T> values, NinoArrayBufferWriter buffer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (!Takes<T, TFormatter>(values))
                return false;
            var writer = new Writer(buffer);
            writer.WriteLength(values.Length);
            ref T item = ref MemoryMarshal.GetReference(values);
            int ahead = values.Length;
            for (int left = values.Length; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
            {
                if (left == ahead)
                {
                    Ahead<TFormatter>(ref Unsafe.As<T, object>(ref item), left);
                    ahead = left - Run;
                }

                default(TFormatter).Serialize(item, ref writer);
            }

            writer.Flush();
            writer.ReleaseReferences();
            return true;
        }

        /// <summary>
        /// Reads the payload <paramref name="data"/> into the existing objects of <paramref name="values"/>
        /// (<see cref="Long{TFormatter}"/>) in runs and returns true, or returns false (nothing read) when its
        /// objects are in allocation order or <paramref name="data"/> holds another number of elements: as
        /// <see cref="ArrayFormatter{T,TFormatter}"/> reads into an array of that length. Out of line.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Read<T, TFormatter>(ReadOnlySpan<byte> data, T[] values)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (!Takes<T, TFormatter>(values) || !SameLength(data, values.Length))
                return false;
            var reader = new Reader(data);
            // The length (checked against the bytes left, as the formatter does): the array is kept.
            reader.TryReadLength(NinoElements.MinWireSize<T, TFormatter>(), out _);
            reader = Read<T, TFormatter>(values, reader);
            reader.ReleaseReferences();
            return true;
        }

        /// <summary>
        /// The list <paramref name="values"/> as <see cref="Read{T,TFormatter}(ReadOnlySpan{byte}, T[])"/> reads into
        /// an array: as <see cref="ListFormatter{TList,T,TFormatter}"/> reads into a list of that count.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Read<T, TFormatter>(ReadOnlySpan<byte> data, List<T> values)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (!Takes<T, TFormatter>(ListAccess.AsSpan<T>(values)) || !SameLength(data, values.Count))
                return false;
            var reader = new Reader(data);
            reader.TryReadLength(NinoElements.MinWireSize<T, TFormatter>(), out int length);
            var span = ListAccess.Open(values, length, NinoElements.ReadOverwrites<T, TFormatter>(), out var buffer);
            reader = Read<T, TFormatter>(span, reader);
            ListAccess.Close(values, length, buffer);
            reader.ReleaseReferences();
            return true;
        }

        /// <summary>Whether <paramref name="data"/> starts with the length <paramref name="length"/> of a collection.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SameLength(ReadOnlySpan<byte> data, int length) =>
            data.Length >= sizeof(int) && BinaryPrimitives.ReadInt32LittleEndian(data) == length;

        /// <summary>
        /// Reads into the existing objects <paramref name="values"/> in runs, as
        /// <see cref="NinoElements.Read{T,TFormatter}(Span{T}, ref Reader)"/> reads them one by one. The reader by value
        /// and returned, as there.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Reader Read<T, TFormatter>(Span<T> values, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            ref T item = ref MemoryMarshal.GetReference(values);
            int ahead = values.Length;
            for (int left = values.Length; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
            {
                if (left == ahead)
                {
                    Ahead<TFormatter>(ref Unsafe.As<T, object>(ref item), left);
                    ahead = left - Run;
                }

                default(TFormatter).Deserialize(ref item, ref reader);
            }

            return reader;
        }

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
        /// (<see cref="INinoPrefetchFormatter.PrefetchBytes"/>), at least <see cref="MinElements"/>; int.MaxValue for a
        /// formatter that is not an <see cref="INinoPrefetchFormatter"/> (a generated class formatter: its collections are
        /// written element by element, never as blocks) or where nothing prefetches (<see cref="Supported"/>). Static
        /// readonly: the JIT takes it as a constant once it is set.
        /// </summary>
        public static class Of<TFormatter> where TFormatter : struct
        {
            public static readonly int Threshold = Supported && default(TFormatter) is INinoPrefetchFormatter formatter
                ? (int)Math.Min(int.MaxValue - 1,
                    Math.Max(MinElements, CacheBytes / CacheShare / Math.Max(1, formatter.PrefetchBytes)))
                : int.MaxValue;
        }

        /// <summary>Whether this runtime prefetches: x64 or Arm64 on .NET 8+ (see <see cref="At"/>).</summary>
        public static bool Supported =>
#if NET8_0_OR_GREATER
            Sse.IsSupported || AdvSimd.Arm64.IsSupported;
#else
            false;
#endif

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
