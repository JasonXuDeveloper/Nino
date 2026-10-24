using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// The settings of <see cref="NinoSettings"/> that read and write paths check, frozen when this class is first used
    /// (the first read or write that reaches one of them): <c>static readonly</c> fields, which the JIT reads as
    /// constants in code compiled after that (tiered and optimized code on CoreCLR), so a check of a setting left at
    /// its default folds away. Elsewhere (Mono, IL2CPP, interpreters) each check is a load and a compare. Setting
    /// them later throws (<see cref="NinoSettings"/>).
    /// </summary>
    internal static class NinoLimits
    {
        // First, in textual order: the fields below copy it.
        private static readonly NinoSettings.Snapshot Frozen = NinoSettings.Freeze();

        /// <summary><see cref="NinoSettings.MaxDepth"/>.</summary>
        public static readonly int MaxDepth = Frozen.MaxDepth;

        /// <summary><see cref="NinoSettings.MaxCollectionLength"/>.</summary>
        public static readonly int MaxCollectionLength = Frozen.MaxCollectionLength;

        /// <summary><see cref="NinoSettings.MaxHashCollisions"/>.</summary>
        public static readonly int MaxHashCollisions = Frozen.MaxHashCollisions;

        /// <summary>Whether <see cref="MaxCollectionLength"/> is lower than any length (its default, int.MaxValue).</summary>
        public static readonly bool LengthCapped = Frozen.MaxCollectionLength != int.MaxValue;

        /// <summary>
        /// Whether a collection length of <paramref name="length"/> (not negative) is past
        /// <see cref="MaxCollectionLength"/>. False without a cap, where the JIT folds it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TooLong(int length) => LengthCapped && length > MaxCollectionLength;
    }
}
