using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Serialization;

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
            byte[] buf = Array.Empty<byte>();
            int tests = 100;

            void Test()
            {
                for (int i = 0; i < tests; i++)
                {
                    buf = Serializer.Serialize(c);
                }
            }

            Parallel.Invoke(
                Test, Test, Test, Test, Test, Test, Test, Test, Test
            );
            var c2 = Deserializer.Deserialize<C>(buf);
            Assert.AreEqual(c.ToString(), c2.ToString());
        }

        [TestMethod]
        public void MultiThreadNoCodeGenSerialize()
        {
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
            byte[] buf = Array.Empty<byte>();
            int tests = 100;

            void Test()
            {
                for (int i = 0; i < tests; i++)
                {
                    buf = Serializer.Serialize(d);
                }
            }

            Parallel.Invoke(
                Test, Test, Test, Test, Test, Test, Test, Test, Test
            );

            var d2 = Deserializer.Deserialize<D>(buf);
            Assert.AreEqual(d.ToString(), d2.ToString());
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
            byte[] buf = Serializer.Serialize(c);

            int tests = 100;
            C c2 = new C();

            void Test()
            {
                for (int i = 0; i < tests; i++)
                {
                    c2 = Deserializer.Deserialize<C>(buf);
                }
            }

            Parallel.Invoke(
                Test, Test, Test, Test, Test, Test, Test, Test, Test
            );
            Assert.AreEqual(c.ToString(), c2.ToString());
        }

        [TestMethod]
        public void MultiThreadNoCodeGenDeserialize()
        {
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
            byte[] buf = Serializer.Serialize(d);

            int tests = 100;
            D d2 = new D();

            void Test()
            {
                for (int i = 0; i < tests; i++)
                {
                    d2 = Deserializer.Deserialize<D>(buf);
                }
            }


            Parallel.Invoke(
                Test, Test, Test, Test, Test, Test, Test, Test, Test
            );

            Assert.AreEqual(d.ToString(), d2.ToString());
        }
    }
}