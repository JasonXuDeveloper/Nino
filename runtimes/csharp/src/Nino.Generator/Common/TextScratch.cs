using System.Text;
using System.Threading;

namespace Nino.Generator.Common;

internal static class TextScratch
{
    // Keep one bounded buffer per generator load context, independent of the compiler thread count.
    private const int MaxScratchCapacity = 8192;
    private static StringBuilder? _scratch;

    public static StringBuilder Rent(int capacity)
    {
        var scratch = capacity <= MaxScratchCapacity ? Interlocked.Exchange(ref _scratch, null) : null;
        if (scratch == null)
            return new StringBuilder(capacity);

        scratch.EnsureCapacity(capacity);
        return scratch;
    }

    public static void Return(StringBuilder? scratch)
    {
        if (scratch is { Capacity: <= MaxScratchCapacity })
        {
            scratch.Clear();
            Interlocked.CompareExchange(ref _scratch, scratch, null);
        }
    }
}
