using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

[TestClass]
public class CodeFixTest
{
    private CSharpCodeFixTest<Generator.Analysis.NinoAnalyzer, Generator.NinoCodeFixProvider, DefaultVerifier>
        SetUpCodeFixTest(string code, string fixedCode, params DiagnosticResult[] diagnostics)
    {
        var test = new CSharpCodeFixTest<Generator.Analysis.NinoAnalyzer, Generator.NinoCodeFixProvider, DefaultVerifier>
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
            // No fix offered: the diagnostics stay, and nothing is expected to change.
            if (code == fixedCode)
                test.FixedState.ExpectedDiagnostics.AddRange(diagnostics);
        }

        TestReferences.Apply(test.TestState);
        TestReferences.Apply(test.FixedState);

        return test;
    }

    #region NINO001 - Make type public

    [TestMethod]
    public async Task NINO001_MakeTypePublic_PrivateProtectedClass()
    {
        var code = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    private protected class TestClass
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

        var diagnostic = new DiagnosticResult("NINO001", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(7, 29, 7, 38);

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO001_MakeTypePublic_ContainingTypes()
    {
        var code = @"
using Nino.Core;

internal class Outer
{
    class Middle
    {
        [NinoType]
        class TestClass
        {
            public int Value { get; set; }
        }
    }
}";

        var fixedCode = @"
using Nino.Core;

internal class Outer
{
    public class Middle
    {
        [NinoType]
        public class TestClass
        {
            public int Value { get; set; }
        }
    }
}";

        var diagnostic = new DiagnosticResult("NINO001", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(9, 15, 9, 24);

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO001_MakeTypePublic_NestedPrivateClass()
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

        var diagnostic = new DiagnosticResult("NINO001", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(7, 19, 7, 28);

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO001_MakeTypePublic_Record()
    {
        var code = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    private record TestRecord(int Value);
}";

        var fixedCode = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    public record TestRecord(int Value);
}";

        var diagnostic = new DiagnosticResult("NINO001", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(7, 20, 7, 30);

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO001_MakeTypePublic_ProtectedStruct()
    {
        var code = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    protected struct {|#0:TestStruct|}
    {
        public int Value;
    }
}";

        var fixedCode = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    public struct TestStruct
    {
        public int Value;
    }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO001").WithLocation(0).WithArguments("OuterClass.TestStruct")).RunAsync();
    }

    [TestMethod]
    public async Task NINO001_NoFixForStaticClass()
    {
        var code = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    private static class {|#0:TestClass|}
    {
    }
}";

        await SetUpCodeFixTest(code, code,
            VerifyCF.Diagnostic("NINO001").WithLocation(0).WithArguments("OuterClass.TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO001_FixAll_SharedContainer()
    {
        var code = @"
using Nino.Core;

public class Outer
{
    private class Middle
    {
        [NinoType]
        private class {|#0:A|}
        {
            public int Value;
        }

        [NinoType]
        private class {|#1:B|}
        {
            public int Value;
        }
    }
}";

        var fixedCode = @"
using Nino.Core;

public class Outer
{
    public class Middle
    {
        [NinoType]
        public class A
        {
            public int Value;
        }

        [NinoType]
        public class B
        {
            public int Value;
        }
    }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO001").WithLocation(0).WithArguments("Outer.Middle.A"),
            VerifyCF.Diagnostic("NINO001").WithLocation(1).WithArguments("Outer.Middle.B")).RunAsync();
    }

    #endregion

    #region NINO002 - Make type partial

    [TestMethod]
    public async Task NINO002_MakeTypePartial_ContainingTypes()
    {
        var code = @"
using Nino.Core;

public class Outer
{
    [NinoType(containNonPublicMembers: true)]
    public class TestClass
    {
        private int _value;
        public int Value => _value;
    }
}";

        var fixedCode = @"
using Nino.Core;

public partial class Outer
{
    [NinoType(containNonPublicMembers: true)]
    public partial class TestClass
    {
        private int _value;
        public int Value => _value;
    }
}";

        var diagnostic = new DiagnosticResult("NINO002", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(7, 18, 7, 27)
            .WithArguments("Outer.TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_MakeTypePartial_TopLevel()
    {
        var code = @"
using Nino.Core;

[NinoType(containNonPublicMembers: true)]
public class {|#0:TestClass|}
{
    private int _value;
    public int Value => _value;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(containNonPublicMembers: true)]
public partial class TestClass
{
    private int _value;
    public int Value => _value;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO002").WithLocation(0).WithArguments("TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_MakeTypePartial_PooledSealedClass()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed class {|#0:TestClass|}
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
            VerifyCF.Diagnostic("NINO002").WithLocation(0).WithArguments("TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_FixAll_SharedContainer()
    {
        var code = @"
using Nino.Core;

public class Outer
{
    [NinoType(containNonPublicMembers: true)]
    public class {|#0:A|}
    {
        private int _value;
        public int Value => _value;
    }

    [NinoType(containNonPublicMembers: true)]
    public class {|#1:B|}
    {
        private int _value;
        public int Value => _value;
    }
}";

        var fixedCode = @"
using Nino.Core;

public partial class Outer
{
    [NinoType(containNonPublicMembers: true)]
    public partial class A
    {
        private int _value;
        public int Value => _value;
    }

    [NinoType(containNonPublicMembers: true)]
    public partial class B
    {
        private int _value;
        public int Value => _value;
    }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO002").WithLocation(0).WithArguments("Outer.A"),
            VerifyCF.Diagnostic("NINO002").WithLocation(1).WithArguments("Outer.B")).RunAsync();
    }

    #endregion

    #region NINO003 - Add [NinoType]

    [TestMethod]
    public async Task NINO003_AddNinoType()
    {
        var code = @"
using Nino.Core;

[NinoType(allowInheritance: false)]
public class Base
{
    public int A;
}

public class {|#0:Derived|} : Base
{
    public int B;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(allowInheritance: false)]
public class Base
{
    public int A;
}

[NinoType]
public class Derived : Base
{
    public int B;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO003").WithLocation(0).WithArguments("Derived", "Base")).RunAsync();
    }

    [TestMethod]
    public async Task NINO003_AddNinoType_AddsUsing()
    {
        var code = @"
[Nino.Core.NinoType(allowInheritance: false)]
public interface IBase
{
}

public class {|#0:Implementation|} : IBase
{
    public int A;
}";

        var fixedCode = @"
using Nino.Core;

[Nino.Core.NinoType(allowInheritance: false)]
public interface IBase
{
}

[NinoType]
public class Implementation : IBase
{
    public int A;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO003").WithLocation(0).WithArguments("Implementation", "IBase")).RunAsync();
    }

    #endregion

    #region NINO006 - Add [NinoIgnore] to a read-only member

    [TestMethod]
    public async Task NINO006_AddNinoIgnore()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public readonly int {|#0:A|};
    public int B;

    public TestClass(int b)
    {
        B = b;
    }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoIgnore]
    public readonly int A;
    public int B;

    public TestClass(int b)
    {
        B = b;
    }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO006").WithLocation(0).WithArguments("A", "TestClass")).RunAsync();
    }

    #endregion

    #region NINO007 - Add [NinoIgnore] to an unserializable member

    [TestMethod]
    public async Task NINO007_AddNinoIgnore()
    {
        var code = @"
using System;
using Nino.Core;

[NinoType]
public class TestClass
{
    public Action Callback;
    public int Value;
}";

        var fixedCode = @"
using System;
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoIgnore]
    public Action Callback;
    public int Value;
}";

        var diagnostic = new DiagnosticResult("NINO007", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 19, 8, 27)
            .WithArguments("Callback", "System.Action", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO007_AddNinoIgnore_PropertyAddsUsing()
    {
        var code = @"
using System;

[Nino.Core.NinoType]
public class TestClass
{
    public Func<int> {|#0:Factory|} { get; set; }
    public int Value;
}";

        var fixedCode = @"
using System;
using Nino.Core;

[Nino.Core.NinoType]
public class TestClass
{
    [NinoIgnore]
    public Func<int> Factory { get; set; }
    public int Value;
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO007").WithLocation(0).WithArguments("Factory", "System.Func<int>", "TestClass"))
            .RunAsync();
    }

    [TestMethod]
    public async Task NINO007_AddNinoIgnore_ExistingAttributes()
    {
        var code = @"
using System;
using Nino.Core;

namespace Sample
{
    [NinoType]
    public class TestClass
    {
        [Obsolete]
        public Action {|#0:Callback|};
        public int Value;
    }
}";

        var fixedCode = @"
using System;
using Nino.Core;

namespace Sample
{
    [NinoType]
    public class TestClass
    {
        [Obsolete]
        [NinoIgnore]
        public Action Callback;
        public int Value;
    }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO007").WithLocation(0)
                .WithArguments("Callback", "System.Action", "Sample.TestClass")).RunAsync();
    }

    #endregion

    #region NINO009 - Private member

    [TestMethod]
    public async Task NINO009_RemoveNinoMember_FromPrivateMember()
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

        var diagnostic = new DiagnosticResult("NINO009", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(8, 17, 8, 18)
            .WithArguments("A", "TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO009_UpdateTypeToAllowNonPublicMembers()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public partial class TestClass
{
    [NinoMember(0)]
    private int A { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false, containNonPublicMembers: true)]
public partial class TestClass
{
    [NinoMember(0)]
    private int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO009", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(8, 17, 8, 18)
            .WithArguments("A", "TestClass");

        var test = SetUpCodeFixTest(code, fixedCode, diagnostic);
        test.CodeActionIndex = 1; // Select "Update type to allow non-public members" option
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO009_AllowNonPublic_ReplacesPositionalArgument()
    {
        var code = @"
using Nino.Core;

[NinoType(false, false)]
public partial class TestClass
{
    [NinoMember(0)]
    private int {|#0:A|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(false, true)]
public partial class TestClass
{
    [NinoMember(0)]
    private int A;
}";

        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO009").WithLocation(0).WithArguments("A", "TestClass"));
        test.CodeActionEquivalenceKey = "NINO009AllowNonPublic";
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO009_AllowNonPublic_AppendsNamedArgument()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public partial class TestClass
{
    [NinoMember(0)]
    private int {|#0:A|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(false, containNonPublicMembers: true)]
public partial class TestClass
{
    [NinoMember(0)]
    private int A;
}";

        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO009").WithLocation(0).WithArguments("A", "TestClass"));
        test.CodeActionEquivalenceKey = "NINO009AllowNonPublic";
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO009_AllowNonPublic_NonPartialTypeThenNeedsPartial()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    private int {|#0:A|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false, containNonPublicMembers: true)]
public class {|#0:TestClass|}
{
    [NinoMember(0)]
    private int A;
}";

        // Allowing non-public members surfaces NINO002 on a non-partial type.
        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO009").WithLocation(0).WithArguments("A", "TestClass"));
        test.FixedState.ExpectedDiagnostics.Add(
            VerifyCF.Diagnostic("NINO002").WithLocation(0).WithArguments("TestClass"));
        test.CodeActionEquivalenceKey = "NINO009AllowNonPublic";
        await test.RunAsync();
    }

    #endregion

    #region NINO010 - Remove redundant [NinoMember]

    [TestMethod]
    public async Task NINO010_RemoveRedundantNinoMember()
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

        var diagnostic = new DiagnosticResult("NINO010", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 16, 8, 17)
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

        var diagnostic1 = new DiagnosticResult("NINO010", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 16, 8, 17)
            .WithArguments("A", "TestClass");

        var diagnostic2 = new DiagnosticResult("NINO010", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(11, 16, 11, 17)
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

        var diagnostic1 = new DiagnosticResult("NINO010", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 16, 8, 17)
            .WithArguments("A", "TestClass1");

        var diagnostic2 = new DiagnosticResult("NINO010", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(11, 16, 11, 17)
            .WithArguments("B", "TestClass1");

        var diagnostic3 = new DiagnosticResult("NINO010", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(18, 16, 18, 17)
            .WithArguments("X", "TestClass2");

        var diagnostic4 = new DiagnosticResult("NINO010", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(21, 16, 21, 17)
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

    #endregion

    #region NINO011 - Remove redundant [NinoIgnore]

    [TestMethod]
    public async Task NINO011_RemoveRedundantNinoIgnore()
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

        var diagnostic = new DiagnosticResult("NINO011", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(8, 16, 8, 17)
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

    #endregion

    #region NINO012 - Ambiguous annotations

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
    public int A { get; set; }
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

        var diagnostic = new DiagnosticResult("NINO012", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
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
    public async Task NINO012_RemoveNinoIgnore_FromAmbiguousAnnotation()
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

        var diagnostic = new DiagnosticResult("NINO012", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(9, 16, 9, 17)
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
    public int A { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    public int A { get; set; }
}";

        var diagnostic = new DiagnosticResult("NINO012", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(9, 16, 9, 17)
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

    #endregion

    #region NINO013 - Use the next free index

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

        var diagnostic = new DiagnosticResult("NINO013", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(11, 16, 11, 17)
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

        var diagnostic = new DiagnosticResult("NINO013", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(14, 16, 14, 17)
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

        var diagnostic1 = new DiagnosticResult("NINO013", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .WithSpan(11, 16, 11, 17)
            .WithArguments("TestClass", "B", "TestClass", "A");

        await SetUpCodeFixTest(code, fixedCode, diagnostic1).RunAsync();
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

    #endregion

    #region NINO015 - Remove trackReferences

    [TestMethod]
    public async Task NINO015_RemoveTrackReferencesFromStruct()
    {
        var code = @"
using Nino.Core;

[NinoType(trackReferences: true)]
public struct TestStruct
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

        var diagnostic = new DiagnosticResult("NINO015", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithSpan(5, 15, 5, 25)
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

    #endregion

    #region NINO018 - Make class sealed

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

    #endregion

    #region NINO019 - Implement pool hooks

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

    #endregion
}
