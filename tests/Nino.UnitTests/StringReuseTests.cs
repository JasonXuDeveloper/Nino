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
