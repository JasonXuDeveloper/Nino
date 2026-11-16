using System;
using System.Linq;
using Nino.Core;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using unsafe PointerAlias = delegate*<int>;
#endif

#if NET8_0_OR_GREATER
// .NET only (net10.0: x64, Arm64, and 32-bit Arm32 under qemu): Mono 6.8 and 6.12 (x64 and x86) overflow their stack
// loading a struct with a delegate* field at all, so the net472 build, which Mono runs, declares none.

/// <summary>A plain (raw) struct holding a function pointer, a pointer and an nint, each 4 bytes on 32-bit hosts.</summary>
public unsafe struct HoldsFunctionPointer
{
    public byte A;
    public delegate*<int> F;
    public int* P;
    public nint N;
}

/// <summary>
/// A function pointer field declared through an alias (C# 12), which syntax cannot tell from any other type: only
/// generic code reaches it (<see cref="FunctionPointers"/>' Save and Load), so the registry has it from the
/// generator's walk of the structs declared here.
/// </summary>
public unsafe struct AliasedFunctionPointer
{
    public byte A;
    public PointerAlias F;
    public nint N;
}

/// <summary>The field a primary constructor captures (<c>&lt;call&gt;P</c>), reached only by generic code too.</summary>
public unsafe struct CapturedFunctionPointer(delegate*<int> call)
{
    public nuint Call => (nuint)call;
}
#endif

/// <summary>
/// A plain struct whose <see cref="F"/> the runtime reports as an <c>IntPtr</c>, as Mono and IL2CPP report a
/// <c>delegate*</c> field: <see cref="FunctionPointers"/> registers it as one, as the generated registry of an assembly
/// declaring a <c>delegate*</c> field does (Unity's Mono and IL2CPP load such structs, stock Mono cannot).
/// </summary>
public struct ReportedAsIntPtr
{
    public byte A;
    public IntPtr F;
    public IntPtr N;
}

/// <summary>
/// A function pointer (<c>delegate*</c>) inside a raw struct is a pointer: 8 bytes zero-extended like <c>T*</c> (spec
/// §5.2), on every runtime, from what the generated registry registers (<c>NinoFunctionPointerFields</c>), not from
/// reflection. The values fit in 32 bits with the top bit set, so a 32-bit host that sign-extended them, or copied
/// them as they lie in its memory, writes other bytes.
/// </summary>
public static unsafe class FunctionPointers
{
    private static readonly byte[] Pointer = { 0x21, 0x43, 0x65, 0x87, 0, 0, 0, 0 };
    private static readonly byte[] MinusTwo = { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    private static readonly byte[] One = { 1, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>The number of cases that wrote or read other bytes; each printed as a comment line.</summary>
    public static int Run()
    {
        int failed = Reported();
#if NET8_0_OR_GREATER
        failed += Declared();
        failed += ReachedByGenericCode();
#endif
        return failed;
    }

    /// <summary>A field the runtime reports as an IntPtr and the registry as a function pointer: zero-extended.</summary>
    private static int Reported()
    {
        NinoFunctionPointerFields.Register(typeof(ReportedAsIntPtr).Assembly.GetName().Name, nameof(ReportedAsIntPtr),
            nameof(ReportedAsIntPtr.F));
        var expected = One.Concat(Pointer).Concat(MinusTwo).ToArray();
        return Report("function pointers reported as IntPtr", () =>
        {
            var value = new ReportedAsIntPtr { A = 1, F = (IntPtr)(void*)0x87654321u, N = (IntPtr)(-2) };
            var array = NinoSerializer.Serialize(new[] { value, value });
            var back = NinoDeserializer.Deserialize<ReportedAsIntPtr>(expected);
            bool writes = NinoSerializer.Serialize(value).SequenceEqual(expected) &&
                          array.Skip(4).SequenceEqual(expected.Concat(expected));
            bool reads = (ulong)(void*)back.F == 0x87654321u && (long)back.N == -2 &&
                         (ulong)(void*)NinoDeserializer.Deserialize<ReportedAsIntPtr[]>(array)[1].F == 0x87654321u;
            return (writes, reads);
        });
    }

#if NET8_0_OR_GREATER
    /// <summary>A declared delegate* field, which .NET 8 and later report as a function pointer type.</summary>
    private static int Declared()
    {
        var expected = One.Concat(Pointer).Concat(new byte[] { 4, 0, 0, 0x80, 0, 0, 0, 0 }).Concat(MinusTwo).ToArray();
        return Report("function pointers", () =>
        {
            var value = new HoldsFunctionPointer
            {
                A = 1, F = (delegate*<int>)(nuint)0x87654321u, P = (int*)(nuint)0x80000004u, N = -2,
            };
            var array = NinoSerializer.Serialize(new[] { value, value });
            var back = NinoDeserializer.Deserialize<HoldsFunctionPointer>(expected);
            var backs = NinoDeserializer.Deserialize<HoldsFunctionPointer[]>(array);
            bool writes = NinoSerializer.Serialize(value).SequenceEqual(expected) &&
                          array.Skip(4).SequenceEqual(expected.Concat(expected));
            bool reads = (nuint)back.F == 0x87654321u && (nuint)back.P == 0x80000004u && back.N == -2 &&
                         (nuint)backs[1].F == 0x87654321u;
            return (writes, reads);
        });
    }
#endif

#if NET8_0_OR_GREATER
    /// <summary>
    /// Structs only generic code reaches (<c>Save&lt;T&gt;</c> calling <c>Serialize&lt;T&gt;</c>): no member or call
    /// names them, yet the registry has their function pointer fields (without it, .NET 8 and later find no formatter
    /// for them and the other runtimes would sign-extend the field).
    /// </summary>
    private static int ReachedByGenericCode()
    {
        var expected = One.Concat(Pointer).Concat(MinusTwo).ToArray();
        int failed = Report("function pointers through an alias, reached by generic code", () =>
        {
            var value = new AliasedFunctionPointer { A = 1, F = (delegate*<int>)(nuint)0x87654321u, N = -2 };
            var back = Load<AliasedFunctionPointer>(expected);
            return (Save(value).SequenceEqual(expected), (nuint)back.F == 0x87654321u && back.N == -2);
        });
        return failed + Report("function pointers a primary constructor captures, reached by generic code", () =>
        {
            var value = new CapturedFunctionPointer((delegate*<int>)(nuint)0x87654321u);
            return (Save(value).SequenceEqual(Pointer), Load<CapturedFunctionPointer>(Pointer).Call == 0x87654321u);
        });
    }

    private static byte[] Save<T>(T value) where T : unmanaged => NinoSerializer.Serialize(value);

    private static T Load<T>(byte[] data) where T : unmanaged => NinoDeserializer.Deserialize<T>(data);
#endif

    private static int Report(string name, Func<(bool Writes, bool Reads)> run)
    {
        string outcome;
        try
        {
            var (writes, reads) = run();
            outcome = writes && reads ? "ok" : (writes ? "" : "WRITES OTHER BYTES ") + (reads ? "" : "READS OTHER VALUES");
        }
        catch (Exception e)
        {
            outcome = "EXCEPTION " + e.GetType().Name + ": " + e.Message;
        }

        Console.WriteLine($"# {name} {outcome}");
        return outcome == "ok" ? 0 : 1;
    }
}
