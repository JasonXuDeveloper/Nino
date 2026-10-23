using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.TypeScript.Tests
{
    /// <summary>
    /// What <see cref="LayoutReader"/> takes from Nino.Core without referencing it (it reads assemblies through a
    /// MetadataLoadContext): the attribute's name, the order and types of its constructor's arguments and the values of
    /// <see cref="NinoFormat"/> and <see cref="NinoHeader"/>. A change to any of them in Nino.Core fails here, not in
    /// the TypeScript a user generates.
    /// </summary>
    [TestClass]
    public class LayoutContractTests
    {
        [TestMethod]
        public void AttributeNameIsNinoCores() =>
            Assert.AreEqual(LayoutReader.LayoutAttribute, typeof(NinoTypeLayoutAttribute).FullName);

        [TestMethod]
        public void ConstructorArgumentsAreReadInOrder()
        {
            // LayoutReader.Read takes ConstructorArguments by position, with these types.
            var expected = new[]
            {
                (typeof(Type), "type"), (typeof(NinoFormat), "format"), (typeof(NinoHeader), "header"),
                (typeof(bool), "trackReferences"), (typeof(bool), "raw"), (typeof(int), "typeId"),
                (typeof(string), "members"),
            };
            var constructors = typeof(NinoTypeLayoutAttribute).GetConstructors();
            Assert.AreEqual(1, constructors.Length, "LayoutReader reads the arguments of one constructor");
            var parameters = constructors[0].GetParameters().Select(p => (p.ParameterType, p.Name)).ToArray();
            CollectionAssert.AreEqual(expected, parameters);
        }

        [TestMethod]
        public void DeferrableIsASettableBool()
        {
            // LayoutReader.Read takes it from the attribute's named arguments, by name.
            var property = typeof(NinoTypeLayoutAttribute).GetProperty(LayoutReader.DeferrableProperty);
            Assert.IsNotNull(property);
            Assert.AreEqual(typeof(bool), property.PropertyType);
            Assert.IsNotNull(property.GetSetMethod());
        }

        [TestMethod]
        public void FormatValuesMatch() =>
            // Every value, by name: a new NinoFormat needs TypeLayout and the emitter to handle it.
            CollectionAssert.AreEquivalent(
                new[]
                {
                    ("Positional", TypeLayout.Positional), ("VersionTolerant", TypeLayout.VersionTolerant),
                    ("Keyed", TypeLayout.Keyed),
                },
                Enum.GetValues<NinoFormat>().Select(v => (v.ToString(), (int)v)).ToArray());

        [TestMethod]
        public void HeaderValuesMatch() =>
            CollectionAssert.AreEquivalent(
                new[]
                {
                    ("None", TypeLayout.NoHeader), ("NullFlag", TypeLayout.NullFlag), ("TypeId", TypeLayout.TypeId),
                },
                Enum.GetValues<NinoHeader>().Select(v => (v.ToString(), (int)v)).ToArray());
    }
}
