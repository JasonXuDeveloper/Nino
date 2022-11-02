using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Shared.Mgr;

namespace Nino.UnitTests
{
    [TestClass]
    public class StringMgrTest
    {
        [TestMethod]
        public void TestStringSplit()
        {
            string txt = "a123|b456|c789|";
            var arr = txt.AsSpan().Split('|');
            Assert.AreEqual(4, arr.Length);
            Assert.AreEqual("a123", arr[0]);
            Assert.AreEqual("b456", arr[1]);
            Assert.AreEqual("c789", arr[2]);
            Assert.AreEqual("", arr[3]);
        }
        
        [TestMethod]
        public void TestStringSplit2()
        {
            string txt = "a123|b456|c789";
            var arr = txt.AsSpan().Split('|');
            Assert.AreEqual(3, arr.Length);
            Assert.AreEqual("a123", arr[0]);
            Assert.AreEqual("b456", arr[1]);
            Assert.AreEqual("c789", arr[2]);
        }
        
        [TestMethod]
        public void TestStringSplit3()
        {
            string txt = "|a123|b456|c789";
            var arr = txt.AsSpan().Split('|');
            Assert.AreEqual(4, arr.Length);
            Assert.AreEqual("", arr[0]);
            Assert.AreEqual("a123", arr[1]);
            Assert.AreEqual("b456", arr[2]);
            Assert.AreEqual("c789", arr[3]);
        }
        
        [TestMethod]
        public void TestStringSplit4()
        {
            string txt = "|a123|b456|c789|";
            var arr = txt.AsSpan().Split('|');
            Assert.AreEqual(5, arr.Length);
            Assert.AreEqual("", arr[0]);
            Assert.AreEqual("a123", arr[1]);
            Assert.AreEqual("b456", arr[2]);
            Assert.AreEqual("c789", arr[3]);
            Assert.AreEqual("", arr[4]);
        }
    }
}