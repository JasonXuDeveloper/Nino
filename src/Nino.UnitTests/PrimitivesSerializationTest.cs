using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        [Serialization.NinoSerialize]
        public struct EmptyStruct
        {
        }

        [TestMethod]
        public void TestEmptyStruct()
        {
            var val = new EmptyStruct();
            byte[] buf = Serialization.Serializer.Serialize(val, Serialization.CompressOption.NoCompression);
            EmptyStruct val2 =
                Serialization.Deserializer.Deserialize<EmptyStruct>(new ArraySegment<byte>(buf, 0, buf.Length),
                    Serialization.CompressOption.NoCompression);
            Assert.AreEqual(val, val2);
        }

        [TestMethod]
        public void TestEnum()
        {
            TestEnumVal val = TestEnumVal.B;
            byte[] buf = Serialization.Serializer.Serialize(val);
            TestEnumVal val2 =
                Serialization.Deserializer.Deserialize<TestEnumVal>(new ArraySegment<byte>(buf, 0, buf.Length));
            Assert.AreEqual(val, val2);
            Assert.AreEqual(TestEnumVal.B, val2);
        }

        [TestMethod]
        public void TestByte()
        {
            byte val = 10;
            byte[] buf = Serialization.Serializer.Serialize(val);
            byte result = Serialization.Deserializer.Deserialize<byte>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(10, val);
        }

        [TestMethod]
        public void TestSByte()
        {
            sbyte val = -95;
            byte[] buf = Serialization.Serializer.Serialize(val);
            sbyte result = Serialization.Deserializer.Deserialize<sbyte>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(-95, val);
        }

        [TestMethod]
        public void TestShort()
        {
            short val = short.MinValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            short result = Serialization.Deserializer.Deserialize<short>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(short.MinValue, val);
        }

        [TestMethod]
        public void TestUShort()
        {
            ushort val = ushort.MaxValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            ushort result = Serialization.Deserializer.Deserialize<ushort>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(ushort.MaxValue, val);
        }

        [TestMethod]
        public void TestInt()
        {
            int val = int.MinValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            int result = Serialization.Deserializer.Deserialize<int>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(int.MinValue, val);
        }

        [TestMethod]
        public void TestUInt()
        {
            uint val = uint.MaxValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            uint result = Serialization.Deserializer.Deserialize<uint>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(uint.MaxValue, val);
        }

        [TestMethod]
        public void TestLong()
        {
            long val = long.MinValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            long result = Serialization.Deserializer.Deserialize<long>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(long.MinValue, val);
        }

        [TestMethod]
        public void TestULong()
        {
            ulong val = ulong.MaxValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            ulong result = Serialization.Deserializer.Deserialize<ulong>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(ulong.MaxValue, val);
        }

        [TestMethod]
        public void TestString()
        {
            string val = "test";
            byte[] buf = Serialization.Serializer.Serialize(val);
            string result = Serialization.Deserializer.Deserialize<string>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual("test", val);
        }

        [TestMethod]
        public void TestBool()
        {
            bool val = true;
            // ReSharper disable ConditionIsAlwaysTrueOrFalse
            byte[] buf = Serialization.Serializer.Serialize(val);
            bool result = Serialization.Deserializer.Deserialize<bool>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(true, val);
            // ReSharper restore ConditionIsAlwaysTrueOrFalse
        }

        [TestMethod]
        public void TestDecimal()
        {
            decimal val = decimal.MaxValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            decimal result = Serialization.Deserializer.Deserialize<decimal>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(decimal.MaxValue, val);
        }

        [TestMethod]
        public void TestDouble()
        {
            double val = double.MaxValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            double result = Serialization.Deserializer.Deserialize<double>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(double.MaxValue, val);
        }

        [TestMethod]
        public void TestFloat()
        {
            float val = float.MaxValue;
            byte[] buf = Serialization.Serializer.Serialize(val);
            float result = Serialization.Deserializer.Deserialize<float>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(float.MaxValue, val);
        }

        [TestMethod]
        public void TestChar()
        {
            char val = 'a';
            byte[] buf = Serialization.Serializer.Serialize(val);
            char result = Serialization.Deserializer.Deserialize<char>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual('a', val);
        }

        [TestMethod]
        public void TestDateTime()
        {
            DateTime val = new DateTime(2000, 1, 1);
            byte[] buf = Serialization.Serializer.Serialize(val);
            DateTime result = Serialization.Deserializer.Deserialize<DateTime>(buf);
            Assert.AreEqual(val, result);
            Assert.AreEqual(new DateTime(2000, 1, 1), val);
        }

        [TestMethod]
        public void TestEnumArr()
        {
            TestEnumVal[] val = new[] { TestEnumVal.A, TestEnumVal.B };
            byte[] buf = Serialization.Serializer.Serialize(val);
            TestEnumVal[] result = Serialization.Deserializer.DeserializeArray<TestEnumVal>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { TestEnumVal.A, TestEnumVal.B }));
        }

        [TestMethod]
        public void TestByteArr()
        {
            byte[] val = new byte[] { 1, 2, 3, 4, 5 };
            byte[] buf = Serialization.Serializer.Serialize(val);
            byte[] result = Serialization.Deserializer.DeserializeArray<byte>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }));
        }

        [TestMethod]
        public void TestSByteArr()
        {
            sbyte[] val = new sbyte[] { -1, -2, -3, -4, -5 };
            byte[] buf = Serialization.Serializer.Serialize(val);
            sbyte[] result = Serialization.Deserializer.DeserializeArray<sbyte>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new sbyte[] { -1, -2, -3, -4, -5 }));
        }

        [TestMethod]
        public void TestShortArr()
        {
            short[] val = new[] { short.MinValue, short.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            short[] result = Serialization.Deserializer.DeserializeArray<short>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { short.MinValue, short.MaxValue }));
        }

        [TestMethod]
        public void TestUShortArr()
        {
            ushort[] val = new[] { ushort.MinValue, ushort.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            ushort[] result = Serialization.Deserializer.DeserializeArray<ushort>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { ushort.MinValue, ushort.MaxValue }));
        }

        [TestMethod]
        public void TestIntArr()
        {
            int[] val = new[] { int.MinValue, int.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            int[] result = Serialization.Deserializer.DeserializeArray<int>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { int.MinValue, int.MaxValue }));
        }

        [TestMethod]
        public void TestUIntArr()
        {
            uint[] val = new[] { uint.MinValue, uint.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            uint[] result = Serialization.Deserializer.DeserializeArray<uint>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { uint.MinValue, uint.MaxValue }));
        }

        [TestMethod]
        public void TestLongArr()
        {
            long[] val = new[] { long.MinValue, long.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            long[] result = Serialization.Deserializer.DeserializeArray<long>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { long.MinValue, long.MaxValue }));
        }

        [TestMethod]
        public void TestULongArr()
        {
            ulong[] val = new[] { ulong.MinValue, ulong.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            ulong[] result = Serialization.Deserializer.DeserializeArray<ulong>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { ulong.MinValue, ulong.MaxValue }));
        }

        [TestMethod]
        public void TestStringArr()
        {
            string[] val = new[] { "a", "b", "c", "d", "e" };
            byte[] buf = Serialization.Serializer.Serialize(val);
            string[] result = Serialization.Deserializer.DeserializeArray<string>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { "a", "b", "c", "d", "e" }));
        }

        [TestMethod]
        public void TestBoolArr()
        {
            bool[] val = new[] { true, false };
            byte[] buf = Serialization.Serializer.Serialize(val);
            bool[] result = Serialization.Deserializer.DeserializeArray<bool>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { true, false }));
        }

        [TestMethod]
        public void TestFloatArr()
        {
            float[] val = new[] { float.MinValue, float.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            float[] result = Serialization.Deserializer.DeserializeArray<float>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { float.MinValue, float.MaxValue }));
        }

        [TestMethod]
        public void TestDoubleArr()
        {
            double[] val = new[] { double.MinValue, double.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            double[] result = Serialization.Deserializer.DeserializeArray<double>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { double.MinValue, double.MaxValue }));
        }

        [TestMethod]
        public void TestCharArr()
        {
            char[] val = new[] { 'a', 'b', 'c', 'd', 'e' };
            byte[] buf = Serialization.Serializer.Serialize(val);
            char[] result = Serialization.Deserializer.DeserializeArray<char>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { 'a', 'b', 'c', 'd', 'e' }));
        }

        [TestMethod]
        public void TestDateTimeArr()
        {
            DateTime[] val = new[] { DateTime.Today, DateTime.Today.AddDays(-1234) };
            byte[] buf = Serialization.Serializer.Serialize(val);
            DateTime[] result = Serialization.Deserializer.DeserializeArray<DateTime>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new[] { DateTime.Today, DateTime.Today.AddDays(-1234) }));
        }

        [TestMethod]
        public void TestEnumList()
        {
            System.Collections.Generic.List<TestEnumVal> val = new System.Collections.Generic.List<TestEnumVal>()
                { TestEnumVal.A, TestEnumVal.B };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<TestEnumVal> result =
                Serialization.Deserializer.DeserializeList<TestEnumVal>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<TestEnumVal>()
                { TestEnumVal.A, TestEnumVal.B }));
        }

        [TestMethod]
        public void TestByteList()
        {
            System.Collections.Generic.List<byte> val = new System.Collections.Generic.List<byte>
                { byte.MinValue, byte.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<byte> result =
                Serialization.Deserializer.DeserializeList<byte>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<byte>
                { byte.MinValue, byte.MaxValue }));
        }

        [TestMethod]
        public void TestSByteList()
        {
            System.Collections.Generic.List<sbyte> val = new System.Collections.Generic.List<sbyte>
                { sbyte.MinValue, sbyte.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<sbyte> result =
                Serialization.Deserializer.DeserializeList<sbyte>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<sbyte>
                { sbyte.MinValue, sbyte.MaxValue }));
        }

        [TestMethod]
        public void TestShortList()
        {
            System.Collections.Generic.List<short> val = new System.Collections.Generic.List<short>
                { short.MinValue, short.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<short> result =
                Serialization.Deserializer.DeserializeList<short>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<short>
                { short.MinValue, short.MaxValue }));
        }

        [TestMethod]
        public void TestUShortList()
        {
            System.Collections.Generic.List<ushort> val = new System.Collections.Generic.List<ushort>
                { ushort.MinValue, ushort.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<ushort> result =
                Serialization.Deserializer.DeserializeList<ushort>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<ushort>
                { ushort.MinValue, ushort.MaxValue }));
        }

        [TestMethod]
        public void TestIntList()
        {
            System.Collections.Generic.List<int> val = new System.Collections.Generic.List<int>
                { int.MinValue, int.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<int> result =
                Serialization.Deserializer.DeserializeList<int>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<int>
                { int.MinValue, int.MaxValue }));
        }

        [TestMethod]
        public void TestUIntList()
        {
            System.Collections.Generic.List<uint> val = new System.Collections.Generic.List<uint>
                { uint.MinValue, uint.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<uint> result =
                Serialization.Deserializer.DeserializeList<uint>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<uint>
                { uint.MinValue, uint.MaxValue }));
        }

        [TestMethod]
        public void TestLongList()
        {
            System.Collections.Generic.List<long> val = new System.Collections.Generic.List<long>
                { long.MinValue, long.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<long> result =
                Serialization.Deserializer.DeserializeList<long>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<long>
                { long.MinValue, long.MaxValue }));
        }

        [TestMethod]
        public void TestULongList()
        {
            System.Collections.Generic.List<ulong> val = new System.Collections.Generic.List<ulong>
                { ulong.MinValue, ulong.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<ulong> result =
                Serialization.Deserializer.DeserializeList<ulong>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<ulong>
                { ulong.MinValue, ulong.MaxValue }));
        }

        [TestMethod]
        public void TestFloatList()
        {
            System.Collections.Generic.List<float> val = new System.Collections.Generic.List<float>
                { float.MinValue, float.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<float> result =
                Serialization.Deserializer.DeserializeList<float>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<float>
                { float.MinValue, float.MaxValue }));
        }

        [TestMethod]
        public void TestDoubleList()
        {
            System.Collections.Generic.List<double> val = new System.Collections.Generic.List<double>
                { double.MinValue, double.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<double> result =
                Serialization.Deserializer.DeserializeList<double>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<double>
                { double.MinValue, double.MaxValue }));
        }

        [TestMethod]
        public void TestDecimalList()
        {
            System.Collections.Generic.List<decimal> val = new System.Collections.Generic.List<decimal>
                { decimal.MinValue, decimal.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<decimal> result =
                Serialization.Deserializer.DeserializeList<decimal>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<decimal>
                { decimal.MinValue, decimal.MaxValue }));
        }

        [TestMethod]
        public void TestCharList()
        {
            System.Collections.Generic.List<char> val = new System.Collections.Generic.List<char>
                { char.MinValue, char.MaxValue };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<char> result =
                Serialization.Deserializer.DeserializeList<char>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<char>
                { char.MinValue, char.MaxValue }));
        }


        [TestMethod]
        public void TestStringList()
        {
            System.Collections.Generic.List<string> val = new System.Collections.Generic.List<string>
                { "Hello", "World" };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<string> result =
                Serialization.Deserializer.DeserializeList<string>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<string> { "Hello", "World" }));
        }

        [TestMethod]
        public void TestBoolList()
        {
            System.Collections.Generic.List<bool> val = new System.Collections.Generic.List<bool>
                { true, false };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<bool> result =
                Serialization.Deserializer.DeserializeList<bool>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<bool> { true, false }));
        }

        [TestMethod]
        public void TestDateTimeList()
        {
            System.Collections.Generic.List<DateTime> val = new System.Collections.Generic.List<DateTime>
                { DateTime.Today, DateTime.Today.AddDays(-1234) };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.List<DateTime> result =
                Serialization.Deserializer.DeserializeList<DateTime>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.List<DateTime>
                { DateTime.Today, DateTime.Today.AddDays(-1234) }));
        }

        [TestMethod]
        public void ByteIntDict()
        {
            System.Collections.Generic.Dictionary<byte, int> val = new System.Collections.Generic.Dictionary<byte, int>
                { { byte.MinValue, int.MinValue }, { byte.MaxValue, int.MaxValue } };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.Dictionary<byte, int> result =
                Serialization.Deserializer.DeserializeDictionary<byte, int>(buf);
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
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.Dictionary<string, short> result = Serialization.Deserializer
                .DeserializeDictionary<string, short>(buf);
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
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.Dictionary<string, string> result = Serialization.Deserializer
                .DeserializeDictionary<string, string>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
            Assert.IsTrue(result.SequenceEqual(new System.Collections.Generic.Dictionary<string, string>
                { { "Hello", "World" }, { "World", "Hello" } }));
        }

        [TestMethod]
        public void TestNullable()
        {
            int? val = 123;
            byte[] buf = Serialization.Serializer.Serialize(val);
            int? result = Serialization.Deserializer.DeserializeNullable<int>(buf);
            Assert.AreEqual(val, result);
            
            val = null;
            // ReSharper disable ExpressionIsAlwaysNull
            buf = Serialization.Serializer.Serialize(val);
            result = Serialization.Deserializer.DeserializeNullable<int>(buf);
            Assert.AreEqual(val, result);
            // ReSharper restore ExpressionIsAlwaysNull
        }
        
        [TestMethod]
        public void TestHashSet()
        {
            System.Collections.Generic.HashSet<int> val = new System.Collections.Generic.HashSet<int>
                { 1, 2, 3, 4, 5 };
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.HashSet<int> result = Serialization.Deserializer
                .DeserializeHashSet<int>(buf);
            Assert.IsTrue(val.SetEquals(result));
            Assert.IsTrue(result.SetEquals(new System.Collections.Generic.HashSet<int>
                { 1, 2, 3, 4, 5 }));
        }
        
        [TestMethod]
        public void TestQueue()
        {
            System.Collections.Generic.Queue<int> val = new System.Collections.Generic.Queue<int>();
            val.Enqueue(1);
            val.Enqueue(2);
            val.Enqueue(3);
            val.Enqueue(4);
            val.Enqueue(5);
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.Queue<int> result = Serialization.Deserializer
                .DeserializeQueue<int>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
        }
        
        [TestMethod]
        public void TestStack()
        {
            System.Collections.Generic.Stack<int> val = new System.Collections.Generic.Stack<int>();
            val.Push(1);
            val.Push(2);
            val.Push(3);
            val.Push(4);
            val.Push(5);
            byte[] buf = Serialization.Serializer.Serialize(val);
            System.Collections.Generic.Stack<int> result = Serialization.Deserializer
                .DeserializeStack<int>(buf);
            Assert.IsTrue(val.SequenceEqual(result));
        }
    }
}