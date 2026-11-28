using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.UnitTests.Subset.Flat;

namespace Nino.UnitTests
{
    /// <summary>
    /// #444: blocks of flat class bodies that hold bools, raw structs and NinoNullable members (NinoBlockAt's TCheck):
    /// the bytes against twins written by generated bodies, the startup check holding where CoreCLR lays them out as
    /// predicted, reads into existing elements, every cut, and bools and presences the reads reject alike both ways.
    /// </summary>
    public partial class FlatTests
    {
        private static FlatBlocks[] Blocks(int count, int seed = 21)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, count).Select(i => i % 13 == 6 ? null : new FlatBlocks
            {
                Stamp = random.Next() * 31L, Id = random.Next(), Level = (short)random.Next(short.MinValue, short.MaxValue),
                Flag = i % 2 == 0, Done = i % 3 == 1,
                At = new DateTime(2021, 5, 6, 0, 0, 0, (DateTimeKind)(i % 3)).AddSeconds(random.Next()),
                Count = i % 4 == 0 ? default : new NinoNullable<int>(-i),
                Name = i % 5 == 2 ? null : Text(random, 0, 20, i % 2 == 1),
                Big = i % 3 == 0 ? default : new NinoNullable<long>(random.Next() * 7L),
                Key = new Guid(random.Next(), (short)i, (short)-i, 1, 2, 3, 4, 5, 6, 7, (byte)i),
                Price = random.Next() / 100m,
                Padded = new FlatPadded { A = (byte)i, B = random.Next() },
            }).ToArray();
        }

        private static FlatBlocksTwin Twin(FlatBlocks v) => v == null ? null : new FlatBlocksTwin
        {
            Stamp = v.Stamp, Id = v.Id, Level = v.Level, Flag = v.Flag, Done = v.Done, At = v.At, Count = v.Count,
            Name = v.Name, Big = v.Big, Key = v.Key, Price = v.Price, Padded = v.Padded,
        };

        private static FlatFlags[] Flags(int count) => Enumerable.Range(0, count)
            .Select(i => i % 11 == 3 ? null : new FlatFlags { Id = i * 5, A = i % 2 == 1, B = i % 3 == 0, Name = "f" + i })
            .ToArray();

        private static FlatFlagsTwin Twin(FlatFlags v) => v == null ? null : new FlatFlagsTwin
            { Id = v.Id, A = v.A, B = v.B, Name = v.Name };

        [TestMethod]
        public void BlocksHoldBoolsStructsAndNinoNullables()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatBlocks>()));
            Assert.IsTrue(HasFlat(Formatter<FlatFlags>()));
            Assert.IsFalse(HasFlat(Formatter<FlatBlocksTwin>()));
            // The generator predicted the blocks as 64-bit CoreCLR lays the fields out (others take the chain of fields,
            // the same bytes), and a read copies them where Untrusted mode checks none of their values.
            if (Environment.Is64BitProcess && Type.GetType("Mono.Runtime") == null)
            {
                Assert.IsTrue(Checked<FlatBlocks>("Write"));
                Assert.IsTrue(Checked<FlatFlags>("Write"));
            }

            Assert.AreEqual(Checked<FlatBlocks>("Write") && !NinoSettings.Untrusted, Checked<FlatBlocks>("Read"));
            Assert.AreEqual(Checked<FlatFlags>("Write"), Checked<FlatFlags>("Read"));

            foreach (int count in RunCounts)
            {
                var blocks = Blocks(count);
                byte[] bytes = NinoSerializer.Serialize(blocks);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(blocks.Select(Twin).ToArray()), bytes, $"blocks {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(blocks.ToList()), $"list {count}");
                CollectionAssert.AreEqual(bytes, Each(blocks), $"each {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatBlocks[]>(bytes)));
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<List<FlatBlocks>>(bytes)));
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(
                    NinoDeserializer.Deserialize<FlatBlocksTwin[]>(bytes)), $"twin read {count}");
                // Into existing elements (other values): every member replaced, the instances kept.
                var existing = Blocks(count, seed: 3);
                var kept = (FlatBlocks[])existing.Clone();
                NinoDeserializer.Deserialize(bytes, ref existing);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(existing), $"into existing {count}");
                for (int i = 0; i < count; i++)
                    if (blocks[i] != null && kept[i] != null)
                        Assert.AreSame(kept[i], existing[i], $"element {i} kept");

                var flags = Flags(count);
                byte[] flagBytes = NinoSerializer.Serialize(flags);
                CollectionAssert.AreEqual(Each(flags), flagBytes, $"flags {count}");
                CollectionAssert.AreEqual(flagBytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatFlags[]>(flagBytes)));
            }

            // One body, written and read through the generated members.
            foreach (var value in Blocks(40).Where(static v => v != null))
            {
                CollectionAssert.AreEqual(NinoSerializer.Serialize(Twin(value)), NinoSerializer.Serialize(value));
                CollectionAssert.AreEqual(NinoSerializer.Serialize(value),
                    NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatBlocks>(NinoSerializer.Serialize(value))));
            }

            foreach (var value in Flags(20).Where(static v => v != null))
                CollectionAssert.AreEqual(NinoSerializer.Serialize(Twin(value)).Skip(4).ToArray(),
                    NinoSerializer.Serialize(value).Skip(4).ToArray());

            // A padded struct is written with its padding cleared, whatever its memory holds.
            var padded = Blocks(20).Where(static v => v != null).ToArray();
            foreach (var value in padded)
                Unsafe.As<FlatPadded, long>(ref value.Padded) |= 0xFFFFFF00L;
            CollectionAssert.AreEqual(NinoSerializer.Serialize(padded.Select(Twin).ToArray()), NinoSerializer.Serialize(padded));

            var (writes, reads) = Stacks(Blocks(100).Where(static v => v != null).ToArray());
            Assert.IsTrue(writes[0].Contains("NinoFlat`2.WriteSealed"), "FlatBlocks[] write");
            Assert.IsTrue(writes[1].Contains("NinoFlat`2.WriteSealed"), "List<FlatBlocks> write");
            Assert.IsTrue(reads[0].Contains("NinoFlat`2.ReadSealed"), "FlatBlocks[] read");
            Assert.IsTrue(reads[1].Contains("NinoFlat`2.ReadSealed"), "List<FlatBlocks> read");
            var (flagWrites, flagReads) = Stacks(Flags(100).Where(static v => v != null).ToArray());
            Assert.IsTrue(flagWrites[0].Contains("NinoFlat`2.WriteTyped"), "FlatFlags[] write");
            Assert.IsTrue(flagReads[0].Contains("NinoFlat`2.ReadTyped"), "FlatFlags[] read");
        }

        /// <summary>
        /// Hostile blocks: every cut of a run, and each bool in a block set to every value but 0 and 1, rejected by
        /// the flat read (the block's check in its bytes) and by the generated one alike, the elements a rejected read
        /// reached left as they were.
        /// </summary>
        [TestMethod]
        public void HostileBlocksAreRejectedBothWays()
        {
            var blocks = Blocks(NinoFlatRuns.MinRun + 4).Select(static v => v ?? new FlatBlocks()).ToArray();
            byte[] bytes = NinoSerializer.Serialize(blocks);
            for (int cut = 0; cut < bytes.Length; cut++)
            {
                var prefix = bytes.AsSpan(0, cut).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatBlocks[]>(prefix), $"cut {cut}");
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatBlocksTwin[]>(prefix), $"twin cut {cut}");
            }

            // The last element's bools: after the count, every element but the last, its presence byte, Stamp (8), Id
            // (4) and Level (2).
            int last = bytes.Length - NinoSerializer.Serialize(blocks[^1]).Length;
            foreach (int at in new[] { last + 1 + 14, last + 1 + 15 })
            foreach (byte flag in new byte[] { 2, 3, 0x80, 0xFF })
            {
                byte[] bad = (byte[])bytes.Clone();
                bad[at] = flag;
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatBlocks[]>(bad), $"{at} {flag}");
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<List<FlatBlocks>>(bad));
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatBlocksTwin[]>(bad));
                // Into existing elements: the bad element keeps its values (nothing copied before the check).
                var existing = Blocks(blocks.Length, seed: 8).Select(static v => v ?? new FlatBlocks()).ToArray();
                byte[] before = NinoSerializer.Serialize(existing[^1]);
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bad, ref existing));
                CollectionAssert.AreEqual(before, NinoSerializer.Serialize(existing[^1]), $"kept {at} {flag}");
            }

            var flags = Flags(NinoFlatRuns.MinRun + 4).Select(static v => v ?? new FlatFlags()).ToArray();
            byte[] flagBytes = NinoSerializer.Serialize(flags);
            int lastFlags = flagBytes.Length - NinoSerializer.Serialize(flags[^1]).Length;
            foreach (int at in new[] { lastFlags + 4 + 4, lastFlags + 4 + 5 })
            {
                byte[] bad = (byte[])flagBytes.Clone();
                bad[at] = 2;
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatFlags[]>(bad), $"flags {at}");
            }
        }
    }
}
