using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.RawNullables;

namespace Nino.UnitTests
{
    /// <summary>
    /// An unmanaged <c>[NinoType]</c> struct is written as its memory, also when it holds <c>T?</c> or
    /// <c>NinoNullable&lt;T&gt;</c> fields (spec 5.1): each <c>T?</c> where it lies, a flag, padding and the value,
    /// with padding and absent values cleared; one copy alone, in arrays and in any collection. A struct that is not
    /// raw for another reason keeps its members one by one, a <c>T?</c> a presence byte and the value.
    /// </summary>
    [TestClass]
    public class RawStructsHoldingNullablesTests
    {
        private static string Layout(Type type) => typeof(RawHoldsNullables).Assembly
            .GetCustomAttributes<NinoTypeLayoutAttribute>().Single(a => a.Type == type).ToString();

        [TestMethod]
        public void TheirBytesAreTheirMemory()
        {
            var value = new RawHoldsNullables { Id = 1, Total = null, Score = 2.0 };
            CollectionAssert.AreEqual(Rt.FromHex(
                "01000000 00000000" + // Id, padding to 8
                "0000000000000000 0000000000000000" + // Total: no flag, no value
                "0100000000000000 0000000000000040"), // Score: its flag, 2.0
                NinoSerializer.Serialize(value));
            Assert.AreEqual(value, NinoDeserializer.Deserialize<RawHoldsNullables>(NinoSerializer.Serialize(value)));
            StringAssert.Contains(Layout(typeof(RawHoldsNullables)), "raw=true");

            var nino = new RawHoldsNinoNullables { Id = 1, Total = 7, Price = default };
            Assert.AreEqual(8 + 16 + 32, NinoSerializer.Serialize(nino).Length);
            Assert.AreEqual(nino, NinoDeserializer.Deserialize<RawHoldsNinoNullables>(NinoSerializer.Serialize(nino)));
            StringAssert.Contains(Layout(typeof(RawHoldsNinoNullables)), "raw=true");

            var nested = new RawHoldsNested { Tag = 3, Inner = new RawHoldsNullables { Id = 5, Total = 6 } };
            Assert.AreEqual(8 + 8 + 40, NinoSerializer.Serialize(nested).Length);
            Assert.AreEqual(nested, NinoDeserializer.Deserialize<RawHoldsNested>(NinoSerializer.Serialize(nested)));
            Assert.AreEqual(default(RawHoldsNested),
                NinoDeserializer.Deserialize<RawHoldsNested>(NinoSerializer.Serialize(default(RawHoldsNested))));
        }

        [TestMethod]
        public void RunsClearWhatTheMemoryHeld()
        {
            foreach (int count in new[] { 1, 2, 7, 64, 1000 })
            {
                var values = new RawHoldsNullables[count];
                var memory = MemoryMarshal.AsBytes(values.AsSpan());
                var expected = new List<byte>(BitConverter.GetBytes(count));
                for (int i = 0; i < count; i++)
                {
                    var clean = new RawHoldsNullables
                    {
                        Id = i, Total = i % 5 == 0 ? null : i * 3L, Score = i % 3 == 0 ? null : i + 0.5,
                    };
                    expected.AddRange(NinoSerializer.Serialize(clean));
                    // Old bytes everywhere, then the fields: the padding and the absent values keep them.
                    var element = memory.Slice(i * 40, 40);
                    element.Fill(0xD7);
                    values[i].Id = clean.Id;
                    if (clean.Total.HasValue)
                        values[i].Total = clean.Total;
                    else
                        element[8] = 0;
                    if (clean.Score.HasValue)
                        values[i].Score = clean.Score;
                    else
                        element[24] = 0;
                }

                byte[] bytes = NinoSerializer.Serialize(values);
                CollectionAssert.AreEqual(expected.ToArray(), bytes, count.ToString());
                CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(new Queue<RawHoldsNullables>(values)), "Queue");
                var read = NinoDeserializer.Deserialize<RawHoldsNullables[]>(bytes);
                Assert.AreEqual(count, read.Length);
                for (int i = 0; i < count; i++)
                    Assert.IsTrue(read[i].Id == i && read[i].Total == (i % 5 == 0 ? null : i * 3L) &&
                                  read[i].Score == (i % 3 == 0 ? null : i + 0.5));
            }
        }

        [TestMethod]
        public void AStructThatIsNotRawKeepsItsMembers()
        {
            // An ignored field: the int, then the T? as a single value.
            CollectionAssert.AreEqual(Rt.FromHex("07000000 00"),
                NinoSerializer.Serialize(new MemberByMember { Id = 7, Skipped = 9 }));
            CollectionAssert.AreEqual(Rt.FromHex("07000000 01 0500000000000000"),
                NinoSerializer.Serialize(new MemberByMember { Id = 7, Total = 5 }));
            Assert.IsFalse(Layout(typeof(MemberByMember)).Contains("raw=true"));
        }
    }
}
