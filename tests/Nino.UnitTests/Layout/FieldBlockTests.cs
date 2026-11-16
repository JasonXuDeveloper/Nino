using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.FieldBlocks;

namespace Nino.UnitTests
{
    /// <summary>
    /// Raw class bodies copied as one block where the runtime lays the fields out as the body does
    /// (<c>NinoFieldBlock</c>), and written field by field where it does not: the bytes are the same either way.
    /// </summary>
    [TestClass]
    public class FieldBlockTests
    {
        private static byte[] Concat(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (var part in parts)
                all.AddRange(part);
            return all.ToArray();
        }

        private static bool? FieldBlock<T>() =>
            (bool?)typeof(T).Assembly.GetType("NinoGen." + typeof(T).FullName!.Replace('.', '_') + "Formatter")
                ?.GetField("__FieldBlock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

        [TestMethod]
        public void FieldsInOrderAreCopiedAsOneBlock()
        {
            var value = new FieldBlockInOrder { A = 0x01020304, B = 1.5f, C = -2 };
            var bytes = NinoSerializer.Serialize(value);

            CollectionAssert.AreEqual(Concat(new byte[] { 1 }, BitConverter.GetBytes(value.A),
                BitConverter.GetBytes(value.B), BitConverter.GetBytes(value.C)), bytes);
            if (Type.GetType("Mono.Runtime") == null)
                Assert.AreEqual(true, FieldBlock<FieldBlockInOrder>());
            var read = NinoDeserializer.Deserialize<FieldBlockInOrder>(bytes);
            Assert.AreEqual((value.A, value.B, value.C), (read.A, read.B, read.C));
        }

        [TestMethod]
        public void ReorderedFieldsAreWrittenOneByOne()
        {
            var value = new FieldBlockReordered { A = 7, B = 0x0102030405060708, C = 9 };
            var bytes = NinoSerializer.Serialize(value);

            CollectionAssert.AreEqual(Concat(new byte[] { 1, value.A }, BitConverter.GetBytes(value.B),
                BitConverter.GetBytes(value.C)), bytes);
            Assert.AreEqual(false, FieldBlock<FieldBlockReordered>());
        }

        [TestMethod]
        public void PaddedValuesAreNotCopiedAsTheyLie()
        {
            var value = new FieldBlockHoldsPadded { Id = 5, Value = new FieldBlockPadded { A = 3, B = 4 } };
            var bytes = NinoSerializer.Serialize(value);

            CollectionAssert.AreEqual(Concat(new byte[] { 1 }, BitConverter.GetBytes(5), new byte[] { 3, 0, 0, 0 },
                BitConverter.GetBytes(4)), bytes);
            Assert.AreEqual(false, FieldBlock<FieldBlockHoldsPadded>());
        }

        [TestMethod]
        public void SubtypeBodiesIncludeTheBaseFields()
        {
            FieldBlockBase value = new FieldBlockDerived { Id = 11, X = 12, Y = 2.5f };
            var bytes = NinoSerializer.Serialize(value);

            var read = (FieldBlockDerived)NinoDeserializer.Deserialize<FieldBlockBase>(bytes);
            Assert.AreEqual((11, 12, 2.5f), (read.Id, read.X, read.Y));
            Assert.AreEqual(4 + 12, bytes.Length);
            CollectionAssert.AreEqual(Concat(BitConverter.GetBytes(11), BitConverter.GetBytes(12),
                BitConverter.GetBytes(2.5f)), bytes[4..]);
            if (Type.GetType("Mono.Runtime") == null)
                Assert.AreEqual(true, FieldBlock<FieldBlockDerived>());
        }

        [TestMethod]
        public void BlockReadsFillAnExistingInstance()
        {
            var bytes = NinoSerializer.Serialize(new FieldBlockInOrder { A = 1, B = 2.5f, C = 3 });
            var target = new FieldBlockInOrder { A = 9, B = 9, C = 9 };
            var same = target;

            NinoDeserializer.Deserialize(bytes, ref target);

            Assert.AreSame(same, target);
            Assert.AreEqual((1, 2.5f, 3), (target.A, target.B, target.C));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<FieldBlockInOrder>(bytes[..^1]));
        }
    }
}
