using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>Writes each scenario's values, serialized by Nino, to &lt;dir&gt;/&lt;name&gt;.bin.</summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            var rng = new Random(42);
            string dir = args[0];
            Directory.CreateDirectory(dir);
            string Word(int n) => new string(Enumerable.Range(0, n).Select(_ => (char)('a' + rng.Next(26))).ToArray());
            string Sentence(int words) => string.Join(" ", Enumerable.Range(0, words).Select(_ => Word(rng.Next(2, 10))));
            Vec3 V() => new() { X = (float)rng.NextDouble(), Y = (float)rng.NextDouble(), Z = (float)rng.NextDouble() };
            Animal Pet(int i) => (i % 3) switch
            {
                0 => new Dog { Name = "pet" + i, Bones = i },
                1 => new Cat { Name = "pet" + i, Lives = 9 },
                _ => new Puppy { Name = "pet" + i, Bones = i, Sleepy = i % 2 == 0 },
            };

            Write(dir, "player", Enumerable.Range(0, 1000).Select(i => new Player
            {
                Id = 1_000_000_000L + i, Name = "Player " + i, Level = rng.Next(100), Score = rng.NextDouble() * 1e4,
                Online = i % 2 == 0, Position = V(), Unlocked = Enumerable.Range(0, 8).Select(_ => rng.Next(1000)).ToArray(),
                Inventory = new Dictionary<string, int> { ["sword"] = rng.Next(5), ["shield"] = rng.Next(5) }, Pet = Pet(i),
            }));
            Write(dir, "zoo", Enumerable.Range(0, 50).Select(i => new Zoo
            {
                Animals = Enumerable.Range(0, 100).Select(k => Pet(i + k)).ToList(),
            }));
            Write(dir, "orders", Enumerable.Range(0, 100).Select(b => new OrderBook
            {
                Orders = Enumerable.Range(0, 50).Select(i => new Order
                {
                    Id = b * 1000 + i, Sku = "SKU-" + rng.Next(100000).ToString("D6"), Customer = Word(6) + " " + Word(8),
                    Quantity = rng.Next(1, 20), Price = Math.Round(rng.NextDouble() * 1000, 2), Paid = rng.Next(2) == 0,
                    Status = (byte)rng.Next(4),
                }).ToList(),
            }));
            Write(dir, "articles", Enumerable.Range(0, 200).Select(i => new Article
            {
                Title = Sentence(8), Author = Word(5) + " " + Word(7), Body = Sentence(300),
                Summary = i % 2 == 0 ? "数据序列化的性能测试，包含中文文本。" + Sentence(5) : "Café crème — résumé naïve " + Sentence(10),
                Tags = Enumerable.Range(0, 5).Select(_ => Word(6)).ToList(), Views = rng.Next(),
            }));
            Write(dir, "mesh", Enumerable.Range(0, 20).Select(i => new Mesh
            {
                Name = "mesh" + i,
                Vertices = Enumerable.Range(0, 500).Select(_ => V()).ToArray(),
                Normals = Enumerable.Range(0, 500).Select(_ => V()).ToArray(),
                Indices = Enumerable.Range(0, 1500).Select(_ => rng.Next(500)).ToArray(),
            }));
            Write(dir, "samples", Enumerable.Range(0, 20).Select(_ => new Samples
            {
                Values = Enumerable.Range(0, 1000).Select(_ => rng.NextDouble() * 1e6).ToArray(),
                Counts = Enumerable.Range(0, 1000).Select(_ => rng.Next(-1000000, 1000000)).ToArray(),
                Weights = Enumerable.Range(0, 1000).Select(_ => (float)rng.NextDouble()).ToArray(),
            }));
            Write(dir, "lookup", Enumerable.Range(0, 50).Select(_ => new Lookup
            {
                Counts = Enumerable.Range(0, 100).ToDictionary(i => "key_" + Word(6) + i, _ => rng.Next(100000)),
                Names = Enumerable.Range(0, 100).ToDictionary(i => i * 7, _ => Word(10)),
            }));
            Write(dir, "events", Enumerable.Range(0, 1000).Select(i => new Event
            {
                At = 1760790645123L * 10000 + i, UserId = rng.NextInt64(1L << 40), Kind = rng.Next(10),
                Refs = Enumerable.Range(0, 4).Select(_ => rng.NextInt64()).ToArray(),
            }));

            // The Go and Rust benchmarks' strings: 1,000 short ASCII strings, and 1,000 of ASCII, Latin-1 (one byte a
            // char in Nino) and other text (UTF-16 in Nino: Cyrillic, CJK and a char outside the BMP) in turn.
            Write(dir, "strings", new[] { new Strings { Values = Enumerable.Range(0, 1000).Select(i => "string " + i).ToArray() } });
            Write(dir, "strings-mixed", new[]
            {
                new Strings
                {
                    Values = Enumerable.Range(0, 1000).Select(i => (i % 3) switch
                    {
                        0 => "string " + i,
                        1 => "crème brûlée " + i,
                        _ => "строка 字符串 🎉 " + i,
                    }).ToArray(),
                },
            });
        }

        /// <summary>The count, then each value's length and bytes.</summary>
        private static void Write<T>(string dir, string name, IEnumerable<T> values)
        {
            var list = values.ToList();
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, name + ".bin")));
            w.Write(list.Count);
            foreach (var v in list)
            {
                byte[] bytes = NinoSerializer.Serialize(v);
                w.Write(bytes.Length);
                w.Write(bytes);
            }
        }
    }
}
