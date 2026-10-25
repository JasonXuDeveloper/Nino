using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>
/// UTF-8 strings are a char (UTF-16 code unit) count plus one (0 = null, 1 = empty, nothing follows either), then a
/// byte count, both as string headers, and exactly that many UTF-8 bytes. Valid counts satisfy
/// <c>chars &lt;= bytes &lt;= 3 * chars</c>. Malformed data throws and is never read past its byte count.
/// </summary>
[TestClass]
public class Utf8WireTests
{
    private static byte[] V(ulong value) => StringEncodingTests.Header(value);

    private static readonly byte[] Null = { 0 }, Empty = { 1 };

    /// <summary>A string with explicit counts: char count (plus one), byte count, then the bytes.</summary>
    private static byte[] Wire(long chars, long bytes, params byte[] payload) =>
        V((ulong)(chars + 1)).Concat(V((ulong)bytes)).Concat(payload).ToArray();

    /// <summary>A string whose byte count is the payload's length.</summary>
    private static byte[] Wire(int chars, byte[] payload) => Wire(chars, payload.Length, payload);

    private static string Read(byte[] data, out int position)
    {
        var reader = new Reader(data);
        var value = reader.ReadUtf8();
        position = reader.Position;
        return value;
    }

    private static void Throws(byte[] data)
    {
        Assert.ThrowsExactly<NinoDataException>(() => Read(data, out _), BitConverter.ToString(data));
    }

    private static byte[] Write(string value)
    {
        using var buffer = new NinoArrayBufferWriter();
        var writer = new Writer(buffer);
        writer.WriteUtf8(value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    [TestMethod]
    public void Layout()
    {
        // ASCII (bytes == chars), 2-byte, 3-byte (bytes == 3 * chars), surrogate pairs (4 bytes for 2 chars), mixed,
        // and past the worst-case reservation (the byte count computed first).
        foreach (var s in new[] { "a", "héllo", "ニノ", "😀", "a😀b", new string('x', 5000) + "é", new string('ニ', 2000) })
            CollectionAssert.AreEqual(Wire(s.Length, Encoding.UTF8.GetBytes(s)), Write(s), s);

        // The documented example.
        CollectionAssert.AreEqual(
            new byte[] { 4, 6, 0xC3, 0xA9, 0xF0, 0x9F, 0x98, 0x80 }, Write("é😀"));

        // Null and empty: the char count alone.
        CollectionAssert.AreEqual(Null, Write(null));
        CollectionAssert.AreEqual(Empty, Write(""));

        // Around the counts' byte widths: the byte count a byte longer than the char count's (the bytes moved up)
        // and as long.
        foreach (int length in new[] { 42, 43, 63, 64, 126, 127, 128, 10922, 10923, 21845, 21846, 32767, 32768, 65535, 65536 })
        {
            foreach (var s in new[] { new string('a', length), new string('ニ', length), new string('é', length),
                         "😀" + new string('a', length) })
                CollectionAssert.AreEqual(Wire(s.Length, Encoding.UTF8.GetBytes(s)), Write(s), s);
        }

        // An unpaired surrogate is U+FFFD: one char, three bytes.
        CollectionAssert.AreEqual(Wire(3, 5, 0x61, 0xEF, 0xBF, 0xBD, 0x62), Write("a\ud800b"));
        Assert.AreEqual("a�b", Read(Write("a\ud800b"), out _));
    }

    [TestMethod]
    public void RoundTripsAtTheCountBounds()
    {
        foreach (var s in new[] { "a", "é", "ニ", "😀", "ニノ😀", "\0", "a\0b", new string('a', 1024), new string('ニ', 1025) })
        {
            var data = Write(s);
            StringEncodingTests.Header(data, 0, out int at);
            int bytes = (int)StringEncodingTests.Header(data, at, out _);
            Assert.IsTrue(s.Length <= bytes && bytes <= 3 * s.Length, s);
            Assert.AreEqual(s, Read(data, out int position));
            Assert.AreEqual(data.Length, position);
        }
    }

    [TestMethod]
    public void NullAndEmptyAtTheEndOfTheData()
    {
        // The char count alone, whatever follows.
        Assert.IsNull(Read(Null, out int position));
        Assert.AreEqual(1, position);
        Assert.AreEqual("", Read(Empty, out position));
        Assert.AreEqual(1, position);
        foreach (int extra in new[] { 1, 2, 3, 4 })
        {
            Assert.IsNull(Read(Null.Concat(new byte[extra]).ToArray(), out position));
            Assert.AreEqual(1, position);
            Assert.AreEqual("", Read(Empty.Concat(Enumerable.Repeat((byte)0xFF, extra)).ToArray(), out position));
            Assert.AreEqual(1, position);
        }
    }

    [TestMethod]
    public void EndsAtItsByteCountWhateverFollows()
    {
        // The bytes after the string (an int -1, an invalid UTF-8 byte, continuation bytes, a lead byte, ASCII) are
        // not decoded.
        foreach (var tail in new[]
                 {
                     new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, new byte[] { 0x80 }, new byte[] { 0xA9 }, new byte[] { 0xE3 },
                     new byte[] { 0x41 }, new byte[] { 0x9F, 0x98, 0x80 },
                 })
        foreach (var s in new[] { "a", "é", "ニノ😀", "😀", "abcdefghijklmnopqrstuvwxyz0123456789" })
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var data = Wire(s.Length, bytes.Length, bytes.Concat(tail).ToArray());
            Assert.AreEqual(s, Read(data, out int position));
            Assert.AreEqual(2 + bytes.Length, position);
        }
    }

    [TestMethod]
    public void MalformedDataThrows()
    {
        // Truncated counts.
        Throws(new byte[0]);
        Throws(new byte[] { 2 }); // a char count with no byte count
        Throws(new byte[] { 0x80, 0x82 }); // a char count cut
        Throws(new byte[] { 2, 0x80, 0x81 }); // a byte count cut
        Throws(new byte[] { 2, 0x81, 0x00, 0x00, 0x01 }); // a byte count cut
        Throws(Wire(1, 1)); // no bytes

        // Counts that are not minimal, start with no header's first byte, or are past their range (chars 2^31 - 1,
        // bytes 2^31 - 1).
        Throws(new byte[] { 0x80, 0x02, 0x00, 1, 0x61 });
        Throws(new byte[] { 2, 0x80, 0x01, 0x00, 0x61 });
        Throws(new byte[] { 2, 0x81, 0x01, 0x00, 0x00, 0x00, 0x61 });
        Throws(new byte[] { 0x80, 0x00, 0x00 }); // null, not minimal
        Throws(new byte[] { 0x81, 0x01, 0x00, 0x00, 0x00 }); // empty, not minimal
        Throws(new byte[] { 0x82, 0x02, 0x00, 0x00, 0x00, 1, 0x61 });
        Throws(new byte[] { 2, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x61 });
        Throws(V((1ul << 31) + 1).Concat(V(1)).Concat(new byte[] { 0x61 }).ToArray());
        Throws(V(0xFFFFFFFF).Concat(V(1)).Concat(new byte[] { 0x61 }).ToArray());
        Throws(Wire(1, 1L << 31, 0x61));
        Throws(Wire(1, uint.MaxValue, 0x61));

        // Byte count out of the data.
        Throws(Wire(1, 2, 0x61));
        Throws(Wire(3, 4, 0x61, 0x62, 0x63));
        Throws(Wire(1, int.MaxValue, 0x61));

        // Counts no UTF-8 string has: chars > bytes (rejected before allocating), bytes > 3 * chars.
        Throws(Wire(2, 1, 0x61, 0x62));
        Throws(Wire(int.MaxValue, 1, 0x61));
        Throws(Wire(int.MaxValue, 8, Encoding.UTF8.GetBytes("abcdefgh")));
        Throws(Wire(1, 0));
        Throws(Wire(1, 4, 0x61, 0x61, 0x61, 0x61));
        Throws(Wire(2, 7, 0xE3, 0x83, 0x8B, 0xE3, 0x83, 0x8E, 0x61));

        // Counts in range that do not match the bytes.
        Throws(Wire(2, Encoding.UTF8.GetBytes("é"))); // fewer chars than counted
        Throws(Wire(3, Encoding.UTF8.GetBytes("ニノ"))); // fewer chars than counted
        Throws(Wire(1, Encoding.UTF8.GetBytes("ab"))); // more chars than counted
        Throws(Wire(2, Encoding.UTF8.GetBytes("abc"))); // more chars than counted
        Throws(Wire(1, 0xF0, 0x9F, 0x98, 0x80)); // a surrogate pair where one char is left (and bytes > 3 * chars)
        Throws(Wire(3, 0xF0, 0x9F, 0x98, 0x80)); // a surrogate pair is two chars, not three
        Throws(Wire(9, Encoding.UTF8.GetBytes("abcdefgh")).Concat(Encoding.UTF8.GetBytes("i")).ToArray()); // a char past the byte count

        // A sequence cut by the end of the byte count, even when the bytes after it would complete it.
        Throws(Wire(1, 1, 0xC3, 0xA9));
        Throws(Wire(1, 2, 0xE3, 0x83, 0x8B));
        Throws(Wire(2, 3, 0xF0, 0x9F, 0x98, 0x80));
        Throws(Wire(2, 2, 0x61, 0xC3, 0xA9));

        // Not UTF-8.
        Throws(Wire(1, 0xC3)); // truncated sequence
        Throws(Wire(1, 0xE3, 0x83)); // truncated sequence
        Throws(Wire(2, 0xF0, 0x9F, 0x98)); // truncated surrogate pair
        Throws(Wire(2, 0x80, 0x61)); // continuation byte first
        Throws(Wire(2, 0xFF, 0x61)); // not UTF-8
        Throws(Wire(1, 0xC0, 0x80)); // overlong
        Throws(Wire(1, 0xE0, 0x80, 0x80)); // overlong
        Throws(Wire(1, 0xED, 0xA0, 0x80)); // encoded surrogate
        Throws(Wire(2, 0xF4, 0x90, 0x80, 0x80)); // above U+10FFFF
        Throws(Wire(2, 0xC3, 0x41, 0x41)); // lead byte without its continuation
    }
}
