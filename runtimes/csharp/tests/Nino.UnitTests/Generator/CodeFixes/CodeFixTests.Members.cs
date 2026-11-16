using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// Members Nino cannot serialize: read-only (NINO006), unserializable (NINO007), private (NINO009).
public partial class CodeFixTests
{
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



    [TestMethod]
    public async Task NINO007_AddNinoIgnore()
    {
        var code = @"
using System;
using Nino.Core;

[NinoType]
public class TestClass
{
    public Action {|#0:Callback|};
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

        var diagnostic = VerifyCF.Diagnostic("NINO007").WithLocation(0)
            .WithArguments("Callback", "System.Action", "TestClass", "", "the member is skipped");

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
            VerifyCF.Diagnostic("NINO007").WithLocation(0).WithArguments("Factory", "System.Func<int>", "TestClass", "", "the member is skipped"))
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
                .WithArguments("Callback", "System.Action", "Sample.TestClass", "", "the member is skipped")).RunAsync();
    }



    [TestMethod]
    public async Task NINO009_RemoveNinoMember_FromPrivateMember()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    private int {|#0:A|} { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    private int A { get; set; }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO009").WithLocation(0)
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
    private int {|#0:A|} { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(autoCollect: false, includeNonPublicMembers: true)]
public partial class TestClass
{
    [NinoMember(0)]
    private int A { get; set; }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO009").WithLocation(0)
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

[NinoType(false, includeNonPublicMembers: true)]
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

[NinoType(autoCollect: false, includeNonPublicMembers: true)]
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
}
