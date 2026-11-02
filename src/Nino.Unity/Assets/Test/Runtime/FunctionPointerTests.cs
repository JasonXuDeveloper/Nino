using System;
using System.Linq;
using Nino.Core;
using NUnit.Framework;

namespace Test.Runtime
{
    /// <summary>
    /// A plain (raw) struct holding a managed and an unmanaged function pointer (C# 9, which Unity compiles), a pointer
    /// and an nint. Mono and IL2CPP report a <c>delegate*</c> field as an <c>IntPtr</c>: what the generated registry
    /// registers (<c>NinoFunctionPointerFields</c>) makes it a pointer, not reflection.
    /// </summary>
    public unsafe struct HoldsFunctionPointers
    {
        public byte A;
        public delegate*<int, int, int> F;
        public delegate* unmanaged[Cdecl]<int> U;
        public int* P;
        public nint N;
    }

    /// <summary>
    /// <c>delegate*</c> fields in Unity's Mono (play mode) and IL2CPP (the player): each written as 8 bytes,
    /// zero-extended like <c>T*</c> (spec §5.2), in the canonical layout (CoreCLR x64's), the bytes CoreCLR writes (tests/Nino.RuntimeTests, FunctionPointers),
    /// and a pointer taken with <c>&amp;</c> still callable after the round trip.
    /// </summary>
    public unsafe class FunctionPointerTests
    {
        private static int Add(int a, int b) => a + b;

        private static byte[] Le64(ulong value)
        {
            var bytes = BitConverter.GetBytes(value);
            return BitConverter.IsLittleEndian ? bytes : bytes.Reverse().ToArray();
        }

        // Pointer constants are built from 64-bit literals: for (nuint)0x87654321u (ldc.i4, conv.u) IL2CPP's players
        // sign-extend the value itself, so the field would hold 0xFFFFFFFF87654321 before Nino ever sees it.
        [Test]
        public void ConstantsZeroExtended()
        {
            var value = new HoldsFunctionPointers
            {
                A = 1,
                F = (delegate*<int, int, int>)(nuint)0x87654321UL,
                U = (delegate* unmanaged[Cdecl]<int>)(nuint)0x80000008UL,
                P = (int*)(nuint)0x80000004UL,
                N = -2,
            };
            var expected = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 } // A, then the padding of the canonical layout
                .Concat(new byte[] { 0x21, 0x43, 0x65, 0x87, 0, 0, 0, 0 })
                .Concat(new byte[] { 8, 0, 0, 0x80, 0, 0, 0, 0 })
                .Concat(new byte[] { 4, 0, 0, 0x80, 0, 0, 0, 0 })
                .Concat(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })
                .ToArray();

            Assert.AreEqual(BitConverter.ToString(expected), BitConverter.ToString(NinoSerializer.Serialize(value)));
            var array = NinoSerializer.Serialize(new[] { value, value });
            Assert.AreEqual(BitConverter.ToString(expected.Concat(expected).ToArray()),
                BitConverter.ToString(array.Skip(4).ToArray()));

            var back = NinoDeserializer.Deserialize<HoldsFunctionPointers>(expected);
            Assert.AreEqual(1, back.A);
            Assert.AreEqual((nuint)0x87654321UL, (nuint)back.F);
            Assert.AreEqual((nuint)0x80000008UL, (nuint)back.U);
            Assert.AreEqual((nuint)0x80000004UL, (nuint)back.P);
            Assert.AreEqual((nint)(-2), back.N);
            Assert.AreEqual((nuint)0x87654321UL, (nuint)NinoDeserializer.Deserialize<HoldsFunctionPointers[]>(array)[1].F);
        }

        [Test]
        public void TakenPointerStillCalls()
        {
            var value = new HoldsFunctionPointers { A = 7, F = &Add, N = 3 };
            var bytes = NinoSerializer.Serialize(value);
            var expected = new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 }.Concat(Le64((ulong)(nuint)value.F)).Concat(new byte[16])
                .Concat(Le64(3)).ToArray();
            Assert.AreEqual(BitConverter.ToString(expected), BitConverter.ToString(bytes));

            var back = NinoDeserializer.Deserialize<HoldsFunctionPointers>(bytes);
            Assert.AreEqual((nuint)value.F, (nuint)back.F);
            Assert.AreEqual(5, back.F(2, 3));
        }
    }
}
