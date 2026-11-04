using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nino.AspNetCore.Tests
{
    /// <summary>
    /// The formatters' type cache keeps no type from a collectible assembly, so an AssemblyLoadContext whose types
    /// reached MVC can still unload.
    /// </summary>
    [TestClass]
    public class CacheTests
    {
        [TestMethod]
        public void CollectibleTypesAreNotCached()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("nino-collectible"),
                AssemblyBuilderAccess.RunAndCollect);
            var type = assembly.DefineDynamicModule("m").DefineType("Collectible", TypeAttributes.Public).CreateType();

            foreach (var t in new[] { type, type.MakeArrayType(), typeof(List<>).MakeGenericType(type) })
            {
                Assert.IsTrue(NinoSupportedTypes.MentionsCollectible(t), t.ToString());
                NinoSupportedTypes.IsSupported(t);
                Assert.IsFalse(NinoSupportedTypes.IsCached(t), t.ToString());
            }

            Assert.IsTrue(NinoSupportedTypes.IsSupported(typeof(int[])));
            Assert.IsTrue(NinoSupportedTypes.IsCached(typeof(int[])));
        }
    }
}
