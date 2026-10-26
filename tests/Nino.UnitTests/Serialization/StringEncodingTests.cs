using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// The string encoding: a header (0 null, 2 * length + 1 for Latin-1 text, one byte a char, 2 * length for
/// other text, UTF-16), its size given by its first byte: the value itself up to 0x7F, 0x80 and a ushort, 0x81 and a
/// uint. Every length around the vector widths (8, 16, 32 chars, the overlapping tails) with the first char above
/// U+00FF at every position, through every path that reads or writes strings.
/// </summary>
[TestClass]
public class StringEncodingTests
{
    private static IEnumerable<string> Strings()
    {
        yield return null;
        yield return "";
        for (int length = 1; length <= 80; length++)
        {
            var ascii = new string(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)).ToArray());
            yield return ascii;
            // Latin-1 above ASCII, at the start, the middle and the end.
            yield return 'é' + ascii.Substring(1);
            yield return ascii.Substring(0, length / 2) + 'ÿ' + ascii.Substring(length / 2 + 1);
            yield return ascii.Substring(0, length - 1) + ' ';
            // One char above U+00FF at each position: UTF-16, whatever was narrowed before it.
            for (int at = 0; at < length; at++)
                yield return ascii.Substring(0, at) + 'Ā' + ascii.Substring(at + 1);
        }

        yield return "中文テキスト😀";
        yield return "\0\u0001\u007F\u0080ÿ";
        yield return new string('x', 100_000);
        yield return new string('x', 99_999) + '中';
        // Around the header's sizes: 1 byte up to 63 chars, 3 up to 32,767, 5 above.
        foreach (int length in new[] { 63, 64, 65, 127, 128, 8191, 8192, 32767, 32768, 32769 })
        {
            yield return new string('é', length);
            yield return new string('中', length);
        }
    }

    /// <summary>The header at the start of <paramref name="bytes"/>, and the bytes it takes.</summary>
    internal static ulong Header(byte[] bytes, int at, out int size)
    {
        byte first = bytes[at];
        if (first < 0x80)
        {
            size = 1;
            return first;
        }

        Assert.IsTrue(first is 0x80 or 0x81, "string header marker");
        size = first == 0x80 ? 3 : 5;
        ulong value = 0;
        for (int i = size - 1; i >= 1; i--)
            value = value << 8 | bytes[at + i];
        return value;
    }

    /// <summary>The header of <paramref name="value"/>: the shortest form that holds it.</summary>
    internal static byte[] Header(ulong value)
    {
        if (value < 0x80)
            return new[] { (byte)value };
        if (value <= ushort.MaxValue)
            return new byte[] { 0x80, (byte)value, (byte)(value >> 8) };
        return new byte[] { 0x81, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
    }

    private static bool IsLatin1(string s) => s.All(c => c <= 0xFF);

    [TestMethod]
    public void EveryLengthRoundTrips()
    {
        foreach (var s in Strings())
        {
            var bytes = NinoSerializer.Serialize(s);
            ulong header = Header(bytes, 0, out int size);
            if (s == null)
            {
                Assert.AreEqual(0ul, header);
                Assert.AreEqual(1, bytes.Length);
            }
            else if (IsLatin1(s))
            {
                // The empty string included (header 1).
                Assert.AreEqual(2ul * (ulong)s.Length + 1, header, s);
                CollectionAssert.AreEqual(Header(header), bytes.Take(size).ToArray());
                Assert.AreEqual(size + s.Length, bytes.Length);
                CollectionAssert.AreEqual(s.Select(c => (byte)c).ToArray(), bytes.Skip(size).ToArray());
            }
            else
            {
                Assert.AreEqual(2ul * (ulong)s.Length, header, s);
                CollectionAssert.AreEqual(Header(header), bytes.Take(size).ToArray());
                CollectionAssert.AreEqual(Encoding.Unicode.GetBytes(s), bytes.Skip(size).ToArray());
            }

            Assert.AreEqual(s, NinoDeserializer.Deserialize<string>(bytes));
        }
    }

    [TestMethod]
    public void CollectionsAndReuse()
    {
        var all = Strings().Where(s => s == null || s.Length < 1000).ToList();
        var array = all.ToArray();
        var bytes = NinoSerializer.Serialize(array);
        CollectionAssert.AreEqual(array, NinoDeserializer.Deserialize<string[]>(bytes));
        // In place: equal strings are kept, others replaced.
        var existing = all.Select(s => s == null ? null : new string(s.ToCharArray())).ToArray();
        var kept = existing.ToArray();
        NinoDeserializer.Deserialize(bytes, ref existing);
        CollectionAssert.AreEqual(array, existing);
        for (int i = 0; i < kept.Length; i++)
        {
            if (kept[i] != null && kept[i].All(c => c < 0x80))
                Assert.AreSame(kept[i], existing[i]);
        }

        CollectionAssert.AreEqual(all, NinoDeserializer.Deserialize<List<string>>(NinoSerializer.Serialize(all)));
        var map = all.Select((s, i) => (s, i)).ToDictionary(p => p.i, p => p.s);
        CollectionAssert.AreEqual(map.ToList(),
            NinoDeserializer.Deserialize<Dictionary<int, string>>(NinoSerializer.Serialize(map)).ToList());
        var byText = all.Where(s => s != null).Distinct().Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        CollectionAssert.AreEqual(byText.ToList(),
            NinoDeserializer.Deserialize<Dictionary<string, int>>(NinoSerializer.Serialize(byText)).ToList());
    }

    [TestMethod]
    public void StringBuildersAreUtf16Strings()
    {
        foreach (var s in Strings().Where(s => s != null))
        {
            // Several chunks: appended in pieces.
            var builder = new StringBuilder(4);
            for (int i = 0; i < s.Length; i += 7)
                builder.Append(s, i, Math.Min(7, s.Length - i));
            var bytes = NinoSerializer.Serialize(builder);
            // Always UTF-16 chars (no widening on read), the empty builder the empty string; a string reads them, and
            // a builder reads a string's bytes.
            Assert.AreEqual(s.Length == 0 ? 1ul : 2ul * (ulong)s.Length, Header(bytes, 0, out _));
            Assert.AreEqual(s, NinoDeserializer.Deserialize<string>(bytes));
            Assert.AreEqual(s, NinoDeserializer.Deserialize<StringBuilder>(bytes).ToString());
            Assert.AreEqual(s, NinoDeserializer.Deserialize<StringBuilder>(NinoSerializer.Serialize(s)).ToString());
        }
    }

    [TestMethod]
    public void TruncatedStringsAreRejected()
    {
        foreach (var s in new[] { "abcdefghijklmnopqrstuvwxyz", "中文テキスト" })
        {
            var bytes = NinoSerializer.Serialize(s);
            for (int length = 0; length < bytes.Length; length++)
            {
                var cut = bytes.Take(length).ToArray();
                Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<string>(cut));
            }
        }
    
    }

    [TestMethod]
    public void MalformedHeadersAreRejected()
    {
        foreach (var data in new[]
                 {
                     // Not minimal: a longer form for a value a shorter one holds.
                     new byte[] { 0x80, 0x00, 0x00 },
                     new byte[] { 0x80, 0x07, 0x00, 0x61, 0x62, 0x63 },
                     new byte[] { 0x80, 0x7F, 0x00 },
                     new byte[] { 0x81, 0x00, 0x00, 0x00, 0x00 },
                     new byte[] { 0x81, 0xFF, 0xFF, 0x00, 0x00 },
                     // A first byte no header starts with.
                     new byte[] { 0x82, 0x00, 0x00, 0x00, 0x00 },
                     new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F },
                     // Cut by the end of the data.
                     new byte[] { 0x80 },
                     new byte[] { 0x80, 0x80 },
                     new byte[] { 0x81, 0x00, 0x00, 0x01 },
                     // The largest headers: 2^31 - 1 chars, of either form, with nothing after them, and past them.
                     new byte[] { 0x81, 0xFF, 0xFF, 0xFF, 0xFF },
                     new byte[] { 0x81, 0xFE, 0xFF, 0xFF, 0xFF },
                     // A three-byte header past the data.
                     new byte[] { 0x80, 0x81, 0x00, 0x61 },
                     // A length past the data.
                     new byte[] { 0x07, 0x61, 0x62 },
                     new byte[] { 0x04, 0x61, 0x00, 0x62 },
                 })
        {
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<string>(data));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<StringBuilder>(data));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<string[]>(
                new byte[] { 1, 0, 0, 0 }.Concat(data).ToArray()));
        }

        // Well-formed headers read, Latin-1 text in the UTF-16 form included (what a StringBuilder writes).
        Assert.AreEqual("abc", NinoDeserializer.Deserialize<string>(new byte[] { 0x07, 0x61, 0x62, 0x63 }));
        Assert.AreEqual("a", NinoDeserializer.Deserialize<string>(new byte[] { 0x02, 0x61, 0x00 }));
        Assert.AreEqual("", NinoDeserializer.Deserialize<string>(new byte[] { 0x01 }));
        Assert.IsNull(NinoDeserializer.Deserialize<string>(new byte[] { 0x00 }));
        var long64 = new string('a', 64);
        Assert.AreEqual(long64, NinoDeserializer.Deserialize<string>(
            new byte[] { 0x80, 0x81, 0x00 }.Concat(Encoding.ASCII.GetBytes(long64)).ToArray()));
    }
}
