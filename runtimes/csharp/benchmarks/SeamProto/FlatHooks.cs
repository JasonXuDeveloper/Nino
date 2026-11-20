// PROTOTYPE (#355, the generator seam): what the generator would emit for a type whose body is flat, written by hand.
// Each formatter gains HasFlat / WriteFlat / ReadFlat: a chain of items (blocks, bools, UTF-16 and UTF-8 strings) at
// the offsets measured once on this runtime, behind the object's header item (a presence byte for a sealed class, the
// type id for one that is not). Any check that fails (a field not where the block expects it, a value Untrusted mode
// checks, a runtime that keeps the method table elsewhere) leaves the type on its generated body. NINO_SEAM=0 turns
// the hooks off (the base of the comparison, the same build).
using System;
using System.Runtime.CompilerServices;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using Nino.SeamProto;
using __OneBody = Nino.Core.Internal.NinoFlatChain<Nino.Core.Internal.NinoBlockItem,
    Nino.Core.Internal.NinoStringItem<Nino.Core.Internal.NinoUtf16Item>>;
using __ManyBody = Nino.Core.Internal.NinoFlatChain<Nino.Core.Internal.NinoFlatChain<Nino.Core.Internal.NinoFlatChain<
    Nino.Core.Internal.NinoFlatChain<Nino.Core.Internal.NinoFlatChain<Nino.Core.Internal.NinoBlockItem,
            Nino.Core.Internal.NinoStringItem<Nino.Core.Internal.NinoUtf16Item>>,
        Nino.Core.Internal.NinoStringItem<Nino.Core.Internal.NinoUtf8Item>>, Nino.Core.Internal.NinoBlockItem>,
    Nino.Core.Internal.NinoBoolItem>, Nino.Core.Internal.NinoStringItem<Nino.Core.Internal.NinoUtf16Item>>;

namespace Nino.SeamProto
{
    /// <summary>The switch: NINO_SEAM=0 keeps every type on its generated body.</summary>
    internal static class Seam
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("NINO_SEAM") != "0";

        /// <summary>NINO_SEAM_FILL=0: reads construct each element through the item's constructor pointer instead
        /// of a chunk made ahead by the generated loop.</summary>
        public static readonly bool Fill = Environment.GetEnvironmentVariable("NINO_SEAM_FILL") != "0";
    }
}

namespace NinoGen
{
    internal unsafe partial struct Nino_SeamProto_SeamOneFormatter
    {
        private static class __Flat
        {
            public static readonly bool Has;
            public static readonly NinoTypedItem<__OneBody> Item;

            static __Flat()
            {
                var p = NinoFieldBlock.Probe<SeamOne>();
                if (!Seam.On || p == null)
                    return;
                // Stamp, Score, Id, Kind: one block where they lie as the wire lays them out.
                nint type = NinoFlatObjects.TypeHandleOf(typeof(SeamOne), p);
                if (type == 0 ||
                    !NinoFieldBlock.IsAt(in p.Stamp, in p.Stamp, 0, 8) ||
                    !NinoFieldBlock.IsAt(in p.Stamp, in p.Score, 8, 8) ||
                    !NinoFieldBlock.IsAt(in p.Stamp, in p.Id, 16, 4) ||
                    !NinoFieldBlock.IsAt(in p.Stamp, in p.Kind, 20, 1) ||
                    !NinoFieldBlock.ReadsAsIs<long>() || !NinoFieldBlock.ReadsAsIs<double>() ||
                    !NinoFieldBlock.ReadsAsIs<int>() || !NinoFieldBlock.ReadsAsIs<SeamKind>())
                    return;
                Item = new NinoTypedItem<__OneBody>(
                    new __OneBody(new NinoBlockItem(ObjectFields.OffsetOf(p, ref p.Stamp), 21),
                        new NinoStringItem<NinoUtf16Item>(ObjectFields.OffsetOf(p, ref p.Name))),
                    type, default(Nino_SeamProto_SeamOneFormatter).TypeId, &__New);
                Has = true;
            }

            private static object __New() => new SeamOne();
        }

        bool INinoFormatter.HasFlat => __Flat.Has;

        Writer INinoFormatter.WriteFlat(scoped NinoRun run, Writer writer) =>
            NinoFlatRuns.WriteRun<SeamOne, Nino_SeamProto_SeamOneFormatter, NinoTypedItem<__OneBody>>(run,
                __Flat.Item, writer);

        Reader INinoFormatter.ReadFlat(scoped NinoRun run, Reader reader) =>
            NinoFlatRuns.ReadRun<SeamOne, Nino_SeamProto_SeamOneFormatter, NinoTypedItem<__OneBody>>(run,
                __Flat.Item, reader, Seam.Fill ? &__Fill : null);

        // The construction item: new SeamOne() for a chunk of a run, inline.
        private static void __Fill(ref byte first, int count)
        {
            ref SeamOne element = ref Unsafe.As<byte, SeamOne>(ref first);
            for (int i = 0; i < count; i++)
                Unsafe.Add(ref element, i) = new SeamOne();
        }
    }

    internal unsafe partial struct Nino_SeamProto_SeamManyFormatter
    {
        private static class __Flat
        {
            public static readonly bool Has;
            public static readonly NinoSealedItem<__ManyBody> Item;

            static __Flat()
            {
                var p = NinoFieldBlock.Probe<SeamMany>();
                if (!Seam.On || p == null)
                    return;
                // Id; Name; Tag (UTF-8); At; Flag (a bool, checked); Text. Each raw run is a block of its own here:
                // CoreCLR puts Id between At and Flag.
                if (!NinoFieldBlock.IsAt(in p.Id, in p.Id, 0, 4) || !NinoFieldBlock.IsAt(in p.At, in p.At, 0, 8) ||
                    !NinoFieldBlock.ReadsAsIs<int>() || !NinoFieldBlock.ReadsAsIs<DateTime>())
                    return;
                Item = new NinoSealedItem<__ManyBody>(
                    new __ManyBody(new NinoFlatChain<NinoFlatChain<NinoFlatChain<NinoFlatChain<NinoBlockItem,
                            NinoStringItem<NinoUtf16Item>>, NinoStringItem<NinoUtf8Item>>, NinoBlockItem>, NinoBoolItem>(
                        new NinoFlatChain<NinoFlatChain<NinoFlatChain<NinoBlockItem, NinoStringItem<NinoUtf16Item>>,
                            NinoStringItem<NinoUtf8Item>>, NinoBlockItem>(
                            new NinoFlatChain<NinoFlatChain<NinoBlockItem, NinoStringItem<NinoUtf16Item>>,
                                NinoStringItem<NinoUtf8Item>>(
                                new NinoFlatChain<NinoBlockItem, NinoStringItem<NinoUtf16Item>>(
                                    new NinoBlockItem(ObjectFields.OffsetOf(p, ref p.Id), 4),
                                    new NinoStringItem<NinoUtf16Item>(ObjectFields.OffsetOf(p, ref p.Name))),
                                new NinoStringItem<NinoUtf8Item>(ObjectFields.OffsetOf(p, ref p.Tag))),
                            new NinoBlockItem(ObjectFields.OffsetOf(p, ref p.At), 8)),
                        new NinoBoolItem(ObjectFields.OffsetOf(p, ref p.Flag))),
                        new NinoStringItem<NinoUtf16Item>(ObjectFields.OffsetOf(p, ref p.Text))),
                    &__New);
                Has = true;
            }

            private static object __New() => new SeamMany();
        }

        bool INinoFormatter.HasFlat => __Flat.Has;

        Writer INinoFormatter.WriteFlat(scoped NinoRun run, Writer writer) =>
            NinoFlatRuns.WriteRun<SeamMany, Nino_SeamProto_SeamManyFormatter, NinoSealedItem<__ManyBody>>(run,
                __Flat.Item, writer);

        Reader INinoFormatter.ReadFlat(scoped NinoRun run, Reader reader) =>
            NinoFlatRuns.ReadRun<SeamMany, Nino_SeamProto_SeamManyFormatter, NinoSealedItem<__ManyBody>>(run,
                __Flat.Item, reader, Seam.Fill ? &__Fill : null);

        // The construction item: new SeamMany() for a chunk of a run, inline.
        private static void __Fill(ref byte first, int count)
        {
            ref SeamMany element = ref Unsafe.As<byte, SeamMany>(ref first);
            for (int i = 0; i < count; i++)
                Unsafe.Add(ref element, i) = new SeamMany();
        }
    }
}
