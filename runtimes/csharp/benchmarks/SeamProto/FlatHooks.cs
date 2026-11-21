// PROTOTYPE (#355, the generator seam), v2: what the generator would emit for a type whose body is flat, written by
// hand: a member accessor struct per item (typed field access, a constant offset on every backend; a block's size a
// constant), a layout struct (new T(), the exact-type test and the type id, all constants), the type of its item
// chain, a startup check (blocks lie in the object as on the wire, nothing Untrusted mode checks in them; read once per
// call, never per element) and the three hooks. Nino.Core holds the one kernel source. NINO_SEAM=0 turns the hooks off
// (the base of the comparison, the same build).
using System;
using System.Runtime.CompilerServices;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using Nino.SeamProto;
using __OneLayout = NinoGen.Nino_SeamProto_SeamOneFormatter.__Layout;
using __OneBody = Nino.Core.Internal.NinoFlatChain<
    Nino.Core.Internal.NinoBlockItem<NinoGen.Nino_SeamProto_SeamOneFormatter.__Stamp>,
    Nino.Core.Internal.NinoStringItem<NinoGen.Nino_SeamProto_SeamOneFormatter.__Name, Nino.Core.Internal.NinoUtf16Item>>;
using __ManyLayout = NinoGen.Nino_SeamProto_SeamManyFormatter.__Layout;
using __ManyBody = Nino.Core.Internal.NinoFlatChain<Nino.Core.Internal.NinoFlatChain<Nino.Core.Internal.NinoFlatChain<
    Nino.Core.Internal.NinoFlatChain<Nino.Core.Internal.NinoFlatChain<
                Nino.Core.Internal.NinoBlockItem<NinoGen.Nino_SeamProto_SeamManyFormatter.__Id>,
                Nino.Core.Internal.NinoStringItem<NinoGen.Nino_SeamProto_SeamManyFormatter.__Name, Nino.Core.Internal.NinoUtf16Item>>,
            Nino.Core.Internal.NinoStringItem<NinoGen.Nino_SeamProto_SeamManyFormatter.__Tag, Nino.Core.Internal.NinoUtf8Item>>,
        Nino.Core.Internal.NinoBlockItem<NinoGen.Nino_SeamProto_SeamManyFormatter.__At>>,
    Nino.Core.Internal.NinoBoolItem<NinoGen.Nino_SeamProto_SeamManyFormatter.__Flag>>,
    Nino.Core.Internal.NinoStringItem<NinoGen.Nino_SeamProto_SeamManyFormatter.__Text, Nino.Core.Internal.NinoUtf16Item>>;

namespace Nino.SeamProto
{
    /// <summary>The switch: NINO_SEAM=0 keeps every type on its generated body.</summary>
    internal static class Seam
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("NINO_SEAM") != "0";
    }
}

namespace NinoGen
{
    internal partial struct Nino_SeamProto_SeamOneFormatter
    {
        // Items: Stamp, Score, Id, Kind (one block of 21 bytes), then Name.
        internal readonly struct __Stamp : INinoMember
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref byte Ref(object value) => ref Unsafe.As<long, byte>(ref Unsafe.As<SeamOne>(value).Stamp);

            public int Size
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => 21;
            }
        }

        internal readonly struct __Name : INinoMember
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref byte Ref(object value) => ref Unsafe.As<string, byte>(ref Unsafe.As<SeamOne>(value).Name);

            public int Size
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => 0;
            }
        }

        internal readonly struct __Layout : INinoFlatLayout
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public object New() => new SeamOne();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsExact(object value) => value.GetType() == typeof(SeamOne);

            public int TypeId
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => -198027967;
            }
        }

        private static class __Flat
        {
            public static readonly bool Has = Check();

            private static bool Check()
            {
                var p = NinoFieldBlock.Probe<SeamOne>();
                return Seam.On && p != null &&
                       NinoFieldBlock.IsAt(in p.Stamp, in p.Stamp, 0, 8) &&
                       NinoFieldBlock.IsAt(in p.Stamp, in p.Score, 8, 8) &&
                       NinoFieldBlock.IsAt(in p.Stamp, in p.Id, 16, 4) &&
                       NinoFieldBlock.IsAt(in p.Stamp, in p.Kind, 20, 1) &&
                       NinoFieldBlock.ReadsAsIs<long>() && NinoFieldBlock.ReadsAsIs<double>() &&
                       NinoFieldBlock.ReadsAsIs<int>() && NinoFieldBlock.ReadsAsIs<SeamKind>();
            }
        }

        bool INinoFormatter.HasFlat => __Flat.Has;

        Writer INinoFormatter.WriteFlat(scoped NinoRun run, Writer writer) =>
            NinoFlatRuns.WriteRun<SeamOne, Nino_SeamProto_SeamOneFormatter, NinoTypedItem<__OneLayout, __OneBody>>(
                run, writer);

        Reader INinoFormatter.ReadFlat(scoped NinoRun run, Reader reader) =>
            NinoFlatRuns.ReadRun<SeamOne, Nino_SeamProto_SeamOneFormatter, NinoTypedItem<__OneLayout, __OneBody>>(
                run, reader);
    }

    internal partial struct Nino_SeamProto_SeamManyFormatter
    {
        // Items: Id (a block of 4), Name, Tag (UTF-8), At (a block of 8), Flag (a bool), Text.
        internal readonly struct __Id : INinoMember
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref byte Ref(object value) => ref Unsafe.As<int, byte>(ref Unsafe.As<SeamMany>(value).Id);

            public int Size
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => 4;
            }
        }

        internal readonly struct __Name : INinoMember
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref byte Ref(object value) => ref Unsafe.As<string, byte>(ref Unsafe.As<SeamMany>(value).Name);

            public int Size
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => 0;
            }
        }

        internal readonly struct __Tag : INinoMember
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref byte Ref(object value) => ref Unsafe.As<string, byte>(ref Unsafe.As<SeamMany>(value).Tag);

            public int Size
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => 0;
            }
        }

        internal readonly struct __At : INinoMember
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref byte Ref(object value) => ref Unsafe.As<DateTime, byte>(ref Unsafe.As<SeamMany>(value).At);

            public int Size
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => 8;
            }
        }

        internal readonly struct __Flag : INinoMember
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref byte Ref(object value) => ref Unsafe.As<bool, byte>(ref Unsafe.As<SeamMany>(value).Flag);

            public int Size
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => 1;
            }
        }

        internal readonly struct __Text : INinoMember
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref byte Ref(object value) => ref Unsafe.As<string, byte>(ref Unsafe.As<SeamMany>(value).Text);

            public int Size
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => 0;
            }
        }

        internal readonly struct __Layout : INinoFlatLayout
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public object New() => new SeamMany();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsExact(object value) => true;

            public int TypeId
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => -134794538;
            }
        }

        private static class __Flat
        {
            public static readonly bool Has = Check();

            private static bool Check()
            {
                var p = NinoFieldBlock.Probe<SeamMany>();
                return Seam.On && p != null && NinoFieldBlock.IsAt(in p.Id, in p.Id, 0, 4) &&
                       NinoFieldBlock.IsAt(in p.At, in p.At, 0, 8) && NinoFieldBlock.ReadsAsIs<int>() &&
                       NinoFieldBlock.ReadsAsIs<DateTime>();
            }
        }

        bool INinoFormatter.HasFlat => __Flat.Has;

        Writer INinoFormatter.WriteFlat(scoped NinoRun run, Writer writer) =>
            NinoFlatRuns.WriteRun<SeamMany, Nino_SeamProto_SeamManyFormatter, NinoSealedItem<__ManyLayout, __ManyBody>>(
                run, writer);

        Reader INinoFormatter.ReadFlat(scoped NinoRun run, Reader reader) =>
            NinoFlatRuns.ReadRun<SeamMany, Nino_SeamProto_SeamManyFormatter, NinoSealedItem<__ManyLayout, __ManyBody>>(
                run, reader);

        /// <summary>One SeamMany's body through the items (what a formatter without a generated body would call).</summary>
        internal static void FlatWriteBody(SeamMany value, ref Writer writer) =>
            NinoFlatRuns.WriteBody<__ManyBody>(value, ref writer);

        internal static void FlatReadBody(SeamMany value, ref Reader reader) =>
            NinoFlatRuns.ReadBody<__ManyBody>(value, ref reader);
    }
}
