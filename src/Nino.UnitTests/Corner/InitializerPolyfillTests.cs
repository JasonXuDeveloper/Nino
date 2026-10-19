using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>
    /// The registry's initializer attributes are used only where the compilation can access them. Libraries carry
    /// internal polyfills of such attributes (ZLinq's netstandard build has ModuleInitializerAttribute, which Unity's
    /// .NET Standard profile lacks): they exist, but naming one was error CS0122 in the generated code.
    /// </summary>
    [TestClass]
    public class InitializerPolyfillTests
    {
        private const string Unity = @"
namespace UnityEngine
{{
    {0} enum RuntimeInitializeLoadType {{ SubsystemRegistration }}
    [System.AttributeUsage(System.AttributeTargets.Method)]
    {0} sealed class RuntimeInitializeOnLoadMethodAttribute : System.Attribute
    {{
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) {{ }}
    }}
}}";

        private const string Game = @"
namespace Game { [Nino.Core.NinoType] public class Item { public int Id; } }";

        private static CornerCompiler.Result CompileWith(string access)
        {
            var library = CornerCompiler.Compile(string.Format(Unity, access), load: false);
            Assert.AreEqual(0, library.Errors.Length, library.ErrorText);
            var game = CornerCompiler.CompileAgainst(Game, library);
            Assert.AreEqual(0, game.Errors.Length, game.ErrorText);
            return game;
        }

        [TestMethod]
        public void InternalPolyfillIsNotUsed() =>
            StringAssert.DoesNotMatch(CompileWith("internal").GeneratedCode,
                new System.Text.RegularExpressions.Regex("RuntimeInitializeOnLoadMethod"));

        [TestMethod]
        public void PublicAttributeIsUsed() =>
            StringAssert.Contains(CompileWith("public").GeneratedCode,
                "[global::UnityEngine.RuntimeInitializeOnLoadMethod(");
    }
}
