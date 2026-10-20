namespace Nino.Core
{
    /// <summary>
    /// Experiment only (not for release): NINO_X picks the variants measured side by side in one run, a bit each:
    /// 1 Arm64 grid lane plans for reads, 2 uninitialized new arrays on Arm64 and AMD, 4 grid plans for writes.
    /// </summary>
    internal static class NinoExperiment
    {
        private static readonly int X =
            int.TryParse(System.Environment.GetEnvironmentVariable("NINO_X"), out int x) ? x : 7;

        public static readonly bool GridReads = (X & 1) != 0, Uninitialized = (X & 2) != 0, GridWrites = (X & 4) != 0;
    }
}
