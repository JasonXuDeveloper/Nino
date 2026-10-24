using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;

namespace Nino.UnitTests
{
    // Types are registered once; each base type's registry adopts a subtype on first lookup.

    [NinoType]
    public interface IRegistryShape
    {
    }

    [NinoType]
    public class RegistryShape : IRegistryShape
    {
        public int Sides;
    }

    [NinoType]
    public class RegistryPolygon : RegistryShape
    {
        public string Name;
    }

    [NinoType]
    public sealed class RegistryStar : RegistryPolygon
    {
        public float Sharpness;
    }

    [NinoType]
    public class RegistryUnrelated
    {
        public int X;
    }

    [TestClass]
    public class PolymorphicRegistryTests
    {
        [TestMethod]
        public void SubtypeIsFoundThroughEveryBase()
        {
            var star = new RegistryStar { Sides = 5, Name = "star", Sharpness = 0.5f };

            var viaInterface = NinoDeserializer.Deserialize<IRegistryShape>(NinoSerializer.Serialize<IRegistryShape>(star));
            Assert.AreEqual(0.5f, ((RegistryStar)viaInterface).Sharpness);

            var viaRoot = NinoDeserializer.Deserialize<RegistryShape>(NinoSerializer.Serialize<RegistryShape>(star));
            Assert.AreEqual(5, viaRoot.Sides);
            Assert.AreEqual("star", ((RegistryStar)viaRoot).Name);

            var viaMiddle = NinoDeserializer.Deserialize<RegistryPolygon>(NinoSerializer.Serialize<RegistryPolygon>(star));
            Assert.IsInstanceOfType(viaMiddle, typeof(RegistryStar));

            var viaObject = NinoDeserializer.Deserialize<object>(NinoSerializer.Serialize<object>(star));
            Assert.IsInstanceOfType(viaObject, typeof(RegistryStar));
        }

        [TestMethod]
        public void IdOfUnrelatedTypeIsRejected()
        {
            // Same layout as a RegistryShape body (type id, then an int), but the id belongs to an unrelated type.
            var bytes = NinoSerializer.Serialize<object>(new RegistryUnrelated { X = 1 });
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<RegistryShape>(bytes));
        }

        [TestMethod]
        public void ExistingInstanceOfAnotherSubtypeIsReplaced()
        {
            RegistryShape target = new RegistryPolygon { Name = "old" };
            var bytes = NinoSerializer.Serialize<RegistryShape>(new RegistryStar { Name = "new", Sharpness = 1 });
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.IsInstanceOfType(target, typeof(RegistryStar));
            Assert.AreEqual("new", ((RegistryStar)target).Name);

            var same = (RegistryStar)target;
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreSame(same, target);
        }

        [TestMethod]
        public void ConcurrentLookupsAgree()
        {
            var values = new List<IRegistryShape>
            {
                new RegistryShape { Sides = 3 },
                new RegistryPolygon { Sides = 4, Name = "p" },
                new RegistryStar { Sides = 5, Name = "s" },
            };
            var bytes = NinoSerializer.Serialize(values);
            using var start = new ManualResetEventSlim();
            var tasks = new Task[8];
            for (int t = 0; t < tasks.Length; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    start.Wait();
                    for (int i = 0; i < 1000; i++)
                    {
                        var result = NinoDeserializer.Deserialize<List<IRegistryShape>>(bytes);
                        Assert.AreEqual(5, ((RegistryStar)result[2]).Sides);
                        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(result));
                    }
                });
            }

            start.Set();
            Task.WaitAll(tasks);
        }

        // Only Nino.Core creates and registers subtype handlers: a handler from elsewhere could claim any type id for
        // any type, and polymorphic reads trust what a handler reads.
        [TestMethod]
        public void HandlersCannotBeMadeOrRegisteredOutsideNinoCore()
        {
            var constructors = typeof(NinoSubtypeHandler).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsTrue(constructors.Length > 0);
            foreach (var constructor in constructors)
                Assert.IsTrue(constructor.IsFamilyAndAssembly || constructor.IsAssembly || constructor.IsPrivate,
                    "a subclass outside Nino.Core could call " + constructor);

            var register = typeof(NinoPolymorphic<object>).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Register" && m.GetParameters().Any(p => p.ParameterType == typeof(NinoSubtypeHandler)));
            Assert.IsFalse(register.Any(), "Register(NinoSubtypeHandler) is public");
        }
    }
}
