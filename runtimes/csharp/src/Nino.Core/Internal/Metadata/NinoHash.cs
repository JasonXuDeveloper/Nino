// Linked into Nino.Generator, where a Nino.Core namespace would shadow its TypeNames.Core constant.
#if NINO_GENERATOR
namespace Nino.Generator.Generation
#else
namespace Nino.Core.Internal
#endif
{
    /// <summary>
    /// The FNV-1a hash behind type ids and keyed member ids. This file is compiled into Nino.Core and linked into
    /// Nino.Generator, so ids computed at compile time and at run time cannot drift apart.
    /// </summary>
    internal static class NinoHash
    {
        /// <summary>32-bit FNV offset basis.</summary>
        public const uint FnvOffset = 2166136261;

        /// <summary>32-bit FNV prime.</summary>
        public const uint FnvPrime = 16777619;

        /// <summary>
        /// FNV-1a over the UTF-16 code units of <paramref name="name"/> (low byte, then high byte). Never returns 0,
        /// which encodes <c>null</c> in polymorphic headers.
        /// </summary>
        public static int Compute(string name)
        {
            unchecked
            {
                uint hash = FnvOffset;
                foreach (char c in name)
                {
                    hash ^= (byte)c;
                    hash *= FnvPrime;
                    hash ^= (byte)(c >> 8);
                    hash *= FnvPrime;
                }

                return hash == 0 ? 1 : (int)hash;
            }
        }
    }
}
