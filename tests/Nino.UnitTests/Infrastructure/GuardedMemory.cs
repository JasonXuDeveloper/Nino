#if NET8_0_OR_GREATER
using System;
using System.Runtime.InteropServices;

namespace Nino.UnitTests;

/// <summary>
/// Memory followed by an inaccessible page (mmap and mprotect on Linux and macOS, VirtualAlloc and VirtualProtect on
/// Windows; none elsewhere), as the fuzzer places inputs (tests/Nino.Fuzz/Guard.cs): a read past the bytes placed
/// against it crashes the test host instead of reading the next object on the heap.
/// </summary>
internal static unsafe partial class GuardedMemory
{
    private const int Window = 1 << 16;
    private static readonly byte* Base = Create();

    /// <summary>A copy of <paramref name="data"/> ending right before the page; empty without one.</summary>
    public static Span<byte> AtEnd(ReadOnlySpan<byte> data)
    {
        if (Base == null || data.Length > Window)
            return default;
        var span = new Span<byte>(Base + Window - data.Length, data.Length);
        data.CopyTo(span);
        return span;
    }

    private static byte* Create()
    {
        int page = Environment.SystemPageSize;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var p = VirtualAlloc(IntPtr.Zero, (nuint)(Window + page), 0x3000 /* commit, reserve */, 0x04 /* read, write */);
                return p != IntPtr.Zero && VirtualProtect(p + Window, (nuint)page, 0x01 /* no access */, out _) ? (byte*)p : null;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // MAP_PRIVATE | MAP_ANONYMOUS: 0x22 on Linux, 0x1002 on macOS.
                var p = mmap(IntPtr.Zero, (nuint)(Window + page), 3 /* read, write */,
                    OperatingSystem.IsLinux() ? 0x22 : 0x1002, -1, 0);
                return p != new IntPtr(-1) && mprotect(p + Window, (nuint)page, 0) == 0 ? (byte*)p : null;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    [LibraryImport("libc")] private static partial IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);
    [LibraryImport("libc")] private static partial int mprotect(IntPtr addr, nuint length, int prot);
    [LibraryImport("kernel32")] private static partial IntPtr VirtualAlloc(IntPtr addr, nuint size, uint type, uint protect);
    [LibraryImport("kernel32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool VirtualProtect(IntPtr addr, nuint size, uint protect, out uint old);
}
#endif
