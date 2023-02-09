using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Shared.IO;

namespace Nino.UnitTests
{
    [TestClass]
    public class ExtensibleBufferTest
    {
        [TestMethod]
        public void SpanTest()
        {
            ExtensibleBuffer<byte> vals = new ExtensibleBuffer<byte>();
            for (int i = 0; i < 10; i++)
            {
                vals[i] = (byte)i;
            }
            Assert.IsTrue(vals[0] == 0);
            var span = vals.AsSpan(0, 10);
            span[0] = 120;
            Assert.IsTrue(vals[0] == 120);
        }
    }
}