using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Conformance.Targets;
using Nino.Core;
using static Conformance.ValidCases;

namespace Conformance
{
    /// <summary>An input every runtime must reject: an error, without crashing, hanging or unbounded memory.</summary>
    internal sealed class MaliciousCase
    {
        public string Name;
        public Type Type;
        public string Category;
        public string Exercises;
        public string Optional;
        public byte[] Bytes;
        /// <summary><c>error</c>, or <c>error-or-value</c> where .NET reads the input by design.</summary>
        public string Expected = "error";
    }

    internal static class MaliciousCases
    {
        private static readonly byte[] NotNull = { 1 };

        public static List<MaliciousCase> All(List<ValidCase> valid)
        {
            var cases = new List<MaliciousCase>();
            var validBytes = valid.ToDictionary(c => c.Name, c => c.Write());

            // Inputs that differ from a malicious one only in its defect, which .NET must read: they show the
            // malicious input is wrong where it means to be, not elsewhere.
            var controls = new List<(string Name, Type Type, byte[] Bytes)>();

            void Control<T>(string name, params byte[][] parts) => controls.Add((name, typeof(T), Concat(parts)));

            void Add<T>(string category, string name, string exercises, params byte[][] parts) =>
                cases.Add(new MaliciousCase
                {
                    Name = $"{category}-{name}", Type = typeof(T), Category = category, Exercises = exercises,
                    Bytes = Concat(parts),
                });

            // ---------------------------------------------------------- collection lengths

            void Lengths<T>(string what, int elementSize, byte[] twoElements)
            {
                Control<T>(what, NotNull, I32(2), twoElements);
                Add<T>("length", $"{what}-int-max", $"{what}: a count of int.MaxValue with 8 bytes of data.",
                    NotNull, I32(int.MaxValue), new byte[8]);
                Add<T>("length", $"{what}-minus-2", $"{what}: a count of -2 (only -1, null, is negative).",
                    NotNull, I32(-2), twoElements);
                Add<T>("length", $"{what}-int-min", $"{what}: a count of int.MinValue.", NotNull, I32(int.MinValue),
                    twoElements);
                Add<T>("length", $"{what}-past-end", $"{what}: a count of 3 with 2 elements' bytes.", NotNull, I32(3),
                    twoElements);
                Add<T>("length", $"{what}-gb-claim",
                    $"{what}: a 25-byte input claiming 100,000,000 elements; a reader must not allocate for the " +
                    "count before the data is there.", NotNull, I32(100_000_000), twoElements);
                if (elementSize > 0)
                {
                    // count * size wraps to exactly the bytes present in 32-bit arithmetic.
                    int count = (int)((1L << 32) / elementSize + 1);
                    Add<T>("length", $"{what}-size-overflow",
                        $"{what}: a count of 0x{count:X8}, whose byte size ({elementSize} a element) is 2^32 + " +
                        $"{elementSize}: {elementSize} bytes in 32-bit arithmetic, which are present.",
                        NotNull, I32(count), new byte[elementSize]);
                }
            }

            Lengths<IntArray>("int-array", 4, Concat(I32(1), I32(2)));
            Lengths<LongList>("long-list", 8, Concat(I64(1), I64(2)));
            Lengths<PaddedArray>("padded-array", 16, new byte[32]);
            Lengths<ByteArray>("byte-array", 0, new byte[] { 1, 2 });
            Lengths<StringList>("string-list", 0, Concat(V(1), V(0)));
            Lengths<PointList>("point-list", 0, Concat(NotNull, I32(1), I32(2), new byte[] { 0 }));
            Lengths<IntMap>("int-map", 8, Concat(I32(1), I32(2), I32(3), I32(4)));
            Lengths<StringSet>("string-set", 0, Concat(V(3), new byte[] { (byte)'a' }, V(1)));

            Control<Grid>("grid", NotNull, I32(2), I32(2), I32(1), I32(2), I32(3), I32(4));
            Control<BigSlot>("bigint", NotNull, I32(4), new byte[4]);
            Add<Grid>("length", "grid-minus-2", "int[,]: a first length of -2.", NotNull, I32(-2), I32(1), I32(0));
            Add<Grid>("length", "grid-negative-second", "int[,]: a second length of -1.", NotNull, I32(1), I32(-1),
                I32(0));
            Add<Grid>("length", "grid-product-overflow",
                "int[,]: 65536 x 65536 elements, 2^32 (0 in 32-bit arithmetic), with no data.", NotNull, I32(65536),
                I32(65536));
            Add<Grid>("length", "grid-past-end", "int[,]: 2 x 2 elements with 3 present.", NotNull, I32(2), I32(2),
                I32(1), I32(2), I32(3));
            Add<Grid>("length", "grid-gb-claim", "int[,]: 10000 x 10000 elements with 8 bytes present.", NotNull,
                I32(10_000), I32(10_000), new byte[8]);
            Add<BigSlot>("length", "bigint-int-max", "BigInteger: a byte length of int.MaxValue.", NotNull,
                I32(int.MaxValue), new byte[4]);
            Add<BigSlot>("length", "bigint-negative", "BigInteger: a byte length of -2.", NotNull, I32(-2), new byte[4]);
            Add<BigSlot>("length", "bigint-past-end", "BigInteger: a byte length of 9 with 4 bytes present.", NotNull,
                I32(9), new byte[4]);

            // ---------------------------------------------------------- string headers

            Control<TextValue>("latin1", NotNull, V(7), new byte[] { 1, 2, 3 });
            Control<TextValue>("utf16", NotNull, V(4), Utf16("ab"));
            Control<TextValue>("latin1-three-byte-header", NotNull, V(129), new byte[64]);
            Control<TextValue>("latin1-five-byte-header", NotNull, V(65537), new byte[32768]);
            Add<TextValue>("string", "latin1-max",
                "A header of 2^32 - 1: 2^31 - 1 Latin-1 bytes, 3 present.", NotNull, V(uint.MaxValue),
                new byte[] { 1, 2, 3 });
            Add<TextValue>("string", "latin1-past-end", "A header of 7 (3 Latin-1 bytes) with 1 present.", NotNull,
                V(7), new byte[] { (byte)'a' });
            Add<TextValue>("string", "utf16-max",
                "A header of 2^32 - 2: 2^31 - 1 UTF-16 code units (2^32 - 2 bytes) with 4 bytes present.", NotNull,
                V(uint.MaxValue - 1), new byte[4]);
            Add<TextValue>("string", "utf16-size-overflow",
                "A header of 2^31: 0x40000000 UTF-16 code units, 2^31 bytes, negative in 32-bit arithmetic.", NotNull,
                V(0x80000000), new byte[4]);
            Add<TextValue>("string", "utf16-size-wrap",
                "A header of 0x80000002: 0x40000001 code units, whose byte size wraps past 2^31.", NotNull,
                V(0x80000002), new byte[4]);
            Add<TextValue>("string", "utf16-past-end", "A header of 10 (5 UTF-16 code units) with 2 present.", NotNull,
                V(10), Utf16("ab"));
            Add<TextValue>("string", "utf16-odd-bytes", "A header of 4 (2 UTF-16 code units) with 3 bytes present.",
                NotNull, V(4), new byte[] { (byte)'a', 0, (byte)'b' });
            Add<TextValue>("string", "gb-claim",
                "A header of 1,000,000,000: 500,000,000 UTF-16 code units in a 13-byte input.", NotNull,
                V(1_000_000_000), new byte[8]);
            Add<TextValue>("string", "header-not-minimal",
                "The header 7 (\"abc\") written in three bytes (80 07 00): a header must be minimal.", NotNull,
                new byte[] { 0x80, 0x07, 0x00 }, new byte[] { 0x61, 0x62, 0x63 });
            Add<TextValue>("string", "header-not-minimal-null",
                "The null header 0 written in three bytes (80 00 00).", NotNull, new byte[] { 0x80, 0x00, 0x00 });
            Add<TextValue>("string", "header-not-minimal-5-bytes",
                "The header 129 written in five bytes (81 81 00 00 00).", NotNull,
                new byte[] { 0x81, 0x81, 0x00, 0x00, 0x00 }, new byte[64]);
            Add<TextValue>("string", "header-not-minimal-65535",
                "The header 65535 written in five bytes (81 FF FF 00 00): three bytes hold it.", NotNull,
                new byte[] { 0x81, 0xFF, 0xFF, 0x00, 0x00 }, new byte[8]);
            Add<TextValue>("string", "header-marker-82",
                "A header whose first byte is 0x82 (only 0x80 and 0x81 start a longer header).", NotNull,
                new byte[] { 0x82, 0x07, 0x00, 0x00, 0x00 }, new byte[8]);
            Add<TextValue>("string", "header-marker-ff",
                "A header whose first byte is 0xFF.", NotNull,
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }, new byte[8]);
            Add<TextValue>("string", "header-cut",
                "A three-byte header cut by the end of the input (80 81).", NotNull, new byte[] { 0x80, 0x81 });
            Add<TextValue>("string", "header-cut-5-bytes",
                "A five-byte header cut by the end of the input (81 00 00 01).", NotNull,
                new byte[] { 0x81, 0x00, 0x00, 0x01 });

            void Utf8(string name, string exercises, params byte[][] parts) =>
                Add<Utf8Value>("utf8", name, exercises, Concat(NotNull, Concat(parts)));

            Control<Utf8Value>("utf8", NotNull, V(4), V(5), new byte[] { 0x61, 0xC3, 0xA9, 0xC3, 0xA9 });
            Control<Utf8Value>("utf8-astral", NotNull, V(3), V(4), new byte[] { 0xF0, 0x9F, 0x98, 0x80 });
            Utf8("char-count-past-max", "[NinoUtf8]: a char count of 2^31 (a header of 2^31 + 1).",
                V(0x80000001), V(1), new byte[] { 0x61 });
            Utf8("char-count-not-minimal", "[NinoUtf8]: the char count header 2 written in three bytes (80 02 00).",
                new byte[] { 0x80, 0x02, 0x00 }, V(1), new byte[] { 0x61 });
            Utf8("byte-count-not-minimal", "[NinoUtf8]: the byte count 1 written in five bytes (81 01 00 00 00).",
                V(2), new byte[] { 0x81, 0x01, 0x00, 0x00, 0x00 }, new byte[] { 0x61 });
            Utf8("byte-count-past-max", "[NinoUtf8]: a byte count of 2^31.", V(2), V(0x80000000), new byte[] { 0x61 });
            Utf8("byte-count-bad-marker", "[NinoUtf8]: a byte count whose first byte is 0xFF (no header starts so).",
                V(2), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x1F }, new byte[] { 0x61 });
            Utf8("byte-count-cut", "[NinoUtf8]: a char count with its byte count cut by the end of the input.",
                V(2), new byte[] { 0x80, 0x81 });
            Utf8("byte-count-below-chars", "[NinoUtf8]: 3 chars in 2 bytes (every char takes a byte).", V(4), V(2),
                new byte[] { 0x61, 0x62 });
            Utf8("byte-count-above-3x", "[NinoUtf8]: 1 char in 4 bytes (a char takes at most 3).", V(2), V(4),
                new byte[] { 0x61, 0x62, 0x63, 0x64 });
            Utf8("byte-count-past-end", "[NinoUtf8]: 2 chars, 100 bytes, 2 present.", V(3), V(100),
                new byte[] { 0x61, 0x62 });
            Utf8("char-count-gb-claim", "[NinoUtf8]: 400,000,000 chars, 1,200,000,000 bytes, 3 present.",
                V(400_000_001), V(1_200_000_000), new byte[] { 0x61, 0x62, 0x63 });
            Utf8("overlong", "[NinoUtf8]: an overlong encoding of U+0000 (C0 80).", V(2), V(2),
                new byte[] { 0xC0, 0x80 });
            Utf8("overlong-3-byte", "[NinoUtf8]: an overlong 3-byte encoding of '/' (E0 80 AF).", V(2), V(3),
                new byte[] { 0xE0, 0x80, 0xAF });
            Utf8("encoded-surrogate", "[NinoUtf8]: an encoded surrogate U+D800 (ED A0 80).", V(2), V(3),
                new byte[] { 0xED, 0xA0, 0x80 });
            Utf8("cut-sequence", "[NinoUtf8]: a 3-byte sequence cut after 2 bytes (E2 82).", V(2), V(2),
                new byte[] { 0xE2, 0x82 });
            Utf8("lone-continuation", "[NinoUtf8]: a continuation byte alone (80).", V(2), V(1),
                new byte[] { 0x80 });
            Utf8("invalid-byte", "[NinoUtf8]: the byte FF, never valid in UTF-8.", V(2), V(1), new byte[] { 0xFF });
            Utf8("five-byte-form", "[NinoUtf8]: an obsolete 5-byte form (F8 88 80 80 80).", V(3), V(5),
                new byte[] { 0xF8, 0x88, 0x80, 0x80, 0x80 });
            Utf8("above-max-code-point", "[NinoUtf8]: U+110000 (F4 90 80 80), past the last code point.", V(3),
                V(4), new byte[] { 0xF4, 0x90, 0x80, 0x80 });
            Utf8("more-chars-than-count", "[NinoUtf8]: a char count of 1 for bytes that decode to 2 chars (\"ab\").",
                V(2), V(2), new byte[] { 0x61, 0x62 });
            Utf8("fewer-chars-than-count",
                "[NinoUtf8]: a char count of 3 for bytes that decode to 2 chars (a surrogate pair, F0 9F 98 80).",
                V(4), V(4), new byte[] { 0xF0, 0x9F, 0x98, 0x80 });

            // ---------------------------------------------------------- bool and presence bytes

            Control<BoolValue>("bool", NotNull, new byte[] { 1 });
            Control<BoolArray>("bool-array", NotNull, I32(2), new byte[] { 1, 0 });
            Control<TextValue>("null-flag", new byte[] { 1 }, V(1));
            Control<MaybeInt>("nullable", NotNull, new byte[] { 1 }, I32(7));
            Control<MaybeIntArray>("nullable-array", NotNull, I32(1), new byte[] { 1, 0, 0, 0 }, I32(7));
            Control<Integers>("root-null-flag", new byte[] { 1 }, new byte[62]);
            foreach (byte b in new byte[] { 2, 0xFF })
            {
                string hex = b.ToString("x2");
                Add<BoolValue>("presence", $"bool-{hex}", $"A bool byte of 0x{hex} (only 0 and 1 are bools).", NotNull,
                    new[] { b });
                Add<BoolArray>("presence", $"bool-array-{hex}", $"A bool[] element of 0x{hex}.", NotNull, I32(2),
                    new byte[] { 1, b });
                Add<TextValue>("presence", $"null-flag-{hex}",
                    $"A sealed class's null flag of 0x{hex} (only 0 and 1).", new[] { b }, V(1));
                Add<MaybeInt>("presence", $"nullable-flag-{hex}", $"A nullable's has-value byte of 0x{hex}.", NotNull,
                    new[] { b }, I32(7));
                Add<MaybeIntArray>("presence", $"nullable-array-flag-{hex}",
                    $"An int?[] element (fixed stride: flag, 3 padding bytes, value) whose flag is 0x{hex}.", NotNull,
                    I32(1), new byte[] { b, 0, 0, 0 }, I32(7));
                Add<Integers>("presence", $"root-null-flag-{hex}", $"The top-level object's null flag of 0x{hex}.",
                    new[] { b }, new byte[62]);
            }

            // ---------------------------------------------------------- type ids

            int Id(Type t) => NinoTypeId.Of(t);
            Control<AnimalSlot>("type-id", NotNull, I32(Id(typeof(Cat))), V(0), new byte[4]);
            Control<DogSlot>("type-id-dog", NotNull, I32(Id(typeof(Puppy))), V(0), I32(0), new byte[] { 1 });
            Control<ShapeSlot>("type-id-shape", NotNull, I32(Id(typeof(Square))), new byte[16]);
            Control<ObjectSlot>("type-id-object", NotNull, I32(Id(typeof(Point))), new byte[] { 1 }, new byte[8]);
            Control<AnimalSlot>("type-id-generic", NotNull, I32(NinoTypeId.Compute("Conformance.Box<System.Int32>")),
                V(0), I32(5));
            Add<AnimalSlot>("type-id", "unknown", "An Animal member whose type id belongs to no type.", NotNull,
                I32(0x12345678), I32(0), I32(0));
            Add<AnimalSlot>("type-id", "unrelated-type",
                "An Animal member holding the type id of Square, an IShape that is not an Animal.", NotNull,
                I32(Id(typeof(Square))), new byte[16]);
            Add<AnimalSlot>("type-id", "abstract-base",
                "An Animal member holding the type id of Animal itself, which is abstract.", NotNull,
                I32(Id(typeof(Animal))), V(0));
            Add<DogSlot>("type-id", "sibling-type",
                "A Dog member holding the type id of Cat, an Animal that is not a Dog.", NotNull, I32(Id(typeof(Cat))),
                V(0), new byte[4]);
            Add<ShapeSlot>("type-id", "sealed-non-implementer",
                "An IShape member holding the type id of Point, a [NinoType] that does not implement IShape.",
                NotNull, I32(Id(typeof(Point))), new byte[8]);
            Add<ShapeSlot>("type-id", "interface-id", "An IShape member holding the type id of IShape itself.", NotNull,
                I32(Id(typeof(IShape))), new byte[8]);
            Add<ObjectSlot>("type-id", "object-unknown", "An object member whose type id belongs to no type.", NotNull,
                I32(0x7EADBEEF), new byte[8]);
            Add<AnimalSlot>("type-id", "unclosed-generic",
                "An Animal member holding the id of Box<T> with T unbound (the open definition's name).", NotNull,
                I32(NinoTypeId.Compute("Conformance.Box<T>")), V(0), I32(0));
            Add<AnimalSlot>("type-id", "generic-not-in-use",
                "An Animal member holding the id of Box<System.Guid>, a closed type no code uses.", NotNull,
                I32(NinoTypeId.Compute("Conformance.Box<System.Guid>")), V(0), new byte[16]);

            // ---------------------------------------------------------- nesting

            // Each built at 10,000 levels (the case) and at 3 (the control, which .NET must read).
            void Nest<T>(string name, string exercises, Func<int, byte[]> build, bool mayRead = false)
            {
                Add<T>("depth", name, exercises, build(10_000));
                if (mayRead)
                    cases[^1].Expected = "error-or-value";
                Control<T>(name, build(3));
            }

            byte[] Repeat(int depth, Func<int, byte[]> level, byte[] end)
            {
                var w = new MemoryStream();
                for (int i = 0; i < depth; i++)
                    w.Write(level(i));
                w.Write(end);
                return w.ToArray();
            }

            Nest<Chain>("chain-inline-10k",
                "A sealed chain nested inline 10,000 deep (a writer defers past 128 open bodies). Outside any region " +
                ".NET reads deep inline data as deferred bodies by design (docs/v4-design.md, Deep nesting), so it " +
                "may be read; a reader must not use stack in proportion to the depth.",
                d => Repeat(d, i => Concat(NotNull, I32(i)), new byte[] { 0 }), true);
            Nest<Link>("links-inline-10k",
                "A polymorphic chain (type ids) nested inline 10,000 deep; may be read, as chain-inline-10k.",
                d => Repeat(d, i => Concat(I32(Id(typeof(RedLink))), I32(i)), I32(0)), true);
            Nest<TreeNode>("tree-inline-10k",
                "A tree nested inline 10,000 deep through List<TreeNode>; may be read, as chain-inline-10k.",
                d => Repeat(d, i => Concat(NotNull, I32(i), I32(1)), Concat(NotNull, I32(d), I32(-1))), true);
            Nest<RecordChain>("record-inline-10k",
                "A record chain (built by its constructor, never deferred) nested inline 10,000 deep, past the limit " +
                "of 512 open bodies.",
                d => Repeat(d, i => Concat(NotNull, I32(i)), new byte[] { 0 }));
            Add<RecordChain>("depth", "record-inline-513",
                "A record chain nested inline 513 deep, one past the limit of 512 open bodies (deep-records-512 is " +
                "valid).", Repeat(513, i => Concat(NotNull, I32(i)), new byte[] { 0 }));
            Nest<TolerantChain>("tolerant-inline-10k",
                "A version-tolerant chain nested inline 10,000 deep, region in region (a writer defers into the " +
                "outermost region's section past 128).",
                d =>
                {
                    byte[] body = Concat(I32(0), new byte[] { 0 });
                    for (int i = 1; i < d; i++)
                        body = Concat(I32(i), NotNull, I32(body.Length), body);
                    return Concat(NotNull, I32(body.Length), body);
                });
            Nest<KeyedChain>("keyed-inline-10k", "A keyed chain nested inline 10,000 deep, region in region.",
                d =>
                {
                    int value = Key(typeof(KeyedChain), "Value"), next = Key(typeof(KeyedChain), "Next");
                    byte[] body = Concat(I32(value), I32(4), I32(0), I32(next), I32(1), new byte[] { 0 });
                    for (int i = 1; i < d; i++)
                    {
                        byte[] child = Concat(NotNull, I32(body.Length), body);
                        body = Concat(I32(value), I32(4), I32(i), I32(next), I32(child.Length), child);
                    }

                    return Concat(NotNull, I32(body.Length), body);
                });
            Nest<TreeHolder>("self-list-10k",
                "class Tree : List<Tree> nested 10,000 deep: 4 bytes a level, no class body in between, so no " +
                "depth count applies.",
                d => Concat(NotNull, Repeat(d, _ => I32(1), I32(0)), I32(-1)));
            Nest<TreeHolder>("self-dictionary-10k",
                "class DictTree : Dictionary<string, DictTree> nested 10,000 deep (key \"\" at each level).",
                d => Concat(NotNull, I32(-1), Repeat(d, _ => Concat(I32(1), V(1)), I32(0))));

            // ---------------------------------------------------------- deferred sections

            {
                // deep-tolerant-chain-300: 01 | int32 (length | 0x80000000) | members... | section | int32 offset.
                byte[] valid300 = validBytes["deep-tolerant-chain-300"];
                int length = BitConverter.ToInt32(valid300, 1) & 0x7FFFFFFF;
                int regionStart = 5, offsetAt = regionStart + length - 4;
                int sectionOffset = BitConverter.ToInt32(valid300, offsetAt);
                int entry = regionStart + sectionOffset;

                byte[] With(int at, int value)
                {
                    byte[] copy = (byte[])valid300.Clone();
                    BitConverter.GetBytes(value).CopyTo(copy, at);
                    return copy;
                }

                void Section(string name, string exercises, byte[] bytes) =>
                    cases.Add(new MaliciousCase
                    {
                        Name = "deferred-" + name, Type = typeof(TolerantChain), Category = "deferred",
                        Exercises = "deep-tolerant-chain-300 with " + exercises, Bytes = bytes,
                    });

                Section("offset-minus-1", "the section offset set to -1.", With(offsetAt, -1));
                Section("offset-past-region", "the section offset past the end of the region.",
                    With(offsetAt, length + 4));
                Section("offset-zero", "the section offset set to 0 (the members read as the section).",
                    With(offsetAt, 0));
                Section("offset-into-members", "the section offset pointing into the members.", With(offsetAt, 4));
                Section("offset-at-itself", "the section offset pointing at the offset itself (an empty section).",
                    With(offsetAt, length - 4));
                Section("entry-length-past-end", "the first entry's length past the end of the region.",
                    With(entry + 4, length));
                Section("entry-length-negative", "the first entry's length set to -1.", With(entry + 4, -1));
                Section("entry-key-unknown", "the first entry's key changed to a position nothing was deferred at.",
                    With(entry, 0x7FFF0000));
                Section("no-section-flag", "the section flag (0x80000000) cleared from the region length.",
                    With(1, length));
                Section("flag-without-section",
                    "a region flagged as holding a section on data whose region holds none (tolerant-old-to-new's " +
                    "TolerantV1 bytes, read as TolerantChain).", Array.Empty<byte>());
                // Replace the last one: a small TolerantChain with the flag set and no section.
                cases[^1].Bytes = Concat(NotNull, I32(9 | int.MinValue), I32(1), new byte[] { 0 }, I32(0));
                cases[^1].Exercises = "A TolerantChain region flagged as holding a deferred section whose offset " +
                                      "(the last 4 bytes) points at itself, with nothing deferred.";

                byte[] chain = validBytes["deep-chain-300"];
                cases.Add(new MaliciousCase
                {
                    Name = "deferred-positional-missing", Type = typeof(Chain), Category = "deferred",
                    Exercises = "deep-chain-300 without its last deferred body (the bytes after the top-level object).",
                    Bytes = chain.AsSpan(0, chain.Length - 9).ToArray(),
                });
            }

            // ---------------------------------------------------------- regions

            {
                byte[] keyed = NinoSerializer.Serialize(new KeyedV1 { Health = 1, Name = "ab", Items = new List<int> { 1 }, Score = 2 });
                int body = BitConverter.ToInt32(keyed, 1);

                byte[] With(int at, int value)
                {
                    byte[] copy = (byte[])keyed.Clone();
                    BitConverter.GetBytes(value).CopyTo(copy, at);
                    return copy;
                }

                void Keyed(string name, string exercises, byte[] bytes) =>
                    cases.Add(new MaliciousCase
                    {
                        Name = "keyed-" + name, Type = typeof(KeyedV1), Category = "keyed",
                        Exercises = "A KeyedV1 body with " + exercises, Bytes = bytes,
                    });

                controls.Add(("keyed", typeof(KeyedV1), keyed));
                // Body: Health (key, 4, int) at 5, Name (key, length, string "ab": 05 61 62) at 17, Items at 28, ...
                Keyed("body-past-end", "a body length 1 past the end of the input.", With(1, body + 1));
                Keyed("body-negative", "a body length of -5.", With(1, -5));
                Keyed("body-int-max", "a body length of int.MaxValue.", With(1, int.MaxValue));
                Keyed("body-section-flag", "the deferred-section flag set on a body without a section.",
                    With(1, body | int.MinValue));
                Keyed("member-past-body", "the first member's length past the end of the body.", With(9, body));
                Keyed("member-negative", "the first member's length set to -1.", With(9, -1));
                Keyed("member-too-short", "an int member whose length is 2.", With(9, 2));
                Keyed("member-string-cut",
                    "the string member's length 1 byte short of its encoding (the string runs past its member).",
                    With(21, BitConverter.ToInt32(keyed, 21) - 1));
                Keyed("member-string-long",
                    "the string member's length 1 byte past its encoding (bytes left over in the member).",
                    Concat(With(1, body + 1).AsSpan(0, 21).ToArray(), I32(BitConverter.ToInt32(keyed, 21) + 1),
                        keyed.AsSpan(25, 3).ToArray(), new byte[] { 0 }, keyed.AsSpan(28).ToArray()));

                byte[] tolerant = NinoSerializer.Serialize(new TolerantV1 { A = 1, B = "ab" });
                int region = BitConverter.ToInt32(tolerant, 1);

                byte[] TolerantWith(int value)
                {
                    byte[] copy = (byte[])tolerant.Clone();
                    BitConverter.GetBytes(value).CopyTo(copy, 1);
                    return copy;
                }

                void Tolerant(string name, string exercises, byte[] bytes) =>
                    cases.Add(new MaliciousCase
                    {
                        Name = "tolerant-" + name, Type = typeof(TolerantV1), Category = "tolerant",
                        Exercises = "A TolerantV1 body with " + exercises, Bytes = bytes,
                    });

                controls.Add(("tolerant", typeof(TolerantV1), tolerant));
                Tolerant("region-past-end", "a region length 1 past the end of the input.", TolerantWith(region + 1));
                Tolerant("region-negative", "a region length of -2.", TolerantWith(-2));
                Tolerant("region-int-max", "a region length of int.MaxValue.", TolerantWith(int.MaxValue));
                Tolerant("region-cuts-member", "a region length that ends inside the string member.",
                    TolerantWith(region - 1));
                Tolerant("region-section-flag", "the deferred-section flag set, with no section.",
                    TolerantWith(region | int.MinValue));
            }

            // ---------------------------------------------------------- duplicate keys

            Control<IntMap>("distinct", NotNull, I32(2), I32(1), I32(10), I32(2), I32(20));
            Control<StringMap>("distinct", NotNull, I32(2), V(3), new byte[] { (byte)'a' }, I32(1), V(2), Utf16("b"), I32(2));
            Control<StringSet>("distinct", NotNull, I32(2), V(1), V(0));
            Control<SortedMap>("distinct", NotNull, I32(2), I32(3), I32(1), I32(4), I32(2));
            Add<IntMap>("duplicate", "int-map", "Dictionary<int, int> with the key 1 twice.", NotNull, I32(2), I32(1),
                I32(10), I32(1), I32(20));
            Add<StringMap>("duplicate", "string-map", "Dictionary<string, int> with the key \"a\" twice.", NotNull,
                I32(2), V(3), new byte[] { (byte)'a' }, I32(1), V(3), new byte[] { (byte)'a' }, I32(2));
            Add<StringMap>("duplicate", "string-map-encodings",
                "Dictionary<string, int> with the key \"a\" twice, once Latin-1 and once UTF-16.", NotNull, I32(2),
                V(3), new byte[] { (byte)'a' }, I32(1), V(2), Utf16("a"), I32(2));
            Add<IntSet>("duplicate", "int-set", "HashSet<int> with 7 twice.", NotNull, I32(2), I32(7), I32(7));
            Add<StringSet>("duplicate", "string-set", "HashSet<string> with \"\" twice.", NotNull, I32(2), V(1), V(1));
            Add<StringSet>("duplicate", "string-set-null", "HashSet<string> with null twice.", NotNull, I32(2), V(0),
                V(0));
            Add<SortedMap>("duplicate", "sorted-map", "SortedDictionary<int, int> with the key 3 twice.", NotNull,
                I32(2), I32(3), I32(1), I32(3), I32(2));

            // ---------------------------------------------------------- other

            Control<Point>("point", NotNull, I32(1), I32(2));
            Control<VersionSlot>("version", NotNull, I32(1), I32(0), I32(-1), I32(-1));
            Control<Scalars>("scalars", NotNull, new byte[] { 0 }, new byte[] { 0x61, 0 }, new byte[] { 0, 0, 28, 0 },
                new byte[12], new byte[16], new byte[8], I32(3_652_058), new byte[8]);
            Control<Dates>("dates", NotNull, I64(3155378975999999999), new byte[8], new byte[8], I64(0),
                BitConverter.GetBytes((short)840));
            Add<Point>("trailing", "byte",
                "A valid Point followed by one more byte: the input must be consumed exactly.", NotNull, I32(1), I32(2),
                new byte[] { 0 });
            Add<VersionSlot>("version", "negative-part", "A Version whose minor part is -2 (a major of -1 is null; build " +
                                                         "and revision may be -1, unset; nothing else is negative).",
                NotNull, I32(1), I32(-2), I32(0), I32(0));
            Add<Scalars>("decimal", "bad-scale",
                "A decimal whose scale byte is 29 (at most 28) and whose reserved flag bits are set.", NotNull,
                new byte[] { 0 }, new byte[] { 0x61, 0 }, new byte[] { 0xFF, 0xFF, 29, 0 }, new byte[12],
                new byte[16], new byte[8], new byte[4], new byte[8]);
            Add<Dates>("datetime", "ticks-past-max",
                "A DateTime whose ticks are past DateTime.MaxValue (the low 62 bits).", NotNull,
                I64(0x3FFFFFFFFFFFFFFF), new byte[8], new byte[8], new byte[10]);
            Add<Dates>("datetime", "offset-past-14h",
                "A DateTimeOffset with an offset of 15 hours (at most 14).", NotNull, new byte[24], I64(0),
                BitConverter.GetBytes((short)900));
            Add<Scalars>("dateonly", "day-past-max", "A DateOnly day number past DateOnly.MaxValue.", NotNull,
                new byte[] { 0 }, new byte[2], new byte[16], new byte[16], new byte[8], I32(int.MaxValue),
                new byte[8]);

            // ---------------------------------------------------------- reference tracking (optional)

            {
                int tracked = Id(typeof(Tracked));
                void Refs(string name, string exercises, params byte[][] parts)
                {
                    Add<TrackedHolder>("references", name, exercises, parts);
                    cases[^1].Optional = "track-references";
                }

                Control<TrackedHolder>("references", NotNull, I32(-1), I32(tracked), I32(1), I32(0), I32(0), I32(1),
                    I32(-1), I32(0));
                Refs("back-reference-empty", "A back reference (1) before any object was read.", NotNull, I32(1));
                Refs("back-reference-past", "A back reference (5) past the objects read so far.", NotNull, I32(-1),
                    I32(tracked), I32(1), I32(0), I32(0), I32(5));
                Refs("header-minus-2", "A reference header of -2 (only -1, 0 and n > 0).", NotNull, I32(-2),
                    I32(tracked), I32(1));
                Refs("header-int-min", "A reference header of int.MinValue.", NotNull, I32(int.MinValue));

                // TrackedMix: Any (object: type id, header, body), Node (sealed: header), Base (header, type id),
                // Nodes, ById (null), Tolerant (header).
                byte[] TrackedAny() => Concat(I32(tracked), I32(-1), I32(1), I32(0), I32(0));
                void Mix(string name, string exercises, params byte[][] parts)
                {
                    Add<TrackedMix>("references", name, exercises, parts);
                    cases[^1].Optional = "track-references";
                }

                Control<TrackedMix>("references-mix", NotNull, TrackedAny(), I32(0), I32(1), I32(-1), I32(-1), I32(0));
                Mix("type-mismatch", "A sealed TrackedNode member referring back to object 1, a Tracked.", NotNull,
                    TrackedAny(), I32(1), I32(0), I32(-1), I32(-1), I32(0));
                Refs("object-mismatch",
                    "An object member naming TrackedNode's type id, referring back to object 1, a Tracked.", NotNull,
                    I32(-1), I32(tracked), I32(1), I32(0), I32(0), I32(0), I32(-1), I32(Id(typeof(TrackedNode))),
                    I32(1));
                Mix("base-header-past", "A non-sealed tracked member referring back to object 2 of 1.", NotNull,
                    TrackedAny(), I32(0), I32(2), I32(-1), I32(-1), I32(0));
                Mix("base-unknown-id", "A non-sealed tracked member, new, with the type id of TrackedNode.", NotNull,
                    TrackedAny(), I32(0), I32(-1), I32(Id(typeof(TrackedNode))), I32(5), I32(0), I32(-1), I32(-1),
                    I32(0));
            }

            // ---------------------------------------------------------- [NinoType] collections

            {
                int bag = Id(typeof(Bag));
                // BagHolder: Bag (type id), Tolerant, Keyed, Set (null flags), Bags (count).
                Control<BagHolder>("nino-collection", NotNull, I32(bag), I32(1), I32(7), V(0), new byte[] { 0, 0, 0 },
                    I32(-1));
                Add<BagHolder>("nino-collection", "elements-null",
                    "A [NinoType] collection whose element count is -1 (null): the collection itself is not null.",
                    NotNull, I32(bag), I32(-1), V(0), new byte[] { 0, 0, 0 }, I32(-1));
                Add<BagHolder>("nino-collection", "elements-past-end",
                    "A [NinoType] collection claiming 1,000,000 elements with one present.", NotNull, I32(bag),
                    I32(1_000_000), I32(7), V(0), new byte[] { 0, 0, 0 }, I32(-1));
            }

            foreach (var (name, type, bytes) in controls)
                if (Probe.InProcess(type, bytes) is { Kind: not "returned" } outcome)
                    throw new VerificationException($"control {type.Name} {name} is not read by .NET: {outcome.Exception}");
            return cases;
        }

        /// <summary>The key of a keyed member, from its layout (<c>:key=0x...</c>).</summary>
        private static int Key(Type type, string member)
        {
            foreach (string entry in Layouts.Split(Layouts.Of(type).Members, ';'))
                if (entry.StartsWith(member + ":", StringComparison.Ordinal))
                {
                    int at = entry.IndexOf(":key=0x", StringComparison.Ordinal) + 7;
                    return (int)uint.Parse(entry.Substring(at, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }

            throw new InvalidOperationException($"{type} has no keyed member {member}");
        }

        private static byte[] I64(long value) => BitConverter.GetBytes(value);

        public static JsonObject Describe(MaliciousCase c, Outcome outcome)
        {
            var involved = new SortedDictionary<string, Type>(StringComparer.Ordinal);
            Layouts.Involved(c.Type, involved);
            var layouts = new JsonObject();
            foreach (var (name, type) in involved)
                layouts[name] = Layouts.Of(type).ToString();
            var json = new JsonObject
            {
                ["case"] = c.Name,
                ["type"] = Layouts.Name(c.Type),
                ["category"] = c.Category,
                ["expected"] = c.Expected,
                ["exercises"] = c.Exercises,
            };
            if (c.Optional != null)
                json["optional"] = c.Optional;
            json["size"] = c.Bytes.Length;
            json["layouts"] = layouts;
            json["dotnet"] = outcome.ToJson();
            return json;
        }

        // ---------------------------------------------------------- truncations

        /// <summary>The valid cases whose every strict prefix is an input that must be rejected.</summary>
        public static List<ValidCase> Truncations(List<ValidCase> valid)
        {
            var names = new[]
            {
                "integers-mixed", "integers-null", "floats-values", "scalars-values", "dates-kinds", "enums-min",
                "big-values", "big-values-edges", "string-ascii", "string-latin1", "string-bmp", "string-astral",
                "string-lone-surrogates", "string-collections", "bytes-values", "structs-values", "nested-objects",
                "empties", "struct-root-raw", "struct-root-nested-raw", "struct-root", "struct-root-keyed",
                "collections-values", "nullables-values", "tolerant", "tolerant-new-to-old", "keyed",
                "keyed-v1-to-v2", "polymorphism", "polymorphism-generic-root", "deep-chain-130",
                "deep-tolerant-chain-129", "deep-keyed-chain-129", "deep-links-129", "deep-in-region",
                "references-shared", "tree-collections",
            };
            return names.Select(n => valid.Single(c => c.Name == n)).ToList();
        }

        public static JsonObject DescribeTruncations(List<ValidCase> cases, List<ValidCase> all, string validDir)
        {
            var list = new JsonArray();
            long prefixes = 0;
            var results = new JsonNode[cases.Count];
            System.Threading.Tasks.Parallel.For(0, cases.Count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
                i => results[i] = Probe.Prefixes(cases[i].Type, Path.Combine(validDir, cases[i].Name + ".bin")));
            for (int i = 0; i < cases.Count; i++)
            {
                long size = new FileInfo(Path.Combine(validDir, cases[i].Name + ".bin")).Length;
                prefixes += size;
                var entry = new JsonObject
                {
                    ["case"] = cases[i].Name,
                    ["type"] = Layouts.Name(cases[i].Type),
                    ["prefixes"] = size,
                };
                if (cases[i].Optional != null)
                    entry["optional"] = cases[i].Optional;
                entry["dotnet"] = results[i];
                list.Add(entry);
            }

            return new JsonObject
            {
                ["rule"] = "For each case, every strict prefix of valid/<case>.bin (lengths 0 to size - 1) read as " +
                           "<type> must be an error.",
                ["expected"] = "error",
                ["prefixCount"] = prefixes,
                ["cases"] = list,
            };
        }
    }
}
