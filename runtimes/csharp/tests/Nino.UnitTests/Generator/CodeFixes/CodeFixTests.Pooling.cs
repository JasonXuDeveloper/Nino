using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// Pooling: make sealed (NINO018), implement the pool hooks (NINO019).
public partial class CodeFixTests
{
    [TestMethod]
    public async Task NINO018_MakeClassSealed()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public partial class {|#0:TestClass|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class TestClass
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO018").WithLocation(0).WithArguments("TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO018_MakeRecordSealed()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public partial record {|#0:TestRecord|}
{
    public int Value { get; set; }

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial record TestRecord
{
    public int Value { get; set; }

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO018").WithLocation(0).WithArguments("TestRecord")).RunAsync();
    }

    [TestMethod]
    public async Task NINO018_NoFixForAbstractClass()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public abstract partial class {|#0:TestClass|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}";

        await SetUpCodeFixTest(code, code,
            VerifyCF.Diagnostic("NINO018").WithLocation(0).WithArguments("TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO018_NoFixForSealedRecreatedClass()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class {|#0:TestClass|}
{
    public readonly int Value;

    public TestClass(int value)
    {
        Value = value;
    }

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}";

        await SetUpCodeFixTest(code, code,
            VerifyCF.Diagnostic("NINO018").WithLocation(0).WithArguments("TestClass")).RunAsync();
    }



    [TestMethod]
    public async Task NINO019_ImplementPoolHooks()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class {|#0:TestClass|}
{
    public int Value;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class TestClass
{
    public int Value;
    private static partial System.Collections.Generic.IEnumerable<TestClass> {|#0:OnNinoPoolWarmup|}()
    {
        return System.Array.Empty<TestClass>();
    }
    private partial void {|#1:OnNinoPoolReturn|}()
    {
    }
}";

        // Without the generator the partial declarations are missing, so the compiler reports CS0759.
        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO019").WithLocation(0)
                .WithArguments("TestClass", "OnNinoPoolWarmup() and OnNinoPoolReturn()"));
        test.FixedState.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("CS0759").WithLocation(0).WithArguments("TestClass.OnNinoPoolWarmup()"));
        test.FixedState.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("CS0759").WithLocation(1).WithArguments("TestClass.OnNinoPoolReturn()"));
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO019_ImplementPoolHooks_OnlyMissingOne()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class {|#0:TestClass|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class TestClass
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    private partial void {|#0:OnNinoPoolReturn|}()
    {
    }
}";

        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO019").WithLocation(0).WithArguments("TestClass", "OnNinoPoolReturn()"));
        test.FixedState.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("CS0759").WithLocation(0).WithArguments("TestClass.OnNinoPoolReturn()"));
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO019_ImplementPoolHooks_KeepsTheFilesLineBreaks()
    {
        // What the fix inserts takes the file's line breaks (CRLF here, LF elsewhere), on any machine.
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class {|#0:TestClass|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
}".Replace("\r\n", "\n").Replace("\n", "\r\n");

        var fixedCode = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class TestClass
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    private partial void {|#0:OnNinoPoolReturn|}()
    {
    }
}".Replace("\r\n", "\n").Replace("\n", "\r\n");

        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO019").WithLocation(0).WithArguments("TestClass", "OnNinoPoolReturn()"));
        test.FixedState.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("CS0759").WithLocation(0).WithArguments("TestClass.OnNinoPoolReturn()"));
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO019_ImplementPoolHooks_AfterPartialDeclaration()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class {|#0:TestClass|}
{
    public int Value;

    private static partial System.Collections.Generic.IEnumerable<TestClass> OnNinoPoolWarmup();
    private partial void OnNinoPoolReturn();
}";

        var fixedCode = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class TestClass
{
    public int Value;

    private static partial System.Collections.Generic.IEnumerable<TestClass> OnNinoPoolWarmup();
    private partial void OnNinoPoolReturn();
    private static partial System.Collections.Generic.IEnumerable<TestClass> OnNinoPoolWarmup()
    {
        return System.Array.Empty<TestClass>();
    }
    private partial void OnNinoPoolReturn()
    {
    }
}";

        // The declarations as the generator emits them: until implemented, the compiler reports them too.
        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO019").WithLocation(0)
                .WithArguments("TestClass", "OnNinoPoolWarmup() and OnNinoPoolReturn()"),
            DiagnosticResult.CompilerError("CS8795").WithSpan(9, 78, 9, 94).WithArguments("TestClass.OnNinoPoolWarmup()"),
            DiagnosticResult.CompilerError("CS8795").WithSpan(10, 26, 10, 42).WithArguments("TestClass.OnNinoPoolReturn()"));
        // Implemented: nothing is left to report (the test state's compiler errors are not inherited).
        test.FixedState.InheritanceMode = StateInheritanceMode.Explicit;
        await test.RunAsync();
    }
}
