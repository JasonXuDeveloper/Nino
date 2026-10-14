using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset;

namespace Nino.UnitTests
{
    [TestClass]
    public class RawStructTests
    {
        [TestMethod]
        public void RawStructIsWrittenAsMemory()
        {
            var value = new SubsetRawStruct { X = 1, Y = 2 };
            var bytes = NinoSerializer.Serialize(value);
            Assert.AreEqual(Unsafe.SizeOf<SubsetRawStruct>(), bytes.Length);
            Assert.AreEqual(value, NinoDeserializer.Deserialize<SubsetRawStruct>(bytes));

            var outer = new SubsetRawOuter { Inner = value, Z = 3 };
            bytes = NinoSerializer.Serialize(outer);
            Assert.AreEqual(Unsafe.SizeOf<SubsetRawOuter>(), bytes.Length);
            Assert.AreEqual(outer, NinoDeserializer.Deserialize<SubsetRawOuter>(bytes));
        }

        [TestMethod]
        public void RawStructCollectionsAreOneBlock()
        {
            var array = new SubsetRawStruct[100];
            for (int i = 0; i < array.Length; i++)
                array[i] = new SubsetRawStruct { X = i, Y = -i };

            var bytes = NinoSerializer.Serialize(array);
            Assert.AreEqual(4 + array.Length * Unsafe.SizeOf<SubsetRawStruct>(), bytes.Length);
            CollectionAssert.AreEqual(array, NinoDeserializer.Deserialize<SubsetRawStruct[]>(bytes));

            var list = new List<SubsetRawStruct>(array);
            CollectionAssert.AreEqual(list,
                NinoDeserializer.Deserialize<List<SubsetRawStruct>>(NinoSerializer.Serialize(list)));
        }

        [TestMethod]
        public void NonRawStructsAreWrittenMemberWise()
        {
            var partial = new SubsetPartialStruct { X = 1, Ignored = 5, Y = 2 };
            var bytes = NinoSerializer.Serialize(partial);
            Assert.AreEqual(12, bytes.Length);
            var result = NinoDeserializer.Deserialize<SubsetPartialStruct>(bytes);
            Assert.AreEqual(1, result.X);
            Assert.AreEqual(0, result.Ignored);
            Assert.AreEqual(2, result.Y);

            var tolerant = new SubsetTolerantStruct { X = 1, Y = 2 };
            bytes = NinoSerializer.Serialize(tolerant);
            Assert.AreEqual(4 + 12, bytes.Length);
            Assert.AreEqual(tolerant, NinoDeserializer.Deserialize<SubsetTolerantStruct>(bytes));
        }
    }
}
