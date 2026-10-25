using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nino.UnitTests")]

// Locals are not zeroed on entry: every local is assigned before it is read, and zeroing the struct copies of a
// Reader/Writer (in paths that are rarely taken) on each inlined call costs stores in element loops.
[module: SkipLocalsInit]
