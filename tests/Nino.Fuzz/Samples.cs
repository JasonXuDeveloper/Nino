using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Nino.Fuzz
{
    /// <summary>
    /// The values whose bytes seed each target. Fixed contents (no random Guid, no clock) so a seed reproduces a run.
    /// </summary>
    public static class Samples
    {
        public static Prims Prims() => new Prims
        {
            B = true, C = Color.Blue, Bg = Big.B, Dec = 12.345m, Dt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
            Dto = DateTimeOffset.UnixEpoch.AddHours(3), G = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            Ts = TimeSpan.FromHours(5), Ch = 'x', D = 3.5, NI = 5, Raw = new RawS { A = 1, B = true, C = Color.Green, D = 99 },
            Str = "hello wörld", U8 = "héllo 😀", BI = BigInteger.Parse("123456789012345678901234567890"),
            Uri = new Uri("https://example.com/a?b=c"), Ver = new Version(1, 2, 3, 4),
            Cul = System.Globalization.CultureInfo.InvariantCulture, SB = new StringBuilder("sb text ✓"),
            Bits = new System.Collections.BitArray(new[] { true, false, true }), Tup = (7, "seven"), Kvp = new(8, "eight"),
            Lz = new Lazy<int>(() => 42), H = (Half)1.5, I128 = Int128.MaxValue, DO = new DateOnly(2020, 1, 2),
            TO = new TimeOnly(10, 11), Ru = new Rune('A'),
        };

        public static Prims PrimsSparse() => new Prims { Str = "", NI = null, Dec = decimal.MinValue, Ver = new Version(0, 1) };

        public static Strings Strings() => new Strings
        {
            Ascii = "Nino fuzz", Latin1 = "Café crème", Utf16 = "中文字符串", Utf8 = "ニノ😀 ünï",
            Many = new[] { "a", null, "", "日本", "ÿĀ", new string('x', 70) },
            ManyUtf8 = new() { "é", null, "😀😀", "" }, Map = new() { ["k"] = "v", ["ключ"] = null, [""] = "e" },
            Chars = "chars".ToCharArray(), CharList = new() { 'a', '\uD800', 'z' }, Empty = "", Null = null,
        };

        public static RawRoot RawRoot() => new RawRoot
        {
            One = new RawS { A = 7, B = true, C = Color.Blue, D = -1 },
            Many = Enumerable.Range(0, 9).Select(i => new RawS { A = i, D = i * 1000L }).ToArray(),
            Pairs = Enumerable.Range(0, 40).Select(i => new Pair2 { A = (byte)i, B = -i }).ToList(),
            Blocks = new[] { new PlainBlock { A = 1, B = 2, C = 3, D = 4 }, default },
            Empties = new Empty[3], Tuples = Enumerable.Range(0, 70).Select(i => ((byte)i, i * 7)).ToArray(),
            Wide = Enumerable.Range(0, 17).Select(i => ((short)i, (long)i << 40)).ToList(),
            Guids = new[] { Guid.Empty, new Guid("0f8fad5b-d9cb-469f-a165-70867728950e") }, Decimals = new[] { 1.5m, -0.0001m },
            Maybe = new RawS { A = 3 }, ById = new() { [1] = new RawS { A = 1 }, [2] = default },
        };

        public static Keyed Keyed() => new Keyed
        {
            Name = "keyed", Id = 42, Tags = new() { ["a"] = 1, ["b"] = 2 },
            Parts = Enumerable.Range(0, 6).Select(i => new KeyedPart { At = i, Note = i % 2 == 0 ? "n" + i : null }).ToList(),
            Inline = new KeyedStruct { X = 1.25, Ys = new[] { 1, 2, 3 } },
        };

        public static Colls Colls() => new Colls
        {
            LI = Enumerable.Range(0, 20).ToList(), LS = new() { "a", "bb", "ccc", null, "ünï", "日本" }, AI = Enumerable.Range(0, 40).ToArray(),
            AS = new[] { "x", "y" }, LBI = Enumerable.Range(0, 70).Select(i => ((byte)i, i * 3)).ToList(),
            ASI = Enumerable.Range(0, 40).Select(i => ((short)i, -i)).ToArray(),
            ADto = new[] { DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1) }, LRaw = new() { new RawS { A = 1, B = true }, new RawS { A = 2 } },
            AP = Enumerable.Range(0, 33).Select(i => new Pair2 { A = (byte)i, B = i }).ToArray(),
            HI = new() { 1, 2, 3, 100, -5 }, HS = new() { "a", "b", "c" }, HL = new() { 1L << 40, 5, 6 },
            DIS = new() { [1] = "one", [2] = "two", [3] = null }, DLI = new() { [10] = 1, [20] = 2 }, DSI = new() { ["k"] = 1, ["l"] = 2 },
            DII = new() { [1] = 1, [2] = 2, [3] = 3 }, SSI = new() { 1, 5, 9, 12 }, SSS = new() { "a", "m", "z" },
            SDI = new() { [1] = 2, [3] = 4, [5] = 6 }, SDS = new() { ["a"] = "b", ["c"] = "d" },
            SLI = new() { [1] = "a", [2] = "b", [9] = "c" }, SLS = new() { ["x"] = 1, ["y"] = 2 },
            LLI = new(new[] { 1, 2, 3, 4 }), LLS = new(new[] { "p", "q" }), QI = new(new[] { 5, 6 }), StS = new(new[] { "s", "t" }),
            QS = new(new[] { "u" }), CD = new(new Dictionary<int, int> { [1] = 1, [2] = 2 }), CQ = new(new[] { 1, 2 }), CS = new(new[] { 3, 4 }),
            M2 = new int[2, 3] { { 1, 2, 3 }, { 4, 5, 6 } }, M3 = new string[1, 2, 2] { { { "a", "b" }, { "c", null } } },
            LNI = new() { 1, null, 3 }, ANI = new int?[] { null, 2 }, AB = new[] { true, false }, LB = new() { true },
            AC = new[] { Color.Red, Color.Blue }, LLInt = new() { new() { 1 }, new() { 2, 3 } }, DNested = new() { [1] = new() { "a" } },
            Look = new[] { 1, 1, 2 }.ToLookup(i => i, i => i.ToString()), IL = new List<int> { 9, 8 }, IE = new[] { "e1", "e2" },
            IRD = new Dictionary<int, int> { [5] = 6 }, Mem = new[] { 1, 2, 3 }, Seg = new ArraySegment<int>(new[] { 4, 5 }),
        };

        public static Colls CollsLarge() => new Colls
        {
            LI = Enumerable.Range(0, 300).ToList(), HI = Enumerable.Range(0, 200).ToHashSet(),
            DII = Enumerable.Range(0, 150).ToDictionary(i => i, i => -i), SSI = new(Enumerable.Range(0, 120).Select(i => i * 3)),
            SDI = new(Enumerable.Range(0, 90).ToDictionary(i => i, i => i)), LLI = new(Enumerable.Range(0, 64)),
            SLS = new(Enumerable.Range(0, 40).ToDictionary(i => "k" + i, i => i)), HS = Enumerable.Range(0, 50).Select(i => "s" + i).ToHashSet(),
        };

        public static Imm Imm() => new Imm
        {
            IA = ImmutableArray.Create(1, 2, 3), IL = ImmutableList.Create("a", "b"), IHI = ImmutableHashSet.Create(1, 2, 3, 4, 5),
            IHS = ImmutableHashSet.Create("x", "y"),
            IDI = ImmutableDictionary.CreateRange(Enumerable.Range(0, 10).Select(i => new KeyValuePair<int, string>(i, "v" + i))),
            IDS = ImmutableDictionary.CreateRange(new[] { new KeyValuePair<string, int>("a", 1), new KeyValuePair<string, int>("b", 2) }),
            ISS = ImmutableSortedSet.Create(1, 4, 9),
            ISD = ImmutableSortedDictionary.CreateRange(new[] { new KeyValuePair<string, int>("a", 1), new KeyValuePair<string, int>("z", 2) }),
            IQ = ImmutableQueue.Create(1, 2), IS = ImmutableStack.Create("s1", "s2"), IAS = ImmutableArray.Create("q", "r"),
            ISSS = ImmutableSortedSet.Create("m", "n", "o"),
            IDShape = ImmutableDictionary.CreateRange(new[] { new KeyValuePair<long, Shape>(1, new Circle { Id = 1 }), new KeyValuePair<long, Shape>(2, null) }),
        };

        public static Imm ImmLarge() => new Imm
        {
            IHI = ImmutableHashSet.CreateRange(Enumerable.Range(0, 300)),
            IDI = ImmutableDictionary.CreateRange(Enumerable.Range(0, 200).Select(i => new KeyValuePair<int, string>(i * 31, null))),
            ISS = ImmutableSortedSet.CreateRange(Enumerable.Range(0, 150)), IL = ImmutableList.CreateRange(Enumerable.Range(0, 80).Select(i => "" + i)),
        };

        public static Holder Holder()
        {
            var t = new TNode { V = 1 };
            var leaf = new TLeaf { V = 2, S = "leaf", Next = t };
            t.Next = leaf; t.Other = t; t.Any = leaf;
            var o = new TOther { S = "o", Back = t };
            return new Holder
            {
                O = new Dog { Name = "rex", Friend = new Cat { Lives = 9 } }, T = t, TO = o,
                S = new Square { Id = 3, S = 2f, Inner = new Circle { Id = 4, R = 1 }, More = new() { new Circle() } },
                A = new Cat { Lives = 3 }, Objs = new() { leaf, o, new Circle { Id = 9 }, null, t, new RawS { A = 1 }, new Pair2 { B = 2 } },
            };
        }

        public static Node Chain(int n) { Node h = null; for (int i = 0; i < n; i++) h = new Node { V = i, Next = h }; return h; }

        public static VNode VChain(int n)
        {
            VNode h = null;
            for (int i = 0; i < n; i++) h = new VNode { V = i, Next = h, S = i % 50 == 0 ? "s" + i : null };
            return h;
        }

        public static KNode KChain(int n)
        {
            KNode h = null;
            for (int i = 0; i < n; i++) h = new KNode { V = i, Next = h, L = i % 40 == 0 ? new() { i } : null };
            return h;
        }

        public static VBase VTree()
        {
            VBase h = new VBase { A = -1 };
            for (int i = 0; i < 200; i++)
                h = i % 3 == 0
                    ? new VDerived { A = i, Child = h, B = "b", Kids = i % 30 == 0 ? new() { new VBase { A = 5 } } : null }
                    : new VBase { A = i, Child = h };
            return h;
        }

        public static Shape Shapes()
        {
            Shape s = new Circle { Id = 1, R = 2 };
            for (int i = 0; i < 160; i++) s = new Square { Id = i, Inner = s, More = i % 40 == 0 ? new() { new Circle { Id = 7 } } : null };
            return s;
        }

        public static TNode Tracked()
        {
            var nodes = Enumerable.Range(0, 200).Select(i => i % 4 == 0 ? new TLeaf { V = i, S = "l" } : new TNode { V = i }).ToArray();
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i].Next = nodes[(i + 1) % nodes.Length];
                nodes[i].Other = nodes[(i * 7) % nodes.Length];
                if (i % 10 == 0) nodes[i].Any = new TOther { S = "x", Back = nodes[i] };
            }
            return nodes[0];
        }

        public static Nest Nest()
        {
            var s = new SNode { V = 0 };
            for (int i = 1; i < 20; i++) s = new SNode { V = i, Kids = new() { s, new SNode { V = -i } } };
            var r = new Rec { V = 0 };
            for (int i = 1; i < 30; i++) r = new Rec { V = i, Kids = new() { r } };
            var sr = new SRec { V = 0 };
            for (int i = 1; i < 25; i++) sr = new SRec { V = i, Kids = new[] { sr } };
            var d = new DictRec();
            for (int i = 1; i < 20; i++) d = new DictRec { D = new() { [i] = d, [-i] = null } };
            var v = new VRecList();
            for (int i = 1; i < 20; i++) v = new VRecList { K = new() { v, null } };
            return new Nest
            {
                S = s, R = r, SR = sr, D = d, V = v, L3 = new() { new() { new() { 1, 2 }, null }, new() },
                DD = new() { ["a"] = new() { [1] = new() { "x", null } } }, Empties = new[] { new List<Empty> { default, default }, null },
            };
        }
    }
}
