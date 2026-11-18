using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using SealedFormatter = NinoGen.Nino_UnitTests_KindSealedFormatter;
using ManagedFormatter = NinoGen.Nino_UnitTests_KindManagedFormatter;
using RootFormatter = NinoGen.Nino_UnitTests_DispatchRootFormatter;

#nullable disable

namespace Nino.UnitTests
{
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class KindSealed
    {
        public int A;
        public string B;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public struct KindManaged
    {
        public int A;
        public string B;
    }

    /// <summary>
    /// The one decision every collection reads (NinoKind, #355): the element kind of each representative type and
    /// formatter, in the order it is decided (raw, slot, block, else per value), with the stride the block kinds move,
    /// and what each formatter says of every value whatever its element (strings, the dispatch of a polymorphic base,
    /// its generated loops).
    /// </summary>
    [TestClass]
    public class ElementKindTests
    {
        [TestMethod]
        public void EachKindIsDecidedOnce()
        {
            // Raw: values whose memory is their wire layout on this runtime; NinoNullable<T> is a raw struct.
            Kind<int, UnmanagedFormatter<int>>(NinoElementKind.Raw, 4);
            Kind<NinoNullable<long>, UnmanagedFormatter<NinoNullable<long>>>(NinoElementKind.Raw, 16);
            // Slot: a raw T? as the element of a collection.
            Kind<int?, UnmanagedNullableFormatter<int>>(NinoElementKind.Slot, 8);
            // Block: a tuple of raw items (auto layout on CoreCLR: never raw), a user's block formatter,
            // DateTimeOffset (a block formatter), and composites of raw and block items (#348): a tuple and a pair
            // holding a DateTimeOffset, a pair holding a user's block, a tuple holding a tuple.
            Kind<(int, long), ValueTupleFormatter<int, long, UnmanagedFormatter<int>, UnmanagedFormatter<long>>>(
                NinoElementKind.Block, 12);
            Kind<Cell, CellBlockFormatter>(NinoElementKind.Block, 6);
            Kind<DateTimeOffset, DateTimeOffsetFormatter>(NinoElementKind.Block, 10);
            Kind<(int, DateTimeOffset), ValueTupleFormatter<int, DateTimeOffset, UnmanagedFormatter<int>,
                DateTimeOffsetFormatter>>(NinoElementKind.Block, 14);
            Kind<KeyValuePair<int, DateTimeOffset>, KeyValuePairFormatter<int, DateTimeOffset, UnmanagedFormatter<int>,
                DateTimeOffsetFormatter>>(NinoElementKind.Block, 14);
            Kind<KeyValuePair<int, Cell>, KeyValuePairFormatter<int, Cell, UnmanagedFormatter<int>,
                CellBlockFormatter>>(NinoElementKind.Block, 10);
            Kind<(Guid, (int, bool)), ValueTupleFormatter<Guid, (int, bool), UnmanagedFormatter<Guid>,
                ValueTupleFormatter<int, bool, UnmanagedFormatter<int>, UnmanagedFormatter<bool>>>>(
                NinoElementKind.Block, 21);
            // Per value: strings, classes (a polymorphic base and a sealed class), a managed struct, a user's
            // formatter, a T? of a type that is not raw.
            Kind<string, StringFormatter>(NinoElementKind.PerValue, -1);
            Kind<DispatchRoot, RootFormatter>(NinoElementKind.PerValue, -1);
            Kind<KindSealed, SealedFormatter>(NinoElementKind.PerValue, -1);
            Kind<KindManaged, ManagedFormatter>(NinoElementKind.PerValue, -1);
            Kind<Label, LabelFormatter>(NinoElementKind.PerValue, -1);
            Kind<Cell, CellPlainFormatter>(NinoElementKind.PerValue, -1);
            Kind<DateTimeOffset?, NullableFormatter<DateTimeOffset, DateTimeOffsetFormatter>>(
                NinoElementKind.PerValue, -1);
            // A composite with an item of no fixed size: a slot, or a string.
            Kind<(int, int?), ValueTupleFormatter<int, int?, UnmanagedFormatter<int>, UnmanagedNullableFormatter<int>>>(
                NinoElementKind.PerValue, -1);
            Kind<KeyValuePair<int, string>, KeyValuePairFormatter<int, string, UnmanagedFormatter<int>, StringFormatter>>(
                NinoElementKind.PerValue, -1);
        }

        [TestMethod]
        public void EachFormatterSaysWhatItDoes()
        {
            // Strings in the default encoding only (UTF-8 strings go per value).
            Assert.IsTrue(NinoKind<StringFormatter>.IsString);
            Assert.IsFalse(NinoKind<Utf8StringFormatter>.IsString);
            Assert.IsFalse(NinoKind<SealedFormatter>.IsString);
            // A polymorphic base's generated formatter dispatches and has the run loop; a sealed class's neither.
            Assert.AreNotEqual(0, NinoKind<RootFormatter>.Traits & NinoBodyTraits.Dispatches);
            Assert.IsTrue(NinoKind<RootFormatter>.Runs);
            Assert.AreEqual(0, NinoKind<SealedFormatter>.Traits & NinoBodyTraits.Dispatches);
            Assert.IsFalse(NinoKind<SealedFormatter>.Runs);
            // A sealed class of strings and raw members opens no class body: written through a local writer.
            Assert.AreEqual(0, NinoKind<SealedFormatter>.Traits & NinoBodyTraits.Nests);
            Assert.IsTrue(NinoKind<KindSealed, SealedFormatter>.FlatClass);
            Assert.IsTrue(NinoElements.WritesOnLocal<KindSealed, SealedFormatter>());
            // A formatter that says nothing: the defaults.
            Assert.AreEqual(NinoBodyTraits.Nests, NinoKind<LabelFormatter>.Traits);
        }

        private static void Kind<T, TFormatter>(NinoElementKind kind, int stride)
            where TFormatter : struct, INinoFormatter<T>
        {
            string name = typeof(T).Name + " / " + typeof(TFormatter).Name;
            Assert.AreEqual(kind, NinoKind.Of<T, TFormatter>(), name);
            // The field holds the same decision, references included.
            Assert.AreEqual(kind, NinoKind<T, TFormatter>.Kind, name);
            Assert.AreEqual(stride, NinoKind.Stride<T, TFormatter>(), name);
            Assert.AreEqual(Math.Max(stride, 1), NinoElements.MinWireSize<T, TFormatter>(), name);
            Assert.AreEqual(stride > 0, NinoElements.ReadOverwrites<T, TFormatter>(), name);
        }
    }
}
