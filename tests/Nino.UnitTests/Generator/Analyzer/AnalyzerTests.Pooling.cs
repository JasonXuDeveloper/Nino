using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Tests;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// Pooling (NINO018-NINO019).
public partial class AnalyzerTests
{
    [TestMethod]
    public async Task NINO018_InvalidPooledType()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public partial class {|#0:NotSealed|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}

[NinoType, NinoPooled]
public sealed partial class {|#1:Recreated|}
{
    public readonly int Value;

    public Recreated(int value)
    {
        Value = value;
    }

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO018").WithLocation(0).WithArguments("NotSealed"),
            Verify.Diagnostic("NINO018").WithLocation(1).WithArguments("Recreated")).RunAsync();
    }

    [TestMethod]
    public async Task NINO018_PooledStruct()
    {
        var code = @"
using Nino.Core;

[NinoType, {|#1:NinoPooled|}]
public partial struct {|#0:TestStruct|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}
";

        // [NinoPooled] only targets classes, so the compiler rejects it as well.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO018").WithLocation(0).WithArguments("TestStruct"),
            DiagnosticResult.CompilerError("CS0592").WithLocation(1).WithArguments("NinoPooled", "class")).RunAsync();
    }

    [TestMethod]
    public async Task NINO018_AbstractAndRecords()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public abstract partial class {|#0:AbstractPooled|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}

[NinoType, Nino.Core.NinoPooled]
public sealed partial record {|#1:PositionalRecord|}(int Value)
{
    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}

[NinoType, NinoPooledAttribute]
public sealed partial record AssignableRecord
{
    public int Value { get; set; }

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO018").WithLocation(0).WithArguments("AbstractPooled"),
            Verify.Diagnostic("NINO018").WithLocation(1).WithArguments("PositionalRecord")).RunAsync();
    }

    [TestMethod]
    public async Task NINO018_AndNINO019_Together()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public class {|#0:TestClass|}
{
    public int Value;
}
";

        // An invalid pooled type is not reported as non-partial, but its hooks are still required.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO018").WithLocation(0).WithArguments("TestClass"),
            Verify.Diagnostic("NINO019").WithLocation(0)
                .WithArguments("TestClass", "OnNinoPoolWarmup() and OnNinoPoolReturn()")).RunAsync();
    }

    [TestMethod]
    public async Task NINO019_PoolHooksNotImplemented()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class {|#0:NoHooks|}
{
    public int Value;
}

[NinoType, NinoPooled]
public sealed partial class {|#1:OnlyWarmup|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
}

[NinoType, NinoPooled]
public sealed partial class {|#2:OnlyReturn|}
{
    public int Value;

    void OnNinoPoolReturn() => Value = 0;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO019").WithLocation(0)
                .WithArguments("NoHooks", "OnNinoPoolWarmup() and OnNinoPoolReturn()"),
            Verify.Diagnostic("NINO019").WithLocation(1).WithArguments("OnlyWarmup", "OnNinoPoolReturn()"),
            Verify.Diagnostic("NINO019").WithLocation(2).WithArguments("OnlyReturn", "OnNinoPoolWarmup()")).RunAsync();
    }

    [TestMethod]
    public async Task NINO019_PartialDefinitionWithoutBody()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class {|#0:TestClass|}
{
    public int Value;

    static partial void OnNinoPoolWarmup();
    partial void OnNinoPoolReturn();
    partial void OnNinoPoolReturn() { }
}
";

        // A partial method declaration (as the generator emits it) is not an implementation.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO019").WithLocation(0).WithArguments("TestClass", "OnNinoPoolWarmup()")).RunAsync();
    }

    [TestMethod]
    public async Task NINO019_PartialDeclarationsReportOnce()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class {|#0:TestClass|}
{
    public int Value;
}

public sealed partial class TestClass
{
    void OnNinoPoolReturn() { }
}

[NinoType, NinoPooled]
public sealed partial class SplitHooks
{
    public int Value;
}

public sealed partial class SplitHooks
{
    static void OnNinoPoolWarmup() { }
}

public sealed partial class SplitHooks
{
    void OnNinoPoolReturn() { }
}
";

        // Hooks may live in any declaration; the type is reported once, at its first declaration.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO019").WithLocation(0).WithArguments("TestClass", "OnNinoPoolWarmup()")).RunAsync();
    }

    [TestMethod]
    public async Task NINO019_NotReportedWithoutNinoType()
    {
        var code = @"
using Nino.Core;

[NinoPooled]
public sealed class NotANinoType
{
    public int Value;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task ValidPooledType_NoDiagnostics()
    {
        var code = @"
using System.Collections.Generic;
using Nino.Core;

[NinoType, NinoPooled]
public sealed partial class TestClass
{
    public int Value;
    public List<int> Items;

    static void OnNinoPoolWarmup() { }

    void OnNinoPoolReturn()
    {
        Items?.Clear();
    }
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }
}
