using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using S = Nino.Core.NinoSerializer;
using static Nino.Core.NinoDeserializer;

namespace Nino.UnitTests;

/// <summary>
/// Top-level calls whose receiver is a <c>using</c> alias, or that have none (<c>using static</c>): the generator
/// binds them like any other call, so they are intercepted where interceptors are enabled (this project's own build)
/// and the generated interceptors compile and round-trip.
/// </summary>
[TestClass]
public class ReceiverAliasTests
{
    [NinoType]
    public sealed class Aliased
    {
        public int Id;
        public string Name;
    }

    [TestMethod]
    public void AliasedAndStaticCallsRoundTrip()
    {
        var value = new Aliased { Id = 3, Name = "three" };
        byte[] bytes = S.Serialize(value);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(value), bytes);
        var read = Deserialize<Aliased>(bytes);
        Assert.AreEqual(3, read.Id);
        Assert.AreEqual("three", read.Name);

        var list = new List<int> { 1, 2, 3 };
        CollectionAssert.AreEqual(list, Deserialize<List<int>>(S.Serialize(list)));
        Assert.IsNull(Deserialize<Aliased>(S.Serialize<Aliased>(null)));
    }
}
