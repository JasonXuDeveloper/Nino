using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using Nino.UnitTests.Subset.Flat;

namespace Nino.UnitTests
{
    /// <summary>
    /// The flat kernels (#355) against the generated body: the bytes of runs, bodies and members written both ways,
    /// read back both ways, reads into existing instances, subtypes in runs, and hostile data.
    /// </summary>
    [TestClass]
    public partial class FlatTests
    {
        private const string Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ";

        private static string Text(Random random, int min, int max, bool wide)
        {
            var chars = new char[random.Next(min, max + 1)];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = wide && i % 7 == 3 ? (char)(0x4E00 + random.Next(500)) : Chars[random.Next(Chars.Length)];
            return new string(chars);
        }

        private static FlatOne[] Ones(int count, bool mixed, int seed = 1)
        {
            var random = new Random(seed);
            var values = new FlatOne[count];
            for (int i = 0; i < count; i++)
            {
                if (mixed && i % 11 == 5)
                    continue;
                var value = mixed && i % 13 == 7 ? new FlatOneDerived { Extra = i } : new FlatOne();
                value.Stamp = random.Next() * 1000L;
                value.Score = random.NextDouble();
                value.Id = random.Next();
                value.Kind = (FlatKind)(i % 3);
                value.Name = mixed && i % 17 == 3 ? null : mixed && i % 19 == 4 ? "" : Text(random, 8, 24, mixed);
                values[i] = value;
            }

            return values;
        }

        private static FlatMany[] Manys(int count, bool mixed, int seed = 2)
        {
            var random = new Random(seed);
            var values = new FlatMany[count];
            for (int i = 0; i < count; i++)
            {
                if (mixed && i % 11 == 5)
                    continue;
                values[i] = new FlatMany
                {
                    Id = random.Next(),
                    Name = mixed && i % 17 == 3 ? null : Text(random, 6, 16, mixed),
                    Tag = mixed && i % 19 == 4 ? "" : Text(random, 4, 10, mixed),
                    At = new DateTime(2020, 1, 1, 0, 0, 0, (DateTimeKind)(i % 3)).AddSeconds(random.Next()),
                    Flag = random.Next(2) == 0,
                    Text = Text(random, 20, 60, mixed),
                    Price = random.Next() / 100m,
                };
            }

            return values;
        }

        private static FlatManyProps Props(FlatMany v) => v == null ? null : new FlatManyProps
            { Id = v.Id, Name = v.Name, Tag = v.Tag, At = v.At, Flag = v.Flag, Text = v.Text, Price = v.Price };

        private static FlatOneProps Props(FlatOne v) => v == null ? null : new FlatOneProps
            { Stamp = v.Stamp, Score = v.Score, Id = v.Id, Kind = v.Kind, Name = v.Name };

        private static bool Same(FlatOne a, FlatOne b) =>
            a == null ? b == null :
            b != null && a.GetType() == b.GetType() && a.Stamp == b.Stamp && a.Score.Equals(b.Score) && a.Id == b.Id &&
            a.Kind == b.Kind && a.Name == b.Name && (a is not FlatOneDerived d || ((FlatOneDerived)b).Extra == d.Extra);

        private static bool Same(FlatMany a, FlatMany b) =>
            a == null ? b == null :
            b != null && a.Id == b.Id && a.Name == b.Name && a.Tag == b.Tag && a.At == b.At &&
            a.At.Kind == b.At.Kind && a.Flag == b.Flag && a.Text == b.Text && a.Price == b.Price;

        private static readonly int[] Counts = { 1, 2, 7, 100, 1000 };

        /// <summary>The generated formatter of <typeparamref name="T"/> (internal to the Subset assembly), or its twin.</summary>
        private static Type Formatter<T>(string twin = null)
        {
            var type = typeof(T).Assembly.GetType($"NinoGen.Nino_UnitTests_Subset_Flat_{typeof(T).Name}Formatter", true);
            return twin == null ? type : type.GetNestedType(twin, BindingFlags.NonPublic | BindingFlags.Public);
        }

        private static bool HasFlat(Type formatter) => ((INinoFormatter)Activator.CreateInstance(formatter)).HasFlat;

        /// <summary>The bytes of a run element by element through the formatter (the loop every other class takes).</summary>
        private static byte[] Each<T>(T[] values) =>
            (byte[])typeof(FlatTests).GetMethod(nameof(EachWith), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(T), Formatter<T>()).Invoke(null, new object[] { values });

        private static byte[] EachWith<T, TFormatter>(T[] values) where TFormatter : struct, INinoFormatter<T>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer = NinoRuns.WriteEach<T, TFormatter>(NinoRun.Of<T>(values), values.Length, writer);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        [TestMethod]
        public void FlatTypesTakeTheKernels()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatOne>()));
            Assert.IsTrue(HasFlat(Formatter<FlatMany>()));
            Assert.IsTrue(HasFlat(Formatter<FlatMany>("__Top")));
            Assert.IsFalse(HasFlat(Formatter<FlatOneProps>()));
            Assert.IsFalse(HasFlat(Formatter<FlatManyProps>()));
            Assert.IsTrue(HasFlat(Formatter<FlatOneDerived>()));
            Assert.IsTrue(HasFlat(Formatter<FlatLeaf>()));
            // The generator predicted FlatOne's block as CoreCLR lays it out (64-bit; others take the chain of fields).
            if (Environment.Is64BitProcess && Type.GetType("Mono.Runtime") == null)
                Assert.IsTrue(Checked<FlatOne>("Write"));
            Assert.AreEqual(Checked<FlatMany>("Write") && !NinoSettings.Untrusted, Checked<FlatMany>("Read"));
        }

        /// <summary>
        /// The runs of flat classes go through the kernels, seen on the stack (their drivers are never inlined), which
        /// costs the kernels nothing: equal bytes cannot tell, a run written element by element writes them too (a
        /// branch placed after the one for formatters that write on a local writer once sent sealed flat classes
        /// there). Writes are seen from the pool of a buffer that starts with one byte, reads from the exception of a
        /// cut run. Through the generated formatter; runs through CachedFormatter, as this assembly's generated code
        /// reaches the Subset's types, in FlatTests.Cached.cs.
        /// </summary>
        [TestMethod]
        public void RunsGoThroughTheKernels()
        {
            const string sealedWrite = "NinoFlat`2.WriteSealed", typedWrite = "NinoFlat`2.WriteTyped";
            const string sealedRead = "NinoFlat`2.ReadSealed", typedRead = "NinoFlat`2.ReadTyped";
            var manys = Manys(100, false);
            var leaves = Enumerable.Range(0, 100).Select(static i => new FlatLeaf { Id = i, Name = "leaf " + i }).ToArray();
            var (manyWrites, manyRead) = Stacks(manys);
            var (leafWrites, leafRead) = Stacks(leaves);
            var (_, oneRead) = Stacks(Ones(100, false));
            Assert.IsTrue(manyWrites[0].Contains(sealedWrite), "FlatMany[] write");
            Assert.IsTrue(manyWrites[1].Contains(sealedWrite), "List<FlatMany> write");
            Assert.IsTrue(leafWrites[0].Contains(typedWrite), "FlatLeaf[] write");
            Assert.IsTrue(leafWrites[1].Contains(typedWrite), "List<FlatLeaf> write");
            Assert.IsTrue(manyRead[0].Contains(sealedRead), "FlatMany[] read");
            Assert.IsTrue(manyRead[1].Contains(sealedRead), "List<FlatMany> read");
            Assert.IsTrue(leafRead[0].Contains(typedRead), "FlatLeaf[] read");
            Assert.IsTrue(leafRead[1].Contains(typedRead), "List<FlatLeaf> read");
            // A base with subtypes: written through its dispatch loop (INinoFormatter.WriteRun), read by the kernels.
            Assert.IsTrue(oneRead[0].Contains(typedRead), "FlatOne[] read");
            Assert.IsTrue(oneRead[1].Contains(typedRead), "List<FlatOne> read");

            // The probes see a run element by element too: a class with a callback is not flat.
            var (propWrites, propRead) = Stacks(manys.Select(Props).ToArray());
            Assert.IsFalse(propWrites[0].Contains("NinoFlat`2") || propRead[0].Contains("NinoFlat`2"));
            Assert.IsTrue(propWrites[0].Contains("Grow"), "the probe saw no growth");
        }

        private static FlatSlots[] Slots(int count, int seed = 5)
        {
            var random = new Random(seed);
            var values = new FlatSlots[count];
            for (int i = 0; i < count; i++)
            {
                if (i % 23 == 9)
                    continue;
                values[i] = new FlatSlots
                {
                    Id = random.Next(),
                    Count = i % 3 == 0 ? null : random.Next(),
                    Name = i % 7 == 2 ? null : Text(random, 0, 40, i % 5 == 1),
                    Big = i % 4 == 1 ? null : random.Next() * 7L,
                    When = i % 6 == 0 ? null : new DateTime(2020, 1, 1).AddMinutes(i),
                    Flag = i % 2 == 0,
                    Score = random.NextDouble(),
                    Tag = i % 9 == 4 ? "" : Text(random, 1, 12, true),
                    Price = i * 1.25m,
                };
            }

            return values;
        }

        private static FlatSlotsTwin Twin(FlatSlots v) => v == null ? null : new FlatSlotsTwin
        {
            Id = v.Id, Count = v.Count, Name = v.Name, Big = v.Big, When = v.When, Flag = v.Flag, Score = v.Score,
            Tag = v.Tag, Price = v.Price,
        };

        private static FlatBareTwin Twin(FlatBare v) => v == null ? null : new FlatBareTwin { A = v.A, B = v.B, C = v.C, D = v.D };

        /// <summary>
        /// Step B of #355: properties (through their accessors), raw T?s (slot items) and bodies without a string go
        /// through the one item chain, with the bytes of twins written by generated bodies, read back new and into
        /// existing elements, the runs through the kernels (their drivers on the stack), and hostile data rejected.
        /// </summary>
        [TestMethod]
        public void PropertiesAndSlotsTakeTheChain()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatSlots>()));
            Assert.IsTrue(HasFlat(Formatter<FlatBare>()));
            Assert.IsFalse(HasFlat(Formatter<FlatSlotsTwin>()));
            Assert.IsFalse(HasFlat(Formatter<FlatBareTwin>()));

            foreach (int count in new[] { 1, 15, 16, 17, 100, 1000 })
            {
                var slots = Slots(count);
                byte[] bytes = NinoSerializer.Serialize(slots);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(slots.Select(Twin).ToArray()), bytes, $"slots {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(slots.ToList()), $"list {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatSlots[]>(bytes)));
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<List<FlatSlots>>(bytes)));
                // Into existing elements (other values, the same count): every member replaced.
                var existing = Slots(count, seed: 9);
                NinoDeserializer.Deserialize(bytes, ref existing);
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(existing), $"into existing {count}");
                for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 37))
                {
                    var prefix = bytes.AsSpan(0, cut).ToArray();
                    Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatSlots[]>(prefix),
                        $"cut {cut} of {count}");
                }

                var bare = Enumerable.Range(0, count).Select(i => i % 13 == 5 ? null : new FlatBare
                {
                    A = i, B = i % 2 == 0 ? null : (short)-i, C = i * 3L, D = i % 3 == 0 ? null : (byte)i,
                }).ToArray();
                byte[] bareBytes = NinoSerializer.Serialize(bare);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(bare.Select(Twin).ToArray()), bareBytes, $"bare {count}");
                CollectionAssert.AreEqual(bareBytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatBare[]>(bareBytes)));
            }

            // A presence byte of a T? other than 0 or 1, in a run: invalid data.
            var one = new[] { new FlatBare { A = 1, B = 2, C = 3, D = 4 } };
            byte[] run = NinoSerializer.Serialize(Enumerable.Repeat(one[0], 20).ToArray());
            run[4 + 1 + 4] = 2; // count, presence, A, then B's presence
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatBare[]>(run));

            var (slotWrites, slotReads) = Stacks(Slots(100).Where(static v => v != null).ToArray());
            Assert.IsTrue(slotWrites[0].Contains("NinoFlat`2.WriteSealed"), "FlatSlots[] write");
            Assert.IsTrue(slotReads[0].Contains("NinoFlat`2.ReadSealed"), "FlatSlots[] read");
            var (bareWrites, bareReads) = Stacks(Enumerable.Range(0, 100).Select(static i => new FlatBare { A = i }).ToArray());
            Assert.IsTrue(bareWrites[0].Contains("NinoFlat`2.WriteSealed"), "FlatBare[] write");
            Assert.IsTrue(bareReads[0].Contains("NinoFlat`2.ReadSealed"), "FlatBare[] read");
        }

        /// <summary>
        /// A struct whose body is flat (#355 step B): its runs go through the item chain in place, with the bytes of a
        /// twin written by the generated body, read back new and into existing elements, every cut rejected.
        /// </summary>
        [TestMethod]
        public void StructsTakeTheChain()
        {
            Assert.IsTrue(HasFlat(Formatter<FlatPoint>()));
            Assert.IsFalse(HasFlat(Formatter<FlatPointTwin>()));
            var random = new Random(7);
            foreach (int count in new[] { 1, 2, 17, 300 })
            {
                var points = Enumerable.Range(0, count).Select(i => new FlatPoint
                {
                    Id = i, Name = i % 5 == 3 ? null : Text(random, 0, 30, i % 4 == 1), X = random.NextDouble(),
                    Flag = i % 2 == 1, Count = i % 3 == 0 ? null : -i, At = new DateTime(2021, 2, 3).AddSeconds(i),
                    Tag = i % 7 == 6 ? "" : Text(random, 1, 9, true),
                }).ToArray();
                var twins = points.Select(static p => new FlatPointTwin
                {
                    Id = p.Id, Name = p.Name, X = p.X, Flag = p.Flag, Count = p.Count, At = p.At, Tag = p.Tag,
                }).ToArray();
                byte[] bytes = NinoSerializer.Serialize(points);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(twins), bytes, $"points {count}");
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(points.ToList()));
                CollectionAssert.AreEqual(points, NinoDeserializer.Deserialize<FlatPoint[]>(bytes));
                CollectionAssert.AreEqual(points, NinoDeserializer.Deserialize<List<FlatPoint>>(bytes));
                var existing = points.Select(static p => p with { Name = p.Name + "?", Count = 5 }).ToArray();
                NinoDeserializer.Deserialize(bytes, ref existing);
                CollectionAssert.AreEqual(points, existing, $"into existing {count}");
                for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 31))
                {
                    var prefix = bytes.AsSpan(0, cut).ToArray();
                    Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatPoint[]>(prefix),
                        $"cut {cut} of {count}");
                }
            }

            var (writes, reads) = Stacks(Enumerable.Range(0, 50).Select(static i => new FlatPoint { Id = i, Name = "p" }).ToArray());
            Assert.IsTrue(writes[0].Contains("NinoFlatValues`1.Write"), "FlatPoint[] write");
            Assert.IsTrue(reads[0].Contains("NinoFlatValues`1.Read"), "FlatPoint[] read");
        }

        /// <summary>
        /// Generic flat types (#355 step B): the item chain where every type argument is a value type, element by
        /// element where one is a class, the same bytes either way as a twin written by the generated body.
        /// </summary>
        [TestMethod]
        public void GenericTypesTakeTheChainOverValueTypes()
        {
            var ints = Enumerable.Range(0, 40).Select(static i => new FlatBox<int> { Id = i, Name = "b" + i, Big = i % 2 == 0 ? null : i }).ToArray();
            var strings = ints.Select(static b => new FlatBox<string> { Id = b.Id, Name = b.Name, Big = b.Big }).ToArray();
            var twins = ints.Select(static b => new FlatBoxTwin<int> { Id = b.Id, Name = b.Name, Big = b.Big }).ToArray();
            byte[] bytes = NinoSerializer.Serialize(ints);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(twins), bytes);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(strings));
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatBox<int>[]>(bytes)));
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<FlatBox<string>[]>(bytes)));

            var (intWrites, intReads) = GenericStacks<FlatBox<int>>(ints, "Nino_UnitTests_Subset_Flat_FlatBox_1Formatter", typeof(int));
            Assert.IsTrue(intWrites.Contains("NinoFlat`2.WriteSealed") && intReads.Contains("NinoFlat`2.ReadSealed"));
            var (stringWrites, stringReads) = GenericStacks<FlatBox<string>>(strings, "Nino_UnitTests_Subset_Flat_FlatBox_1Formatter", typeof(string));
            Assert.IsFalse(stringWrites.Contains("NinoFlat`2") || stringReads.Contains("NinoFlat`2"));

            var entries = Enumerable.Range(0, 300).Select(static i => new FlatEntry<long> { Key = "entry key " + i, Value = i }).ToArray();
            byte[] entryBytes = NinoSerializer.Serialize(entries);
            CollectionAssert.AreEqual(entries, NinoDeserializer.Deserialize<FlatEntry<long>[]>(entryBytes));
            CollectionAssert.AreEqual(entryBytes, NinoSerializer.Serialize(entries.Select(static e => new FlatEntry<string> { Key = e.Key, Value = e.Value }).ToArray()));
            var (entryWrites, entryReads) = GenericStacks<FlatEntry<long>>(entries, "Nino_UnitTests_Subset_Flat_FlatEntry_1Formatter", typeof(long));
            Assert.IsTrue(entryWrites.Contains("NinoFlatValues`1.Write"), entryWrites);
            Assert.IsTrue(entryReads.Contains("NinoFlatValues`1.Read"), entryReads);
        }

        /// <summary><see cref="Stacks{T}"/>' array stacks of a generic type's formatter, closed over
        /// <paramref name="argument"/> and its formatter.</summary>
        private static (string Writes, string Reads) GenericStacks<T>(T[] values, string formatter, Type argument)
        {
            var open = typeof(T).Assembly.GetType("NinoGen." + formatter + "`2", true)!;
            var argumentFormatter = argument == typeof(string) ? typeof(StringFormatter) : typeof(UnmanagedFormatter<>).MakeGenericType(argument);
            var closed = open.MakeGenericType(argument, argumentFormatter);
            var (writes, reads) = ((string[], string[]))typeof(FlatTests).GetMethod(nameof(StacksWith), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(T), closed).Invoke(null, new object[] { values })!;
            return (writes[0], reads[0]);
        }

        /// <summary>
        /// The stacks of an array and a list of <paramref name="values"/> through the generated formatter: of every
        /// growth of the buffer they are written to, and of the exception reading their bytes without the last three.
        /// </summary>
        private static (string[] Writes, string[] Reads) Stacks<T>(T[] values) =>
            ((string[], string[]))typeof(FlatTests).GetMethod(nameof(StacksWith), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(T), Formatter<T>()).Invoke(null, new object[] { values })!;

        private static (string[] Writes, string[] Reads) StacksWith<T, TFormatter>(T[] values)
            where TFormatter : struct, INinoFormatter<T>
        {
            var list = values.ToList();
            return (new[] { WriteStacks<T[], ArrayFormatter<T, TFormatter>>(values),
                    WriteStacks<List<T>, ListFormatter<List<T>, T, TFormatter>>(list) },
                new[] { CutReadStack<T[], ArrayFormatter<T, TFormatter>>(values),
                    CutReadStack<List<T>, ListFormatter<List<T>, T, TFormatter>>(list) });
        }

        private static string WriteStacks<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            var pool = new StackPool();
            using var buffer = new NinoArrayBufferWriter(1, pool);
            var writer = new Writer(buffer);
            default(TFormatter).Serialize(value, ref writer);
            writer.Flush();
            return pool.Stacks.ToString();
        }

        private static string CutReadStack<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            default(TFormatter).Serialize(value, ref writer);
            writer.Flush();
            byte[] bytes = buffer.WrittenSpan.ToArray();
            var thrown = Assert.ThrowsExactly<NinoDataException>(() =>
            {
                var reader = new Reader(bytes.AsSpan(0, bytes.Length - 3));
                T read = default;
                default(TFormatter).Deserialize(ref reader, ref read);
            });
            return thrown.StackTrace;
        }

        /// <summary>A pool that records the stack of each array it hands out (a writer's buffer growing).</summary>
        private sealed class StackPool : ArrayPool<byte>
        {
            public readonly System.Text.StringBuilder Stacks = new();

            public override byte[] Rent(int minimumLength)
            {
                Stacks.AppendLine(new StackTrace().ToString());
                return new byte[minimumLength];
            }

            public override void Return(byte[] array, bool clearArray = false)
            {
            }
        }

        /// <summary>A flag of the startup check (__Check) of <typeparamref name="T"/>'s formatter.</summary>
        private static bool Checked<T>(string flag) =>
            (bool)Formatter<T>("__Check").GetField(flag, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        [TestMethod]
        public void SealedRunsWriteTheGeneratedBodysBytes()
        {
            foreach (int count in Counts)
            foreach (bool mixed in new[] { false, true })
            {
                var values = Manys(count, mixed);
                var props = values.Select(Props).ToArray();
                byte[] expected = NinoSerializer.Serialize(props);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values), $"array {count} {mixed}");
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()), $"list {count} {mixed}");
                CollectionAssert.AreEqual(expected, Each(values), $"each {count} {mixed}");
                CollectionAssert.AreEqual(NinoSerializer.Serialize(props.ToList()),
                    NinoSerializer.Serialize(values.ToList()));

                var array = NinoDeserializer.Deserialize<FlatMany[]>(expected);
                var list = NinoDeserializer.Deserialize<List<FlatMany>>(expected);
                for (int i = 0; i < count; i++)
                {
                    Assert.IsTrue(Same(values[i], array[i]), $"array element {i}");
                    Assert.IsTrue(Same(values[i], list[i]), $"list element {i}");
                }
            }
        }

        [TestMethod]
        public void TypedRunsWriteTheGeneratedBodysBytes()
        {
            foreach (int count in Counts)
            foreach (bool mixed in new[] { false, true })
            {
                var values = Ones(count, mixed);
                byte[] each = Each(values);
                CollectionAssert.AreEqual(each, NinoSerializer.Serialize(values), $"array {count} {mixed}");
                CollectionAssert.AreEqual(each, NinoSerializer.Serialize(values.ToList()), $"list {count} {mixed}");
                var array = NinoDeserializer.Deserialize<FlatOne[]>(each);
                var list = NinoDeserializer.Deserialize<List<FlatOne>>(each);
                for (int i = 0; i < count; i++)
                {
                    Assert.IsTrue(Same(values[i], array[i]), $"array element {i}");
                    Assert.IsTrue(Same(values[i], list[i]), $"list element {i}");
                }

                // Each body against the generated body of the same members (the type ids differ).
                foreach (var value in values.Where(v => v != null && v.GetType() == typeof(FlatOne)))
                    CollectionAssert.AreEqual(NinoSerializer.Serialize(Props(value)).Skip(4).ToArray(),
                        NinoSerializer.Serialize(value).Skip(4).ToArray());
            }
        }

        [TestMethod]
        public void BodiesAndMembersWriteTheGeneratedBodysBytes()
        {
            foreach (var value in Manys(200, true))
            {
                CollectionAssert.AreEqual(NinoSerializer.Serialize(Props(value)), NinoSerializer.Serialize(value));
                var read = NinoDeserializer.Deserialize<FlatMany>(NinoSerializer.Serialize(value));
                Assert.IsTrue(Same(value, read));
            }

            var holder = new FlatHolder { One = Manys(1, false)[0], Many = Manys(9, true).ToList(), Ones = Ones(9, true) };
            var back = NinoDeserializer.Deserialize<FlatHolder>(NinoSerializer.Serialize(holder));
            Assert.IsTrue(Same(holder.One, back.One));
            Assert.AreEqual(holder.Many.Count, back.Many.Count);
            for (int i = 0; i < holder.Many.Count; i++)
                Assert.IsTrue(Same(holder.Many[i], back.Many[i]));
            for (int i = 0; i < holder.Ones.Length; i++)
                Assert.IsTrue(Same(holder.Ones[i], back.Ones[i]));
        }

        [TestMethod]
        public void SetsAndDictionariesOfFlatClasses()
        {
            // Free entries (a removed element): the same slots in both sets, so the same order.
            var values = Manys(20, false);
            var set = new HashSet<FlatMany>(values);
            var props = new HashSet<FlatManyProps>(values.Select(Props));
            set.Remove(values[3]);
            props.Remove(props.First(p => p.Id == values[3].Id));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(props), NinoSerializer.Serialize(set));
            Assert.AreEqual(19, NinoDeserializer.Deserialize<HashSet<FlatMany>>(NinoSerializer.Serialize(set)).Count);

            var map = values.Select((v, i) => (v, i)).ToDictionary(x => x.i, x => x.v);
            var mapProps = values.Select((v, i) => (v, i)).ToDictionary(x => x.i, x => Props(x.v));
            CollectionAssert.AreEqual(NinoSerializer.Serialize(mapProps), NinoSerializer.Serialize(map));
            var mapBack = NinoDeserializer.Deserialize<Dictionary<int, FlatMany>>(NinoSerializer.Serialize(map));
            for (int i = 0; i < values.Length; i++)
                Assert.IsTrue(Same(values[i], mapBack[i]));
        }

        [TestMethod]
        public void ReadsIntoExistingElementsKeepTheirInstances()
        {
            var values = Ones(100, true);
            byte[] data = NinoSerializer.Serialize(values);
            var existing = NinoDeserializer.Deserialize<FlatOne[]>(data);
            var kept = (FlatOne[])existing.Clone();
            NinoDeserializer.Deserialize(data, ref existing);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.IsTrue(Same(values[i], existing[i]), $"element {i}");
                if (kept[i] != null)
                    Assert.AreSame(kept[i], existing[i], $"element {i} kept");
            }

            // Existing elements that differ from the data: nulls, subtypes and other values in other places. An element
            // of the data's exact type keeps its instance; the rest are what the data says.
            var others = Ones(100, true, 7).Select(v => v ?? new FlatOneDerived()).ToArray();
            others[3] = null;
            var before = (FlatOne[])others.Clone();
            NinoDeserializer.Deserialize(data, ref others);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.IsTrue(Same(values[i], others[i]), $"other element {i}");
                if (values[i] != null && before[i] != null && values[i].GetType() == typeof(FlatOne) &&
                    before[i].GetType() == typeof(FlatOne))
                    Assert.AreSame(before[i], others[i], $"other element {i} kept");
            }

            var manys = Manys(40, true);
            var intoManys = Manys(40, false, 9).ToList();
            var keptManys = intoManys.ToArray();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(manys), ref intoManys);
            for (int i = 0; i < manys.Length; i++)
            {
                Assert.IsTrue(Same(manys[i], intoManys[i]), $"many {i}");
                if (manys[i] != null)
                    Assert.AreSame(keptManys[i], intoManys[i], $"many {i} kept");
            }

            // An existing object whose strings equal the data's keeps them (no store).
            var many = Manys(1, false)[0];
            var target = NinoDeserializer.Deserialize<FlatMany>(NinoSerializer.Serialize(many));
            string name = target.Name;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(many), ref target);
            Assert.AreSame(name, target.Name);
        }

        [TestMethod]
        public void TruncatedDataIsRejected()
        {
            byte[] ones = NinoSerializer.Serialize(Ones(50, true));
            byte[] manys = NinoSerializer.Serialize(Manys(50, true));
            for (int cut = 1; cut < 200; cut += 3)
            {
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<FlatOne[]>(ones.AsSpan(0, ones.Length - cut)));
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize<List<FlatMany>>(manys.AsSpan(0, manys.Length - cut)));
            }

            byte[] one = NinoSerializer.Serialize(Manys(1, false)[0]);
            for (int length = 1; length < one.Length; length++)
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatMany>(one.AsSpan(0, length)));
        }

        [TestMethod]
        public void InvalidBoolAndPresenceAreRejected()
        {
            // Only the bool is 1 after the presence byte: zeros and null strings around it.
            var value = new FlatMany { Flag = true };
            byte[] one = NinoSerializer.Serialize(value);
            int flag = Array.IndexOf(one, (byte)1, 1);
            Assert.AreEqual(-1, Array.IndexOf(one, (byte)1, flag + 1));
            byte[] bad = (byte[])one.Clone();
            bad[flag] = 2;
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatMany>(bad));
            // A run through the flat kernels, the bad element last.
            const int count = 20;
            byte[] run = BitConverter.GetBytes(count).Concat(Enumerable.Repeat(one, count - 1).SelectMany(x => x))
                .Concat(bad).ToArray();
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatMany[]>(run));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<List<FlatMany>>(run));

            byte[] presence = BitConverter.GetBytes(count).Concat(Enumerable.Repeat(one, count - 1).SelectMany(x => x))
                .Concat(new byte[] { 7 }).Concat(one.Skip(1)).ToArray();
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FlatMany[]>(presence));
        }

        [TestMethod]
        public void RunsOfAClassInAGenericClassWriteItsTypeId()
        {
            // Its type id is computed at run time: the typed kernels once wrote the model's 0, null's id, and read the
            // null element as a body (end of data).
            var one = new FlatHolder<long>.Leaf { W = 7, X = 8, S = "open" };
            byte[] alone = NinoSerializer.Serialize(one);
            byte[] bytes = NinoSerializer.Serialize(new[] { one, null, one });
            CollectionAssert.AreEqual(alone.Take(4).ToArray(), bytes.Skip(4).Take(4).ToArray());
            var back = NinoDeserializer.Deserialize<FlatHolder<long>.Leaf[]>(bytes);
            Assert.AreEqual(3, back.Length);
            Assert.IsNull(back[1]);
            Assert.IsTrue(back[2] is { W: 7, X: 8, S: "open" });
        }

        [TestMethod]
        public void RunsPastTheInlineDepthWriteTheGeneratedBodysBytes()
        {
            // Nodes past NinoDepth.Inline are deferred: their runs go element by element, the ones above through the
            // kernels; both as the generated bodies write them.
            const int depth = 200;
            FlatNode node = null;
            FlatNodeProps twin = null;
            for (int i = 0; i < depth; i++)
            {
                var items = Manys(18, true, i);
                node = new FlatNode { Items = items, Next = node };
                twin = new FlatNodeProps { Items = items.Select(Props).ToArray(), Next = twin };
            }

            byte[] bytes = NinoSerializer.Serialize(node);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(twin), bytes);
            var back = NinoDeserializer.Deserialize<FlatNode>(bytes);
            for (FlatNode a = node, b = back; a != null; a = a.Next, b = b.Next)
            {
                Assert.AreEqual(a.Items.Length, b.Items.Length);
                for (int i = 0; i < a.Items.Length; i++)
                    Assert.IsTrue(Same(a.Items[i], b.Items[i]));
            }

            Assert.IsTrue(Same(node.Items[3], NinoDeserializer.Deserialize<FlatNodeProps>(bytes).Items[3] is { } p
                ? new FlatMany { Id = p.Id, Name = p.Name, Tag = p.Tag, At = p.At, Flag = p.Flag, Text = p.Text, Price = p.Price }
                : null));
        }

        [TestMethod]
        public void LongUtf8StringsAtEveryHeaderSize()
        {
            // The flat kernels size a long UTF-8 string by its length where every count of its bytes takes one header
            // size, and count the bytes elsewhere: the bytes are WriteUtf8's either way, at each boundary.
            int[] lengths = { 1024, 1025, 21845, 21846, 30000, 65535, 65536, 70000 };
            foreach (int length in lengths)
            foreach (bool wide in new[] { false, true })
            {
                var text = new string('a', length).ToCharArray();
                if (wide)
                    for (int i = 0; i < length; i += 3)
                        text[i] = i % 2 == 0 ? '\u4E2D' : '\u00E9';
                var value = new FlatMany { Id = length, Tag = new string(text), Text = "t" };
                CollectionAssert.AreEqual(NinoSerializer.Serialize(Props(value)), NinoSerializer.Serialize(value), $"{length} {wide}");
                var run = Enumerable.Repeat(value, 16).ToArray();
                byte[] bytes = NinoSerializer.Serialize(run);
                CollectionAssert.AreEqual(NinoSerializer.Serialize(run.Select(Props).ToArray()), bytes, $"run {length} {wide}");
                Assert.AreEqual(value.Tag, NinoDeserializer.Deserialize<FlatMany[]>(bytes)[15].Tag);
            }
        }
    }
}
