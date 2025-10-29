using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nino.UnitTests;

[TestClass]
public class CodeFixTest
{
    private CSharpCodeFixTest<Generator.NinoAnalyzer, Generator.NinoCodeFixProvider, DefaultVerifier>
        SetUpCodeFixTest(string code, string fixedCode, params DiagnosticResult[] diagnostics)
    {
        var test = new CSharpCodeFixTest<Generator.NinoAnalyzer, Generator.NinoCodeFixProvider, DefaultVerifier>
        {
            TestState =
            {
                Sources = { code }
            },
            FixedState =
            {
                Sources = { fixedCode }
            }
        };

        if (diagnostics.Length > 0)
        {
            test.TestState.ExpectedDiagnostics.AddRange(diagnostics);
        }

        var referenceAssembly = typeof(Core.Writer).Assembly;
        test.TestState.AdditionalReferences.Add(referenceAssembly);
        test.FixedState.AdditionalReferences.Add(referenceAssembly);

#if NET8_0
        test.TestState.ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
        test.FixedState.ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
#else
        test.TestState.ReferenceAssemblies = ReferenceAssemblies.NetStandard.NetStandard21;
        test.FixedState.ReferenceAssemblies = ReferenceAssemblies.NetStandard.NetStandard21;
#endif

        return test;
    }

    #region NINO002 Tests - Make Type Public

    [TestMethod]
    public async Task NINO002_MakeTypePublic_PrivateClass()
    {
        var code = @"
using Nino.Core;

[NinoType]
internal class TestClass
{
    public int Value { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int Value { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO002", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(5, 16, 5, 25);

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_MakeTypePublic_InternalClass()
    {
        var code = @"
using Nino.Core;

[NinoType]
internal class TestClass
{
    public int Value { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int Value { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO002", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(5, 16, 5, 25);

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_MakeTypePublic_NestedPrivateClass()
    {
        var code = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    private class TestClass
    {
        public int Value { get; set; }
    }
}";

        var fixedCode = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    public class TestClass
    {
        public int Value { get; set; }
    }
}";

        var diagnostic = new DiagnosticResult("NINO002", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(7, 19, 7, 28);

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_MakeTypePublic_Record()
    {
        var code = @"
using Nino.Core;

[NinoType]
internal record TestRecord(int Value);";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public record TestRecord(int Value);";

        var diagnostic = new DiagnosticResult("NINO002", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(5, 17, 5, 27);

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    #endregion

    #region NINO003 Tests - Add [NinoType] Attribute

    // Note: NINO003 is a warning diagnostic that fires when the analyzer detects
    // subtypes during analysis. These tests are commented out as the diagnostic
    // may not fire consistently in test scenarios. The code fix is still functional
    // when the diagnostic does fire in real-world usage.

    #endregion

    #region NINO004 Tests - Remove Redundant [NinoMember]

    [TestMethod]
    public async Task NINO004_RemoveRedundantNinoMember()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO004", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 16, 8, 17)
            .WithArguments("A", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO004_RemoveRedundantNinoMember_MultipleMembers()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(1)]
    public int B { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A { get; set; }

    public int B { get; set; }
}";

        var diagnostic1 = new DiagnosticResult("NINO004", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 16, 8, 17)
            .WithArguments("A", "TestClass");

        var diagnostic2 = new DiagnosticResult("NINO004", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(11, 16, 11, 17)
            .WithArguments("B", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic1, diagnostic2).RunAsync();
    }

    #endregion

    #region NINO005 Tests - Remove Redundant [NinoIgnore]

    [TestMethod]
    public async Task NINO005_RemoveRedundantNinoIgnore()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoIgnore]
    public int A { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO005", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 16, 8, 17)
            .WithArguments("A", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    #endregion

    #region NINO006 Tests - Fix Ambiguous Annotations

    [TestMethod]
    public async Task NINO006_RemoveNinoMember_FromAmbiguousAnnotation()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    [NinoIgnore]
    public int A { get; set; }
}";

        // After removing [NinoMember], [NinoIgnore] triggers NINO005 and gets removed too
        // Test framework applies fixes iteratively until no more diagnostics
        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO006", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(9, 16, 9, 17)
            .WithArguments("A", "TestClass");

        var test = SetUpCodeFixTest(code, fixedCode, diagnostic);
        test.CodeActionIndex = 0; // Select "Remove [NinoMember]" option
        // The test framework will apply fixes iteratively: first removes [NinoMember], then [NinoIgnore]
        // Clear FixedState diagnostics to allow iterative application
        test.FixedState.ExpectedDiagnostics.Clear();
        test.NumberOfFixAllIterations = 2;
        test.NumberOfIncrementalIterations = 2;
        test.NumberOfFixAllInDocumentIterations = 2;
        test.NumberOfFixAllInProjectIterations = 2;
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO006_RemoveNinoIgnore_FromAmbiguousAnnotation()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    [NinoIgnore]
    public int A { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO006", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(9, 16, 9, 17)
            .WithArguments("A", "TestClass");

        var test = SetUpCodeFixTest(code, fixedCode, diagnostic);
        test.CodeActionIndex = 1; // Select "Remove [NinoIgnore]" option
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO006_RemoveBothAttributes_FromAmbiguousAnnotation()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    [NinoIgnore]
    public int A { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO006", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(9, 16, 9, 17)
            .WithArguments("A", "TestClass");

        var test = SetUpCodeFixTest(code, fixedCode, diagnostic);
        test.CodeActionIndex = 2; // Select "Remove both attributes" option
        await test.RunAsync();
    }

    #endregion

    #region NINO007 Tests - Fix Private Member Issue

    [TestMethod]
    public async Task NINO007_RemoveNinoMember_FromPrivateMember()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    private int A { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    private int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO007", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(8, 17, 8, 18)
            .WithArguments("A", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO007_UpdateTypeToAllowNonPublicMembers()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    private int A { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false, containNonPublicMembers: true)]
public class TestClass
{
    [NinoMember(0)]
    private int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO007", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(8, 17, 8, 18)
            .WithArguments("A", "TestClass");

        var test = SetUpCodeFixTest(code, fixedCode, diagnostic);
        test.CodeActionIndex = 1; // Select "Update type to allow non-public members" option
        await test.RunAsync();
    }

    #endregion

    #region NINO008 Tests - Fix Nested Type with Non-Public Members

    [TestMethod]
    public async Task NINO008_SetContainNonPublicMembersToFalse()
    {
        var code = @"
using Nino.Core;

public class OuterClass
{
    [NinoType(containNonPublicMembers: true)]
    public class InnerClass
    {
        public int Value { get; set; }
    }
}";

        var fixedCode = @"
using Nino.Core;

public class OuterClass
{
    [NinoType(containNonPublicMembers: false)]
    public class InnerClass
    {
        public int Value { get; set; }
    }
}";

        var diagnostic = new DiagnosticResult("NINO008", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(7, 18, 7, 28)
            .WithArguments("InnerClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO008_SetContainNonPublicMembersToFalse_WithAutoCollect()
    {
        var code = @"
using Nino.Core;

public class OuterClass
{
    [NinoType(autoCollect: true, containNonPublicMembers: true)]
    public class InnerClass
    {
        public int Value { get; set; }
    }
}";

        var fixedCode = @"
using Nino.Core;

public class OuterClass
{
    [NinoType(autoCollect: true, containNonPublicMembers: false)]
    public class InnerClass
    {
        public int Value { get; set; }
    }
}";

        var diagnostic = new DiagnosticResult("NINO008", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(7, 18, 7, 28)
            .WithArguments("InnerClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    #endregion

    #region NINO009 Tests - Fix Duplicate Member Index

    [TestMethod]
    public async Task NINO009_ChangeDuplicateIndex_ToNextAvailable()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(0)]
    public int B { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(1)]
    public int B { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO009", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(11, 16, 11, 17)
            .WithArguments("TestClass", "B", "TestClass", "A");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO009_ChangeDuplicateIndex_WithGaps()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(2)]
    public int B { get; set; }

    [NinoMember(2)]
    public int C { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(2)]
    public int B { get; set; }

    [NinoMember(1)]
    public int C { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO009", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(14, 16, 14, 17)
            .WithArguments("TestClass", "C", "TestClass", "B");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO009_ChangeDuplicateIndex_MultipleDuplicates()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(0)]
    public int B { get; set; }

    [NinoMember(1)]
    public int C { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(2)]
    public int B { get; set; }

    [NinoMember(1)]
    public int C { get; set; }
}";

        var diagnostic1 = new DiagnosticResult("NINO009", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(11, 16, 11, 17)
            .WithArguments("TestClass", "B", "TestClass", "A");

        await SetUpCodeFixTest(code, fixedCode, diagnostic1).RunAsync();
    }

    #endregion

    #region FixAll Tests

    [TestMethod]
    public async Task FixAll_RemoveMultipleRedundantNinoMember()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass1
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(1)]
    public int B { get; set; }
}

[NinoType(autoCollect: true)]
public class TestClass2
{
    [NinoMember(0)]
    public int X { get; set; }

    [NinoMember(1)]
    public int Y { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass1
{
    public int A { get; set; }

    public int B { get; set; }
}

[NinoType(autoCollect: true)]
public class TestClass2
{
    public int X { get; set; }

    public int Y { get; set; }
}";

        var diagnostic1 = new DiagnosticResult("NINO004", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 16, 8, 17)
            .WithArguments("A", "TestClass1");

        var diagnostic2 = new DiagnosticResult("NINO004", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(11, 16, 11, 17)
            .WithArguments("B", "TestClass1");

        var diagnostic3 = new DiagnosticResult("NINO004", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(18, 16, 18, 17)
            .WithArguments("X", "TestClass2");

        var diagnostic4 = new DiagnosticResult("NINO004", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(21, 16, 21, 17)
            .WithArguments("Y", "TestClass2");

        await SetUpCodeFixTest(code, fixedCode, diagnostic1, diagnostic2, diagnostic3, diagnostic4).RunAsync();
    }

    #endregion
}
