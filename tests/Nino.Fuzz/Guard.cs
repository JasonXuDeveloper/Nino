using System;
using System.Runtime.InteropServices;

namespace Nino.Fuzz
{
    /// <summary>Where an input is placed in memory for one read.</summary>
    public enum Placement : byte
    {
        /// <summary>The last byte sits right before an inaccessible page: a read past the end faults.</summary>
        End,
        /// <summary>The first byte sits right after an inaccessible page: a read before the start faults.</summary>
        Start,
        /// <summary>An ordinary array (the only placement on a platform without page protection).</summary>
        Heap,
    }

    /// <summary>
    /// A window of memory with an inaccessible page on each side (mmap and mprotect on Linux and macOS, VirtualAlloc
    /// and VirtualProtect on Windows). An input is copied against one of the guard pages, so reading one byte outside
    /// it crashes the process with an access violation instead of reading the neighbouring heap; the supervisor
    /// (Supervisor.cs) records the input. On any other platform, or when the calls fail, inputs stay in arrays.
    /// </summary>
    public static unsafe class Guard
    {
        public const int Window = 8 << 20;
        static readonly byte* Base;
        static readonly int Page;
        public static readonly bool Available;
        public static readonly string Mechanism;

        static Guard()
        {
            Page = Environment.SystemPageSize;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var p = VirtualAlloc(IntPtr.Zero, (nuint)(Window + 2 * Page), 0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x04 /* PAGE_READWRITE */);
                    if (p == IntPtr.Zero) { Mechanism = "arrays (VirtualAlloc failed)"; return; }
                    if (!VirtualProtect(p, (nuint)Page, 0x01 /* PAGE_NOACCESS */, out _) ||
                        !VirtualProtect(p + Page + Window, (nuint)Page, 0x01, out _)) { Mechanism = "arrays (VirtualProtect failed)"; return; }
                    Base = (byte*)p;
                    Mechanism = "VirtualAlloc guard pages";
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                {
                    // MAP_PRIVATE | MAP_ANONYMOUS: 0x22 on Linux, 0x1002 on macOS and FreeBSD.
                    int flags = OperatingSystem.IsLinux() ? 0x22 : 0x1002;
                    var p = mmap(IntPtr.Zero, (nuint)(Window + 2 * Page), 3 /* PROT_READ | PROT_WRITE */, flags, -1, 0);
                    if (p == new IntPtr(-1)) { Mechanism = "arrays (mmap failed)"; return; }
                    if (mprotect(p, (nuint)Page, 0) != 0 || mprotect(p + Page + Window, (nuint)Page, 0) != 0)
                    { Mechanism = "arrays (mprotect failed)"; return; }
                    Base = (byte*)p;
                    Mechanism = "mmap guard pages";
                }
                else
                {
                    Mechanism = "arrays (no page protection on this platform)";
                    return;
                }
                Available = true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                Mechanism = "arrays (" + e.GetType().Name + ")";
            }
        }

        /// <summary>The placement to use for an input; falls back to <see cref="Placement.Heap"/> when guards are unavailable.</summary>
        public static Placement Effective(Placement wanted, int length) =>
            Available && length <= Window ? wanted : Placement.Heap;

        /// <summary>Copies <paramref name="data"/> into place. The span stays valid until the next call.</summary>
        public static ReadOnlySpan<byte> Place(byte[] data, Placement placement)
        {
            switch (Effective(placement, data.Length))
            {
                case Placement.End:
                {
                    byte* dst = Base + Page + Window - data.Length;
                    data.AsSpan().CopyTo(new Span<byte>(dst, data.Length));
                    return new ReadOnlySpan<byte>(dst, data.Length);
                }
                case Placement.Start:
                {
                    byte* dst = Base + Page;
                    data.AsSpan().CopyTo(new Span<byte>(dst, data.Length));
                    return new ReadOnlySpan<byte>(dst, data.Length);
                }
                default:
                    return (byte[])data.Clone();
            }
        }

        [DllImport("libc", SetLastError = true)] static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);
        [DllImport("libc", SetLastError = true)] static extern int mprotect(IntPtr addr, nuint length, int prot);
        [DllImport("kernel32", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, nuint size, uint type, uint protect);
        [DllImport("kernel32", SetLastError = true)] static extern bool VirtualProtect(IntPtr addr, nuint size, uint protect, out uint old);
    }
}
