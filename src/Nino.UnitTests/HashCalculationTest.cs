using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Shared.Util;

namespace Nino.UnitTests
{
    [TestClass]
    public class HashCalculationTest
    {
        [TestMethod]
        public void TestStringHash()
        {
            string v1 = "abcdefg";
            string v2 = "abcdefg";
            string v3 = "gfedcba";
            Assert.AreEqual(v1.GetStringHashCode(), v2.GetStringHashCode());
            Assert.AreNotEqual(v1.GetStringHashCode(), v3.GetStringHashCode());
            Assert.AreNotEqual(v2.GetStringHashCode(), v3.GetStringHashCode());
            Assert.AreEqual(v3.GetStringHashCode(), v3.GetStringHashCode());
        }

        [TestMethod]
        public void TestTypeHash()
        {
            Type v1 = typeof(int);
            Type v2 = typeof(int);
            Type v3 = typeof(HashCalculationTest);
            Type v4 = typeof(TestEnumVal);
            Type v5 = typeof(short);
            Type v6 = Enum.GetUnderlyingType(typeof(TestEnumVal));
            int intVal = 10;
            Type v7 = intVal.GetType();
            Assert.AreEqual(v1.GetHashCode(),v7.GetHashCode());
            Assert.AreEqual(v2.GetHashCode(),v7.GetHashCode());
            Assert.AreEqual(v7.GetHashCode(),v7.GetHashCode());
            Assert.AreEqual(v1.GetTypeHashCode(), v2.GetTypeHashCode());
            Assert.AreNotEqual(v1.GetTypeHashCode(), v3.GetTypeHashCode());
            Assert.AreNotEqual(v2.GetTypeHashCode(), v3.GetTypeHashCode());
            Assert.AreEqual(v3.GetTypeHashCode(), v3.GetTypeHashCode());
            Assert.AreNotEqual(v4.GetTypeHashCode(), v5.GetTypeHashCode());
            Assert.AreEqual(v5.GetTypeHashCode(), v5.GetTypeHashCode());
            Assert.AreEqual(v4.GetTypeHashCode(), v4.GetTypeHashCode());
            Assert.AreEqual(v6.GetTypeHashCode(), v5.GetTypeHashCode());
        }
    }
}