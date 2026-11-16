using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// The layout baseline (<c>[assembly: NinoLayouts]</c>): the attribute stays out of the assembly but in the
/// compilation the analyzer reads.
/// </summary>
[TestClass]
public class LayoutBaselineTests
{
    [TestMethod]
    public void TheAnalyzerSeesTheConditionalAttributeAndTheAssemblyDoesNot()
    {
        const string source = @"
using Nino.Core;
[assembly: NinoLayouts(@""['Ns.T']
format = 'Keyed'
"")]
namespace Ns { [NinoType(format: NinoFormat.Keyed)] public sealed class T { public int A; } }";
        var compilation = TestCompilation.Create("Layouts", new[] { source });
        var attribute = compilation.Assembly.GetAttributes()
            .Single(a => a.AttributeClass?.Name == nameof(NinoLayoutsAttribute));
        Assert.AreEqual("['Ns.T']\nformat = 'Keyed'\n",
            ((string)attribute.ConstructorArguments[0].Value)!.Replace("\r\n", "\n"));
        Assert.IsNotNull(attribute.ApplicationSyntaxReference);

        var assembly = TestCompilation.EmitAndLoad(compilation);
        Assert.AreEqual(0, assembly.GetCustomAttributes<NinoLayoutsAttribute>().Count());
        Assert.IsFalse(assembly.GetCustomAttributesData()
            .Any(a => a.AttributeType.Name == nameof(NinoLayoutsAttribute)));
    }
}
