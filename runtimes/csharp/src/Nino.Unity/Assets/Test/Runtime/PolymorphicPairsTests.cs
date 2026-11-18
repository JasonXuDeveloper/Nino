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
    /// Dictionaries of a polymorphic base in Unity's Mono (play mode) and IL2CPP (the player): the base's generated
    /// pair loop (<c>INinoFormatter&lt;T&gt;.WritePairs</c>, one generic member reached by a constrained call on the
    /// formatter's struct type) writes the count, then each key and each value as they write alone, for value-type and
    /// class keys alike (IL2CPP shares the code over a class key), and reads back.
    /// </summary>
    public class PolymorphicPairsTests
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

                // Then with an entry removed: no longer read in place.
                dictionary.Remove(key(3));
            }
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
