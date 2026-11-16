using System;
using System.Reflection;
using Nino.Core;
using NUnit.Framework;

namespace Test.Runtime
{
    // Generic [NinoType]s whose closed types no code names: RuntimeGenericTests makes them with MakeGenericType only,
    // so the generator registers none of them and Nino's resolver closes their formatter definitions at run time.
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

    /// <summary>
    /// A closed generic <c>[NinoType]</c> that exists only as a <c>Type</c> made at run time, written and read through
    /// the boxed API (<c>SerializeBoxed</c> / <c>DeserializeBoxed</c>): Nino.Core's resolver closes the generated
    /// formatter definition over it with <c>MakeGenericType</c> and creates it with <c>Activator</c>. Mono (play mode)
    /// compiles it. The IL2CPP player has no compiled code for these instantiations: it relies on Unity's full generic
    /// sharing (always on since 2022.1; the package requires 2022.3), which runs the fully shared code of
    /// the formatter instead. If IL2CPP stops providing that, these tests fail in the player only (unity.yml) and
    /// Nino.Core's NinoFormatterResolver comment and the docs that say it works must change with them.
    /// </summary>
    public class RuntimeGenericTests
    {
        private static readonly RuntimeOnlyPoint Point = new RuntimeOnlyPoint { X = 3, Y = -4 };

        [Test]
        public void ClassOverReferenceType() => RoundTrip(typeof(RuntimeOnlyBox<>), typeof(string), "Value", "text");

        [Test]
        public void ClassOverInt() => RoundTrip(typeof(RuntimeOnlyBox<>), typeof(int), "Value", 42);

        [Test]
        public void ClassOverStruct() => RoundTrip(typeof(RuntimeOnlyBox<>), typeof(RuntimeOnlyPoint), "Value", Point);

        [Test]
        public void StructOverInt() => RoundTrip(typeof(RuntimeOnlyPair<>), typeof(int), "First", 42);

        [Test]
        public void StructOverStruct() =>
            RoundTrip(typeof(RuntimeOnlyPair<>), typeof(RuntimeOnlyPoint), "First", Point);

        private static void RoundTrip(Type definition, Type argument, string field, object value)
        {
            var type = definition.MakeGenericType(argument);
            FieldInfo member = type.GetField(field);
            Assert.IsNotNull(member, $"{type}.{field}");
            // Boxed: setting the field of a boxed struct sets it in the box.
            var instance = Activator.CreateInstance(type);
            member.SetValue(instance, value);

            var bytes = NinoSerializer.SerializeBoxed(instance, type);
            var back = NinoDeserializer.DeserializeBoxed(bytes, type);
            Assert.IsNotNull(back, type.ToString());
            Assert.AreEqual(type, back.GetType());
            Assert.AreEqual(value, member.GetValue(back), type.ToString());
            // The same bytes again: the formatter built at run time is stable.
            Assert.AreEqual(bytes, NinoSerializer.SerializeBoxed(back, type), type.ToString());
        }
    }
}
