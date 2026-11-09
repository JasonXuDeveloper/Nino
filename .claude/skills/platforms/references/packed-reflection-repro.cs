using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

var field = typeof(Packed).GetField(nameof(Packed.B));
unsafe
{
    for (int i = 0; i < 100; i++)
    {
        var pad = new object();
        object value = new Packed { A = 5, B = long.MinValue, C = 6 };
        nuint address = (nuint)Unsafe.AsPointer(ref Unsafe.Unbox<Packed>(value)) + 1;
        GC.KeepAlive(pad);
        if ((address & 15) != 9)
            continue;
        Console.WriteLine($"Reflecting Packed.B at offset {address & 15} in a 16-byte block");
        Console.WriteLine(field.GetValue(value));
        return;
    }
}
throw new Exception("No crossing address");

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Packed
{
    public byte A;
    public long B;
    public short C;
}
