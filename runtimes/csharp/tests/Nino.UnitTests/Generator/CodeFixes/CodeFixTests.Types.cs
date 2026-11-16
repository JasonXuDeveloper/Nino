using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// Types: make public (NINO001), make partial (NINO002), add [NinoType] (NINO003).
public partial class CodeFixTests
{
    [TestMethod]
    public async Task NINO001_MakeTypePublic_PrivateProtectedClass()
    {
        var code = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    private protected class {|#0:TestClass|}
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

        var diagnostic = VerifyCF.Diagnostic("NINO001").WithLocation(0).WithArguments("OuterClass.TestClass");

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
        class {|#0:TestClass|}
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

        var diagnostic = VerifyCF.Diagnostic("NINO001").WithLocation(0).WithArguments("Outer.Middle.TestClass");

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
    private class {|#0:TestClass|}
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

        var diagnostic = VerifyCF.Diagnostic("NINO001").WithLocation(0).WithArguments("OuterClass.TestClass");

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
    private record {|#0:TestRecord|}(int Value);
}";

        var fixedCode = @"
using Nino.Core;

public class OuterClass
{
    [NinoType]
    public record TestRecord(int Value);
}";

        var diagnostic = VerifyCF.Diagnostic("NINO001").WithLocation(0).WithArguments("OuterClass.TestRecord");

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



    [TestMethod]
    public async Task NINO002_MakeTypePartial_ContainingTypes()
    {
        var code = @"
using Nino.Core;

public class Outer
{
    [NinoType(includeNonPublicMembers: true)]
    public class {|#0:TestClass|}
    {
        private int _value;
        public int Value => _value;
    }
}";

        var fixedCode = @"
using Nino.Core;

public partial class Outer
{
    [NinoType(includeNonPublicMembers: true)]
    public partial class TestClass
    {
        private int _value;
        public int Value => _value;
    }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO002").WithLocation(0)
            .WithArguments("Outer.TestClass");

        await SetUpCodeFixTest(code, fixedCode, diagnostic).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_MakeTypePartial_TopLevel()
    {
        var code = @"
using Nino.Core;

[NinoType(includeNonPublicMembers: true)]
public class {|#0:TestClass|}
{
    private int _value;
    public int Value => _value;
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(includeNonPublicMembers: true)]
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
    [NinoType(includeNonPublicMembers: true)]
    public class {|#0:A|}
    {
        private int _value;
        public int Value => _value;
    }

    [NinoType(includeNonPublicMembers: true)]
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
    [NinoType(includeNonPublicMembers: true)]
    public partial class A
    {
        private int _value;
        public int Value => _value;
    }

    [NinoType(includeNonPublicMembers: true)]
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



    [TestMethod]
    public async Task NINO003_AddNinoType()
    {
        var code = @"
using Nino.Core;

[NinoType(inheritedBySubtypes: false)]
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

[NinoType(inheritedBySubtypes: false)]
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
    [DataRow("\n")]
    [DataRow("\r\n")]
    public async Task NINO003_AddNinoType_AddsUsing(string newLine)
    {
        // The using directive and the attribute take the file's line breaks, on any machine.
        var code = @"
[Nino.Core.NinoType(inheritedBySubtypes: false)]
public interface IBase
{
}

public class {|#0:Implementation|} : IBase
{
    public int A;
}".Replace("\r\n", "\n").Replace("\n", newLine);

        var fixedCode = @"
using Nino.Core;

[Nino.Core.NinoType(inheritedBySubtypes: false)]
public interface IBase
{
}

[NinoType]
public class Implementation : IBase
{
    public int A;
}".Replace("\r\n", "\n").Replace("\n", newLine);

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO003").WithLocation(0).WithArguments("Implementation", "IBase")).RunAsync();
    }

    [TestMethod]
    public async Task FormerTypeArguments_Renamed()
    {
        // The names before 4.0 (containNonPublicMembers in v3, allowInheritance in the release candidates) are a
        // compiler error (CS1739); the fix renames each, the value stays. Other arguments and attributes stay.
        var code = @"
using Nino.Core;

[NinoType({|#0:allowInheritance|}: false, format: NinoFormat.Keyed)]
public class Base
{
    public int A;
}

[Nino.Core.NinoTypeAttribute({|#1:allowInheritance|}: true)]
public class Other
{
    public int B;
}

[NinoType(false, {|#2:containNonPublicMembers|}: true)]
public partial class Private
{
    [NinoMember(0)] private int {|#3:_c|};
}";

        var fixedCode = @"
using Nino.Core;

[NinoType(inheritedBySubtypes: false, format: NinoFormat.Keyed)]
public class Base
{
    public int A;
}

[Nino.Core.NinoTypeAttribute(inheritedBySubtypes: true)]
public class Other
{
    public int B;
}

[NinoType(false, includeNonPublicMembers: true)]
public partial class Private
{
    [NinoMember(0)] private int _c;
}";

        await SetUpCodeFixTest(code, fixedCode,
            DiagnosticResult.CompilerError("CS1739").WithLocation(0).WithArguments("NinoTypeAttribute", "allowInheritance"),
            DiagnosticResult.CompilerError("CS1739").WithLocation(1).WithArguments("NinoTypeAttribute", "allowInheritance"),
            DiagnosticResult.CompilerError("CS1739").WithLocation(2)
                .WithArguments("NinoTypeAttribute", "containNonPublicMembers"),
            // Until the rename, the option is not set.
            VerifyCF.Diagnostic("NINO009").WithLocation(3).WithArguments("_c", "Private"))
            .RunAsync();
    }
}
