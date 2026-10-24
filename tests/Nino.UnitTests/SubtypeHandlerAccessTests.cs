using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Only Nino.Core creates and registers subtype handlers: a handler from elsewhere could claim any type id for any
/// type, and polymorphic reads trust what a handler reads.
/// </summary>
[TestClass]
public class SubtypeHandlerAccessTests
{
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
