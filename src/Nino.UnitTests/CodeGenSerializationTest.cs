using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino_UnitTests_Nino;
using Nino.Core;

#pragma warning disable 8618

namespace Nino.UnitTests
{
    [NinoType]
    public class A
    {
        public int Val { get; set; }
    }

    [NinoType]
    public struct B
    {
        public int Val { get; set; }
    }

    [NinoType]
    public class C
    {
        public string Name { get; set; }
            
        public List<A> As { get; set; }

        public override string ToString()
        {
            return $"{Name}={string.Join(",", As.Select(a => a.Val))}";
        }
    }
        
    [NinoType]
    public class D
    {
        public string Name { get; set; }
            
        public List<B> Bs { get; set; }
            
        public override string ToString()
        {
            return $"{Name}={string.Join(",", Bs.Select(b => b.Val))}";
        }
    }
    
    [TestClass]
    public class CodeGenSerializationTest
    {
        [TestMethod]
        public void TestCodeGen()
        {
            C c = new C()
            {
                Name = "test",
                As = new List<A>()
                {
                    new A() { Val = 1 },
                    new A() { Val = 2 },
                    new A() { Val = 3 }
                }
            };
            D d = new D()
            {
                Name = "test",
                Bs = new List<B>()
                {
                    new B() { Val = 1 },
                    new B() { Val = 2 },
                    new B() { Val = 3 }
                }
            };
            
            var bufC = c.Serialize();
            var bufD = d.Serialize();
            
            Console.WriteLine(string.Join(", ", bufC));
            Console.WriteLine(string.Join(", ", bufD));
            
            Deserializer.Deserialize(bufC, out C c2);
            Deserializer.Deserialize(bufD, out D d2);
            
            Assert.AreEqual(c.ToString(), c.ToString());
            Assert.AreEqual(c.ToString(), d.ToString());
            Assert.AreEqual(c.ToString(), d2.ToString());
            Assert.AreEqual(c.ToString(), c2.ToString());
            Assert.AreEqual(d.ToString(), d2.ToString());
            Assert.AreEqual(d.ToString(), c2.ToString());
            
            Console.WriteLine(c2);
        }
    }
}