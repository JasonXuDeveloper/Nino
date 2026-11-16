using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// [NinoUtf8] (NINO030, NINO031).
public partial class CodeFixTests
{
    [TestMethod]
    public async Task NINO030_RemoveNinoUtf8()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [{|#0:NinoUtf8|}]
    public int A;

    public string B;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;

    public string B;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO030").WithLocation(0)
                .WithArguments("A", "TestClass", "its type 'int' holds no string")).RunAsync();
    }

    [TestMethod]
    public async Task NINO031_UseNinoUtf8()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [{|#0:NinoMemberFormatter(typeof(Utf8StringFormatter))|}]
    public string A;

    [System.ComponentModel.Description(""b""), {|#1:Nino.Core.NinoMemberFormatterAttribute(typeof(Utf8StringFormatter))|}] public string B;

    [NinoUtf8]
    [{|#2:NinoMemberFormatter(typeof(Utf8StringFormatter))|}]
    public string C;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoUtf8]
    public string A;

    [System.ComponentModel.Description(""b""), Nino.Core.NinoUtf8] public string B;

    [NinoUtf8]
    public string C;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO031").WithLocation(0).WithArguments("A", "TestClass"),
            VerifyCF.Diagnostic("NINO031").WithLocation(1).WithArguments("B", "TestClass"),
            VerifyCF.Diagnostic("NINO031").WithLocation(2).WithArguments("C", "TestClass")).RunAsync();
    }
}
