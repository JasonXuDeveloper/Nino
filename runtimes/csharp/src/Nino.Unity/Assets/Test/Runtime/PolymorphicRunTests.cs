using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Core;
using NUnit.Framework;

namespace Test.Runtime
{
    [NinoType]
    public abstract class PairShape
    {
        public int Id;
    }

    [NinoType]
    public sealed class PairCircle : PairShape
    {
        public float R;
    }

    [NinoType]
    public sealed class PairRect : PairShape
    {
        public int W;
        public string Name;
    }

    /// <summary>A struct key with a body of its own (it holds a string, so it is not raw).</summary>
    [NinoType]
    public struct PairKey : IEquatable<PairKey>
    {
        public int A;
        public string B;

        public bool Equals(PairKey other) => A == other.A && B == other.B;

        public override bool Equals(object obj) => obj is PairKey other && Equals(other);

        public override int GetHashCode() => A;
    }

    /// <summary>
    /// Collections of a polymorphic base in Unity's Mono (play mode) and IL2CPP (the player): the base's generated run
    /// loop (<c>INinoFormatter.WriteRun</c>, one non-generic member over Nino.Core's cursor, reached by a constrained
    /// call on the formatter's struct type) writes a dictionary's count, then each key and each value as they write
    /// alone, for keys it writes as their bytes or strings and for keys written value by value (a struct key, a class
    /// key: IL2CPP shares the code over a class key), and an array's, a list's and a set's elements; each reads back.
    /// </summary>
    public class PolymorphicRunTests
    {
        private static PairShape Shape(int i) => i % 5 == 4 ? null
            : i % 2 == 0 ? new PairCircle { Id = i, R = i * 0.5f }
            : (PairShape)new PairRect { Id = i, W = -i, Name = "r" + i };

        private static string Text(PairShape shape) => shape switch
        {
            null => "null",
            PairCircle c => $"circle {c.Id} {c.R}",
            PairRect r => $"rect {r.Id} {r.W} {r.Name}",
            _ => shape.GetType().Name,
        };

        /// <summary>
        /// Writes and reads through the callers' calls, which name the closed types: generated code registers each,
        /// so IL2CPP has the exact code of each dictionary (none made at run time by full generic sharing).
        /// </summary>
        private static void Check<TKey>(Func<int, TKey> key, Func<TKey, byte[]> writeKey,
            Func<Dictionary<TKey, PairShape>, byte[]> write, Func<byte[], Dictionary<TKey, PairShape>> read)
        {
            var dictionary = Enumerable.Range(0, 20).ToDictionary(key, Shape);
            for (int round = 0; round < 2; round++)
            {
                var expected = new List<byte>(BitConverter.GetBytes(dictionary.Count));
                foreach (var pair in dictionary)
                {
                    expected.AddRange(writeKey(pair.Key));
                    expected.AddRange(NinoSerializer.Serialize(pair.Value));
                }

                byte[] bytes = write(dictionary);
                Assert.AreEqual(BitConverter.ToString(expected.ToArray()), BitConverter.ToString(bytes),
                    typeof(TKey).Name);
                var back = read(bytes);
                CollectionAssert.AreEqual(dictionary.Select(p => Text(p.Value)).ToArray(),
                    dictionary.Select(p => Text(back[p.Key])).ToArray(), typeof(TKey).Name);

                // Then with an entry removed: a free entry the run skips.
                dictionary.Remove(key(3));
            }
        }

        [Test]
        public void Sequences()
        {
            var values = Enumerable.Range(0, 20).Select(Shape).ToArray();
            var expected = new List<byte>(BitConverter.GetBytes(values.Length));
            foreach (var value in values)
                expected.AddRange(NinoSerializer.Serialize(value));
            string bytes = BitConverter.ToString(expected.ToArray());
            Assert.AreEqual(bytes, BitConverter.ToString(NinoSerializer.Serialize(values)));
            Assert.AreEqual(bytes, BitConverter.ToString(NinoSerializer.Serialize(values.ToList())));
            var back = NinoDeserializer.Deserialize<List<PairShape>>(NinoSerializer.Serialize(values.ToList()));
            CollectionAssert.AreEqual(values.Select(Text).ToArray(), back.Select(Text).ToArray());

            var set = new HashSet<PairShape>(values.Where(v => v != null));
            set.Remove(values[1]);
            var inOrder = new List<byte>(BitConverter.GetBytes(set.Count));
            foreach (var value in set)
                inOrder.AddRange(NinoSerializer.Serialize(value));
            Assert.AreEqual(BitConverter.ToString(inOrder.ToArray()), BitConverter.ToString(NinoSerializer.Serialize(set)));
        }

        [Test]
        public void IntKeys() => Check(i => i * 3, k => NinoSerializer.Serialize(k), d => NinoSerializer.Serialize(d),
            b => NinoDeserializer.Deserialize<Dictionary<int, PairShape>>(b));

        [Test]
        public void StringKeys() => Check(i => "k" + i, k => NinoSerializer.Serialize(k),
            d => NinoSerializer.Serialize(d), b => NinoDeserializer.Deserialize<Dictionary<string, PairShape>>(b));

        [Test]
        public void GuidKeys() => Check(i => new Guid(i, 1, 2, new byte[8]), k => NinoSerializer.Serialize(k),
            d => NinoSerializer.Serialize(d), b => NinoDeserializer.Deserialize<Dictionary<Guid, PairShape>>(b));

        [Test]
        public void StructKeys() => Check(i => new PairKey { A = i, B = "s" + i }, k => NinoSerializer.Serialize(k),
            d => NinoSerializer.Serialize(d), b => NinoDeserializer.Deserialize<Dictionary<PairKey, PairShape>>(b));

        [Test]
        public void ShortKeys() => Check(i => (short)i, k => NinoSerializer.Serialize(k),
            d => NinoSerializer.Serialize(d), b => NinoDeserializer.Deserialize<Dictionary<short, PairShape>>(b));
    }
}
