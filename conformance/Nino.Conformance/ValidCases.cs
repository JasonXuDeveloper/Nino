using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nino.Core;

namespace Conformance
{
    /// <summary>A value .NET writes, which every runtime must read to <see cref="Value"/> and write back.</summary>
    internal sealed class ValidCase
    {
        public string Name;
        public string Description;
        /// <summary>The type the data is read as.</summary>
        public Type Type;
        /// <summary>The wire features the case exercises (see README.md).</summary>
        public string[] Features;
        /// <summary>A feature a runtime may leave out (reference tracking).</summary>
        public string Optional;
        public Func<byte[]> Write;
        public Func<byte[], object> Read;
        public Func<object, byte[]> Rewrite;
    }

    internal static class ValidCases
    {
        public static List<ValidCase> All()
        {
            var cases = new List<ValidCase>();

            void Add<T>(string name, string description, T value, params string[] features) =>
                cases.Add(new ValidCase
                {
                    Name = name, Description = description, Type = typeof(T), Features = features,
                    Write = () => NinoSerializer.Serialize(value),
                    Read = b => NinoDeserializer.Deserialize<T>(b),
                    Rewrite = v => NinoSerializer.Serialize((T)v),
                });

            void Optional<T>(string feature, string name, string description, T value, params string[] features)
            {
                Add(name, description, value, features);
                cases[^1].Optional = feature;
            }

            // Written as one type, read as another (old and new versions of a type).
            void Across<TWrite, TRead>(string name, string description, TWrite value, params string[] features) =>
                cases.Add(new ValidCase
                {
                    Name = name, Description = description, Type = typeof(TRead), Features = features,
                    Write = () => NinoSerializer.Serialize(value),
                    Read = b => NinoDeserializer.Deserialize<TRead>(b),
                    Rewrite = v => NinoSerializer.Serialize((TRead)v),
                });

            // Bytes made by hand that .NET reads, though it never writes them.
            void Forged<T>(string name, string description, byte[] bytes, params string[] features) =>
                cases.Add(new ValidCase
                {
                    Name = name, Description = description, Type = typeof(T), Features = features,
                    Write = () => bytes,
                    Read = b => NinoDeserializer.Deserialize<T>(b),
                    Rewrite = v => NinoSerializer.Serialize((T)v),
                });

            // ---------------------------------------------------------- primitives

            Add("integers-zero", "Every integer type, zero.", new Integers(), "int", "null-flag");
            Add("integers-min", "Every integer type at its minimum.", new Integers
            {
                I8 = sbyte.MinValue, U8 = byte.MinValue, I16 = short.MinValue, U16 = ushort.MinValue,
                I32 = int.MinValue, U32 = uint.MinValue, I64 = long.MinValue, U64 = ulong.MinValue,
                I128 = Int128.MinValue, U128 = UInt128.MinValue,
            }, "int", "int128");
            Add("integers-max", "Every integer type at its maximum.", new Integers
            {
                I8 = sbyte.MaxValue, U8 = byte.MaxValue, I16 = short.MaxValue, U16 = ushort.MaxValue,
                I32 = int.MaxValue, U32 = uint.MaxValue, I64 = long.MaxValue, U64 = ulong.MaxValue,
                I128 = Int128.MaxValue, U128 = UInt128.MaxValue,
            }, "int", "int128");
            Add("integers-mixed", "Values around 2^53 and byte patterns that show the byte order.", new Integers
            {
                I8 = -1, U8 = 0x80, I16 = -2, U16 = 0x1234, I32 = 0x12345678, U32 = 0x89ABCDEF,
                I64 = (1L << 53) + 1, U64 = 1UL << 53, I128 = (Int128)long.MinValue * 3 + 7,
                U128 = ((UInt128)0x0102030405060708UL << 64) | 0x090A0B0C0D0E0F10UL,
            }, "int", "int128");
            Add<Integers>("integers-null", "A null sealed class: the null flag alone.", null, "null-flag");

            Add("floats-zero", "Positive zero.", new Floats(), "float");
            Add("floats-negative-zero", "Negative zero.", new Floats
            {
                F16 = BitConverter.UInt16BitsToHalf(0x8000), F32 = -0.0f, F64 = -0.0,
            }, "float");
            Add("floats-max", "The largest finite values.", new Floats
            {
                F16 = Half.MaxValue, F32 = float.MaxValue, F64 = double.MaxValue,
            }, "float");
            Add("floats-min", "The most negative finite values.", new Floats
            {
                F16 = Half.MinValue, F32 = float.MinValue, F64 = double.MinValue,
            }, "float");
            Add("floats-subnormal-min", "The smallest subnormals (epsilon).", new Floats
            {
                F16 = Half.Epsilon, F32 = float.Epsilon, F64 = double.Epsilon,
            }, "float");
            Add("floats-subnormal-max", "The largest subnormals.", new Floats
            {
                F16 = BitConverter.UInt16BitsToHalf(0x03FF), F32 = BitConverter.UInt32BitsToSingle(0x007FFFFF),
                F64 = BitConverter.UInt64BitsToDouble(0x000FFFFFFFFFFFFF),
            }, "float");
            Add("floats-infinity", "Positive infinity.", new Floats
            {
                F16 = Half.PositiveInfinity, F32 = float.PositiveInfinity, F64 = double.PositiveInfinity,
            }, "float");
            Add("floats-negative-infinity", "Negative infinity.", new Floats
            {
                F16 = Half.NegativeInfinity, F32 = float.NegativeInfinity, F64 = double.NegativeInfinity,
            }, "float");
            Add("floats-nan", "The default NaN of each width (sign bit set on x64).", new Floats
            {
                F16 = Half.NaN, F32 = float.NaN, F64 = double.NaN,
            }, "float");
            Add("floats-nan-payload", "Quiet NaNs with a payload, one negative: the bits are kept as they are.", new Floats
            {
                F16 = BitConverter.UInt16BitsToHalf(0x7E01), F32 = BitConverter.UInt32BitsToSingle(0xFFC00001),
                F64 = BitConverter.UInt64BitsToDouble(0x7FF8DEADBEEF0001),
            }, "float");
            // Made from bytes: a signaling NaN constant in C# may be quieted by the compiler or the JIT.
            Forged<Floats>("floats-nan-signaling", "Signaling NaNs: the bits are kept as they are (never quieted).",
                Concat(new byte[] { 1 }, BitConverter.GetBytes((ushort)0x7C01), BitConverter.GetBytes(0x7F800001u),
                    BitConverter.GetBytes(0x7FF0000000000001UL)), "float");
            Add("floats-values", "Ordinary values.", new Floats
            {
                F16 = (Half)1.5f, F32 = 3.14159274f, F64 = -2.718281828459045,
            }, "float");

            Add("scalars-default", "bool, char, decimal, Guid, TimeSpan, DateOnly, TimeOnly at their defaults.",
                new Scalars(), "bool", "char", "decimal", "guid", "timespan", "dateonly", "timeonly");
            Add("scalars-max", "The largest values.", new Scalars
            {
                Flag = true, Char = '￿', Decimal = decimal.MaxValue,
                Guid = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"), Span = TimeSpan.MaxValue,
                Date = DateOnly.MaxValue, Time = TimeOnly.MaxValue,
            }, "bool", "char", "decimal", "guid", "timespan", "dateonly", "timeonly");
            Add("scalars-min", "The smallest values.", new Scalars
            {
                Flag = false, Char = '\0', Decimal = decimal.MinValue, Guid = Guid.Empty, Span = TimeSpan.MinValue,
                Date = DateOnly.MinValue, Time = TimeOnly.MinValue,
            }, "bool", "char", "decimal", "guid", "timespan", "dateonly", "timeonly");
            Add("scalars-values", "A lone surrogate char, a decimal with its scale kept, a Guid that shows its byte order.",
                new Scalars
                {
                    Flag = true, Char = '\ud800', Decimal = -1234.5600m,
                    Guid = new Guid("00112233-4455-6677-8899-aabbccddeeff"), Span = TimeSpan.FromTicks(-1),
                    Date = new DateOnly(2026, 10, 23), Time = new TimeOnly(23, 59, 59, 999),
                }, "bool", "char", "decimal", "guid", "timespan", "dateonly", "timeonly");
            Add("scalars-decimal-scale", "A decimal with the largest scale (28).", new Scalars
            {
                Decimal = new decimal(1, 0, 0, false, 28),
            }, "decimal");

            Add("dates-default", "DateTime and DateTimeOffset at their defaults.", new Dates(), "datetime", "datetimeoffset");
            Add("dates-kinds", "DateTime of each kind (the kind is in the top two bits), DateTimeOffset with an offset.",
                new Dates
                {
                    Utc = new DateTime(2026, 10, 23, 12, 30, 45, 123, DateTimeKind.Utc).AddTicks(4567),
                    Local = new DateTime(638_000_000_000_000_000, DateTimeKind.Local),
                    Unspecified = new DateTime(2000, 1, 1),
                    Offset = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.FromMinutes(-330)),
                }, "datetime", "datetimeoffset");
            Add("dates-extremes", "The smallest and largest DateTime and DateTimeOffset.", new Dates
            {
                Utc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
                Local = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Local),
                Unspecified = DateTime.MaxValue,
                Offset = new DateTimeOffset(DateTime.MaxValue.Ticks - TimeSpan.TicksPerHour * 14, TimeSpan.FromHours(14)),
            }, "datetime", "datetimeoffset");
            Add("dates-offset-min", "A DateTimeOffset at the smallest offset.", new Dates
            {
                Offset = new DateTimeOffset(DateTime.MinValue.Ticks + TimeSpan.TicksPerHour * 14, TimeSpan.FromHours(-14)),
            }, "datetimeoffset");

            Add("enums-default", "Enums of every underlying type, zero.", new Enums(), "enum");
            Add("enums-min", "Enums at their underlying minimum, flags combined.", new Enums
            {
                A = E8.Min, B = EU8.Zero, C = E16.Min, D = EU16.Zero, E = E32.Min, F = EU32.Zero, G = E64.Min,
                H = EU64.Zero, Flags = Permissions.Read | Permissions.Execute, Wide = WideFlags.Low | WideFlags.High,
                Array = new[] { E32.Min, E32.Zero, E32.Max },
            }, "enum", "flags");
            Add("enums-max", "Enums at their underlying maximum.", new Enums
            {
                A = E8.Max, B = EU8.Max, C = E16.Max, D = EU16.Max, E = E32.Max, F = EU32.Max, G = E64.Max,
                H = EU64.Max, Flags = Permissions.All, Wide = WideFlags.High, Array = Array.Empty<E32>(),
            }, "enum", "flags");
            Add("enums-undefined", "Values no enum member names: written and read as the integer.", new Enums
            {
                A = (E8)5, B = (EU8)200, C = (E16)(-7), D = (EU16)9, E = (E32)12345, F = (EU32)77, G = (E64)(-99),
                H = (EU64)1234567890123456789, Flags = (Permissions)0xF0, Wide = (WideFlags)6,
                Array = new[] { (E32)42 },
            }, "enum", "flags");

            Add("big-values", "BigInteger and Version.", new BigValues
            {
                Big = BigInteger.Parse("-123456789012345678901234567890"), Version = new Version(1, 2, 3, 4),
            }, "bigint", "version");
            Add("big-values-default", "BigInteger zero and a null Version.", new BigValues(), "bigint", "version");
            Add("big-values-edges", "BigInteger values whose two's complement needs a sign byte; Version with unset parts.",
                new BigValues { Big = BigInteger.Parse("255"), Version = new Version(1, 2) }, "bigint", "version");
            Add("big-values-negative", "BigInteger -128 and -129 shapes; a Version with a build only.",
                new BigValues { Big = BigInteger.MinusOne << 200, Version = new Version(0, 0, 7) }, "bigint", "version");

            // ---------------------------------------------------------- strings and bytes

            void StringCase(string name, string description, string value, params string[] features) =>
                Add(name, description, new Strings { Text = value, Utf8 = value }, features);

            StringCase("string-null", "A null string, both encodings.", null, "string", "utf8");
            StringCase("string-empty", "The empty string, both encodings.", "", "string", "utf8");
            StringCase("string-ascii", "ASCII: one byte a char (Latin-1 header).", "Hello, Nino!", "string", "utf8");
            StringCase("string-latin1", "Latin-1 above ASCII, including U+0080-U+009F and U+00FF.",
                "\u0080\u0081\u009f é ñ ü ÿ", "string", "utf8");
            StringCase("string-nul", "NUL and other control chars.", "a\0b\u0001\u001f\u007f", "string", "utf8");
            StringCase("string-bmp", "Text past U+00FF in the BMP: UTF-16.", "Привет, 世界 ✓ ﻿", "string", "utf8");
            StringCase("string-astral", "Surrogate pairs.", "😀 𝄞 \U0010FFFF", "string", "utf8");
            StringCase("string-lone-surrogates",
                "Unpaired surrogates: kept in UTF-16; [NinoUtf8] writes each as U+FFFD (so it reads back changed).",
                "a\ud800b\udfffc\udc00\ud800", "string", "utf8");
            StringCase("string-one-char-past-latin1", "Latin-1 text with one char past U+00FF at the end: UTF-16.",
                new string('é', 100) + "Ā", "string", "utf8");
            StringCase("string-63-chars", "63 ASCII chars: the longest string with a one-byte header.",
                new string('a', 63), "string", "utf8");
            StringCase("string-64-chars", "64 ASCII chars: a three-byte header.", new string('a', 64), "string", "utf8");
            StringCase("string-64-wide-chars", "64 chars past U+00FF: a three-byte UTF-16 header, 192 bytes of UTF-8.",
                new string('中', 64), "string", "utf8");
            StringCase("string-43-wide-chars",
                "43 chars past U+00FF: a one-byte header (and [NinoUtf8] char count), a three-byte UTF-8 byte count.",
                new string('中', 43), "string", "utf8");
            StringCase("string-32768-chars", "32,768 Latin-1 chars: a five-byte header (and a five-byte UTF-8 byte count).",
                new string('é', 32768), "string", "utf8");
            StringCase("string-long-ascii", "10,000 ASCII chars.",
                string.Concat(Enumerable.Range(0, 10_000).Select(i => (char)('a' + i % 26))), "string", "utf8");
            StringCase("string-long-latin1", "5,000 Latin-1 chars.", new string('ÿ', 5_000), "string", "utf8");
            StringCase("string-long-utf16", "Mixed scripts with surrogate pairs, 1,500 code units.",
                string.Concat(Enumerable.Repeat("a中😀é", 300)), "string", "utf8");
            Forged<Strings>("string-utf16-header-for-latin1",
                "Latin-1 text written with a UTF-16 header (as a StringBuilder writes it): read, but written back " +
                "with a Latin-1 header.",
                Concat(new byte[] { 1 }, V(6), Utf16("abc"), V(0)), "string");
            Add("string-collections", "Strings in arrays and lists, mixed encodings and nulls.", new StringCollections
            {
                Array = new[] { "a", null, "", "é", "中", "😀", "\udc00" },
                List = new List<string> { null, "x", new string('y', 300) },
                Utf8A = "é😀", Plain = "plain", Utf8B = "",
            }, "string", "utf8", "seq");
            Add("string-collections-null", "Null string collections.", new StringCollections(), "string", "utf8", "seq");

            Add("bytes-null", "Null byte collections.", new Bytes(), "seq");
            Add("bytes-empty", "Empty byte collections.", new Bytes
            {
                Array = Array.Empty<byte>(), List = new List<byte>(), Signed = Array.Empty<sbyte>(),
            }, "seq");
            Add("bytes-values", "Every byte value.", new Bytes
            {
                Array = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(),
                List = new List<byte> { 0, 0x7F, 0x80, 0xFF },
                Signed = new sbyte[] { sbyte.MinValue, -1, 0, 1, sbyte.MaxValue },
            }, "seq");

            // ---------------------------------------------------------- objects and structs

            Add("structs-default", "Raw structs (with padding, packed, sized, nested), a non-raw struct, tuples.",
                new Structs(), "raw-struct", "struct", "tuple", "seq");
            Add("structs-values", "Raw structs with values: the padding is written as zeros.", new Structs
            {
                Vec = new Vec2 { X = 1.5f, Y = -2 },
                Padded = new Padded { A = 0xAB, B = -1 },
                Mixed = new Mixed { A = 1, B = -2, C = 3, D = 4 },
                Packed = new Packed { A = 5, B = long.MinValue, C = 6 },
                Sized = new Sized { A = 7 },
                Nested = new Nested
                {
                    Inner = new Padded { A = 8, B = 9 }, Position = new Vec2 { X = 10, Y = 11 }, Kind = EU8.Max,
                    Flag = true,
                },
                Label = new Label { Name = "label", Weight = -3 },
                VecArray = new[] { new Vec2 { X = 1, Y = 2 }, new Vec2 { X = 3, Y = 4 } },
                PaddedList = new List<Padded> { new() { A = 1, B = 2 }, new() { A = 255, B = long.MaxValue } },
                MixedArray = new[] { new Mixed { A = 0xFF, B = short.MinValue, C = int.MaxValue, D = 0x80 } },
                Labels = new[] { new Label { Name = null, Weight = 1 }, new Label { Name = "two", Weight = 2 } },
                RawTuple = (9, long.MaxValue),
                Tuple = (5, "five"),
                Pair = new KeyValuePair<string, Vec2>("home", new Vec2 { X = 7, Y = 8 }),
            }, "raw-struct", "struct", "tuple", "seq");
            Add("structs-empty-collections", "Empty raw-struct collections.", new Structs
            {
                VecArray = Array.Empty<Vec2>(), PaddedList = new List<Padded>(), MixedArray = Array.Empty<Mixed>(),
                Labels = Array.Empty<Label>(),
            }, "raw-struct", "struct", "seq");

            Add("struct-root-raw", "A raw struct at the top level: its bytes alone, no header.",
                new Padded { A = 1, B = -2 }, "raw-struct");
            Add("struct-root-nested-raw", "A raw struct holding raw structs at the top level.", new Nested
            {
                Inner = new Padded { A = 3, B = 4 }, Position = new Vec2 { X = -1, Y = 0.5f }, Kind = EU8.Max,
                Flag = true,
            }, "raw-struct");
            Add("struct-root", "A non-raw struct at the top level: its members, no header.",
                new Label { Name = "root", Weight = 9 }, "struct");
            Add("struct-root-keyed", "A keyed struct at the top level.", new KeyedStruct { A = -1, B = 0.25 },
                "keyed", "struct");
            Add("struct-root-empty", "A struct without members at the top level: raw, one zero byte (its size in memory).", new EmptyStruct(),
                "raw-struct");
            Add("nested-objects", "Sealed classes three levels deep.", new Outer
            {
                Name = "outer", Point = new Point { X = 1, Y = 2 },
                Middle = new Middle
                {
                    First = new Point { X = 3, Y = 4 }, Second = null,
                    Inner = new Inner { Value = long.MinValue, Text = "inner", Point = new Point { X = 5, Y = 6 } },
                },
            }, "null-flag", "object");
            Add("nested-objects-nulls", "Sealed classes with null members.", new Outer { Name = null, Middle = new Middle() },
                "null-flag", "object");

            Add("empties", "Types without members.", new Empties
            {
                Class = new Empty(), Struct = new EmptyStruct(), Structs = new EmptyStruct[3],
                Classes = new List<Empty> { new(), null, new() },
            }, "null-flag", "raw-struct", "seq");
            Add("empties-null", "Types without members, null.", new Empties(), "null-flag", "raw-struct", "seq");

            // ---------------------------------------------------------- collections

            Add("collections-null", "Every collection null.", new Collections(), "seq", "map", "grid");
            Add("collections-empty", "Every collection empty.", new Collections
            {
                Ints = Array.Empty<int>(), IntList = new List<int>(), IntSet = new HashSet<int>(),
                Longs = Array.Empty<long>(), Doubles = Array.Empty<double>(), Bools = Array.Empty<bool>(),
                Chars = Array.Empty<char>(), Strings = new List<string>(), StringSet = new HashSet<string>(),
                Points = Array.Empty<Point>(), PointList = new List<Point>(), IntMap = new Dictionary<int, int>(),
                StringMap = new Dictionary<string, string>(), PointMap = new Dictionary<string, Point>(),
                ListMap = new Dictionary<int, List<string>>(), SortedInts = new SortedDictionary<int, string>(),
                SortedStrings = new SortedDictionary<string, int>(StringComparer.Ordinal),
                Nested = new List<List<int>>(), Jagged = Array.Empty<int[]>(), Grid = new int[0, 0],
                StringGrid = new string[0, 3],
            }, "seq", "map", "grid");
            Add("collections-values", "Collections of primitives, strings and objects, with nulls inside.", new Collections
            {
                Ints = new[] { int.MinValue, -1, 0, 1, int.MaxValue },
                IntList = new List<int> { 3, 1, 2 },
                IntSet = new HashSet<int> { 5, -5, 0 },
                Longs = new[] { long.MinValue, long.MaxValue },
                Doubles = new[] { double.NaN, -0.0, 1e300 },
                Bools = new[] { true, false, true },
                Chars = "a\ud800é".ToCharArray(),
                Strings = new List<string> { "a", null, "", "中" },
                StringSet = new HashSet<string> { "x", "y", "" },
                Points = new[] { new Point { X = 1, Y = 2 }, null },
                PointList = new List<Point> { null, new Point { X = -1, Y = -2 } },
                IntMap = new Dictionary<int, int> { [1] = 10, [-1] = -10, [int.MaxValue] = 0 },
                StringMap = new Dictionary<string, string> { ["k"] = "v", [""] = null, ["中"] = "" },
                PointMap = new Dictionary<string, Point> { ["origin"] = new Point(), ["none"] = null },
                ListMap = new Dictionary<int, List<string>> { [1] = new List<string> { "a" }, [2] = null, [3] = new() },
                SortedInts = new SortedDictionary<int, string> { [3] = "c", [1] = "a", [2] = null },
                SortedStrings = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["b"] = 2, ["a"] = 1 },
                Nested = new List<List<int>> { new() { 1, 2 }, null, new() },
                Jagged = new[] { new[] { 1 }, null, Array.Empty<int>() },
                Grid = new[,] { { 1, 2, 3 }, { 4, 5, 6 } },
                StringGrid = new[,] { { "a", null }, { "", "d" } },
            }, "seq", "map", "grid", "string", "null-flag");
            Add("collections-large", "Collections of 2,000 and 500 elements.", new Collections
            {
                Ints = Enumerable.Range(0, 2_000).ToArray(),
                IntMap = Enumerable.Range(0, 500).ToDictionary(i => i * 7, i => -i),
            }, "seq", "map");

            // ---------------------------------------------------------- nullables

            Add("nullables-null", "Every nullable null.", new Nullables(), "nullable");
            Add("nullables-values", "Nullables with values, in collections with the fixed-stride layout.", new Nullables
            {
                Int = -1, Long = long.MinValue, Double = double.NegativeInfinity, Bool = false,
                Guid = new Guid("00112233-4455-6677-8899-aabbccddeeff"), Enum = E16.Min, Vec = new Vec2 { X = 1, Y = 2 },
                Padded = new Padded { A = 1, B = 2 }, Label = new Label { Name = "n", Weight = 1 },
                IntArray = new int?[] { 1, null, int.MinValue },
                LongList = new List<long?> { null, long.MaxValue },
                PaddedArray = new Padded?[] { new Padded { A = 3, B = 4 }, null },
                Map = new Dictionary<int, int?> { [1] = null, [2] = 0 },
            }, "nullable", "seq", "map");
            Add("nullables-empty-collections", "Empty nullable collections.", new Nullables
            {
                IntArray = Array.Empty<int?>(), LongList = new List<long?>(), PaddedArray = Array.Empty<Padded?>(),
                Map = new Dictionary<int, int?>(),
            }, "nullable", "seq", "map");

            // ---------------------------------------------------------- version tolerance and keyed members

            Add("tolerant", "Version-tolerant bodies (one length a body), in a hierarchy, and empty.", new TolerantHolder
            {
                First = new TolerantV1 { A = 1, B = "b" },
                Polymorphic = new TolerantLeaf { A = 2, B = 3 },
                Empty = new TolerantEmpty(),
                After = 4,
            }, "version-tolerant", "type-id");
            Add("tolerant-nulls", "Null version-tolerant members.", new TolerantHolder { After = -1 },
                "version-tolerant", "type-id");
            Add("tolerant-base", "A version-tolerant base class written as itself.", new TolerantHolder
            {
                Polymorphic = new TolerantBase { A = 9 },
            }, "version-tolerant", "type-id");
            Across<TolerantV1, TolerantV2>("tolerant-old-to-new",
                "Old data read by the new version: the appended members keep their defaults.",
                new TolerantV1 { A = 7, B = "old" }, "version-tolerant", "versioning");
            Across<TolerantV2, TolerantV1>("tolerant-new-to-old",
                "New data read by the old version: the trailing members are skipped.",
                new TolerantV2 { A = 7, B = "new", C = long.MaxValue, D = new List<int> { 1, 2, 3 } },
                "version-tolerant", "versioning");

            Add("keyed", "Keyed members: (key, length, value) each.", new KeyedHolder
            {
                Keyed = new KeyedV1 { Health = 100, Name = "hero", Items = new List<int> { 1, 2 }, Score = -5 },
                Struct = new KeyedStruct { A = 1, B = 2.5 },
                Array = new[] { new KeyedV1 { Name = "" }, null },
            }, "keyed");
            Add("keyed-nulls", "Keyed members, null.", new KeyedHolder(), "keyed");
            Across<KeyedV1, KeyedV2>("keyed-v1-to-v2",
                "Old keyed data read by a reordered version: a renamed member found by its former name's key, a " +
                "removed member skipped, an added member left at its default.",
                new KeyedV1 { Health = 42, Name = "old", Items = new List<int> { 9 }, Score = 1L << 60 },
                "keyed", "versioning");
            Across<KeyedV2, KeyedV1>("keyed-v2-to-v1", "New keyed data read by the old version.",
                new KeyedV2 { Score = 3, Name = "new", Hp = 5, Level = 6 }, "keyed", "versioning");

            // ---------------------------------------------------------- polymorphism

            Add("polymorphism", "Abstract base, interface, generic subtypes, object, through type ids.", new Zoo
            {
                Star = new Puppy { Name = "Rex", Bones = 2, Sleepy = true },
                Dog = new Puppy { Name = "Pup", Bones = 1 },
                Animals = new List<Animal>
                {
                    new Dog { Name = "Fido", Bones = 3 }, null, new Cat { Name = "Tom", Lives = 9 },
                    new Box<int> { Name = "int box", Value = 42 }, new Box<string> { Name = "string box", Value = "s" },
                    new Box<List<Point>> { Name = "points", Value = new List<Point> { new() { X = 1, Y = 1 } } },
                },
                Cages = new Animal[] { null, new Cat { Name = "Cage", Lives = 1 } },
                Shape = new Square { Area = 4, Side = 2 },
                Shapes = new List<IShape> { new Circle { Area = 3.14, Radius = 1 }, null, new Square() },
                Plain = new Plain { Value = 1 },
                Anything = new Point { X = 1, Y = 2 },
                ByName = new Dictionary<string, Animal> { ["dog"] = new Dog { Name = "d" }, ["none"] = null },
            }, "type-id", "generic-type-id", "object", "seq", "map");
            Add("polymorphism-nulls", "Every polymorphic member null.", new Zoo(), "type-id");
            Add("polymorphism-base-types", "Members holding exactly their declared (non-sealed) type.", new Zoo
            {
                Dog = new Dog { Name = "plain dog" }, Shape = new Circle(), Anything = new Zoo(),
            }, "type-id", "object");
            Add<Animal>("polymorphism-root", "A polymorphic value at the top level.", new Cat { Name = "root", Lives = 3 },
                "type-id");
            Add<Animal>("polymorphism-root-null", "A null polymorphic value at the top level: type id 0.", null, "type-id");
            Add<Animal>("polymorphism-generic-root", "A generic subtype at the top level.",
                new Box<Vec2> { Name = "vec", Value = new Vec2 { X = 1, Y = 2 } }, "type-id", "generic-type-id");

            // ---------------------------------------------------------- deep nesting

            static Chain MakeChain(int depth)
            {
                Chain head = null;
                for (int i = depth; i > 0; i--)
                    head = new Chain { Value = i, Next = head };
                return head;
            }

            static TolerantChain MakeTolerant(int depth)
            {
                TolerantChain head = null;
                for (int i = depth; i > 0; i--)
                    head = new TolerantChain { Value = i, Next = head };
                return head;
            }

            static KeyedChain MakeKeyed(int depth)
            {
                KeyedChain head = null;
                for (int i = depth; i > 0; i--)
                    head = new KeyedChain { Value = i, Next = head };
                return head;
            }

            static Link MakeLinks(int depth)
            {
                Link head = null;
                for (int i = depth; i > 0; i--)
                    head = i % 2 == 0 ? new RedLink { Value = i, Next = head } : new BlueLink { Value = i, Next = head, Note = i % 10 == 1 ? "n" : null };
                return head;
            }

            static RecordChain MakeRecords(int depth)
            {
                RecordChain head = null;
                for (int i = depth; i > 0; i--)
                    head = new RecordChain(i, head);
                return head;
            }

            static TreeNode MakeTree(int depth, int fanout)
            {
                var root = new TreeNode { Value = 0, Children = new List<TreeNode>() };
                var level = root;
                for (int i = 1; i < depth; i++)
                {
                    var next = new TreeNode { Value = i, Children = new List<TreeNode>() };
                    for (int j = 1; j < fanout; j++)
                        level.Children.Add(new TreeNode { Value = -j });
                    level.Children.Add(next);
                    level = next;
                }

                return root;
            }

            foreach (int depth in new[] { 1, 127, 128, 129, 130, 300, 1000 })
                Add($"deep-chain-{depth}", $"A sealed chain {depth} objects long" +
                                           (depth > 127 ? ": the bodies past 128 open ones are deferred to the end." : "."),
                    MakeChain(depth), "deferral");
            foreach (int depth in new[] { 128, 129, 300, 1000 })
                Add($"deep-tolerant-chain-{depth}", $"A version-tolerant chain {depth} objects long: deferred bodies " +
                                                    "in the outermost region's section.",
                    MakeTolerant(depth), "deferral", "deferral-section", "version-tolerant");
            foreach (int depth in new[] { 129, 300 })
                Add($"deep-keyed-chain-{depth}", $"A keyed chain {depth} objects long: deferred bodies in the outermost " +
                                                 "region's section.",
                    MakeKeyed(depth), "deferral", "deferral-section", "keyed");
            foreach (int depth in new[] { 129, 1000 })
                Add($"deep-links-{depth}", $"A polymorphic chain {depth} objects long (type ids).", MakeLinks(depth),
                    "deferral", "type-id");
            Add("deep-tree", "A tree 200 levels deep through lists, three children a level.", MakeTree(200, 3),
                "deferral", "seq");
            Add("deep-in-region", "A positional chain inside a version-tolerant body: deferred into its section.",
                new TolerantRoot { Name = "root", Chain = MakeChain(200), After = 7 },
                "deferral", "deferral-section", "version-tolerant");
            foreach (int depth in new[] { 128, 500, 512 })
                Add($"deep-records-{depth}", $"A record chain {depth} deep: never deferred, nested inline" +
                                                (depth == 512 ? ", at the limit of 512 open bodies." : "."),
                    MakeRecords(depth), "constructor-type", "depth-limit");

            // ---------------------------------------------------------- reference tracking (optional)

            var shared = new Tracked { Value = 1 };
            var cycle = new Tracked { Value = 2 };
            cycle.Next = cycle;
            var leaf = new TrackedLeaf { Value = 3, Name = "leaf", Other = shared };
            Optional("track-references", "references-shared",
                "Shared references and a cycle: written once, then back references.", new TrackedHolder
                {
                    A = shared, B = cycle, All = new List<Tracked> { shared, leaf, cycle, null, leaf }, Any = leaf,
                }, "track-references");
            Optional("track-references", "references-null", "Tracked members, null.", new TrackedHolder(),
                "track-references");

            // ---------------------------------------------------------- self-nesting collections

            Add("tree-collections", "Collections whose elements are themselves (class Tree : List<Tree>).", new TreeHolder
            {
                Tree = new Tree { new Tree { new Tree() }, null, new Tree() },
                DictTree = new DictTree { ["a"] = new DictTree { ["b"] = null }, [""] = new DictTree() },
            }, "seq", "map");

            // ---------------------------------------------------------- reference tracking, every slot form (optional)

            var node = new TrackedNode { Value = 1 };
            node.Next = node;
            var other = new TrackedNode { Value = 2, Next = node };
            var based = new TrackedLeaf { Value = 3, Name = "base", Other = new Tracked { Value = 4 } };
            based.Next = based;
            var tolerant = new TrackedTolerant { Value = 5 };
            tolerant.Next = new TrackedTolerant { Value = 6, Next = tolerant };
            Optional("track-references", "references-forms",
                "Tracked objects through an object member (type id, then header), a sealed member (header alone), a " +
                "non-sealed member (header, then type id), collections and a version-tolerant type: one numbering.",
                new TrackedMix
                {
                    Any = other, Node = node, Base = based,
                    Nodes = new List<TrackedNode> { other, null, new TrackedNode { Value = 7 }, node },
                    ById = new Dictionary<int, TrackedNode> { [1] = node, [2] = null, [7] = other },
                    Tolerant = tolerant,
                }, "track-references", "type-id", "object", "version-tolerant");

            static TrackedNode MakeTrackedChain(int depth)
            {
                TrackedNode head = null, tail = null;
                for (int i = depth; i > 0; i--)
                {
                    head = new TrackedNode { Value = i, Next = head };
                    tail ??= head;
                }

                tail.Next = head;
                return head;
            }

            static TrackedTolerant MakeTrackedTolerant(int depth)
            {
                TrackedTolerant head = null, tail = null;
                for (int i = depth; i > 0; i--)
                {
                    head = new TrackedTolerant { Value = i, Next = head };
                    tail ??= head;
                }

                tail.Next = head;
                return head;
            }

            Optional("track-references", "references-deep-cycle",
                "A tracked ring of 200: the bodies past 128 are deferred, their headers numbered where they stand; " +
                "the last refers back to the first.", MakeTrackedChain(200), "track-references", "deferral");
            Optional("track-references", "references-deep-tolerant",
                "A version-tolerant tracked ring of 200: deferred bodies in the outermost region's section.",
                MakeTrackedTolerant(200), "track-references", "deferral", "deferral-section", "version-tolerant");

            // ---------------------------------------------------------- [NinoType] collections

            var bag = new Bag { 1, 2, 3 };
            bag.Tag = "bag";
            var tolerantBag = new TolerantBag { "a", null, "" };
            tolerantBag.Tag = "tolerant";
            var keyedBag = new KeyedBag { ["one"] = 1, ["two"] = 2 };
            keyedBag.Version = 9;
            var set = new PointSet { 5, 3 };
            set.Origin = new Point { X = 1, Y = 2 };
            var empty = new Bag();
            Add("nino-collections", "[NinoType] collections in every format: elements, then members (positional), " +
                                    "elements outside the region (version tolerant), inside it (keyed).", new BagHolder
            {
                Bag = bag, Tolerant = tolerantBag, Keyed = keyedBag, Set = set, Bags = new List<Bag> { empty, null, bag },
            }, "nino-collection", "type-id", "version-tolerant", "keyed", "seq", "map");
            Add("nino-collections-null", "[NinoType] collection members, null.", new BagHolder(), "nino-collection");

            // ---------------------------------------------------------- generic types behind type ids

            Add("generic-slots", "Generic abstract classes, classes and interfaces written behind type ids: each " +
                                 "closed type is its own slot, holding generic and non-generic subtypes.", new GenericSlots
            {
                IntShelf = new IntShelf { Item = 1, Extra = 2 },
                TextShelf = new ListShelf<string> { Item = "x", More = new List<string> { "y" } },
                Crate = new LabeledCrate<int> { Content = 3, Label = "l" },
                TextCrate = new Crate<string> { Content = "c" },
                Holder = new Holder<long> { Held = long.MaxValue, Count = 1 },
                Shelves = new List<Shelf<int>> { new ListShelf<int> { Item = 4, More = new List<int> { 5 } }, null, new IntShelf() },
            }, "type-id", "generic-type-id");
            Add("generic-slots-null", "Generic slots, null.", new GenericSlots(), "type-id");

            // ---------------------------------------------------------- generic types holding themselves

            Add("generic-nest", "A generic type holding itself with another type argument (Nest<T> holds " +
                                "Nest<List<T>>): every level is another closed type.", new NestHolder
            {
                Nest = new Nest<int>
                {
                    Value = 1,
                    Deeper = new Nest<List<int>>
                    {
                        Value = new List<int> { 2 },
                        Deeper = new Nest<List<List<int>>> { Value = new List<List<int>> { new() { 3 }, null } },
                    },
                },
            }, "seq");

            // ---------------------------------------------------------- nullables and pairs as type arguments

            Add("generic-nullable-arguments", "Nullables and pairs inside the type arguments of a generic type: raw " +
                                              "nullables in arrays and lists at a fixed stride, item by item elsewhere.",
                new WrappedArgs
                {
                    NullableArray = new Wrap<int?[]> { Value = new int?[] { 1, null, 3 } },
                    NullableList = new Wrap<List<int?>> { Value = new List<int?> { null, 2 } },
                    NullableSet = new Wrap<HashSet<int?>> { Value = new HashSet<int?> { 4, null } },
                    NullableStructs = new Wrap<List<Vec2?>> { Value = new List<Vec2?> { new Vec2 { X = 1, Y = 2 }, null } },
                    NullableValues = new Wrap<Dictionary<int, double?>> { Value = new Dictionary<int, double?> { [1] = 0.5, [2] = null } },
                    Pairs = new Wrap<List<KeyValuePair<int, string>>>
                    {
                        Value = new List<KeyValuePair<int, string>> { new(1, "one"), new(2, null) },
                    },
                    PairArray = new Wrap<KeyValuePair<string, int?>[]> { Value = new[] { new KeyValuePair<string, int?>("k", null) } },
                }, "nullable", "seq", "map");

            return cases;
        }

        internal static byte[] I32(int value) => BitConverter.GetBytes(value);

        /// <summary>A string header: <paramref name="value"/> as a minimal unsigned LEB128 varint.</summary>
        /// <summary>A string header: the value itself below 0x80, else 0x80 and a ushort, else 0x81 and a uint.</summary>
        internal static byte[] V(ulong value)
        {
            if (value < 0x80)
                return new[] { (byte)value };
            if (value <= ushort.MaxValue)
                return new byte[] { 0x80, (byte)value, (byte)(value >> 8) };
            return new byte[] { 0x81, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
        }
        internal static byte[] Utf16(string s) => System.Text.Encoding.Unicode.GetBytes(s);
        internal static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    }
}
