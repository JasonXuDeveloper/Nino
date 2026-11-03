using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nino.Core;
using Nino.Core.CompilerServices;

namespace Nino.Benchmark;

/// <summary>
/// The 1,200-byte copy of NinoSerializeSimpleStructs in three shapes, in one binary: main's NinoUnsafe.Copy (a call to
/// CopyOver64 with the loop inlined, Tier1), rc.7's (size checks inline, then a call to a fully optimized
/// CopyVectors&lt;Vector512&gt;), and Memmove; plus the case itself. Separates the copy's shape from everything else
/// that differs between two builds on the CPUs where the case differs (A/B only).
/// </summary>
[NinoJob]
public class CopyShapeProbe
{
    private static readonly SimpleStruct[] Structs = CreateStructs();
    private readonly NinoArrayBufferWriter _buffer = new(320 * 1024);

    private static SimpleStruct[] CreateStructs()
    {
        var values = new SimpleStruct[100];
        for (int i = 0; i < values.Length; i++)
            values[i] = SimpleStruct.Create();
        return values;
    }

    private ref byte Destination => ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer.GetSpan(1300)), 4);
    private static ref byte Source => ref Unsafe.As<SimpleStruct, byte>(ref MemoryMarshal.GetArrayDataReference(Structs));
    private static uint Size => (uint)(Structs.Length * Unsafe.SizeOf<SimpleStruct>());

    [Benchmark]
    public int MainCopyProbe()
    {
        NinoUnsafe.Copy(ref Destination, ref Source, Size);
        return 0;
    }

    [Benchmark]
    public int Rc7ShapeCopyProbe()
    {
        uint size = Size;
        if (size <= 128)
            throw new InvalidOperationException();
        CopyVectors512(ref Destination, ref Source, size);
        return 0;
    }

    [Benchmark]
    public int Tier1LoopProbe()
    {
        uint size = Size;
        if (size <= 128)
            throw new InvalidOperationException();
        CopyVectors512Tier1(ref Destination, ref Source, size);
        return 0;
    }

    [Benchmark]
    public int FullOptsDispatchProbe()
    {
        CopyDispatchFullOpts(ref Destination, ref Source, Size);
        return 0;
    }

    [Benchmark]
    public int Tier1DispatchProbe()
    {
        CopyDispatchTier1(ref Destination, ref Source, Size);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyVectors512Tier1(ref byte destination, ref byte source, uint size) =>
        Loop(ref destination, ref source, size);

    // CopyOver64's shape: 65-128 with four 32-byte vectors, else the 64-byte loop up to 32 KB, else Memmove.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void CopyDispatchFullOpts(ref byte destination, ref byte source, uint size) =>
        Dispatch(ref destination, ref source, size);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyDispatchTier1(ref byte destination, ref byte source, uint size) =>
        Dispatch(ref destination, ref source, size);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dispatch(ref byte destination, ref byte source, uint size)
    {
        if (size <= 128)
        {
            var a = Vector256.LoadUnsafe(ref source);
            var b = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, 32));
            var c = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 64));
            var d = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 32));
            a.StoreUnsafe(ref destination);
            b.StoreUnsafe(ref Unsafe.Add(ref destination, 32));
            c.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 64));
            d.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 32));
            return;
        }

        if (size <= 32 * 1024)
        {
            Loop(ref destination, ref source, size);
            return;
        }

        Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
    }

    [Benchmark]
    public int MemmoveCopyProbe()
    {
        Unsafe.CopyBlockUnaligned(ref Destination, ref Source, Size);
        return 0;
    }

    [Benchmark]
    public int CaseProbe()
    {
        _buffer.ResetWrittenCount();
        NinoSerializer.Serialize(Structs, _buffer);
        return _buffer.WrittenCount;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void CopyVectors512(ref byte destination, ref byte source, uint size) =>
        Loop(ref destination, ref source, size);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Loop(ref byte destination, ref byte source, uint size)
    {
        nuint width = 64;
        Unsafe.WriteUnaligned(ref destination, Vector512.LoadUnsafe(ref source));
        nuint i = width - ((nuint)Unsafe.AsPointer(ref destination) & (width - 1));
        nuint end = size - width;
        for (; i + 3 * width <= end; i += 4 * width)
        {
            var a = Vector512.LoadUnsafe(ref source, i);
            var b = Vector512.LoadUnsafe(ref source, i + width);
            var c = Vector512.LoadUnsafe(ref source, i + 2 * width);
            var e = Vector512.LoadUnsafe(ref source, i + 3 * width);
            a.StoreUnsafe(ref destination, i);
            b.StoreUnsafe(ref destination, i + width);
            c.StoreUnsafe(ref destination, i + 2 * width);
            e.StoreUnsafe(ref destination, i + 3 * width);
        }

        for (; i <= end; i += width)
            Vector512.LoadUnsafe(ref source, i).StoreUnsafe(ref destination, i);
        Vector512.LoadUnsafe(ref source, end).StoreUnsafe(ref destination, end);
    }
}
