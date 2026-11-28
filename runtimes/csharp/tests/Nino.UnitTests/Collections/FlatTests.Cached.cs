using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.FieldBlocks;
using Nino.UnitTests.Subset.Flat;

namespace Nino.UnitTests
{
    public partial class FlatTests
    {
        /// <summary>The run hooks <see cref="CachedFormatter{T}"/> reports for <typeparamref name="T"/>.</summary>
        private static (bool Flat, bool Runs) CachedHooks<T>()
        {
            INinoFormatter formatter = default(CachedFormatter<T>);
            return (formatter.HasFlat, formatter.HasRunLoop);
        }

        private static byte[] WriteWith<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            default(TFormatter).Serialize(value, ref writer);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static T ReadWith<T, TFormatter>(byte[] bytes, T value = default)
            where TFormatter : struct, INinoFormatter<T>
        {
            var reader = new Reader(bytes);
            default(TFormatter).Deserialize(ref reader, ref value);
            Assert.AreEqual(0, reader.Remaining);
            return value;
        }

        /// <summary>
        /// Runs of another assembly's types, reached through <see cref="CachedFormatter{T}"/> (the generated formatter
        /// is internal there), take the registered formatter's run hooks, one call per run (#440): the flat kernels of
        /// flat classes and structs (their drivers on the stack), a polymorphic base's run loop, the same bytes as the
        /// registered formatter writes element by element, read back new and into existing elements, every cut
        /// rejected; a formatter without hooks reports none.
        /// </summary>
        [TestMethod]
        public void CachedRunsTakeTheHooks()
        {
            Assert.AreEqual((true, false), CachedHooks<FlatMany>());
            Assert.AreEqual((true, false), CachedHooks<FlatLeaf>());
            Assert.AreEqual((true, false), CachedHooks<FlatPoint>());
            // A flat base with subtypes: its run loop (WriteRun) and its kernels (ReadFlat).
            Assert.AreEqual((true, true), CachedHooks<FlatOne>());
            Assert.AreEqual((false, true), CachedHooks<FieldBlockBase>());
            Assert.AreEqual((false, false), CachedHooks<FlatManyProps>());
            Assert.AreEqual((false, false), CachedHooks<FlatPointTwin>());
            Assert.AreEqual((false, false), CachedHooks<List<int>>());

            foreach (int count in Counts)
            foreach (bool mixed in new[] { false, true })
            {
                var manys = Manys(count, mixed);
                byte[] bytes = WriteWith<FlatMany[], ArrayFormatter<FlatMany, CachedFormatter<FlatMany>>>(manys);
                CollectionAssert.AreEqual(Each(manys), bytes, $"FlatMany[] {count} {mixed}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(manys.Select(Props).ToArray()), bytes);
                CollectionAssert.AreEqual(bytes,
                    WriteWith<List<FlatMany>, ListFormatter<List<FlatMany>, FlatMany, CachedFormatter<FlatMany>>>(manys.ToList()));
                var array = ReadWith<FlatMany[], ArrayFormatter<FlatMany, CachedFormatter<FlatMany>>>(bytes);
                var existing = Manys(count, !mixed, 9);
                var into = ReadWith<FlatMany[], ArrayFormatter<FlatMany, CachedFormatter<FlatMany>>>(bytes, existing);
                for (int i = 0; i < count; i++)
                {
                    Assert.IsTrue(Same(manys[i], array[i]), $"FlatMany element {i}");
                    Assert.IsTrue(Same(manys[i], into[i]), $"FlatMany existing element {i}");
                }

                var ones = Ones(count, mixed);
                byte[] oneBytes = WriteWith<FlatOne[], ArrayFormatter<FlatOne, CachedFormatter<FlatOne>>>(ones);
                CollectionAssert.AreEqual(Each(ones), oneBytes, $"FlatOne[] {count} {mixed}");
                var oneArray = ReadWith<FlatOne[], ArrayFormatter<FlatOne, CachedFormatter<FlatOne>>>(oneBytes);
                for (int i = 0; i < count; i++)
                    Assert.IsTrue(Same(ones[i], oneArray[i]), $"FlatOne element {i}");

                var points = Enumerable.Range(0, count).Select(i => new FlatPoint
                    { Id = i, Name = mixed && i % 3 == 0 ? null : "p" + i, Count = i % 2 == 0 ? null : i, Tag = "t" }).ToArray();
                byte[] pointBytes = WriteWith<FlatPoint[], ArrayFormatter<FlatPoint, CachedFormatter<FlatPoint>>>(points);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(points), pointBytes, $"FlatPoint[] {count} {mixed}");
                CollectionAssert.AreEqual(points,
                    ReadWith<FlatPoint[], ArrayFormatter<FlatPoint, CachedFormatter<FlatPoint>>>(pointBytes));

                for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 23))
                {
                    var prefix = bytes.AsSpan(0, cut).ToArray();
                    Assert.ThrowsExactly<NinoDataException>(
                        () => ReadWith<FlatMany[], ArrayFormatter<FlatMany, CachedFormatter<FlatMany>>>(prefix),
                        $"cut {cut} of {count}");
                }
            }

            // A polymorphic base that is not flat: its run loop, the bytes of the registered formatter.
            var bases = Enumerable.Range(0, 40).Select(static i =>
                i % 7 == 3 ? null : (FieldBlockBase)new FieldBlockDerived { Id = i, X = -i, Y = i / 2f }).ToArray();
            byte[] baseBytes = WriteWith<FieldBlockBase[], ArrayFormatter<FieldBlockBase, CachedFormatter<FieldBlockBase>>>(bases);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(bases), baseBytes);
            var readBases = ReadWith<FieldBlockBase[], ArrayFormatter<FieldBlockBase, CachedFormatter<FieldBlockBase>>>(baseBytes);
            for (int i = 0; i < bases.Length; i++)
                Assert.AreEqual(bases[i]?.Id, readBases[i]?.Id);

            // The kernels on the stack, as RunsGoThroughTheKernels sees them for the registered formatters.
            var (manyWrites, manyReads) = StacksWith<FlatMany, CachedFormatter<FlatMany>>(Manys(100, false));
            Assert.IsTrue(manyWrites[0].Contains("NinoFlat`2.WriteSealed") && manyWrites[1].Contains("NinoFlat`2.WriteSealed"), "FlatMany write");
            Assert.IsTrue(manyReads[0].Contains("NinoFlat`2.ReadSealed") && manyReads[1].Contains("NinoFlat`2.ReadSealed"), "FlatMany read");
            var leaves = Enumerable.Range(0, 100).Select(static i => new FlatLeaf { Id = i, Name = "leaf " + i }).ToArray();
            var (leafWrites, leafReads) = StacksWith<FlatLeaf, CachedFormatter<FlatLeaf>>(leaves);
            Assert.IsTrue(leafWrites[0].Contains("NinoFlat`2.WriteTyped"), "FlatLeaf write");
            Assert.IsTrue(leafReads[0].Contains("NinoFlat`2.ReadTyped"), "FlatLeaf read");
            var (pointWrites, pointReads) = StacksWith<FlatPoint, CachedFormatter<FlatPoint>>(
                Enumerable.Range(0, 50).Select(static i => new FlatPoint { Id = i, Name = "p" }).ToArray());
            Assert.IsTrue(pointWrites[0].Contains("NinoFlatValues`1.Write"), "FlatPoint write");
            Assert.IsTrue(pointReads[0].Contains("NinoFlatValues`1.Read"), "FlatPoint read");
            var (propWrites, propReads) = StacksWith<FlatManyProps, CachedFormatter<FlatManyProps>>(
                Manys(100, false).Select(Props).ToArray());
            Assert.IsFalse(propWrites[0].Contains("NinoFlat`2") || propReads[0].Contains("NinoFlat`2"));
            Assert.IsTrue(propWrites[0].Contains("Grow"), "the probe saw no growth");
        }
    }
}
