using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.UnitTests;
using Nino.UnitTests.GenericPolymorphism;
using RootFormatter = NinoGen.Nino_UnitTests_DispatchRootFormatter;

[assembly: NinoFormatter(typeof(DispatchCustomKeyFormatter))]

namespace Nino.UnitTests
{
    // Bases dispatch the subtypes of their assembly through generated code (the hierarchy's root holds the dispatch;
    // classes below it use the root's).

    [NinoType(format: NinoFormat.VersionTolerant)]
    public abstract class DispatchRoot
    {
        public int A;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class DispatchMid : DispatchRoot
    {
        public string B;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchLeaf : DispatchMid
    {
        public long C;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchSibling : DispatchRoot
    {
        public double D;
    }

    /// <summary>A subtype holding a list of the base: nested bodies, deep enough to take the deferred path.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchHolder : DispatchRoot
    {
        public List<DispatchRoot> Kids;
    }

    /// <summary>A struct key with a body of its own (not raw: it holds a string).</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public struct DispatchStructKey
    {
        public int A;
        public string B;
    }

    /// <summary>A key written by a formatter of the user's (<see cref="DispatchCustomKeyFormatter"/>).</summary>
    public readonly struct DispatchCustomKey : IEquatable<DispatchCustomKey>
    {
        public readonly int Value;

        public DispatchCustomKey(int value) => Value = value;

        public bool Equals(DispatchCustomKey other) => Value == other.Value;

        public override bool Equals(object obj) => obj is DispatchCustomKey other && Equals(other);

        public override int GetHashCode() => Value;
    }

    /// <summary>Writes the key as its value negated, in 8 bytes, so it differs from any built-in form.</summary>
    public struct DispatchCustomKeyFormatter : INinoFormatter<DispatchCustomKey>
    {
        public void Serialize(DispatchCustomKey value, ref Writer writer) => writer.UnsafeWrite(-(long)value.Value);

        public void Deserialize(ref Reader reader, ref DispatchCustomKey value) =>
            value = new DispatchCustomKey((int)-reader.UnsafeRead<long>());
    }

    /// <summary>An unmanaged key without padding, written as its bytes.</summary>
    public struct DispatchVec2
    {
        public float X, Y;
    }

    /// <summary>An unmanaged key with three bytes of padding after <see cref="A"/>.</summary>
    public struct DispatchPadded
    {
        public byte A;
        public int B;
    }

    /// <summary>A sealed class key, equal by its id.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchClassKey : IEquatable<DispatchClassKey>
    {
        public int Id;

        public bool Equals(DispatchClassKey other) => other != null && Id == other.Id;

        public override bool Equals(object obj) => Equals(obj as DispatchClassKey);

        public override int GetHashCode() => Id;
    }

    /// <summary>A class key with a subtype (<see cref="DispatchOpenKeySub"/>), equal by its type and id.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public class DispatchOpenKey
    {
        public int Id;

        public override bool Equals(object obj) => obj != null && obj.GetType() == GetType() && ((DispatchOpenKey)obj).Id == Id;

        public override int GetHashCode() => Id;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchOpenKeySub : DispatchOpenKey
    {
        public string Tag;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public record DispatchRecordKey
    {
        public int Id { get; init; }
        public string Name { get; init; }
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public record struct DispatchRecordStructKey
    {
        public int Id;
        public string Name;
    }

    /// <summary>
    /// The dispatch root's generated formatter, counting the calls Nino.Core makes to its pair loop: marked as it is,
    /// so NinoDictionary routes to <c>WritePairs</c> exactly as for the generated one.
    /// </summary>
    public struct RecordingRootFormatter : INinoFormatter<DispatchRoot>, INinoPairsFormatter
    {
        [ThreadStatic] public static int PairCalls;

        public void Serialize(DispatchRoot value, ref Writer writer) =>
            default(RootFormatter).Serialize(value, ref writer);

        public void Deserialize(ref Reader reader, ref DispatchRoot value) =>
            default(RootFormatter).Deserialize(ref reader, ref value);

        public void WritePairs<TKey, TKeyFormatter>(Dictionary<TKey, DispatchRoot> pairs, ref Writer writer)
            where TKeyFormatter : struct, INinoFormatter<TKey>
        {
            PairCalls++;
            default(RootFormatter).WritePairs<TKey, TKeyFormatter>(pairs, ref writer);
        }
    }

    /// <summary>A dictionary of the dispatch root whose keys are written as UTF-8.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class DispatchUtf8Keys
    {
        [NinoUtf8] public Dictionary<string, DispatchRoot> Map;
    }

    [TestClass]
    public class DispatchTests
    {
        [TestMethod]
        public void ElementLoopWritesWhatEachElementWritesAlone()
        {
            DispatchRoot[] values =
            {
                new DispatchSibling { A = 1, D = 2.5 }, null, new DispatchLeaf { A = 3, B = "leaf", C = 4 },
                new DispatchMid { A = 5, B = "mid" }, new DispatchHolder { A = 6, Kids = new List<DispatchRoot> { null } },
            };
            var expected = new List<byte>(BitConverter.GetBytes(values.Length));
            foreach (var value in values)
                expected.AddRange(NinoSerializer.Serialize(value));

            CollectionAssert.AreEqual(expected.ToArray(), NinoSerializer.Serialize(values));
            CollectionAssert.AreEqual(expected.ToArray(), NinoSerializer.Serialize(values.ToList()));
        }

        /// <summary>
        /// Arrays, lists and dictionary values of <typeparamref name="T"/> write each element as it writes alone (the
        /// element loops go through INinoElementsFormatter.WriteElement, the dispatch inline).
        /// </summary>
        private static void ElementsMatch<T>(params T[] values)
        {
            var elements = new List<byte>();
            var pairs = new List<byte>(BitConverter.GetBytes(values.Length));
            for (int i = 0; i < values.Length; i++)
            {
                var alone = NinoSerializer.Serialize(values[i]);
                elements.AddRange(alone);
                pairs.AddRange(BitConverter.GetBytes(i));
                pairs.AddRange(alone);
            }

            var expected = BitConverter.GetBytes(values.Length).Concat(elements).ToArray();
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()));
            var dictionary = new Dictionary<int, T>();
            for (int i = 0; i < values.Length; i++)
                dictionary.Add(i, values[i]);
            CollectionAssert.AreEqual(pairs.ToArray(), NinoSerializer.Serialize(dictionary));

            // Keys of every kind: the generated pair loop (INinoPairsFormatter) takes them all.
            PairsMatch(values, i => "key" + i);
            PairsMatch(values, i => (long)i << 40);
            PairsMatch(values, i => (uint)i);
            PairsMatch(values, i => (DispatchKey)i);
            PairsMatch(values, i => (short)i);
            PairsMatch(values, i => new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
            PairsMatch(values, i => new DispatchStructKey { A = i, B = "s" + i });
            PairsMatch(values, i => new DispatchCustomKey(i));
            PairsMatch(values, i => new DispatchVec2 { X = i, Y = -i });
            PairsMatch(values, i => new DispatchPadded { A = (byte)i, B = i * 3 });
            PairsMatch(values, i => (int?)i);
            PairsMatch(values, i => new DispatchClassKey { Id = i });
            PairsMatch(values, i => i % 2 == 0 ? new DispatchOpenKey { Id = i } : new DispatchOpenKeySub { Id = i, Tag = "t" });
            PairsMatch(values, i => new DispatchRecordKey { Id = i, Name = "r" + i });
            PairsMatch(values, i => new DispatchRecordStructKey { Id = i, Name = "s" + i });
        }

        /// <summary>
        /// A dictionary of <paramref name="values"/> writes its count, then each key and each value as they write
        /// alone, in its enumeration order: whole, and with an entry removed (no longer read in place).
        /// </summary>
        private static void PairsMatch<TKey, T>(T[] values, Func<int, TKey> key)
        {
            var dictionary = new Dictionary<TKey, T>();
            for (int i = 0; i < values.Length; i++)
                dictionary.Add(key(i), values[i]);
            CollectionAssert.AreEqual(Pairs(dictionary), NinoSerializer.Serialize(dictionary));

            dictionary.Add(key(values.Length), values[0]);
            dictionary.Remove(key(0));
            CollectionAssert.AreEqual(Pairs(dictionary), NinoSerializer.Serialize(dictionary));
        }

        private static byte[] Pairs<TKey, T>(Dictionary<TKey, T> dictionary)
        {
            var expected = new List<byte>(BitConverter.GetBytes(dictionary.Count));
            foreach (var pair in dictionary)
            {
                expected.AddRange(NinoSerializer.Serialize(pair.Key));
                expected.AddRange(NinoSerializer.Serialize(pair.Value));
            }

            return expected.ToArray();
        }

        public enum DispatchKey
        {
        }

        /// <summary>
        /// Every key, with every key formatter, reaches the base's generated pair loop: the dispatch root's formatter
        /// is marked (INinoPairsFormatter) and declares WritePairs itself, which NinoDictionary calls for any key
        /// (a constrained call; no key type is named). Each writes what the shared loop writes (the count, then each
        /// key by its formatter and each value by the root's Serialize), UTF-8 strings and a custom formatter too.
        /// </summary>
        [TestMethod]
        public void PairLoopServesEveryKeyFormatter()
        {
            Assert.IsTrue(typeof(INinoPairsFormatter).IsAssignableFrom(typeof(RootFormatter)));
            Assert.AreEqual(typeof(RootFormatter), typeof(RootFormatter).GetMethod("WritePairs")!.DeclaringType);
            DispatchRoot[] values =
            {
                new DispatchSibling { A = 1, D = 2.5 }, null, new DispatchLeaf { A = 3, B = "leaf", C = 4 },
                new DispatchMid { A = 5, B = "mid" }, new DispatchHolder { A = 6, Kids = new List<DispatchRoot> { null } },
            };
            FormatterPairsMatch<int, UnmanagedFormatter<int>>(values, i => i);
            FormatterPairsMatch<short, UnmanagedFormatter<short>>(values, i => (short)i);
            FormatterPairsMatch<Guid, UnmanagedFormatter<Guid>>(values, i => new Guid(i, 0, 0, new byte[8]));
            FormatterPairsMatch<string, StringFormatter>(values, i => "k" + i);
            FormatterPairsMatch<string, Utf8StringFormatter>(values, i => "ü" + i);
            FormatterPairsMatch<DispatchCustomKey, DispatchCustomKeyFormatter>(values, i => new DispatchCustomKey(i));
            FormatterPairsMatch<DispatchKey, UnmanagedFormatter<DispatchKey>>(values, i => (DispatchKey)i);
            FormatterPairsMatch<DispatchVec2, UnmanagedFormatter<DispatchVec2>>(values, i => new DispatchVec2 { X = i });
            FormatterPairsMatch<DispatchPadded, UnmanagedFormatter<DispatchPadded>>(values,
                i => new DispatchPadded { A = (byte)i, B = -i });
            FormatterPairsMatch<int?, UnmanagedNullableFormatter<int>>(values, i => i);
            FormatterPairsMatch<DispatchStructKey, NinoGen.Nino_UnitTests_DispatchStructKeyFormatter>(values,
                i => new DispatchStructKey { A = i, B = "s" + i });
            FormatterPairsMatch<DispatchClassKey, NinoGen.Nino_UnitTests_DispatchClassKeyFormatter>(values,
                i => new DispatchClassKey { Id = i });
            FormatterPairsMatch<DispatchOpenKey, NinoGen.Nino_UnitTests_DispatchOpenKeyFormatter>(values,
                i => i % 2 == 0 ? new DispatchOpenKey { Id = i } : new DispatchOpenKeySub { Id = i, Tag = "t" });
            FormatterPairsMatch<DispatchRecordKey, NinoGen.Nino_UnitTests_DispatchRecordKeyFormatter>(values,
                i => new DispatchRecordKey { Id = i, Name = "r" });
            FormatterPairsMatch<DispatchRecordStructKey, NinoGen.Nino_UnitTests_DispatchRecordStructKeyFormatter>(
                values, i => new DispatchRecordStructKey { Id = i, Name = "s" });

            var holder = new DispatchUtf8Keys { Map = new Dictionary<string, DispatchRoot> { { "ä", values[0] }, { "b", null } } };
            var back = NinoDeserializer.Deserialize<DispatchUtf8Keys>(NinoSerializer.Serialize(holder));
            Assert.AreEqual(2.5, ((DispatchSibling)back.Map["ä"]).D);
            Assert.IsNull(back.Map["b"]);
        }

        private static void FormatterPairsMatch<TKey, TKeyFormatter>(DispatchRoot[] values, Func<int, TKey> key)
            where TKeyFormatter : struct, INinoFormatter<TKey>
        {
            var dictionary = new Dictionary<TKey, DispatchRoot>();
            for (int i = 0; i < values.Length; i++)
                dictionary.Add(key(i), values[i]);
            for (int round = 0; round < 2; round++)
            {
                var expected = new NinoArrayBufferWriter();
                var writer = new Writer(expected);
                writer.WriteLength(dictionary.Count);
                foreach (var pair in dictionary)
                {
                    default(TKeyFormatter).Serialize(pair.Key, ref writer);
                    default(RootFormatter).Serialize(pair.Value, ref writer);
                }

                writer.Flush();
                var actual = new NinoArrayBufferWriter();
                writer = new Writer(actual);
                int calls = RecordingRootFormatter.PairCalls;
                NinoDictionary<TKey, DispatchRoot, TKeyFormatter, RecordingRootFormatter>.Write(dictionary, ref writer);
                writer.Flush();
                CollectionAssert.AreEqual(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
                // The generated pair loop took the pairs: NinoDictionary named no key type to get there.
                Assert.AreEqual(calls + 1, RecordingRootFormatter.PairCalls, typeof(TKey).Name);

                // Then with an entry removed: no longer read in place.
                dictionary.Add(key(values.Length + round), values[0]);
                dictionary.Remove(key(round));
            }
        }

        /// <summary>
        /// Values that are no polymorphic base (a sealed class, a struct with a reference, an unmanaged struct) keep
        /// the shared loop: each still writes what it writes alone, whatever the key, and reads back.
        /// </summary>
        [TestMethod]
        public void OtherValuesWriteAsAlone()
        {
            var sealedValues = new[] { new DispatchSibling { A = 1, D = 2 }, null, new DispatchSibling { A = 3 } };
            var structValues = new[] { new DispatchStructKey { A = 1, B = "x" }, default, new DispatchStructKey { A = 2 } };
            var rawValues = new[] { new DispatchVec2 { X = 1 }, default, new DispatchVec2 { Y = 2 } };
            PairsMatch(sealedValues, i => i);
            PairsMatch(sealedValues, i => "k" + i);
            PairsMatch(sealedValues, i => new DispatchClassKey { Id = i });
            PairsMatch(structValues, i => i);
            PairsMatch(structValues, i => new DispatchRecordStructKey { Id = i, Name = "n" });
            PairsMatch(rawValues, i => (short)i);
            PairsMatch(rawValues, i => new Guid(i, 0, 0, new byte[8]));

            var map = new Dictionary<string, DispatchSibling> { { "a", sealedValues[0] }, { "b", null } };
            var back = NinoDeserializer.Deserialize<Dictionary<string, DispatchSibling>>(NinoSerializer.Serialize(map));
            Assert.AreEqual(2d, back["a"].D);
            Assert.IsNull(back["b"]);
        }

        [TestMethod]
        public void PolymorphicDictionariesRoundTripAtEverySize()
        {
            foreach (int count in new[] { 1, 4, 12, 32, 100 })
            {
                var byInt = new Dictionary<int, DispatchRoot>();
                var byString = new Dictionary<string, DispatchRoot>();
                for (int i = 0; i < count; i++)
                {
                    DispatchRoot value = (i % 4) switch
                    {
                        0 => new DispatchSibling { A = i, D = i * 0.5 },
                        1 => new DispatchLeaf { A = i, B = "leaf" + i, C = -i },
                        2 => new DispatchMid { A = i, B = "mid" + i },
                        _ => i % 8 == 3 ? null : new DispatchHolder { A = i, Kids = new List<DispatchRoot> { null } },
                    };
                    byInt.Add(i * 7, value);
                    byString.Add("k" + i, value);
                }

                RoundTrips(byInt);
                RoundTrips(byString);
            }
        }

        private static void RoundTrips<TKey>(Dictionary<TKey, DispatchRoot> dictionary)
        {
            var bytes = NinoSerializer.Serialize(dictionary);
            var back = NinoDeserializer.Deserialize<Dictionary<TKey, DispatchRoot>>(bytes);
            Assert.AreEqual(dictionary.Count, back.Count);
            foreach (var pair in dictionary)
            {
                var read = back[pair.Key];
                Assert.AreEqual(pair.Value?.GetType(), read?.GetType());
                Assert.AreEqual(pair.Value?.A, read?.A);
            }

            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back));
        }

        [TestMethod]
        public void EveryDispatchingBaseWritesElementsAsAlone()
        {
            // An instantiable base (its own id first), a middle class, a generic root and a generic subtype's base.
            ElementsMatch<DelegLevel1>(new DelegLevel1 { A = 1, Name = "one" }, null,
                new DelegLevel2 { A = 2, Items = new List<int> { 3 } }, new DelegLevel3 { A = 4, D = 5, E = 6 });
            ElementsMatch<DelegLevel2>(new DelegLevel2 { A = 7 }, new DelegLevel3 { A = 8, E = 9 }, null);
            ElementsMatch<PureBox<int>>(new PureIntBox { Value = 1, X = 2 }, null, new PureIntBox { Value = 3 });
            ElementsMatch<GenericPolyBase>(new GenericPolyIntBox { Id = 1, Value = 2 }, null);
            ElementsMatch<NonGenericPolyBase>(new NonGenericPolyLeaf { Id = 5, Value = 6 }, null);
            ElementsMatch<TrackedBox<int>>(new TrackedIntBox { Value = 1 }, null);
        }

        [TestMethod]
        public void DeepElementLoopsRoundTrip()
        {
            // Past NinoDepth.Inline open bodies, the loop writes each element through the dispatch's deep path.
            DispatchRoot node = new DispatchSibling { A = -1, D = 1 };
            for (int i = 0; i < 300; i++)
                node = new DispatchHolder { A = i, Kids = new List<DispatchRoot> { node, null, new DispatchMid { B = "m" + i } } };

            var bytes = NinoSerializer.Serialize(node);
            var back = NinoDeserializer.Deserialize<DispatchRoot>(bytes);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(back));
            int depth = 0;
            while (back is DispatchHolder holder)
            {
                Assert.IsNull(holder.Kids[1]);
                Assert.AreEqual("m" + holder.A, ((DispatchMid)holder.Kids[2]).B);
                back = holder.Kids[0];
                depth++;
            }

            Assert.AreEqual(300, depth);
            Assert.AreEqual(1d, ((DispatchSibling)back).D);
        }

        [TestMethod]
        public void EveryLevelRoundTrips()
        {
            DispatchRoot[] values =
            {
                new DispatchMid { A = 1, B = "m" }, new DispatchLeaf { A = 2, B = "l", C = 3 },
                new DispatchSibling { A = 4, D = 5.5 }, null,
            };
            var back = NinoDeserializer.Deserialize<DispatchRoot[]>(NinoSerializer.Serialize(values));
            Assert.AreEqual("m", ((DispatchMid)back[0]).B);
            Assert.AreEqual(3, ((DispatchLeaf)back[1]).C);
            Assert.AreEqual(5.5, ((DispatchSibling)back[2]).D);
            Assert.IsNull(back[3]);

            // Through the middle class: its own type and its subtype.
            DispatchMid mid = new DispatchLeaf { A = 7, B = "x", C = 8 };
            var midBack = NinoDeserializer.Deserialize<DispatchMid>(NinoSerializer.Serialize(mid));
            Assert.AreEqual(8, ((DispatchLeaf)midBack).C);
            midBack = NinoDeserializer.Deserialize<DispatchMid>(NinoSerializer.Serialize(new DispatchMid { B = "own" }));
            Assert.AreEqual(typeof(DispatchMid), midBack.GetType());
        }

        [TestMethod]
        public void SiblingTypeInAMiddleSlotIsRejected()
        {
            var bytes = NinoSerializer.Serialize<DispatchRoot>(new DispatchSibling { D = 1 });
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<DispatchMid>(bytes));
        }

        [TestMethod]
        public void InstanceOfAnotherSubtypeIsReplaced()
        {
            DispatchRoot target = new DispatchSibling { D = 9 };
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<DispatchRoot>(new DispatchLeaf { C = 6 }), ref target);
            Assert.AreEqual(6, ((DispatchLeaf)target).C);

            // The same subtype is reused in place.
            var same = target;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<DispatchRoot>(new DispatchLeaf { C = 10 }), ref target);
            Assert.AreSame(same, target);
            Assert.AreEqual(10, ((DispatchLeaf)target).C);
        }
    }
}
