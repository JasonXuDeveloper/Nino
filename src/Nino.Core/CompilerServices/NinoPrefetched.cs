using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>The prefetch the generated <see cref="INinoPrefetchFormatter.PrefetchMembers"/> calls per member.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoPrefetched
    {
        /// <summary>
        /// Prefetches the object <paramref name="member"/> refers to (null: nothing) and returns what
        /// <see cref="INinoPrefetchFormatter.PrefetchMembers"/> sums: 0 on x64, the byte loaded on Arm64 (see
        /// <see cref="NinoPrefetch"/>), nothing in the netstandard builds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int At(object member) => NinoPrefetch.At(member);
    }
}
