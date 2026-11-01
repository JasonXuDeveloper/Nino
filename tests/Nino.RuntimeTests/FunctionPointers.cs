#if NET8_0_OR_GREATER
using System;
using System.Linq;
using Nino.Core;

// .NET only (net10.0: x64, Arm64, and 32-bit Arm32 under qemu): Mono 6.8 overflows its stack loading a struct with a
// delegate* field, which the generated registry names, so the net472 build, which Mono runs, declares none.

/// <summary>A plain (raw) struct holding a function pointer, a pointer and an nint, each 4 bytes on 32-bit hosts.</summary>
public unsafe struct HoldsFunctionPointer
{
    public byte A;
    public delegate*<int> F;
    public int* P;
    public nint N;
}

/// <summary>
/// A function pointer (<c>delegate*</c>) inside a raw struct is a pointer: 8 bytes zero-extended like <c>T*</c> (spec
/// §5.2), as .NET 8 and later report it (a function pointer type; the IntPtr of earlier runtimes and the field
/// signature that tells it apart are PortableLayoutTests'). The values fit in 32 bits with the top bit set, so a 32-bit
/// host that sign-extended them, or copied them as they lie in its memory, writes other bytes.
/// </summary>
public static unsafe class FunctionPointers
{
    private static readonly byte[] Expected =
    {
        1, 0, 0, 0, 0, 0, 0, 0,
        0x21, 0x43, 0x65, 0x87, 0, 0, 0, 0,
        4, 0, 0, 0x80, 0, 0, 0, 0,
        0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    };

    /// <summary>1 when this runtime writes or reads other bytes, else 0; printed as a comment (.NET only).</summary>
    public static int Run()
    {
        string outcome;
        try
        {
            var value = new HoldsFunctionPointer
            {
                A = 1, F = (delegate*<int>)(nuint)0x87654321u, P = (int*)(nuint)0x80000004u, N = -2,
            };
            var array = NinoSerializer.Serialize(new[] { value, value });
            var back = NinoDeserializer.Deserialize<HoldsFunctionPointer>(Expected);
            var backs = NinoDeserializer.Deserialize<HoldsFunctionPointer[]>(array);
            bool writes = NinoSerializer.Serialize(value).SequenceEqual(Expected) &&
                          array.Skip(4).SequenceEqual(Expected.Concat(Expected));
            bool reads = (nuint)back.F == 0x87654321u && (nuint)back.P == 0x80000004u && back.N == -2 &&
                         (nuint)backs[1].F == 0x87654321u;
            outcome = writes && reads ? "ok" : (writes ? "" : "WRITES OTHER BYTES ") + (reads ? "" : "READS OTHER VALUES");
        }
        catch (Exception e)
        {
            outcome = "EXCEPTION " + e.GetType().Name + ": " + e.Message;
        }

        Console.WriteLine($"# function pointers {outcome}");
        return outcome == "ok" ? 0 : 1;
    }
}
#endif
