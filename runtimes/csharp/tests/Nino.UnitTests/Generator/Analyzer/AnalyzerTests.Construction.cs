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

// Construction (NINO004-NINO006).
public partial class AnalyzerTests
{
    [TestMethod]
    public async Task NINO004_NoUsableConstructor()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class {|#0:TestClass|}
{
    public int A;

    public TestClass(int unrelated)
    {
        A = unrelated;
    }
}

[NinoType]
public class MatchingConstructor
{
    public int A;

    public MatchingConstructor(int a)
    {
        A = a;
    }
}

[NinoType]
public struct StructWithoutMatch
{
    public int A;

    public StructWithoutMatch(int unrelated)
    {
        A = unrelated;
    }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO004").WithLocation(0).WithArguments("TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO004_NonPublicParameterlessConstructor()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class {|#0:PrivateConstructor|}
{
    public int A;

    private PrivateConstructor()
    {
    }
}

[NinoType]
public class InternalConstructor
{
    public int A;

    internal InternalConstructor()
    {
    }
}

[NinoType]
public abstract class AbstractWithoutConstructor
{
    public int A;

    protected AbstractWithoutConstructor(string unrelated)
    {
    }
}

[NinoType]
public record Person(string Name, int Age);

[NinoType]
public record struct Point(int X, int Y);
";

        // Abstract types are never constructed; records use their positional constructor.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO004").WithLocation(0).WithArguments("PrivateConstructor")).RunAsync();
    }

    [TestMethod]
    public async Task NINO005_MissingConstructorMember()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public int A;

    [NinoConstructor]
    public {|#0:TestClass|}(int b)
    {
        A = b;
    }
}

[NinoType]
public class NamedParameters
{
    public int A;

    [NinoConstructor(""missing"")]
    public {|#1:NamedParameters|}(int x)
    {
        A = x;
    }
}

[NinoType]
public class ValidConstructor
{
    public int A;

    [NinoConstructor(nameof(A))]
    public ValidConstructor(int x)
    {
        A = x;
    }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO005").WithLocation(0).WithArguments("b", "TestClass"),
            Verify.Diagnostic("NINO005").WithLocation(1).WithArguments("missing", "NamedParameters")).RunAsync();
    }

    [TestMethod]
    public async Task NINO005_StaticFactoryAndStruct()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class Factory
{
    public int A;

    [NinoConstructor]
    public static Factory {|#0:Create|}(int b) => new Factory { A = b };
}

[NinoType]
public class ValidFactory
{
    public int A;

    [NinoConstructor]
    public static ValidFactory Create(int a) => new ValidFactory { A = a };
}

[NinoType]
public struct TestStruct
{
    public int A;
    public string Name;

    [NinoConstructor]
    public {|#1:TestStruct|}(int b)
    {
        A = b;
        Name = null;
    }
}

[NinoType]
public struct RawStruct
{
    public int A;

    [NinoConstructor]
    public RawStruct(int b)
    {
        A = b;
    }
}
";

        // Unmanaged structs are copied as raw bytes, so their constructors are never planned.

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO005").WithLocation(0).WithArguments("b", "Factory"),
            Verify.Diagnostic("NINO005").WithLocation(1).WithArguments("b", "TestStruct")).RunAsync();
    }

    [TestMethod]
    public async Task NINO021_InaccessibleConstructor()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class PrivateConstructor
{
    public int A;

    public PrivateConstructor() { }

    [NinoConstructor]
    private {|#0:PrivateConstructor|}(int a)
    {
        A = a;
    }
}

[NinoType]
public class ProtectedFactory
{
    public int A;

    [NinoConstructor]
    protected static ProtectedFactory {|#1:Create|}(int a) => new ProtectedFactory { A = a };
}

[NinoType]
public class InternalConstructor
{
    public int A;

    [NinoConstructor]
    internal InternalConstructor(int a)
    {
        A = a;
    }
}

[NinoType]
public class ProtectedInternalConstructor
{
    public int A;

    [NinoConstructor]
    protected internal ProtectedInternalConstructor(int a)
    {
        A = a;
    }
}
";

        // Checked before the parameters: an inaccessible constructor is not matched against members at all.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO021").WithLocation(0)
                .WithArguments("PrivateConstructor.PrivateConstructor(int)", "PrivateConstructor"),
            Verify.Diagnostic("NINO021").WithLocation(1)
                .WithArguments("ProtectedFactory.Create(int)", "ProtectedFactory")).RunAsync();
    }

    [TestMethod]
    public async Task NINO025_FactoryIgnoredBecauseOfNinoConstructor()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class StaticConstructor
{
    public int A;

    [NinoFactory]
    public static StaticConstructor {|#0:Rent|}() => new StaticConstructor();

    [NinoConstructor]
    public static StaticConstructor Create(int a) => new StaticConstructor { A = a };
}

[NinoType]
public class InstanceConstructor
{
    public int A;

    [NinoFactory]
    public static InstanceConstructor {|#1:Rent|}() => new InstanceConstructor(0);

    [NinoConstructor]
    public InstanceConstructor(int a)
    {
        A = a;
    }
}

[NinoType]
public class FactoryOnly
{
    public int A;

    [NinoFactory]
    public static FactoryOnly Rent() => new FactoryOnly();

    public static FactoryOnly Create(int a) => new FactoryOnly { A = a };
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO025").WithLocation(0)
                .WithArguments("Rent", "StaticConstructor", "StaticConstructor.Create(int)"),
            Verify.Diagnostic("NINO025").WithLocation(1)
                .WithArguments("Rent", "InstanceConstructor", "InstanceConstructor.InstanceConstructor(int)")).RunAsync();
    }

    [TestMethod]
    public async Task NINO006_ReadOnlyMember()
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
}

[NinoType]
public class ReadOnlyInConstructor
{
    public readonly int A;
    public int B;

    public ReadOnlyInConstructor(int a)
    {
        A = a;
    }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO006").WithLocation(0).WithArguments("A", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO006_GetOnlyAutoProperties()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class Immutable
{
    public int Id { get; }
    public string Name { get; }

    // Taken by no constructor: not a member.
    public int Fixed { get; } = 1;

    public Immutable(int id, string name)
    {
        Id = id;
        Name = name;
    }
}

[NinoType]
public class OtherConstructor
{
    // Taken by a constructor that cannot be used (c matches no member): a member nothing restores.
    public int {|#0:A|} { get; }
    public int B { get; set; }

    public OtherConstructor(int b)
    {
        B = b;
    }

    public OtherConstructor(int a, int c)
    {
        A = a;
    }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO006").WithLocation(0).WithArguments("A", "OtherConstructor")).RunAsync();
    }

    [TestMethod]
    public async Task NINO006_InitOnlyPrivateAndAbstract()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class InitOnly
{
    public int A { get; init; }
    public readonly int B;

    public InitOnly(int b)
    {
        B = b;
    }
}

[NinoType(includeNonPublicMembers: true)]
public partial class PrivateReadOnly
{
    private readonly int {|#0:_a|};
    public int B;

    public PrivateReadOnly(int b)
    {
        B = b;
    }
}

[NinoType]
public abstract class AbstractBase
{
    public readonly int A;
}
";

        // Accessible init-only properties are set through the object initializer; abstract types are not built.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO006").WithLocation(0).WithArguments("_a", "PrivateReadOnly")).RunAsync();
    }

    [TestMethod]
    public async Task NINO006_InheritedMember()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class Base
{
    public readonly int {|#0:A|};
}

[NinoType]
public class Derived : Base
{
    public int B;

    public Derived(int b)
    {
        B = b;
    }
}
";

        // Reported where the member is declared, once for each type that re-creates it.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO006").WithLocation(0).WithArguments("A", "Base"),
            Verify.Diagnostic("NINO006").WithLocation(0).WithArguments("A", "Derived")).RunAsync();
    }

    [TestMethod]
    public async Task NINO006_SeveralMembersReportedSeparately()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    public readonly int {|#0:A|};
    public readonly int {|#1:B|};
    public int C;

    public TestClass(int c)
    {
        C = c;
    }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO006").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO006").WithLocation(1).WithArguments("B", "TestClass")).RunAsync();
    }
}
