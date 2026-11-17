#if !NINO_BENCH_V3
using System.Collections.Generic;
using MemoryPack;

namespace Nino.Benchmark
{
    /// <summary>
    /// A list that holds itself, with no [NinoType] body between its levels (CollectionsTest). Not in the v3 build:
    /// v3's generator recurses without end on it.
    /// </summary>
    [MemoryPackable(GenerateType.Collection)]
    public sealed partial class SelfTree : List<SelfTree>
    {
        /// <summary>A tree <paramref name="depth"/> levels deep, every inner list holding <paramref name="width"/>.</summary>
        public static SelfTree Create(int depth, int width)
        {
            var tree = new SelfTree();
            for (int i = 0; depth > 0 && i < width; i++)
                tree.Add(Create(depth - 1, width));
            return tree;
        }
    }
}
#endif
