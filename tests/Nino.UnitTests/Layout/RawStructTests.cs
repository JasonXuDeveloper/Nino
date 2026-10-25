using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.UnitTests.Subset;

namespace Nino.UnitTests
{
    public struct PlainGenericPair<T> where T : unmanaged
    {
        public T A;
        public T B;
    }

    [NinoType]
    public struct PredicateNinoStruct
    {
        public int X;
    }

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
        public void RawStructMultiDimArraysAreOneBlock()
        {
            var grid = new SubsetRawStruct[3, 4];
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 4; j++)
                grid[i, j] = new SubsetRawStruct { X = i, Y = j };
            var bytes = NinoSerializer.Serialize(grid);
            Assert.AreEqual(8 + 12 * Unsafe.SizeOf<SubsetRawStruct>(), bytes.Length);
            var result = NinoDeserializer.Deserialize<SubsetRawStruct[,]>(bytes);
            CollectionAssert.AreEqual(grid, result);
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

        // NativeAOT decides which unregistered unmanaged types are raw with the resolver's own rule
        // (NinoReflection.IsRawShape), so a type is raw with or without dynamic code.
        [TestMethod]
        public void NativeAotAndTheResolverAgreeOnRawTypes()
        {
            var raw = new[] { typeof(int), typeof(DateTime), typeof(Guid), typeof(PlainGenericPair<int>), typeof(decimal) };
            var notRaw = new[]
            {
                typeof(int?), typeof(KeyValuePair<int, int>), typeof(ValueTuple<int, int>), typeof(DateTimeOffset),
                typeof(IntPtr), typeof(UIntPtr), typeof(PredicateNinoStruct)
            };
            foreach (var type in raw)
            {
                Assert.IsTrue(NinoReflection.IsRawShape(type), type.Name);
                Assert.IsTrue(NinoReflection.IsRawUnmanaged(type), type.Name);
            }

            foreach (var type in notRaw)
                Assert.IsFalse(NinoReflection.IsRawShape(type), type.Name);
            // Generic structs that are not [NinoType]s were left out under NativeAOT only; the resolver writes them raw.
            var pair = new PlainGenericPair<int> { A = 1, B = 2 };
            CollectionAssert.AreEqual(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 },
                NinoSerializer.SerializeBoxed(pair, typeof(PlainGenericPair<int>)));
        }
    }
}
