using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.SeamProto;

namespace Nino.UnitTests
{
    /// <summary>
    /// PROTOTYPE (#355, the generator seam): runs of user-type elements through the flat kernels (a chain of block,
    /// bool, UTF-16 and UTF-8 items behind the object's header item) write the bytes of the generated per-element body
    /// and read them back the same, new, reused, with nulls, subtypes and truncated data
    /// (runtimes/csharp/benchmarks/SeamProto: Shapes.cs, FlatHooks.cs and SeamCheck.cs, linked).
    /// </summary>
    [TestClass]
    public class SeamProtoTests
    {
        [TestMethod]
        public void FlatKernelsMatchTheGeneratedBody()
        {
            var failures = SeamCheck.Run();
            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
        }
    }
}
