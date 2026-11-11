using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    [NinoType(format: NinoFormat.VersionTolerant)]
    public class LazyProbe
    {
        public int X;

        [NinoType(format: NinoFormat.VersionTolerant)]
        public class Inner
        {
            public string S;
        }
    }

    // Generic [NinoType]s whose closed types no code in this project names: only RuntimeTypeGenericsRoundTrip makes
    // them, with MakeGenericType, so no generated registration covers them.
    [NinoType]
    public class RuntimeOnlyBox<T>
    {
        public T Value;
        public string Label;
    }

    [NinoType]
    public struct RuntimeOnlyPair<T>
    {
        public T First;
        public int Second;
    }

    [NinoType]
    public struct RuntimeOnlyPoint
    {
        public int X;
        public long Y;
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

        [TestMethod]
        public void RuntimeTypeGenericsRoundTrip()
        {
            // A closed generic [NinoType] only made at run time: the resolver closes its generated formatter
            // definition with MakeGenericType on a JIT (Native AOT throws NinoThrow.NoGeneratedFormatter instead; the
            // IL2CPP player runs Unity's fully shared code: Test.Runtime.RuntimeGenericTests in unity.yml).
            var point = new RuntimeOnlyPoint { X = 3, Y = -4 };
            RoundTrip(typeof(RuntimeOnlyBox<>), typeof(string), "Value", "text", "box of string");
            RoundTrip(typeof(RuntimeOnlyBox<>), typeof(int), "Value", 42, "box of int");
            RoundTrip(typeof(RuntimeOnlyBox<>), typeof(RuntimeOnlyPoint), "Value", point, "box of a struct");
            RoundTrip(typeof(RuntimeOnlyPair<>), typeof(int), "First", 42, "pair of int");
            RoundTrip(typeof(RuntimeOnlyPair<>), typeof(RuntimeOnlyPoint), "First", point, "pair of a struct");
        }

        private static void RoundTrip(Type definition, Type argument, string field, object value, string what)
        {
            var type = definition.MakeGenericType(argument);
            // No generated registration names the closed type: the usage registry leaves it unregistered.
            NinoGen.NinoGenUsages.Register(NinoTypeId.Of(type));
            var registered = typeof(NinoFormatterCache<>).MakeGenericType(type)
                .GetProperty(nameof(NinoFormatterCache<int>.IsRegistered))!;
            Assert.IsFalse((bool)registered.GetValue(null)!, what);

            var instance = Activator.CreateInstance(type)!;
            type.GetField(field)!.SetValue(instance, value);
            var back = NinoDeserializer.DeserializeBoxed(NinoSerializer.SerializeBoxed(instance, type), type);
            Assert.AreEqual(type, back.GetType(), what);
            Assert.AreEqual(value, type.GetField(field)!.GetValue(back), what);
            Assert.IsTrue((bool)registered.GetValue(null)!, what);
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
