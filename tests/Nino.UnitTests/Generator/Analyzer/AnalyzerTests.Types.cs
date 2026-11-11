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

// The type (NINO001-NINO003).
public partial class AnalyzerTests
{
    [TestMethod]
    public async Task NINO001_InaccessibleType()
    {
        var code = @"
using Nino.Core;

[NinoType]
internal class TestClass
{
    public int A;
    public string B;
}

public class NestedTestClass
{
    [NinoType]
    internal class NestedA
    {
        public int A;
        public string B;
    }

    [NinoType]
    private class {|#0:NestedB|}
    {
        public int A;
        public string B;
    }

    [NinoType]
    private static class {|#1:NestedC|}
    {
        public static int A;
        public static string B;
    }
}

internal class SomeContainer
{
    public class A
    {
        [NinoType]
        private class {|#2:B|}
        {

        }
    }
    [NinoType]
    class {|#3:C|}
    {
        [NinoType]
        public class {|#4:D|}
        {

        }
    }
}
";

        // Internal types are fine; private and protected ones cannot be named by the generated formatters.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO001").WithLocation(0).WithArguments("NestedTestClass.NestedB"),
            Verify.Diagnostic("NINO001").WithLocation(1).WithArguments("NestedTestClass.NestedC"),
            Verify.Diagnostic("NINO001").WithLocation(2).WithArguments("SomeContainer.A.B"),
            Verify.Diagnostic("NINO001").WithLocation(3).WithArguments("SomeContainer.C"),
            Verify.Diagnostic("NINO001").WithLocation(4).WithArguments("SomeContainer.C.D")).RunAsync();
    }

    [TestMethod]
    public async Task NINO001_TopLevelStaticRefStructAndProtectedNesting()
    {
        var code = @"
using Nino.Core;

[NinoType]
public static class {|#0:StaticClass|}
{
}

[NinoType]
public ref struct {|#1:RefStruct|}
{
    public int A;
}

public class Container
{
    [NinoType]
    protected class {|#2:ProtectedNested|}
    {
        public int A;
    }

    [NinoType]
    private protected class {|#3:PrivateProtectedNested|}
    {
        public int A;
    }

    [NinoType]
    protected internal class ProtectedInternalNested
    {
        public int A;
    }
}
";

        // protected internal is reachable from the generated code in the same assembly.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO001").WithLocation(0).WithArguments("StaticClass"),
            Verify.Diagnostic("NINO001").WithLocation(1).WithArguments("RefStruct"),
            Verify.Diagnostic("NINO001").WithLocation(2).WithArguments("Container.ProtectedNested"),
            Verify.Diagnostic("NINO001").WithLocation(3).WithArguments("Container.PrivateProtectedNested")).RunAsync();
    }

    [TestMethod]
    public async Task NINO001_InaccessibleTypeSkipsModelDiagnostics()
    {
        var code = @"
using System;
using Nino.Core;

public class Container
{
    [NinoType(includeNonPublicMembers: true)]
    private class {|#0:Hidden|}
    {
        private int _value;
        public Action Callback;
    }
}
";

        // Only NINO001: the model (NINO002, NINO007, ...) is not built for a type the generator skips.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO001").WithLocation(0).WithArguments("Container.Hidden")).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_NonPublicMembersNeedPartial()
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
}

[NinoType(includeNonPublicMembers: true)]
public partial class PartialClass
{
    private int _value;
    public int Value => _value;
}
";

        // The test compilation does not define NET8_0_OR_GREATER, so private members need a partial accessor.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO002").WithLocation(0).WithArguments("Outer.TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_PooledTypeNeedsPartial()
    {
        var code = @"
using Nino.Core;

[NinoType, NinoPooled]
public sealed class {|#0:TestClass|}
{
    public int Value;

    static void OnNinoPoolWarmup() { }
    void OnNinoPoolReturn() { }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO002").WithLocation(0).WithArguments("TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_NestedAndGenericTypes()
    {
        var code = @"
using Nino.Core;

public class Outer
{
    [NinoType, NinoPooled]
    public sealed partial class {|#0:Pooled|}
    {
        public int Value;

        static void OnNinoPoolWarmup() { }
        void OnNinoPoolReturn() { }
    }
}

[NinoType(includeNonPublicMembers: true)]
public class {|#1:Box|}<T>
{
    private T _value;
    public T Value => _value;
}

[NinoType]
public class PublicMembersOnly
{
    public int Value;
}
";

        // The containing type must be partial too; generic types use [UnsafeAccessor] only on .NET 9+ (GenericAccessorTests).
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO002").WithLocation(0).WithArguments("Outer.Pooled"),
            Verify.Diagnostic("NINO002").WithLocation(1).WithArguments("Box<T>")).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_PartialDeclarationsReportOnce()
    {
        var code = @"
using Nino.Core;

[NinoType(includeNonPublicMembers: true)]
public partial class TestClass
{
    private int _a;
    public int A => _a;
}

public partial class TestClass
{
    private int _b;
    public int B => _b;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO003_SubtypeWithoutNinoType()
    {
        var code = @"
using Nino.Core;

[NinoType(inheritedBySubtypes: true)]
public interface IBase
{
}

public class TestClass : IBase
{
    public int A;
    public string B;
}

public abstract class AbstractClass : IBase
{
    public int A;
    public string B;
}

[NinoType(inheritedBySubtypes: false)]
public abstract class AbstractClass2
{
    public int A;
    public string B;
}

public class {|#0:TestClass2|} : AbstractClass2
{
}

public class {|#1:TestClass3|} : TestClass2
{
}

public class TestClass4 : Something, IBase
{
}

public class Something
{
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO003").WithLocation(0).WithArguments("TestClass2", "AbstractClass2"),
            Verify.Diagnostic("NINO003").WithLocation(1).WithArguments("TestClass3", "AbstractClass2")).RunAsync();
    }

    [TestMethod]
    public async Task NINO003_ViaInterface()
    {
        var code = @"
using Nino.Core;

[NinoType(inheritedBySubtypes: false)]
public interface IBase
{
    int A { get; set; }
}

public class {|#0:Implementation|} : IBase
{
    public int A { get; set; }
}

public struct {|#1:StructImplementation|} : IBase
{
    public int A { get; set; }
}

public interface {|#2:IDerived|} : IBase
{
}

[NinoType]
public class Annotated : IBase
{
    public int A { get; set; }
}

public enum NotAType
{
    A,
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO003").WithLocation(0).WithArguments("Implementation", "IBase"),
            Verify.Diagnostic("NINO003").WithLocation(1).WithArguments("StructImplementation", "IBase"),
            Verify.Diagnostic("NINO003").WithLocation(2).WithArguments("IDerived", "IBase")).RunAsync();
    }

    [TestMethod]
    public async Task NINO003_ReportsNearestBase()
    {
        var code = @"
using Nino.Core;

[NinoType(inheritedBySubtypes: false)]
public interface IBase
{
}

[NinoType(inheritedBySubtypes: false)]
public class Base : IBase
{
    public int A;
}

public class {|#0:Derived|} : Base, IBase
{
}
";

        // Base classes are checked before interfaces, and only one diagnostic is reported per type.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO003").WithLocation(0).WithArguments("Derived", "Base")).RunAsync();
    }
}
