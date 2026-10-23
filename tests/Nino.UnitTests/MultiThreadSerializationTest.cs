using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    [TestClass]
    public class MultiThreadSerializationTest
    {
        [TestMethod]
        public void MultiThreadCodeGenSerialize()
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
            int tests = 100;

            void Test()
            {
                for (int i = 0; i < tests; i++)
                {
                    _ = NinoSerializer.Serialize(c);
                }
            }

            Parallel.Invoke(
                Test, Test, Test, Test, Test, Test, Test, Test
            );

            var buf2 = NinoSerializer.Serialize(c);
            C c2 = NinoDeserializer.Deserialize<C>(buf2);
            Assert.AreEqual(c.ToString(), c2.ToString());
        }

        [TestMethod]
        public void MultiThreadCodeGenDeserialize()
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
            byte[] buf = NinoSerializer.Serialize(c);

            int tests = 100;

            void Test()
            {
                for (int i = 0; i < tests; i++)
                {
                    C c2 = NinoDeserializer.Deserialize<C>(buf);
                    Assert.AreEqual(c.ToString(), c2.ToString());
                }
            }

            Parallel.Invoke(
                Test, Test, Test, Test, Test, Test, Test, Test
            );
        }
    }
}