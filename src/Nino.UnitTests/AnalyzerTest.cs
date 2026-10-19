using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

[TestClass]
public class AnalyzerTest
{
    private CSharpAnalyzerTest<Generator.Analysis.NinoAnalyzer, DefaultVerifier> SetUpAnalyzerTest(string code,
        params DiagnosticResult[] diagnostics)
    {
        var test = new CSharpAnalyzerTest<Generator.Analysis.NinoAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { code }
            }
        };

        if (diagnostics.Length > 0)
        {
            test.TestState.ExpectedDiagnostics.AddRange(diagnostics);
        }

        TestReferences.Apply(test.TestState);

        return test;
    }

    #region The type (NINO001-NINO003)

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
    [NinoType(containNonPublicMembers: true)]
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
    [NinoType(containNonPublicMembers: true)]
    public class {|#0:TestClass|}
    {
        private int _value;
        public int Value => _value;
    }
}

[NinoType(containNonPublicMembers: true)]
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

[NinoType(containNonPublicMembers: true)]
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

        // The containing type must be partial too, and generic types never use [UnsafeAccessor].
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO002").WithLocation(0).WithArguments("Outer.Pooled"),
            Verify.Diagnostic("NINO002").WithLocation(1).WithArguments("Box<T>")).RunAsync();
    }

    [TestMethod]
    public async Task NINO002_PartialDeclarationsReportOnce()
    {
        var code = @"
using Nino.Core;

[NinoType(containNonPublicMembers: true)]
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
    public async Task NINO003_SubTypeWithoutNinoType()
    {
        var code = @"
using Nino.Core;

[NinoType(allowInheritance: true)]
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

[NinoType(allowInheritance: false)]
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

[NinoType(allowInheritance: false)]
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

[NinoType(allowInheritance: false)]
public interface IBase
{
}

[NinoType(allowInheritance: false)]
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

    #endregion

    #region Construction (NINO004-NINO006)

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

[NinoType(containNonPublicMembers: true)]
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

    #endregion

    #region Members (NINO007-NINO013, NINO022-NINO023)

    [TestMethod]
    public async Task NINO007_UnserializableMember()
    {
        var code = @"
using System;
using System.Threading.Tasks;
using Nino.Core;

[NinoType]
public class TestClassWithDelegate
{
    // Delegates are not directly serializable
    public Action {|#0:UnsupportedDelegate|};

    // Valid fields that will be serialized
    public int Id;
    public string Name;
}

[NinoType]
public class TestClassWithTask
{
    // Task types cannot be serialized
    public Task<int> {|#1:UnsupportedTask|};

    // Valid fields
    public double Value;
    public bool Flag;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO007").WithLocation(0)
                .WithArguments("UnsupportedDelegate", "System.Action", "TestClassWithDelegate"),
            Verify.Diagnostic("NINO007").WithLocation(1)
                .WithArguments("UnsupportedTask", "System.Threading.Tasks.Task<int>", "TestClassWithTask")
        ).RunAsync();
    }

    [TestMethod]
    public async Task NINO007_PropertiesAndInheritedMembers()
    {
        var code = @"
using System;
using Nino.Core;

[NinoType]
public abstract class AbstractBase
{
    public Action {|#0:Callback|};
    public int Id;
}

[NinoType]
public class Derived : AbstractBase
{
    public Func<int> {|#1:Factory|} { get; set; }

    [NinoIgnore]
    public Action Ignored;
}
";

        // Abstract types report nothing themselves; the subtype reports the inherited member where it is declared.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO007").WithLocation(0).WithArguments("Callback", "System.Action", "Derived"),
            Verify.Diagnostic("NINO007").WithLocation(1).WithArguments("Factory", "System.Func<int>", "Derived"))
            .RunAsync();
    }

    [TestMethod]
    public async Task NINO008_InvalidCustomFormatter()
    {
        var code = @"
using Nino.Core;

public struct StringFormatter : INinoFormatter<string>
{
    public void Serialize(string value, ref Writer writer) { }
    public void Deserialize(ref string value, ref Reader reader) { }
}

public struct IntFormatter : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref int value, ref Reader reader) { }
}

public class NotAFormatter
{
}

[NinoType]
public class TestClass
{
    [NinoCustomFormatter(typeof(StringFormatter))]
    public int {|#0:A|};

    [NinoCustomFormatter(typeof(NotAFormatter))]
    public int {|#1:B|};

    [NinoCustomFormatter(typeof(IntFormatter))]
    public int C;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO008").WithLocation(0).WithArguments("StringFormatter", "int"),
            Verify.Diagnostic("NINO008").WithLocation(1).WithArguments("NotAFormatter", "int")).RunAsync();
    }

    [TestMethod]
    public async Task NINO008_CustomFormattersAreStructsOfTheMemberType()
    {
        var code = @"
using Nino.Core;

public struct IntFormatter : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref int value, ref Reader reader) { }
}

public struct NullableIntFormatter : INinoFormatter<int?>
{
    public void Serialize(int? value, ref Writer writer) { }
    public void Deserialize(ref int? value, ref Reader reader) { }
}

// A class implementing the interface is not accepted: custom formatters are inlined structs.
public sealed class ClassIntFormatter : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref int value, ref Reader reader) { }
}

public struct GenericFormatter<T> : INinoFormatter<T>
{
    public void Serialize(T value, ref Writer writer) { }
    public void Deserialize(ref T value, ref Reader reader) { }
}

[NinoType]
public class TestClass
{
    [NinoCustomFormatter(typeof(IntFormatter))]
    public int Property { get; set; }

    [NinoCustomFormatter(typeof(NullableIntFormatter))]
    public int {|#0:Nullable|} { get; set; }

    [NinoCustomFormatterAttribute(typeof(IntFormatter))]
    public int? {|#1:NullableMember|};

    [NinoCustomFormatter(typeof(IntFormatter))]
    public long {|#2:WrongType|};

    [NinoCustomFormatter(typeof(ClassIntFormatter))]
    public int {|#3:Class|};

    [NinoCustomFormatter(typeof(GenericFormatter<int>))]
    public int Generic;

    [NinoCustomFormatter(typeof(GenericFormatter<>))]
    public int {|#4:Open|};
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO008").WithLocation(0).WithArguments("NullableIntFormatter", "int"),
            Verify.Diagnostic("NINO008").WithLocation(1).WithArguments("IntFormatter", "int?"),
            Verify.Diagnostic("NINO008").WithLocation(2).WithArguments("IntFormatter", "long"),
            Verify.Diagnostic("NINO008").WithLocation(3).WithArguments("ClassIntFormatter", "int"),
            Verify.Diagnostic("NINO008").WithLocation(4).WithArguments("GenericFormatter<>", "int")).RunAsync();
    }

    [TestMethod]
    public async Task NINO009_PrivateMember()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public class TestClass
{
    [NinoMember(1)]
    private int {|#0:A|};

    public string B;
}

[NinoType(false, true)]
public partial class TestClass2
{
    [NinoMember(1)]
    private int A;

    public string B;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO009").WithLocation(0).WithArguments("A", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO009_NestedTypeMayContainNonPublicMembers()
    {
        var code = @"
using Nino.Core;

public partial class TestNested
{
    [NinoType(false, true)]
    public partial class TestClass
    {
        [NinoMember(1)]
        private int A;
    }
}
";

        // Nested types are supported: the generator declares their accessor inside the partial chain.
        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO009_PropertiesAndAttributeSpellings()
    {
        var code = @"
using Nino.Core;
using NM = Nino.Core.NinoMemberAttribute;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMemberAttribute(0)]
    private int {|#0:A|} { get; set; }

    [Nino.Core.NinoMember(1)]
    private int {|#1:B|};

    [NM(2)]
    private int {|#2:C|};

    [NinoMember(3)]
    public int D;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO009").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO009").WithLocation(1).WithArguments("B", "TestClass"),
            Verify.Diagnostic("NINO009").WithLocation(2).WithArguments("C", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO009_AnyNonPublicMember()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0)]
    protected int {|#0:A|};

    [NinoMember(1)]
    internal int {|#1:B|};

    [NinoMember(2)]
    public int C;
}
";

        // Protected and internal [NinoMember]s are not collected without containNonPublicMembers either.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO009").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO009").WithLocation(1).WithArguments("B", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO010_RedundantNinoMember()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoMember(1)]
    public int {|#0:A|};

    [NinoMember(2)]
    public string {|#1:B|};
}

[NinoType(false)]
public class TestClass2
{
    [NinoMember(1)]
    public int A;

    [NinoMember(2)]
    public string B;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO010").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO010").WithLocation(1).WithArguments("B", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO010_PropertiesPartialDeclarationsAndSpellings()
    {
        var code = @"
using Nino.Core;

[NinoType]
public partial class TestClass
{
    [NinoMemberAttribute(0)]
    public int {|#0:A|} { get; set; }
}

public partial class TestClass
{
    [Nino.Core.NinoMember(1)]
    public int {|#1:B|};

    [NinoMember(2)]
    private int {|#2:C|};
}
";

        // A non-public [NinoMember] in an autoCollect type is not collected without containNonPublicMembers:
        // that is NINO009, not NINO010.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO010").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO010").WithLocation(1).WithArguments("B", "TestClass"),
            Verify.Diagnostic("NINO009").WithLocation(2).WithArguments("C", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO011_RedundantNinoIgnore()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public class TestClass
{
    [NinoIgnore]
    public int {|#0:A|};

    [NinoMember(2)]
    public string B;
}

[NinoType]
public class TestClass2
{
    [NinoIgnore]
    public int A;

    public string B;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO011").WithLocation(0).WithArguments("A", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO011_PropertiesAndSpellings()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [Nino.Core.NinoIgnore]
    public int {|#0:A|} { get; set; }

    [NinoIgnoreAttribute]
    public int {|#1:B|};
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO011").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO011").WithLocation(1).WithArguments("B", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO012_AmbiguousMember()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public class TestClass
{
    [NinoIgnore]
    [NinoMember(2)]
    public int {|#0:A|};

    public string B;
}

[NinoType]
public class TestClass2
{
    [NinoIgnore]
    [NinoMember(2)]
    public int {|#1:A|};

    public string B;
}
";

        // With autoCollect the [NinoMember] is redundant instead (NINO010).
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO012").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO010").WithLocation(1).WithArguments("A", "TestClass2")).RunAsync();
    }

    [TestMethod]
    public async Task NINO013_DuplicateIndex()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public class TestClass : TestBase
{
    [NinoMember(3)]
    public float C;

    [NinoMember(3)]
    public double {|#0:D|};

    [NinoMember(2)]
    public bool E;
}

[NinoType(false)]
public class TestBase
{
    [NinoMember(1)]
    public int A;

    [NinoMember(2)]
    public string B;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO013").WithLocation(0).WithArguments("TestClass", "D", "TestClass", "C")).RunAsync();
    }

    [TestMethod]
    public async Task NINO012_PrivateAmbiguousMember()
    {
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public class TestClass
{
    [NinoMember(0), NinoIgnore]
    private int {|#0:A|} { get; set; }
}
";

        // Both problems are reported on the same member.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO012").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO009").WithLocation(0).WithArguments("A", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO013_ReportedOncePerIndex()
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

    [NinoMember(0)]
    public int C;

    [NinoMember(1)]
    public int D { get; set; }

    [NinoMember(1)]
    public int {|#1:E|} { get; set; }
}

[NinoType]
public class AutoCollect
{
    public int A;
    public int B;
}
";

        // A third member with an index already reported adds nothing; autoCollect types do not check indices.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO013").WithLocation(0).WithArguments("TestClass", "B", "TestClass", "A"),
            Verify.Diagnostic("NINO013").WithLocation(1).WithArguments("TestClass", "E", "TestClass", "D")).RunAsync();
    }

    [TestMethod]
    public async Task NINO022_HiddenMember()
    {
        var code = @"
using Nino.Core;

[NinoType(containNonPublicMembers: true)]
public partial class Base
{
    protected int {|#0:Value|};
    public string Name;
}

[NinoType(containNonPublicMembers: true)]
public partial class Derived : Base
{
    public new string Value;
}

[NinoType]
public class PublicBase
{
    public int Value;
}

[NinoType]
public class PublicDerived : PublicBase
{
    public new string Value;
}
";

        // The test compilation does not define NET8_0_OR_GREATER: no [UnsafeAccessor]. A public hidden member is
        // reached through a cast.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO022").WithLocation(0).WithArguments("Value", "Base", "Derived")).RunAsync();
    }

    [TestMethod]
    public async Task NINO023_EmptyStruct()
    {
        var code = @"
using System;
using Nino.Core;

[NinoType]
public struct {|#0:AllIgnored|}
{
    [NinoIgnore] public int A;
}

[NinoType]
public struct {|#1:AllSkipped|}
{
    public Action {|#2:Callback|};
}

// No fields: written as its raw memory (1 byte).
[NinoType]
public struct NoFields
{
}

// Each instance writes its length.
[NinoType(format: NinoFormat.VersionTolerant)]
public struct Tolerant
{
    [NinoIgnore] public int A;
}

[NinoType(format: NinoFormat.Keyed)]
public struct Keyed
{
    [NinoIgnore] public int A;
}

// Classes have a header.
[NinoType]
public sealed class EmptyClass
{
    [NinoIgnore] public int A;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO023").WithLocation(0).WithArguments("AllIgnored"),
            Verify.Diagnostic("NINO007").WithLocation(2)
                .WithArguments("Callback", "System.Action", "AllSkipped"),
            Verify.Diagnostic("NINO023").WithLocation(1).WithArguments("AllSkipped")).RunAsync();
    }

    #endregion

    #region Features (NINO014-NINO017, NINO024, NINO026)

    [TestMethod]
    public async Task NINO014_KeyCollision()
    {
        // dzFy and zsaa have the same 32-bit FNV-1a hash.
        var code = @"
using Nino.Core;

[NinoType(format: NinoFormat.Keyed)]
public class {|#0:TestClass|}
{
    public int dzFy;
    public int zsaa;
}

[NinoType]
public class NotKeyed
{
    public int dzFy;
    public int zsaa;
}

[NinoType(format: NinoFormat.Keyed)]
public class NoCollision
{
    public int A;
    public int B;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO014").WithLocation(0).WithArguments("dzFy", "zsaa", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO014_CollisionWithInheritedMember()
    {
        var code = @"
using Nino.Core;

[NinoType(format: NinoFormat.Keyed)]
public class Base
{
    public int dzFy;
}

[NinoType(format: NinoFormat.Keyed)]
public class {|#0:Derived|} : Base
{
    public int zsaa { get; set; }
}
";

        // Inherited members share the key space; only the type that has both reports.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO014").WithLocation(0).WithArguments("dzFy", "zsaa", "Derived")).RunAsync();
    }

    [TestMethod]
    public async Task NINO014_FormerNameCollision()
    {
        var code = @"
using Nino.Core;

[NinoType(format: NinoFormat.Keyed)]
public class TestClass
{
    [NinoFormerName(""B"")] public int {|#0:A|};
    public int B;
    [NinoFormerName(""Old"")] public int C { get; set; }
    [NinoFormerName(""Old"")] public int {|#1:D|} { get; set; }
}

[NinoType(format: NinoFormat.Keyed)]
public class NoCollision
{
    [NinoFormerName(""A"")] [NinoFormerName(""Older"")] [NinoFormerName(""Older"")] public int A;
    [NinoFormerName(""Hp"")] [NinoFormerName(""Health"")] public int HitPoints;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO014").WithLocation(0).WithArguments("B", "A (former name 'B')", "TestClass"),
            Verify.Diagnostic("NINO014").WithLocation(1)
                .WithArguments("C (former name 'Old')", "D (former name 'Old')", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO024_FormerNameNotKeyed()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class Positional
{
    [NinoFormerName(""Hp"")] public int {|#0:Health|};
}

[NinoType(format: NinoFormat.VersionTolerant)]
public struct Tolerant
{
    [NinoFormerName(""Hp"")] public int {|#1:Health|} { get; set; }
}

[NinoFormerName(""Old.Name"")]
[NinoType(format: NinoFormat.Keyed)]
public class Keyed
{
    [NinoFormerName(""Hp"")] public int Health;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO024").WithLocation(0).WithArguments("Health", "Positional"),
            Verify.Diagnostic("NINO024").WithLocation(1).WithArguments("Health", "Tolerant")).RunAsync();
    }

    [TestMethod]
    public async Task NINO026_MultipleTypeFormerNames()
    {
        // Issue #201: the generator took the first, NinoTypeId.Of threw AmbiguousMatchException.
        var code = @"
using Nino.Core;

[NinoType, NinoFormerName(""A.Old""), {|#0:NinoFormerName(""B.Old"")|}]
public partial struct Renamed { public int X; }

[NinoFormerName(""Old.Single"")]
[NinoType(format: NinoFormat.Keyed)]
public class Single
{
    [NinoFormerName(""Hp""), NinoFormerName(""Life"")] public int Health;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO026").WithLocation(0).WithArguments("Renamed")).RunAsync();
    }

    [TestMethod]
    public async Task NINO015_TrackReferencesOnStruct()
    {
        var code = @"
using Nino.Core;

[NinoType(trackReferences: true)]
public struct {|#0:TestStruct|}
{
    public int Value;
}

[NinoType(trackReferences: true)]
public class TestClass
{
    public int Value;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO015").WithLocation(0).WithArguments("TestStruct")).RunAsync();
    }

    [TestMethod]
    public async Task NINO015_InheritedFromInterfaceAndRecordStruct()
    {
        var code = @"
using Nino.Core;

[NinoType(trackReferences: true)]
public interface ITracked
{
}

[NinoType]
public struct {|#0:Implementation|} : ITracked
{
    public int Value;
}

[NinoType(trackReferences: true)]
public record struct {|#1:Point|}(int X, int Y);

[NinoType]
public class ClassImplementation : ITracked
{
    public int Value;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO015").WithLocation(0).WithArguments("Implementation"),
            Verify.Diagnostic("NINO015").WithLocation(1).WithArguments("Point")).RunAsync();
    }

    [TestMethod]
    public async Task NINO016_TrackReferencesOnRecreatedClass()
    {
        var code = @"
using Nino.Core;

[NinoType(trackReferences: true)]
public class {|#0:TestClass|}
{
    public readonly int Value;

    public TestClass(int value)
    {
        Value = value;
    }
}

[NinoType(trackReferences: true)]
public class Assignable
{
    public int Value;

    public Assignable(int value)
    {
        Value = value;
    }

    public Assignable()
    {
    }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO016").WithLocation(0).WithArguments("TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO016_InheritedTrackingAndRecords()
    {
        var code = @"
using Nino.Core;

[NinoType(trackReferences: true)]
public class Base
{
    public int A;
}

[NinoType]
public class {|#0:Derived|} : Base
{
    public readonly int B;

    public Derived(int b)
    {
        B = b;
    }
}

[NinoType(trackReferences: true)]
public record {|#1:Person|}(string Name);

[NinoType]
public class AssignableDerived : Base
{
    public int C;
}
";

        // Tracking is inherited by subtypes; positional records are re-created from their constructor.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO016").WithLocation(0).WithArguments("Derived"),
            Verify.Diagnostic("NINO016").WithLocation(1).WithArguments("Person")).RunAsync();
    }

    [TestMethod]
    public async Task NINO017_NotPublic()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFactory]
    private static TestClass {|#0:GetInstance|}() => new TestClass();

    public int Value;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO017").WithLocation(0).WithArguments("GetInstance", "must be public")).RunAsync();
    }

    [TestMethod]
    public async Task NINO017_NotStatic()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFactory]
    public TestClass {|#0:GetInstance|}() => new TestClass();

    public int Value;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO017").WithLocation(0).WithArguments("GetInstance", "must be static")).RunAsync();
    }

    [TestMethod]
    public async Task NINO017_HasParameters()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFactory]
    public static TestClass {|#0:GetInstance|}(int value) => new TestClass();

    public int Value;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO017").WithLocation(0).WithArguments("GetInstance", "must be callable without arguments (parameterless, or every parameter optional)")).RunAsync();
    }

    [TestMethod]
    public async Task NINO017_WrongReturnType()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFactory]
    public static object {|#0:GetInstance|}() => new TestClass();

    public int Value;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO017").WithLocation(0).WithArguments("GetInstance", "must return TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO017_Generic()
    {
        // Generated code calls the factory without type arguments, which cannot be inferred.
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFactory]
    public static TestClass {|#0:GetInstance|}<T>() => new TestClass();

    public int Value;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO017").WithLocation(0).WithArguments("GetInstance", "must not be generic")).RunAsync();
    }

    [TestMethod]
    public async Task NINO017_MultipleErrors()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFactory]
    private object {|#0:GetInstance|}(int value) => new TestClass();

    public int Value;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO017").WithLocation(0).WithArguments("GetInstance",
                "must be public, must be static, must be callable without arguments (parameterless, or every parameter optional), must return TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO017_ValidUsage()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFactory]
    public static TestClass GetInstance() => new TestClass();

    public int Value;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO017_InternalStructAndNonNinoType()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoFactory]
    internal static TestClass {|#0:GetInstance|}() => new TestClass();

    public int Value;
}

[NinoType]
public struct TestStruct
{
    [NinoFactory]
    public static TestStruct GetInstance() => default;

    public int Value;
}

public class NotANinoType
{
    [NinoFactory]
    public NotANinoType {|#1:GetInstance|}() => this;
}
";

        // The method is checked wherever the attribute is used.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO017").WithLocation(0).WithArguments("GetInstance", "must be public"),
            Verify.Diagnostic("NINO017").WithLocation(1).WithArguments("GetInstance", "must be static")).RunAsync();
    }

    #endregion

    #region Pooling (NINO018-NINO019)

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

    #endregion

    #region Formatter registrations (NINO020)

    [TestMethod]
    public async Task NINO020_InvalidFormatterRegistration()
    {
        var code = @"
using System.Collections.Generic;
using Nino.Core;

[assembly: {|#0:NinoFormatter(typeof(ClassFormatter))|}]
[assembly: {|#1:NinoFormatter(typeof(BoxFormatter<int, UnmanagedFormatter<int>>))|}]
[assembly: {|#2:NinoFormatter(typeof(Uninferable<,>))|}]
[assembly: {|#3:NinoFormatter(typeof(BoxFormatter<,>), typeof(List<>))|}]
[assembly: {|#4:NinoFormatter(typeof(NotAFormatter))|}]
[assembly: NinoFormatter(typeof(BoxFormatter<,>))]
[assembly: NinoFormatter(typeof(AnyListFormatter<,,>), typeof(IList<>))]

public sealed class Box<T> { public T Value; }

public class ClassFormatter : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref int value, ref Reader reader) { }
}

public struct NotAFormatter { }

public struct BoxFormatter<T, TF> : INinoFormatter<Box<T>> where TF : struct, INinoFormatter<T>
{
    public void Serialize(Box<T> value, ref Writer writer) { }
    public void Deserialize(ref Box<T> value, ref Reader reader) { }
}

public struct Uninferable<T, TExtra> : INinoFormatter<Box<T>>
{
    public void Serialize(Box<T> value, ref Writer writer) { }
    public void Deserialize(ref Box<T> value, ref Reader reader) { }
}

public struct AnyListFormatter<TList, T, TF> : INinoFormatter<TList>
    where TList : class, IEnumerable<T> where TF : struct, INinoFormatter<T>
{
    public void Serialize(TList value, ref Writer writer) { }
    public void Deserialize(ref TList value, ref Reader reader) { }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO020").WithLocation(0).WithArguments("ClassFormatter", "it is not a struct"),
            Verify.Diagnostic("NINO020").WithLocation(1).WithArguments(
                "BoxFormatter<int, Nino.Core.UnmanagedFormatter<int>>", "pass the open generic definition (typeof(F<,>))"),
            Verify.Diagnostic("NINO020").WithLocation(2).WithArguments("Uninferable<,>",
                "its type parameter 'TExtra' cannot be inferred from the formatted type"),
            Verify.Diagnostic("NINO020").WithLocation(3).WithArguments("BoxFormatter<,>",
                "targets only apply to a formatter of a bare type parameter (INinoFormatter<T>)"),
            Verify.Diagnostic("NINO020").WithLocation(4).WithArguments("NotAFormatter",
                "it does not implement INinoFormatter<T>")).RunAsync();
    }

    #endregion
}
