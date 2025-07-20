using System;
using System.Linq;
using Nino.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.UnitTests.NinoGen;

namespace Nino.UnitTests
{
    public enum TestEnumVal : short
    {
        A = Int16.MinValue,
        B = Int16.MaxValue,
    }

    [TestClass]
    public class PrimitivesSerializationTest
    {
        [NinoType]
        public struct EmptyStruct
        {
        }

        [TestMethod]
        public void TestEmptyStruct()
        {
            var val = new EmptyStruct();
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(new ArraySegment<byte>(buf, 0, buf.Length), out EmptyStruct val2);
            Assert.AreEqual(val, val2);
        }

        [TestMethod]
        public void TestEnum()
        {
            TestEnumVal val = TestEnumVal.B;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(new ArraySegment<byte>(buf, 0, buf.Length), out TestEnumVal val2);
            Assert.AreEqual(val, val2);
            Assert.AreEqual(TestEnumVal.B, val2);
        }

        [TestMethod]
        public void TestByte()
        {
            byte val = 10;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out byte result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(10, val);
        }

        [TestMethod]
        public void TestSByte()
        {
            sbyte val = -95;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out sbyte result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(-95, val);
        }

        [TestMethod]
        public void TestShort()
        {
            short val = short.MinValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out short result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(short.MinValue, val);
        }

        [TestMethod]
        public void TestUShort()
        {
            ushort val = ushort.MaxValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out ushort result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(ushort.MaxValue, val);
        }

        [TestMethod]
        public void TestInt()
        {
            int val = int.MinValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out int result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(int.MinValue, val);
        }

        [TestMethod]
        public void TestUInt()
        {
            uint val = uint.MaxValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out uint result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(uint.MaxValue, val);
        }

        [TestMethod]
        public void TestLong()
        {
            long val = long.MinValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out long result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(long.MinValue, val);
        }

        [TestMethod]
        public void TestULong()
        {
            ulong val = ulong.MaxValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out ulong result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(ulong.MaxValue, val);
        }

        [TestMethod]
        public void TestString()
        {
            string val = "test";
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out string result);
            Assert.AreEqual(val, result);
            Assert.AreEqual("test", val);
        }

        [TestMethod]
        public void TestBool()
        {
            bool val = true;
            // ReSharper disable ConditionIsAlwaysTrueOrFalse
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out bool result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(true, val);
            // ReSharper restore ConditionIsAlwaysTrueOrFalse
        }

        [TestMethod]
        public void TestDecimal()
        {
            decimal val = decimal.MaxValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out decimal result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(decimal.MaxValue, val);
        }

        [TestMethod]
        public void TestDouble()
        {
            double val = double.MaxValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out double result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(double.MaxValue, val);
        }

        [TestMethod]
        public void TestFloat()
        {
            float val = float.MaxValue;
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out float result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(float.MaxValue, val);
        }

        [TestMethod]
        public void TestChar()
        {
            char val = 'a';
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out char result);
            Assert.AreEqual(val, result);
            Assert.AreEqual('a', val);
        }

        [TestMethod]
        public void TestDateTime()
        {
            DateTime val = new DateTime(2000, 1, 1);
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out DateTime result);
            Assert.AreEqual(val, result);
            Assert.AreEqual(new DateTime(2000, 1, 1), val);
        }

        [TestMethod]
        public void TestEnumArr()
        {
            TestEnumVal[] val = new[] { TestEnumVal.A, TestEnumVal.B };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out TestEnumVal[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { TestEnumVal.A, TestEnumVal.B }));
        }

        [TestMethod]
        public void TestByteArr()
        {
            byte[] val = new byte[] { 1, 2, 3, 4, 5 };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out byte[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }));
        }

        [TestMethod]
        public void TestSByteArr()
        {
            sbyte[] val = new sbyte[] { -1, -2, -3, -4, -5 };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out sbyte[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new sbyte[] { -1, -2, -3, -4, -5 }));
        }

        [TestMethod]
        public void TestShortArr()
        {
            short[] val = new[] { short.MinValue, short.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out short[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { short.MinValue, short.MaxValue }));
        }

        [TestMethod]
        public void TestUShortArr()
        {
            ushort[] val = new[] { ushort.MinValue, ushort.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out ushort[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { ushort.MinValue, ushort.MaxValue }));
        }

        [TestMethod]
        public void TestIntArr()
        {
            int[] val = new[] { int.MinValue, int.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out int[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { int.MinValue, int.MaxValue }));
        }

        [TestMethod]
        public void TestUIntArr()
        {
            uint[] val = new[] { uint.MinValue, uint.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out uint[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { uint.MinValue, uint.MaxValue }));
        }

        [TestMethod]
        public void TestLongArr()
        {
            long[] val = new[] { long.MinValue, long.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out long[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { long.MinValue, long.MaxValue }));
        }

        [TestMethod]
        public void TestULongArr()
        {
            ulong[] val = new[] { ulong.MinValue, ulong.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out ulong[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { ulong.MinValue, ulong.MaxValue }));
        }

        [TestMethod]
        public void TestStringArr()
        {
            string[] val = new[] { "a", "b", "c", "d", "e" };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out string[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { "a", "b", "c", "d", "e" }));
        }

        [TestMethod]
        public void TestBoolArr()
        {
            bool[] val = new[] { true, false };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out bool[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { true, false }));
        }

        [TestMethod]
        public void TestFloatArr()
        {
            float[] val = new[] { float.MinValue, float.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out float[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { float.MinValue, float.MaxValue }));
        }

        [TestMethod]
        public void TestDoubleArr()
        {
            double[] val = new[] { double.MinValue, double.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out double[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { double.MinValue, double.MaxValue }));
        }

        [TestMethod]
        public void TestCharArr()
        {
            char[] val = new[] { 'a', 'b', 'c', 'd', 'e' };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out char[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { 'a', 'b', 'c', 'd', 'e' }));
        }

        [TestMethod]
        public void TestDateTimeArr()
        {
            DateTime[] val = new[] { DateTime.Today, DateTime.Today.AddDays(-1234) };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out DateTime[] result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { DateTime.Today, DateTime.Today.AddDays(-1234) }));
        }

        [TestMethod]
        public void TestEnumList()
        {
            System.Collections.Generic.List<TestEnumVal> val = new System.Collections.Generic.List<TestEnumVal>()
                { TestEnumVal.A, TestEnumVal.B };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<TestEnumVal> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<TestEnumVal>()
                { TestEnumVal.A, TestEnumVal.B }));
        }

        [TestMethod]
        public void TestByteList()
        {
            System.Collections.Generic.List<byte> val = new System.Collections.Generic.List<byte>
                { byte.MinValue, byte.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<byte> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<byte>
                { byte.MinValue, byte.MaxValue }));
        }

        [TestMethod]
        public void TestSByteList()
        {
            System.Collections.Generic.List<sbyte> val = new System.Collections.Generic.List<sbyte>
                { sbyte.MinValue, sbyte.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<sbyte> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<sbyte>
                { sbyte.MinValue, sbyte.MaxValue }));
        }

        [TestMethod]
        public void TestShortList()
        {
            System.Collections.Generic.List<short> val = new System.Collections.Generic.List<short>
                { short.MinValue, short.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<short> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<short>
                { short.MinValue, short.MaxValue }));
        }

        [TestMethod]
        public void TestUShortList()
        {
            System.Collections.Generic.List<ushort> val = new System.Collections.Generic.List<ushort>
                { ushort.MinValue, ushort.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<ushort> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<ushort>
                { ushort.MinValue, ushort.MaxValue }));
        }

        [TestMethod]
        public void TestIntList()
        {
            System.Collections.Generic.List<int> val = new System.Collections.Generic.List<int>
                { int.MinValue, int.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<int> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<int>
                { int.MinValue, int.MaxValue }));
        }

        [TestMethod]
        public void TestUIntList()
        {
            System.Collections.Generic.List<uint> val = new System.Collections.Generic.List<uint>
                { uint.MinValue, uint.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<uint> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<uint>
                { uint.MinValue, uint.MaxValue }));
        }

        [TestMethod]
        public void TestLongList()
        {
            System.Collections.Generic.List<long> val = new System.Collections.Generic.List<long>
                { long.MinValue, long.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<long> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<long>
                { long.MinValue, long.MaxValue }));
        }

        [TestMethod]
        public void TestULongList()
        {
            System.Collections.Generic.List<ulong> val = new System.Collections.Generic.List<ulong>
                { ulong.MinValue, ulong.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<ulong> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<ulong>
                { ulong.MinValue, ulong.MaxValue }));
        }

        [TestMethod]
        public void TestFloatList()
        {
            System.Collections.Generic.List<float> val = new System.Collections.Generic.List<float>
                { float.MinValue, float.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<float> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<float>
                { float.MinValue, float.MaxValue }));
        }

        [TestMethod]
        public void TestDoubleList()
        {
            System.Collections.Generic.List<double> val = new System.Collections.Generic.List<double>
                { double.MinValue, double.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<double> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<double>
                { double.MinValue, double.MaxValue }));
        }

        [TestMethod]
        public void TestDecimalList()
        {
            System.Collections.Generic.List<decimal> val = new System.Collections.Generic.List<decimal>
                { decimal.MinValue, decimal.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<decimal> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<decimal>
                { decimal.MinValue, decimal.MaxValue }));
        }

        [TestMethod]
        public void TestCharList()
        {
            System.Collections.Generic.List<char> val = new System.Collections.Generic.List<char>
                { char.MinValue, char.MaxValue };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<char> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<char>
                { char.MinValue, char.MaxValue }));
        }


        [TestMethod]
        public void TestStringList()
        {
            System.Collections.Generic.List<string> val = new System.Collections.Generic.List<string>
                { "Hello", "World" };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<string> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<string> { "Hello", "World" }));
        }

        [TestMethod]
        public void TestBoolList()
        {
            System.Collections.Generic.List<bool> val = new System.Collections.Generic.List<bool>
                { true, false };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<bool> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<bool> { true, false }));
        }

        [TestMethod]
        public void TestDateTimeList()
        {
            System.Collections.Generic.List<DateTime> val = new System.Collections.Generic.List<DateTime>
                { DateTime.Today, DateTime.Today.AddDays(-1234) };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.List<DateTime> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<DateTime>
                { DateTime.Today, DateTime.Today.AddDays(-1234) }));
        }

        [TestMethod]
        public void ByteIntDict()
        {
            System.Collections.Generic.Dictionary<byte, int> val = new System.Collections.Generic.Dictionary<byte, int>
                { { byte.MinValue, int.MinValue }, { byte.MaxValue, int.MaxValue } };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.Dictionary<byte, int> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.Dictionary<byte, int>
                { { byte.MinValue, int.MinValue }, { byte.MaxValue, int.MaxValue } }));
        }

        [TestMethod]
        public void StringShortDict()
        {
            System.Collections.Generic.Dictionary<string, short> val =
                new System.Collections.Generic.Dictionary<string, short>
                    { { "Hello", short.MinValue }, { "World", short.MaxValue } };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.Dictionary<string, short> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.Dictionary<string, short>
                { { "Hello", short.MinValue }, { "World", short.MaxValue } }));
        }

        [TestMethod]
        public void StringStringDict()
        {
            System.Collections.Generic.Dictionary<string, string> val =
                new System.Collections.Generic.Dictionary<string, string>
                    { { "Hello", "World" }, { "World", "Hello" } };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.Dictionary<string, string> result);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.Dictionary<string, string>
                { { "Hello", "World" }, { "World", "Hello" } }));
        }

        [TestMethod]
        public void TestNullable()
        {
            int? val = 123;
            byte[] buf = Serializer.Serialize(val);
            Deserializer.Deserialize(buf, out int? result);
            Assert.AreEqual(val, result);

            val = null;
            // ReSharper disable ExpressionIsAlwaysNull
            buf = val.Serialize();
            Deserializer.Deserialize(buf, out result);
            Assert.AreEqual(val, result);
            // ReSharper restore ExpressionIsAlwaysNull
        }

        [TestMethod]
        public void TestHashSet()
        {
            System.Collections.Generic.HashSet<int> val = new System.Collections.Generic.HashSet<int>
                { 1, 2, 3, 4, 5 };
            byte[] buf = val.Serialize();
            Deserializer.Deserialize(buf, out System.Collections.Generic.HashSet<int> result);
            Assert.IsTrue(val.SetEquals(result));
            Assert.IsTrue(result.SetEquals(new System.Collections.Generic.HashSet<int>
                { 1, 2, 3, 4, 5 }));
        }
    }
}