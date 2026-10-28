using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// The element loops of long collections of objects (<see cref="INinoPrefetchFormatter"/>): each object is
    /// prefetched <see cref="NinoPrefetch.Far"/> elements ahead of the one written or read, and the objects its members
    /// point to <see cref="NinoPrefetch.Near"/> ahead (see <see cref="NinoPrefetch"/>), the last ones as in the loop of
    /// shorter collections. Out of line, so that the callers' loops are what they are without it.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoPrefetched
    {
        /// <summary>
        /// Writes the <paramref name="count"/> elements whose references start at <paramref name="first"/>. The writer
        /// by reference, the caller's copy (see <see cref="INinoPrefetchFormatter.WritePrefetched"/>), and a local
        /// here, its cursor in registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static unsafe void Write<T, TFormatter>(ref nint first, int count, ref Writer target)
            where T : class where TFormatter : struct, INinoFormatter<T>, INinoPrefetchFormatter
        {
            Writer writer = target;
            ref T item = ref Unsafe.As<nint, T>(ref first);
#if NET8_0_OR_GREATER
            int left = count, sink = 0;
            for (; left > NinoPrefetch.Far; left--, item = ref Unsafe.Add(ref item, 1))
            {
                sink += NinoPrefetch.At(Unsafe.Add(ref item, NinoPrefetch.Far));
                sink += default(TFormatter).PrefetchMembers(Unsafe.Add(ref item, NinoPrefetch.Near));
                default(TFormatter).Serialize(item, ref writer);
            }

            for (; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                default(TFormatter).Serialize(item, ref writer);
            if (!NinoPrefetch.Hints)
                NinoPrefetch.Sink = sink;
#else
            fixed (nint* references = &first)
            {
                for (int i = 0; i < count; i++, item = ref Unsafe.Add(ref item, 1))
                {
                    if ((i & (NinoPrefetch.Far - 1)) == 0)
                        Ahead(references, i, count);
                    default(TFormatter).Serialize(item, ref writer);
                }
            }
#endif
            target = writer;
        }

        /// <summary>
        /// Reads into the <paramref name="count"/> elements whose references start at <paramref name="first"/>, the
        /// reader taken as <see cref="Write{T, TFormatter}"/> takes the writer.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static unsafe void Read<T, TFormatter>(ref nint first, int count, ref Reader target)
            where T : class where TFormatter : struct, INinoFormatter<T>, INinoPrefetchFormatter
        {
            Reader reader = target;
            ref T item = ref Unsafe.As<nint, T>(ref first);
#if NET8_0_OR_GREATER
            int left = count, sink = 0;
            for (; left > NinoPrefetch.Far; left--, item = ref Unsafe.Add(ref item, 1))
            {
                sink += NinoPrefetch.At(Unsafe.Add(ref item, NinoPrefetch.Far));
                sink += default(TFormatter).PrefetchMembers(Unsafe.Add(ref item, NinoPrefetch.Near));
                default(TFormatter).Deserialize(ref item, ref reader);
            }

            for (; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                default(TFormatter).Deserialize(ref item, ref reader);
            if (!NinoPrefetch.Hints)
                NinoPrefetch.Sink = sink;
#else
            fixed (nint* references = &first)
            {
                for (int i = 0; i < count; i++, item = ref Unsafe.Add(ref item, 1))
                {
                    if ((i & (NinoPrefetch.Far - 1)) == 0)
                        Ahead(references, i, count);
                    default(TFormatter).Deserialize(ref item, ref reader);
                }
            }
#endif
            target = reader;
        }

#if !NET8_0_OR_GREATER
        /// <summary>
        /// The registered kernels' prefetch of the run of <see cref="NinoPrefetch.Far"/> elements from
        /// <paramref name="index"/> + <see cref="NinoPrefetch.Far"/> (the array pinned by the caller); nothing once
        /// the kernels were unregistered.
        /// </summary>
        private static unsafe void Ahead(nint* references, int index, int count)
        {
            int start = index + NinoPrefetch.Far;
            var kernels = NinoKernelHook.Kernels;
            if (start < count && kernels != null)
                kernels.Prefetch((System.IntPtr*)(references + start), System.Math.Min(NinoPrefetch.Far, count - start));
        }
#endif

        /// <summary>
        /// Prefetches the object <paramref name="member"/> refers to (null: nothing), for
        /// <see cref="INinoPrefetchFormatter.PrefetchMembers"/>: what it returns goes into the sum it returns.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int At(object member) => NinoPrefetch.At(member);
    }
}
