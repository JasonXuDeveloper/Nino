using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>
/// UTF-8 strings are an <c>int32</c> char (UTF-16 code unit) count, then the UTF-8 bytes; the reader finds the end by
/// decoding that many chars. Malformed data throws and never reads past the payload.
/// </summary>
[TestClass]
public class Utf8WireTests
{
    private static byte[] Prefixed(int count, params byte[] bytes) => BitConverter.GetBytes(count).Concat(bytes).ToArray();

    private static string Read(byte[] data, out int position)
    {
        var reader = new Reader(data);
        var value = reader.ReadUtf8();
        position = reader.Position;
        return value;
    }

    private static void Throws(byte[] data)
    {
        Assert.ThrowsException<InvalidOperationException>(() => Read(data, out _), BitConverter.ToString(data));
    }

    [TestMethod]
    public void Layout()
    {
        foreach (var s in new[] { "a", "héllo", "ニノ", "😀", "a😀b", new string('x', 5000) + "é" })
        {
            using var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer.WriteUtf8(s);
            writer.Flush();
            CollectionAssert.AreEqual(Prefixed(s.Length, Encoding.UTF8.GetBytes(s)), buffer.WrittenSpan.ToArray());
        }

        using (var buffer = new NinoArrayBufferWriter())
        {
            var writer = new Writer(buffer);
            writer.WriteUtf8(null);
            writer.WriteUtf8("");
            writer.Flush();
            CollectionAssert.AreEqual(BitConverter.GetBytes(-1).Concat(BitConverter.GetBytes(0)).ToArray(),
                buffer.WrittenSpan.ToArray());
        }
    }

    [TestMethod]
    public void EndsAfterItsCharsWhateverFollows()
    {
        // The bytes after the string (here an int -1, an invalid UTF-8 byte, and a lead byte) are not decoded.
        foreach (var tail in new[] { new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, new byte[] { 0x80 }, new byte[] { 0xE3 }, new byte[] { 0x41 } })
        foreach (var s in new[] { "a", "é", "ニノ😀", "😀", "abcdefghijklmnopqrstuvwxyz0123456789" })
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var data = Prefixed(s.Length, bytes.Concat(tail).ToArray());
            Assert.AreEqual(s, Read(data, out int position));
            Assert.AreEqual(4 + bytes.Length, position);
        }
    }

    [TestMethod]
    public void MalformedDataThrows()
    {
        Throws(new byte[] { 1, 0, 0 }); // truncated prefix
        Throws(Prefixed(2, 0x61)); // more chars than bytes
        Throws(Prefixed(3, 0x61, 0x62)); // more chars than bytes
        Throws(Prefixed(-2)); // invalid length
        Throws(Prefixed(1, 0xC3)); // truncated sequence
        Throws(Prefixed(1, 0xE3, 0x83)); // truncated sequence
        Throws(Prefixed(2, 0xF0, 0x9F, 0x98)); // truncated surrogate pair
        Throws(Prefixed(1, 0xF0, 0x9F, 0x98, 0x80)); // a surrogate pair where one char is left
        Throws(Prefixed(1, 0x80, 0x61)); // continuation byte first
        Throws(Prefixed(1, 0xFF, 0x61)); // not UTF-8
        Throws(Prefixed(1, 0xC0, 0x80)); // overlong
        Throws(Prefixed(1, 0xED, 0xA0, 0x80)); // encoded surrogate
        Throws(Prefixed(2, 0xC3, 0x41, 0x41)); // lead byte without its continuation
        Throws(Prefixed(9, Encoding.UTF8.GetBytes("abcdefgh"))); // ASCII run cut short
        Throws(Prefixed(int.MaxValue, 0x61));
    }
}
