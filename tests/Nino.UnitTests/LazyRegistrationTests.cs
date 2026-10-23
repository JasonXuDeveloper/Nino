using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    [NinoType]
    public class LazyProbe
    {
        public int X;

        [NinoType]
        public class Inner
        {
            public string S;
        }
    }

    [TestClass]
    public class LazyRegistrationTests
    {
        // The registry is keyed by runtime type id: the generator's id of a closed type must equal NinoTypeId.Of,
        // or AOT platforms would fall back to MakeGenericType.
        private static void AssertRegisteredById<T>()
        {
            NinoGen.NinoGenUsages.Register(NinoTypeId.Of(typeof(T)));
            Assert.IsTrue(NinoFormatterCache<T>.IsRegistered, typeof(T).ToString());
        }

        [TestMethod]
        public void UsageIdsMatchRuntimeIds()
        {
            AssertRegisteredById<List<LazyProbe>>();
            AssertRegisteredById<Dictionary<int, LazyProbe[]>>();
            AssertRegisteredById<List<List<LazyProbe.Inner>>>();
            AssertRegisteredById<(int, LazyProbe)>();
            AssertRegisteredById<LazyProbe[][]>();
            AssertRegisteredById<KeyValuePair<string, LazyProbe>>();
        }

        [TestMethod]
        public void TypesRegisterOnFirstUse()
        {
            // Never used before this test: registered by the lookup, not at startup.
            var value = new LazyProbe.Inner { S = "s" };
            Assert.AreEqual("s", NinoDeserializer.Deserialize<LazyProbe.Inner>(NinoSerializer.Serialize(value)).S);
            Assert.AreEqual("s", ((LazyProbe.Inner)NinoDeserializer.Deserialize<object>(NinoSerializer.Serialize<object>(value))).S);
        }

        // Top-level calls the generator sees, so their closed formatters are part of the usage registry.
        private static void Usages()
        {
            NinoSerializer.Serialize(new List<LazyProbe>());
            NinoSerializer.Serialize(new Dictionary<int, LazyProbe[]>());
            NinoSerializer.Serialize(new List<List<LazyProbe.Inner>>());
            NinoSerializer.Serialize((1, new LazyProbe()));
            NinoSerializer.Serialize(new LazyProbe[0][]);
            NinoSerializer.Serialize(new KeyValuePair<string, LazyProbe>());
        }
    }
}
