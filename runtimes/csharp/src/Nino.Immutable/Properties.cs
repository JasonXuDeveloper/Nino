using System.Runtime.CompilerServices;

// Locals are not zeroed on entry, as in Nino.Core: every local is assigned before it is read, and zeroing the struct
// copies of a Reader/Writer and the formatters' locals costs stores on each read and write.
[module: SkipLocalsInit]
