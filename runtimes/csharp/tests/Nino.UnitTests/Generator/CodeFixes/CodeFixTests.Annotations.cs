using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// Annotations: no effect (NINO028), redundant (NINO010, NINO011), ambiguous (NINO012), the next free index
// (NINO013).
public partial class CodeFixTests
{
    [TestMethod]
    public async Task NINO028_RemoveNinoMemberOfAMemberThatIsNeverSerialized()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)] public int A;
    [NinoMember(1)] public static int {|#0:Shared|};
    [NinoMember(2), NinoUtf8] public string {|#1:Name|} => ""n"";
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)] public int A;
    public static int Shared;
    public string Name => ""n"";
}";

        // Without [NinoMember], the [NinoUtf8] of the member never serialized is NINO030's, whose fix removes it next.
        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO028").WithLocation(0).WithArguments("Shared", "TestClass", "it is static"),
            VerifyCF.Diagnostic("NINO028").WithLocation(1)
                .WithArguments("Name", "TestClass", "it has no setter, and no constructor takes it"));
        test.NumberOfIncrementalIterations = 3;
        test.NumberOfFixAllIterations = 2;
        test.NumberOfFixAllInDocumentIterations = 2;
        test.NumberOfFixAllInProjectIterations = 2;
        await test.RunAsync();
    }



    [TestMethod]
    public async Task NINO010_RemoveRedundantNinoMember()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoMember(0)]
    public int {|#0:A|} { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO010").WithLocation(0)
            .WithArguments("A", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO010_RemoveRedundantNinoMember_MultipleMembers()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoMember(0)]
    public int {|#0:A|} { get; set; }

    [NinoMember(1)]
    public int {|#1:B|} { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A { get; set; }

    public int B { get; set; }
}";

        var diagnostic1 = VerifyCF.Diagnostic("NINO010").WithLocation(0)
            .WithArguments("A", "TestClass");

        var diagnostic2 = VerifyCF.Diagnostic("NINO010").WithLocation(1)
            .WithArguments("B", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic1, diagnostic2).RunAsync();
    }

    [TestMethod]
    public async Task NINO010_FixAll_RemoveRedundantNinoMember()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass1
{
    [NinoMember(0)]
    public int {|#0:A|} { get; set; }

    [NinoMember(1)]
    public int {|#1:B|} { get; set; }
}

[NinoType(autoCollect: true)]
public class TestClass2
{
    [NinoMember(0)]
    public int {|#2:X|} { get; set; }

    [NinoMember(1)]
    public int {|#3:Y|} { get; set; }
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

        var diagnostic1 = VerifyCF.Diagnostic("NINO010").WithLocation(0)
            .WithArguments("A", "TestClass1");

        var diagnostic2 = VerifyCF.Diagnostic("NINO010").WithLocation(1)
            .WithArguments("B", "TestClass1");

        var diagnostic3 = VerifyCF.Diagnostic("NINO010").WithLocation(2)
            .WithArguments("X", "TestClass2");

        var diagnostic4 = VerifyCF.Diagnostic("NINO010").WithLocation(3)
            .WithArguments("Y", "TestClass2");

        await SetUpCodeFixTest(code, fixedCode, diagnostic1, diagnostic2, diagnostic3, diagnostic4).RunAsync();
    }

    [TestMethod]
    public async Task NINO010_RemoveNinoMember_FromAttributeList()
    {
        var code = @"
using System;
using Nino.Core;

[NinoType]
public class TestClass
{
    [Obsolete, NinoMember(0)]
    public int {|#0:A|};
}";

        var fixedCode = @"
using System;
using Nino.Core;

[NinoType]
public class TestClass
{
    [Obsolete]
    public int A;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO010").WithLocation(0).WithArguments("A", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO010_RemoveNinoMember_AttributeSpellings()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoMemberAttribute(0)]
    public int {|#0:A|};

    /// <summary>B</summary>
    [Nino.Core.NinoMember(1)]
    public int {|#1:B|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;

    /// <summary>B</summary>
    public int B;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO010").WithLocation(0).WithArguments("A", "TestClass"),
            VerifyCF.Diagnostic("NINO010").WithLocation(1).WithArguments("B", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO010_RemoveNinoMember_Alias()
    {
        var code = @"
using Nino.Core;
using NM = Nino.Core.NinoMemberAttribute;

[NinoType]
public class TestClass
{
    [NM(0)]
    public int {|#0:A|};
}";

        var fixedCode = @"
using Nino.Core;
using NM = Nino.Core.NinoMemberAttribute;

[NinoType]
public class TestClass
{
    public int A;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO010").WithLocation(0).WithArguments("A", "TestClass")).RunAsync();
    }



    [TestMethod]
    public async Task NINO011_RemoveRedundantNinoIgnore()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoIgnore]
    public int {|#0:A|} { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO011").WithLocation(0)
            .WithArguments("A", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO011_RemoveQualifiedNinoIgnore()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A;

    [Nino.Core.NinoIgnoreAttribute]
    public int {|#0:B|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A;

    public int B;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO011").WithLocation(0).WithArguments("B", "TestClass")).RunAsync();
    }



    [TestMethod]
    public async Task NINO012_RemoveNinoMember_FromAmbiguousAnnotation()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    [NinoIgnore]
    public int {|#0:A|} { get; set; }
}";

        // After removing [NinoMember], [NinoIgnore] triggers NINO011 and gets removed too
        // Test framework applies fixes iteratively until no more diagnostics
        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO012").WithLocation(0)
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
    public async Task NINO012_RemoveNinoIgnore_FromAmbiguousAnnotation()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    [NinoIgnore]
    public int {|#0:A|} { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO012").WithLocation(0)
            .WithArguments("A", "TestClass");

        var test = SetUpCodeFixTest(code, fixedCode, diagnostic);
        test.CodeActionIndex = 1; // Select "Remove [NinoIgnore]" option
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO012_RemoveBothAttributes_FromAmbiguousAnnotation()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    [NinoIgnore]
    public int {|#0:A|} { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO012").WithLocation(0)
            .WithArguments("A", "TestClass");

        var test = SetUpCodeFixTest(code, fixedCode, diagnostic);
        test.CodeActionIndex = 2; // Select "Remove both attributes" option
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO012_RemoveBothAttributes_SingleList()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0), NinoIgnore]
    public int {|#0:A|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    public int A;
}";

        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO012").WithLocation(0).WithArguments("A", "TestClass"));
        test.CodeActionEquivalenceKey = "NINO012RemoveBoth";
        await test.RunAsync();
    }



    [TestMethod]
    public async Task NINO013_ChangeDuplicateIndex_ToNextAvailable()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(0)]
    public int {|#0:B|} { get; set; }
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

        var diagnostic = VerifyCF.Diagnostic("NINO013").WithLocation(0)
            .WithArguments("TestClass", "B", "TestClass", "A");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO013_ChangeDuplicateIndex_WithGaps()
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
    public int {|#0:C|} { get; set; }
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

        var diagnostic = VerifyCF.Diagnostic("NINO013").WithLocation(0)
            .WithArguments("TestClass", "C", "TestClass", "B");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO013_ChangeDuplicateIndex_MultipleDuplicates()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(0)]
    public int {|#0:B|} { get; set; }

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

        var diagnostic1 = VerifyCF.Diagnostic("NINO013").WithLocation(0)
            .WithArguments("TestClass", "B", "TestClass", "A");

        await SetUpCodeFixTest(code, fixedCode, diagnostic1).RunAsync();
    }

    [TestMethod]
    public async Task NINO013_ChangeDuplicateIndex_EachReportedDuplicateTakesItsOwnFreeIndex()
    {
        // The analyzer ranks the duplicates it reports (Diagnostic.Properties): fixed together, each sees the
        // original document and still takes a different free index.
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A { get; set; }

    [NinoMember(0)]
    public int {|#0:B|} { get; set; }

    [NinoMember(1)]
    public int C { get; set; }

    [NinoMember(1)]
    public int {|#1:D|} { get; set; }
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

    [NinoMember(3)]
    public int D { get; set; }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO013").WithLocation(0).WithArguments("TestClass", "B", "TestClass", "A"),
            VerifyCF.Diagnostic("NINO013").WithLocation(1).WithArguments("TestClass", "D", "TestClass", "C")).RunAsync();
    }

    [TestMethod]
    public async Task NINO013_ChangeDuplicateIndex_Property()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(1)]
    public int A { get; set; }

    [NinoMember(1)]
    public int {|#0:B|} { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(1)]
    public int A { get; set; }

    [NinoMember(0)]
    public int B { get; set; }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO013").WithLocation(0).WithArguments("TestClass", "B", "TestClass", "A")).RunAsync();
    }

    [TestMethod]
    public async Task NINO013_FixAll_TwoDuplicateIndices()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A;

    [NinoMember(0)]
    public int {|#0:B|};

    [NinoMember(1)]
    public int C;

    [NinoMember(1)]
    public int {|#1:D|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    public int A;

    [NinoMember(2)]
    public int B;

    [NinoMember(1)]
    public int C;

    [NinoMember(3)]
    public int D;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO013").WithLocation(0).WithArguments("TestClass", "B", "TestClass", "A"),
            VerifyCF.Diagnostic("NINO013").WithLocation(1).WithArguments("TestClass", "D", "TestClass", "C")).RunAsync();
    }
}
