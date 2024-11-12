using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.UnitTests.NinoGen;

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
                    _ = c.Serialize();
                }
            }

            Parallel.Invoke(
                Test, Test, Test, Test, Test, Test, Test, Test
            );

            var buf2 = c.Serialize();
            Deserializer.Deserialize(buf2, out C c2);
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
            byte[] buf = c.Serialize();

            int tests = 100;

            void Test()
            {
                for (int i = 0; i < tests; i++)
                {
                    Deserializer.Deserialize(buf, out C c2);
                    Assert.AreEqual(c.ToString(), c2.ToString());
                }
            }

            Parallel.Invoke(
                Test, Test, Test, Test, Test, Test, Test, Test
            );
        }
    }
}