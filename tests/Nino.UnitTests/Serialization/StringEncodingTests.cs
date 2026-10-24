using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// The string encoding: Latin-1 text one byte per char under the header ~length, other text UTF-16 under its
/// length. Every length around the vector widths (8, 16, 32 chars, the overlapping tails) with the first char above
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
    }

    private static bool IsLatin1(string s) => s.All(c => c <= 0xFF);

    [TestMethod]
    public void EveryLengthRoundTrips()
    {
        foreach (var s in Strings())
        {
            var bytes = NinoSerializer.Serialize(s);
            int header = BitConverter.ToInt32(bytes, 0);
            if (s == null)
                Assert.AreEqual(-1, header);
            else if (s.Length == 0)
                Assert.AreEqual(0, header);
            else if (IsLatin1(s))
            {
                Assert.AreEqual(~s.Length, header, s);
                Assert.AreEqual(4 + s.Length, bytes.Length);
                CollectionAssert.AreEqual(s.Select(c => (byte)c).ToArray(), bytes.Skip(4).ToArray());
            }
            else
            {
                Assert.AreEqual(s.Length, header, s);
                CollectionAssert.AreEqual(Encoding.Unicode.GetBytes(s), bytes.Skip(4).ToArray());
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
            // Always UTF-16 chars (no widening on read); a string reads them, and a builder reads a string's bytes.
            Assert.AreEqual(s.Length, BitConverter.ToInt32(bytes, 0));
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
}
