using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// For Nino's own packages (Nino.Immutable): one element of a collection written as Nino.Core's collections write
    /// their elements, for a collection that can only be walked (an enumerator, linked nodes).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoElement
    {
        /// <summary>
        /// Writes <paramref name="value"/> as an element of a collection of them, with the count written before by the
        /// caller: the bytes every collection of <typeparamref name="T"/> writes for it (a raw <c>T?</c> as a slot of
        /// a fixed size, which its formatter's <c>Serialize</c> does not write, so a caller never decides which
        /// elements are slots), else <typeparamref name="TFormatter"/>'s <c>Serialize</c>. The element kind is a
        /// JIT-time constant: a constrained call, inlined.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(in T value, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoElements.WriteOne<T, TFormatter>(in value, ref writer);
    }
}
