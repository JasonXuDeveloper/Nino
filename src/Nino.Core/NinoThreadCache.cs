using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Helpers for the objects cached per thread between calls (scratch buffers, reference tables). Each cache is
    /// a <c>[ThreadStatic]</c> field of a non-generic class, so access stays a plain thread-local load.
    /// </summary>
    internal static class NinoThreadCache
    {
        /// <summary>Reference tables that grew beyond this many slots are dropped instead of cached.</summary>
        public const int MaxRetainedTableCapacity = 1 << 12;

        /// <summary>
        /// Takes the instance cached in <paramref name="slot"/>, or null. The slot is left empty, so a nested call
        /// on the same thread (e.g. from a custom formatter) creates its own instance instead of sharing it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Take<T>(ref T slot) where T : class
        {
            var value = slot;
            if (value != null)
                slot = null;
            return value;
        }
    }
}
