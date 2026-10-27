using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests
{
    [NinoType]
    public class StringReuseHolder
    {
        public string Field;
        public string Property { get; set; }
        [NinoUtf8] public string Ascii;
        [NinoUtf8] public string Wide;
        public List<string> Tags;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public class StringReuseKeyed
    {
        public string Name;
        [NinoUtf8] public string Code;
    }

    [TestClass]
    public class StringReuseTests
    {
        private static StringReuseHolder Make(string suffix) => new StringReuseHolder
        {
            Field = "field" + suffix,
            Property = "property" + suffix,
            Ascii = "ascii" + suffix,
            Wide = "wide 中文" + suffix,
            Tags = new List<string> { "a" + suffix, "b" + suffix },
        };

        [TestMethod]
        public void UnchangedStringsAreKeptWhenReadingIntoAnExistingObject()
        {
            var bytes = NinoSerializer.Serialize(Make("1"));
            var target = Make("1");
            var (field, property, ascii, tag0) = (target.Field, target.Property, target.Ascii, target.Tags[0]);

            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreSame(field, target.Field);
            Assert.AreSame(property, target.Property);
            Assert.AreSame(tag0, target.Tags[0]);
            Assert.AreSame(ascii, target.Ascii);
            Assert.AreEqual("wide 中文1", target.Wide);
        }

        [TestMethod]
        public void ChangedStringsAreReplaced()
        {
            var bytes = NinoSerializer.Serialize(Make("2"));
            var target = Make("1");
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreEqual("field2", target.Field);
            Assert.AreEqual("property2", target.Property);
            Assert.AreEqual("ascii2", target.Ascii);
            Assert.AreEqual("wide 中文2", target.Wide);
            CollectionAssert.AreEqual(new[] { "a2", "b2" }, target.Tags);

            // Same length, other chars; shorter and longer; to and from null and empty.
            target = new StringReuseHolder { Field = "fielX1", Property = "p", Ascii = null, Wide = "", Tags = null };
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreEqual("field2", target.Field);
            Assert.AreEqual("property2", target.Property);
            Assert.AreEqual("ascii2", target.Ascii);
            Assert.AreEqual("wide 中文2", target.Wide);

            var empty = NinoSerializer.Serialize(new StringReuseHolder { Field = "", Property = null });
            NinoDeserializer.Deserialize(empty, ref target);
            Assert.AreEqual("", target.Field);
            Assert.IsNull(target.Property);
            Assert.IsNull(target.Ascii);
        }

        [TestMethod]
        public void KeyedStringsAreKept()
        {
            var bytes = NinoSerializer.Serialize(new StringReuseKeyed { Name = "name", Code = "CODE" });
            var target = new StringReuseKeyed { Name = new string('n', 1) + "ame", Code = new string('C', 1) + "ODE" };
            var (name, code) = (target.Name, target.Code);
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreSame(name, target.Name);
            Assert.AreSame(code, target.Code);
            Assert.AreEqual("CODE", target.Code);
        }

        [TestMethod]
        public void LongUtf8StringsAreKeptOrReplaced()
        {
            // ASCII of every length around the header sizes (one byte up to 126 chars, three from 128 to 65,534),
            // kept when unchanged; one char changed at either end replaces it; non-ASCII text of the same length is decoded.
            foreach (int length in new[] { 1, 126, 127, 128, 129, 360, 4096, 65534, 65535, 70000 })
            {
                string text = new string('x', length - 1) + "y";
                var bytes = NinoSerializer.Serialize(new StringReuseKeyed { Name = "n", Code = text });
                var target = new StringReuseKeyed { Code = new string('x', length - 1) + "y" };
                var kept = target.Code;
                NinoDeserializer.Deserialize(bytes, ref target);
                Assert.AreEqual(text, target.Code, $"{length}");
                // 127 chars (counts of three bytes and one) and from 65,535 (five-byte counts) are decoded.
                if (length != 127 && length < 65535)
                    Assert.AreSame(kept, target.Code, $"{length}");

                foreach (string other in new[] { "z" + text.Substring(1), text.Substring(0, length - 1) + "z" })
                {
                    target = new StringReuseKeyed { Code = other };
                    NinoDeserializer.Deserialize(bytes, ref target);
                    Assert.AreEqual(text, target.Code, $"{length}");
                }

                string wide = text.Substring(0, length - 1) + "\u00e9";
                var wideBytes = NinoSerializer.Serialize(new StringReuseKeyed { Code = wide });
                target = new StringReuseKeyed { Code = text };
                NinoDeserializer.Deserialize(wideBytes, ref target);
                Assert.AreEqual(wide, target.Code, $"{length}");
                target = new StringReuseKeyed { Code = wide };
                NinoDeserializer.Deserialize(bytes, ref target);
                Assert.AreEqual(text, target.Code, $"{length}");

                // Cut inside the text: still invalid, not a match.
                target = new StringReuseKeyed { Code = text };
                var cut = bytes;
                Assert.ThrowsExactly<NinoDataException>(() =>
                    NinoDeserializer.Deserialize(new System.ReadOnlySpan<byte>(cut, 0, cut.Length - 1), ref target));
            }
        }

        [TestMethod]
        public void TruncatedDataStillThrows()
        {
            var bytes = NinoSerializer.Serialize(Make("1"));
            for (int cut = 1; cut < bytes.Length; cut++)
            {
                var target = Make("1");
                bool threw = false;
                try
                {
                    NinoDeserializer.Deserialize(new System.ReadOnlySpan<byte>(bytes, 0, bytes.Length - cut), ref target);
                }
                catch (System.Exception)
                {
                    threw = true;
                }

                Assert.IsTrue(threw, $"no exception with {cut} bytes cut");
            }
        }
    }
}
