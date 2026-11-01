using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nino.Core;

namespace Bench
{
    [NinoType] public struct Vec3 { public float X; public float Y; public float Z; }

    [NinoType] public abstract class Animal { public string Name; }
    [NinoType] public class Dog : Animal { public int Bones; }
    [NinoType] public sealed class Puppy : Dog { public bool Sleepy; }
    [NinoType] public sealed class Cat : Animal { public float Lives; }

    /// <summary>A typical record: numbers, short text, a small array, a map and a subtype behind a type id.</summary>
    [NinoType] public sealed class Player
    {
        public long Id; public string Name; public int Level; public double Score; public bool Online; public Vec3 Position;
        public int[] Unlocked; public Dictionary<string, int> Inventory; public Animal Pet;
    }

    /// <summary>Values behind a type id.</summary>
    [NinoType] public sealed class Zoo { public List<Animal> Animals; }

    [NinoType] public sealed class Order
    {
        public int Id; public string Sku; public string Customer; public int Quantity; public double Price; public bool Paid;
        public byte Status;
    }

    /// <summary>A list of small records.</summary>
    [NinoType] public sealed class OrderBook { public List<Order> Orders; }

    /// <summary>Mostly text, some of it not Latin-1.</summary>
    [NinoType] public sealed class Article
    {
        public string Title; public string Author; public string Body; public string Summary; public List<string> Tags;
        public int Views;
    }

    /// <summary>Arrays of small structs and numbers.</summary>
    [NinoType] public sealed class Mesh { public string Name; public Vec3[] Vertices; public Vec3[] Normals; public int[] Indices; }

    [NinoType] public sealed class Samples { public double[] Values; public int[] Counts; public float[] Weights; }

    /// <summary>Maps keyed and valued by short text.</summary>
    [NinoType] public sealed class Lookup { public Dictionary<string, int> Counts; public Dictionary<int, string> Names; }

    [NinoType] public sealed class Event { public long At; public long UserId; public int Kind; public long[] Refs; }

    /// <summary>Text only: the string cases of the Go and Rust benchmarks.</summary>
    [NinoType] public sealed class Strings { public string[] Values; }

    /// <summary>Sets: the set cases of the Go and Rust benchmarks.</summary>
    [NinoType] public sealed class IntSet { public HashSet<int> Values; }

    [NinoType] public sealed class StringSet { public HashSet<string> Values; }

    /// <summary>
    /// Writes each scenario's values, serialized by Nino, to &lt;dir&gt;/&lt;name&gt;.bin, in <see cref="Variants"/>
    /// variants: the same shapes and sizes with other content. bench.ts times each pass on the next variant in turn,
    /// because V8 reuses work when it reads the same JSON text again (hidden-class transitions, internalized keys): a
    /// Lookup-like text took 0.21 ms a parse repeated, 7.8 to 8.4 ms with 200 different texts in turn.
    /// </summary>
    public static class Program
    {
        /// <summary>Enough that no pass finds the previous passes' work cached; few enough to set up quickly.</summary>
        public const int Variants = 64;

        public static void Main(string[] args)
        {
            string dir = args[0];
            Directory.CreateDirectory(dir);
            // Variant 0 is the data from before the rotation (seed 42, text as written); every other variant has its
            // own seed and scrambles the fixed text, letter for letter in the same script, so every string keeps its
            // length and its encoding in Nino and in UTF-8.
            for (int variant = 0; variant < Variants; variant++)
                WriteVariant(dir, variant);
        }

        private static void WriteVariant(string dir, int variant)
        {
            var rng = new Random(42 + variant);
            string T(string text) => variant == 0 ? text : Scramble(text, rng);
            void Write<TValue>(string name, IEnumerable<TValue> values) => Append(dir, name, variant, values);
            string Word(int n) => new string(Enumerable.Range(0, n).Select(_ => (char)('a' + rng.Next(26))).ToArray());
            string Sentence(int words) => string.Join(" ", Enumerable.Range(0, words).Select(_ => Word(rng.Next(2, 10))));
            Vec3 V() => new() { X = (float)rng.NextDouble(), Y = (float)rng.NextDouble(), Z = (float)rng.NextDouble() };
            Animal Pet(int i) => (i % 3) switch
            {
                0 => new Dog { Name = T("pet" + i), Bones = i },
                1 => new Cat { Name = T("pet" + i), Lives = 9 },
                _ => new Puppy { Name = T("pet" + i), Bones = i, Sleepy = i % 2 == 0 },
            };

            Write("player", Enumerable.Range(0, 1000).Select(i => new Player
            {
                Id = 1_000_000_000L + i, Name = T("Player " + i), Level = rng.Next(100), Score = rng.NextDouble() * 1e4,
                Online = i % 2 == 0, Position = V(), Unlocked = Enumerable.Range(0, 8).Select(_ => rng.Next(1000)).ToArray(),
                Inventory = new Dictionary<string, int> { ["sword"] = rng.Next(5), ["shield"] = rng.Next(5) }, Pet = Pet(i),
            }));
            Write("zoo", Enumerable.Range(0, 50).Select(i => new Zoo
            {
                Animals = Enumerable.Range(0, 100).Select(k => Pet(i + k)).ToList(),
            }));
            Write("orders", Enumerable.Range(0, 100).Select(b => new OrderBook
            {
                Orders = Enumerable.Range(0, 50).Select(i => new Order
                {
                    Id = b * 1000 + i, Sku = "SKU-" + rng.Next(100000).ToString("D6"), Customer = Word(6) + " " + Word(8),
                    Quantity = rng.Next(1, 20), Price = Math.Round(rng.NextDouble() * 1000, 2), Paid = rng.Next(2) == 0,
                    Status = (byte)rng.Next(4),
                }).ToList(),
            }));
            Write("articles", Enumerable.Range(0, 200).Select(i => new Article
            {
                Title = Sentence(8), Author = Word(5) + " " + Word(7), Body = Sentence(300),
                Summary = i % 2 == 0
                    ? T("数据序列化的性能测试，包含中文文本。") + Sentence(5)
                    : T("Café crème — résumé naïve ") + Sentence(10),
                Tags = Enumerable.Range(0, 5).Select(_ => Word(6)).ToList(), Views = rng.Next(),
            }));
            Write("mesh", Enumerable.Range(0, 20).Select(i => new Mesh
            {
                Name = T("mesh" + i),
                Vertices = Enumerable.Range(0, 500).Select(_ => V()).ToArray(),
                Normals = Enumerable.Range(0, 500).Select(_ => V()).ToArray(),
                Indices = Enumerable.Range(0, 1500).Select(_ => rng.Next(500)).ToArray(),
            }));
            Write("samples", Enumerable.Range(0, 20).Select(_ => new Samples
            {
                Values = Enumerable.Range(0, 1000).Select(_ => rng.NextDouble() * 1e6).ToArray(),
                Counts = Enumerable.Range(0, 1000).Select(_ => rng.Next(-1000000, 1000000)).ToArray(),
                Weights = Enumerable.Range(0, 1000).Select(_ => (float)rng.NextDouble()).ToArray(),
            }));
            Write("lookup", Enumerable.Range(0, 50).Select(_ => new Lookup
            {
                Counts = Enumerable.Range(0, 100).ToDictionary(i => "key_" + Word(6) + i, _ => rng.Next(100000)),
                Names = Enumerable.Range(0, 100).ToDictionary(i => i * 7, _ => Word(10)),
            }));
            Write("events", Enumerable.Range(0, 1000).Select(i => new Event
            {
                At = 1760790645123L * 10000 + i, UserId = rng.NextInt64(1L << 40), Kind = rng.Next(10),
                Refs = Enumerable.Range(0, 4).Select(_ => rng.NextInt64()).ToArray(),
            }));

            // The Go and Rust benchmarks' strings: 1,000 short ASCII strings, and 1,000 of ASCII, Latin-1 (one byte a
            // char in Nino) and other text (UTF-16 in Nino: Cyrillic, CJK and a char outside the BMP) in turn.
            Write("strings", new[] { new Strings { Values = Enumerable.Range(0, 1000).Select(i => T("string " + i)).ToArray() } });
            Write("strings-mixed", new[]
            {
                new Strings
                {
                    Values = Enumerable.Range(0, 1000).Select(i => (i % 3) switch
                    {
                        0 => T("string " + i),
                        1 => T("crème brûlée " + i),
                        _ => T("строка 字符串 🎉 " + i),
                    }).ToArray(),
                },
            });
            // The Go and Rust benchmarks' sets: 1,000 distinct non-negative integers in no order, and "member 0" to
            // "member 999" in no order (each variant's letters scrambled, the digits keep them distinct).
            Write("int-set", Enumerable.Range(0, 20).Select(_ =>
            {
                var set = new HashSet<int>();
                while (set.Count < 1000) set.Add(rng.Next());
                return new IntSet { Values = set };
            }));
            Write("string-set", Enumerable.Range(0, 20).Select(_ => new StringSet
            {
                Values = new HashSet<string>(Enumerable.Range(0, 1000).OrderBy(k => rng.Next()).Select(k => T("member " + k))),
            }));
        }

        /// <summary>
        /// Appends a variant to the scenario's file: first -<see cref="Variants"/> (the file of a commit before the
        /// rotation starts with its one variant's count instead), then per variant the count, then each value's length
        /// and bytes.
        /// </summary>
        private static void Append<T>(string dir, string name, int variant, IEnumerable<T> values)
        {
            var list = values.ToList();
            using var w = new BinaryWriter(new FileStream(Path.Combine(dir, name + ".bin"),
                variant == 0 ? FileMode.Create : FileMode.Append));
            if (variant == 0) w.Write(-Variants);
            w.Write(list.Count);
            foreach (var v in list)
            {
                byte[] bytes = NinoSerializer.Serialize(v);
                w.Write(bytes.Length);
                w.Write(bytes);
            }
        }

        /// <summary>
        /// The text with each letter replaced by a random one of the same kind (ASCII lower or upper case, Latin-1
        /// lower case, Cyrillic, CJK, emoji); digits, spaces and punctuation stay.
        /// </summary>
        private static string Scramble(string text, Random rng)
        {
            var sb = new StringBuilder(text.Length);
            foreach (Rune r in text.EnumerateRunes())
            {
                int c = r.Value;
                int Pick(int first, int last) => rng.Next(first, last + 1);
                c = c switch
                {
                    >= 'a' and <= 'z' => Pick('a', 'z'),
                    >= 'A' and <= 'Z' => Pick('A', 'Z'),
                    >= 0xE0 and <= 0xFF and not 0xF7 => Pick(0xE0, 0xF6), // à to ö: never ÷
                    >= 0x430 and <= 0x44F => Pick(0x430, 0x44F),
                    >= 0x4E00 and <= 0x9FFF => Pick(0x4E00, 0x9FFF),
                    >= 0x1F300 and <= 0x1FAFF => Pick(0x1F600, 0x1F64F),
                    _ => c,
                };
                sb.Append(new Rune(c).ToString());
            }
            return sb.ToString();
        }
    }
}
