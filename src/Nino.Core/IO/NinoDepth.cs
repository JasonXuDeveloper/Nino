using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Bounds on how deep class bodies nest on the stack. A body that would open below <see cref="Inline"/> open
    /// bodies is deferred: its slot gets the instance now and its members are written (and read) later, from a
    /// shallow point, so a long linked list or a deep tree costs a bounded stack however deep it is, and data
    /// cannot exhaust the reader's stack.
    /// <para>
    /// Where a deferred body goes depends on what encloses it. Outside any length-prefixed region, deferred bodies
    /// follow the top-level object they belong to, once the bodies open around it have all closed. Inside a region
    /// (a version-tolerant body, a keyed body or member), they go into a section at the end of the outermost
    /// region, keyed by where the deferral happened (relative to the region's start): a reader that skips part of
    /// the region (members it does not know) skips the sections' entries for it too. A region holding a section
    /// has <see cref="SectionFlag"/> set in its length; the section is followed by its offset in the region.
    /// </para>
    /// </summary>
    internal static class NinoDepth
    {
        /// <summary>Open class bodies after which the next one is deferred.</summary>
        public const int Inline = 128;

        // Open class bodies after which reading or writing fails, for types that cannot be deferred (built by their
        // constructor, or compared by value): NinoSettings.MaxDepth (NinoLimits.MaxDepth), 512 by default.

        /// <summary>Set in the length of a region followed by a section of deferred bodies.</summary>
        public const int SectionFlag = int.MinValue;

        /// <summary>
        /// The depth a body deferred inside a region is written and read at, from its section, when the region ended
        /// at <paramref name="depth"/>: one body deeper, but never as deep as <see cref="Inline"/> (a deferred body
        /// is written in place).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Deferred(int depth) => Math.Min(depth, Inline - 1) + 1;

        /// <summary>
        /// Whether the stack has room for more nesting (the runtime's own check, which keeps about 64 KB, 128 KB on
        /// 64-bit .NET, for what follows). Reads past <see cref="Inline"/> that cannot be deferred check it: up to
        /// <see cref="NinoSettings.MaxDepth"/> bodies of a large type take more than a small stack (256 KB IIS or mobile threads) has.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool StackLeft()
        {
#if NETSTANDARD2_0
            try
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                return true;
            }
            catch (InsufficientExecutionStackException)
            {
                return false;
            }
#else
            return RuntimeHelpers.TryEnsureSufficientExecutionStack();
#endif
        }
    }
}
