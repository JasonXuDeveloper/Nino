using System.Runtime.CompilerServices;
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
        /// <paramref name="count"/> of them: a platform that prefetches, a generated class formatter, and at least its
        /// threshold. The first two are JIT-time constants: where they are false, nothing of this is compiled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Takes<TFormatter>(int count) where TFormatter : struct =>
#if NET8_0_OR_GREATER
            Supported && default(TFormatter) is CompilerServices.INinoPrefetchFormatter &&
            count >= ((CompilerServices.INinoPrefetchFormatter)default(TFormatter)).PrefetchThreshold;
#else
            // Kernels that prefetch (NinoKernels.Prefetches; Unity's Burst ones), tested first: one load and a
            // compare without them. The formatter's threshold from a cache (int.MaxValue for other formatters): a
            // cast of default(TFormatter) to the interface would box it, where IL2CPP does not fold the test.
            NinoKernelHook.Prefetches && count >= Of<TFormatter>.Threshold;
#endif

        /// <summary>The formatter <typeparamref name="TFormatter"/> as <see cref="CompilerServices.INinoPrefetchFormatter"/>, for a collection <see cref="Takes"/> is true for.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompilerServices.INinoPrefetchFormatter Formatter<TFormatter>() where TFormatter : struct =>
#if NET8_0_OR_GREATER
            (CompilerServices.INinoPrefetchFormatter)default(TFormatter);
#else
            Of<TFormatter>.Formatter;
#endif

#if !NET8_0_OR_GREATER
        /// <summary><typeparamref name="TFormatter"/> boxed once as the interface, and its threshold.</summary>
        private static class Of<TFormatter> where TFormatter : struct
        {
            public static readonly CompilerServices.INinoPrefetchFormatter Formatter =
                default(TFormatter) as CompilerServices.INinoPrefetchFormatter;

            public static readonly int Threshold = Formatter?.PrefetchThreshold ?? int.MaxValue;
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
