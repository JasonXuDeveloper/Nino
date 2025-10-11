using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    [TestClass]
    public class InheritanceSerializationTests
    {
        [NinoType]
        public interface IBase
        {
            public int Val { get; set; }
        }
        
        public class IBaseIns : IBase
        {
            public int Val { get; set; }
            public override string ToString() => $"{Val}";
        }

        [NinoType]
        public abstract class ABase
        {
            public abstract int Val { get; set; }
        }
        
        public class ABaseIns : ABase
        {
            public override int Val { get; set; }
            public override string ToString() => $"{Val}";
        }
        
        [NinoType(allowInheritance: false)]
        public abstract class ABase2
        {
            public abstract int Val { get; set; }
        }
        
        [NinoType]
        public class ABase2Ins : ABase2
        {
            public override int Val { get; set; }
            public override string ToString() => $"{Val}";
        }
        
        public class ABase2Ins2 : ABase2
        {
            public override int Val { get; set; }
        }
        
        [TestMethod]
        public void TestInheritance()
        {
            var iBaseIns = new IBaseIns() { Val = 1 };
            var aBaseIns = new ABaseIns() { Val = 2 };
            var aBase2Ins = new ABase2Ins() { Val = 3 };
            var aBase2Ins2 = new ABase2Ins2() { Val = 4 };

            Assert.AreEqual(iBaseIns.ToString(), NinoDeserializer.Deserialize<IBaseIns>(NinoSerializer.Serialize(iBaseIns)).ToString());
            Assert.AreEqual(aBaseIns.ToString(), NinoDeserializer.Deserialize<ABaseIns>(NinoSerializer.Serialize(aBaseIns)).ToString());
            Assert.AreEqual(aBase2Ins.ToString(), NinoDeserializer.Deserialize<ABase2Ins>(NinoSerializer.Serialize(aBase2Ins)).ToString());
            // 由于没有 ABase2Ins2 的CachedSerializer报空
            Assert.ThrowsException<NullReferenceException>(() => NinoSerializer.Serialize(aBase2Ins2));
        }
    }
}