using System;
using System.Linq;
using Nino.Serialization;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
#pragma warning disable 8618

namespace Nino.UnitTests
{
    [NinoSerialize]
    public partial class A
    {
        [NinoMember(0)]
        public int Val { get; set; }
    }
        
    [NinoSerialize]
    [CodeGenIgnore]
    public partial class B
    {
        [NinoMember(0)]
        public int Val { get; set; }
    }
        
    [NinoSerialize]
    public partial class C
    {
        [NinoMember(0)]
        public string Name { get; set; }
            
        [NinoMember(1)]
        public List<A> As { get; set; }

        public override string ToString()
        {
            return $"{Name}={string.Join(",", As.Select(a => a.Val))}";
        }
    }
        
    [NinoSerialize]
    [CodeGenIgnore]
    public partial class D
    {
        [NinoMember(0)]
        public string Name { get; set; }
            
        [NinoMember(1)]
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
            // CodeGenerator.GenerateSerializationCodeForAllTypePossible();
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
            
            var bufC = Serializer.Serialize(c);
            var bufD = Serializer.Serialize(d);
            Assert.IsTrue(bufC.SequenceEqual(bufD));
            
            var c2 = Deserializer.Deserialize<C>(bufC);
            var d2 = Deserializer.Deserialize<D>(bufD);
            
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