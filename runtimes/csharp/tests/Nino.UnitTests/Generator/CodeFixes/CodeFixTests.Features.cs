using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// Features: trackReferences (NINO015), attributes without effect (NINO024, NINO025).
public partial class CodeFixTests
{
    [TestMethod]
    public async Task NINO015_RemoveTrackReferencesFromStruct()
    {
        var code = @"
using Nino.Core;

[NinoType(trackReferences: true)]
public struct {|#0:TestStruct|}
{
    public int Value;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public struct TestStruct
{
    public int Value;
}";

        var diagnostic = VerifyCF.Diagnostic("NINO015").WithLocation(0)
            .WithArguments("TestStruct");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO015_RemoveTrackReferences_Positional()
    {
        var code = @"
using Nino.Core;

[NinoType(true, false, true, NinoFormat.Positional, true)]
public struct {|#0:TestStruct|}
{
    public int Value;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(true, false, true, NinoFormat.Positional)]
public struct TestStruct
{
    public int Value;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO015").WithLocation(0).WithArguments("TestStruct")).RunAsync();
    }

    [TestMethod]
    public async Task NINO015_RemoveTrackReferences_KeepsOtherArguments()
    {
        var code = @"
using Nino.Core;

[NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)]
public struct {|#0:TestStruct|}
{
    public int Value;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(format: NinoFormat.VersionTolerant)]
public struct TestStruct
{
    public int Value;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO015").WithLocation(0).WithArguments("TestStruct")).RunAsync();
    }

    [TestMethod]
    public async Task NINO024_RemoveFormerNamesOfAMemberOfAPositionalType()
    {
        // Every former name of the member goes; other attributes stay.
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFormerName(""Old""), NinoFormerName(""Older"")]
    public int {|#0:A|};

    [NinoFormerName(""Was"")]
    [System.Obsolete]
    public int {|#1:B|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;

    [System.Obsolete]
    public int B;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO024").WithLocation(0).WithArguments("A", "TestClass"),
            VerifyCF.Diagnostic("NINO024").WithLocation(1).WithArguments("B", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO025_RemoveTheFactoryAConstructorOverrides()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;

    [NinoFactory]
    public static TestClass {|#0:Rent|}() => new TestClass();

    [NinoConstructor]
    public static TestClass Create(int a) => new TestClass { A = a };
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;

    public static TestClass Rent() => new TestClass();

    [NinoConstructor]
    public static TestClass Create(int a) => new TestClass { A = a };
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO025").WithLocation(0)
                .WithArguments("Rent", "TestClass", "TestClass.Create(int)")).RunAsync();
    }
}
