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

[TestClass]
public class AnalyzerTests
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

    #endregion

    #region Members (NINO007-NINO013, NINO022-NINO023, NINO029)

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
                .WithArguments("UnsupportedDelegate", "System.Action", "TestClassWithDelegate", "", "the member is skipped"),
            Verify.Diagnostic("NINO007").WithLocation(1)
                .WithArguments("UnsupportedTask", "System.Threading.Tasks.Task<int>", "TestClassWithTask", "", "the member is skipped")
        ).RunAsync();
    }

    [TestMethod]
    public async Task NINO007_TypeArgumentWithoutFormatter()
    {
        // The member is written, but Box<Action>'s formatter cannot write its Action: every value throws. Reported
        // nested in collections, arrays and other generics too; not where no member uses the argument (Tag<T>), the
        // argument has a formatter, or the member is ignored.
        var code = @"
using System;
using System.Collections.Generic;
using Nino.Core;

[NinoType]
public class Box<T> { public T Value; }

[NinoType]
public class Tag<T> { public int X; }

[NinoType]
public class Wrapped<T> : Box<T> { }

[NinoType]
public class Holder
{
    public Box<Action> {|#0:Direct|};
    public List<Box<Func<int>>> {|#1:Listed|};
    public Box<Box<Action>> {|#2:Nested|};
    public Box<Action>[] {|#3:Array|};
    public Wrapped<Action> {|#4:Inherited|};
    public Tag<Action> Unused;
    public Box<int> Fine;
    [NinoIgnore] public Box<Action> Ignored;
}
";

        const string Throws = "writing a value of it throws";
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO007").WithLocation(0)
                .WithArguments("Direct", "System.Action", "Holder", ", a type argument of 'Box<System.Action>',", Throws),
            Verify.Diagnostic("NINO007").WithLocation(1)
                .WithArguments("Listed", "System.Func<int>", "Holder", ", a type argument of 'Box<System.Func<int>>',", Throws),
            Verify.Diagnostic("NINO007").WithLocation(2)
                .WithArguments("Nested", "System.Action", "Holder", ", a type argument of 'Box<System.Action>',", Throws),
            Verify.Diagnostic("NINO007").WithLocation(3)
                .WithArguments("Array", "System.Action", "Holder", ", a type argument of 'Box<System.Action>',", Throws),
            Verify.Diagnostic("NINO007").WithLocation(4)
                .WithArguments("Inherited", "System.Action", "Holder", ", a type argument of 'Wrapped<System.Action>',", Throws))
            .RunAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NINO007_TypeArgumentsTheFormatterWrites()
    {
        // Only a type argument the closed type's formatter writes counts, decided by substitution through the
        // serialized members: not one used only by a [NinoIgnore] member, a method or a nested generic that never
        // writes it (Tag<T>), nor anything inside such an argument (Tag<Box<Action>>), nor one a type holding itself
        // over a bigger argument never writes (Expanding<T>). One written through a nested generic, a dictionary, a
        // base type, a subtype of a slot or a growing self-reference is reported.
        var code = @"
using System;
using System.Collections.Generic;
using Nino.Core;

[NinoType]
public class Box<T> { public T Value; }

[NinoType]
public class Tag<T> { public int Id; [NinoIgnore] public T Scratch; public T Make() => default; }

[NinoType]
public class Outer<T> { public Tag<T> Inner; }

[NinoType]
public class Expanding<T> { public int Id; public Expanding<List<T>> Next; }

[NinoType]
public class Growing<T> { public T Value; public Growing<List<T>> Next; }

[NinoType]
public class Nested<T> { public Box<T> Inner; }

[NinoType]
public class Mapped<T> { public Dictionary<string, Box<T>> Map; }

[NinoType]
public class TagBase<T> { public Tag<T> Inner; }

[NinoType]
public class FromTagBase<T> : TagBase<T> { public int Id; }

[NinoType]
public abstract class Shelf<T> { public int Id; }

[NinoType]
public class ListShelf<T> : Shelf<T> { public List<T> Items; }

[NinoType]
public class Holder
{
    public Outer<Action> NestedUnwritten;
    public Tag<Box<Action>> InsideUnwritten;
    public Expanding<Action> NeverWritten;
    public FromTagBase<Action> BaseUnwritten;
    public Nested<Action> {|#0:NestedWritten|};
    public Mapped<Action> {|#1:InDictionary|};
    public Growing<Action> {|#2:GrowingWritten|};
    public Shelf<Action> {|#3:BySubtype|};
}
";

        const string Throws = "writing a value of it throws";
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO007").WithLocation(0).WithArguments("NestedWritten", "System.Action", "Holder",
                ", a type argument of 'Nested<System.Action>',", Throws),
            Verify.Diagnostic("NINO007").WithLocation(1).WithArguments("InDictionary", "System.Action", "Holder",
                ", a type argument of 'Mapped<System.Action>',", Throws),
            Verify.Diagnostic("NINO007").WithLocation(2).WithArguments("GrowingWritten", "System.Action", "Holder",
                ", a type argument of 'Growing<System.Action>',", Throws),
            Verify.Diagnostic("NINO007").WithLocation(3).WithArguments("BySubtype", "System.Action", "Holder",
                ", a type argument of 'Shelf<System.Action>',", Throws)).RunAsync();
    }

    [TestMethod]
    public async Task NINO007_PartsOfArgumentsSubtypesWrite()
    {
        // A part of a slot's argument a subtype writes (TagShelf<U> : Shelf<Tag<U>> in a Shelf<Tag<Action>>) needs a
        // formatter; one no subtype writes (QuietShelf<U> : Shelf<Box<U>>) does not.
        var code = @"
using System;
using Nino.Core;

[NinoType]
public abstract class Shelf<T> { public int Id; }

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class Box<T> { public int Id; }

[NinoType]
public class TagShelf<U> : Shelf<Tag<U>> { public U Value; }

[NinoType]
public class QuietShelf<U> : Shelf<Box<U>> { public int Count; }

[NinoType]
public class Holder
{
    public Shelf<Tag<Action>> {|#0:Written|};
    public Shelf<Box<Action>> NeverWritten;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO007").WithLocation(0).WithArguments("Written", "System.Action", "Holder",
                ", a type argument of 'Shelf<Tag<System.Action>>',", "writing a value of it throws")).RunAsync();
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
            Verify.Diagnostic("NINO007").WithLocation(0).WithArguments("Callback", "System.Action", "Derived", "", "the member is skipped"),
            Verify.Diagnostic("NINO007").WithLocation(1).WithArguments("Factory", "System.Func<int>", "Derived", "", "the member is skipped"))
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
    [NinoMemberFormatter(typeof(StringFormatter))]
    public int {|#0:A|};

    [NinoMemberFormatter(typeof(NotAFormatter))]
    public int {|#1:B|};

    [NinoMemberFormatter(typeof(IntFormatter))]
    public int C;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO008").WithLocation(0).WithArguments("StringFormatter", "int", "A", "TestClass"),
            Verify.Diagnostic("NINO008").WithLocation(1).WithArguments("NotAFormatter", "int", "B", "TestClass")).RunAsync();
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
    [NinoMemberFormatter(typeof(IntFormatter))]
    public int Property { get; set; }

    [NinoMemberFormatter(typeof(NullableIntFormatter))]
    public int {|#0:Nullable|} { get; set; }

    [NinoMemberFormatterAttribute(typeof(IntFormatter))]
    public int? {|#1:NullableMember|};

    [NinoMemberFormatter(typeof(IntFormatter))]
    public long {|#2:WrongType|};

    [NinoMemberFormatter(typeof(ClassIntFormatter))]
    public int {|#3:Class|};

    [NinoMemberFormatter(typeof(GenericFormatter<int>))]
    public int Generic;

    [NinoMemberFormatter(typeof(GenericFormatter<>))]
    public int {|#4:Open|};
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO008").WithLocation(0).WithArguments("NullableIntFormatter", "int", "Nullable", "TestClass"),
            Verify.Diagnostic("NINO008").WithLocation(1).WithArguments("IntFormatter", "int?", "NullableMember", "TestClass"),
            Verify.Diagnostic("NINO008").WithLocation(2).WithArguments("IntFormatter", "long", "WrongType", "TestClass"),
            Verify.Diagnostic("NINO008").WithLocation(3).WithArguments("ClassIntFormatter", "int", "Class", "TestClass"),
            Verify.Diagnostic("NINO008").WithLocation(4).WithArguments("GenericFormatter<>", "int", "Open", "TestClass")).RunAsync();
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
    public async Task NINO009_NestedTypeMayIncludeNonPublicMembers()
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

        // Protected and internal [NinoMember]s are not collected without includeNonPublicMembers either.
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
    public async Task NINO028_NinoMemberOnAMemberThatIsNeverSerialized()
    {
        // NINO028 alone, whatever else the [NinoMember] would be: redundant (NINO010 with autoCollect), non-public
        // (NINO009) or a duplicate index (NINO013). Not for members Nino serializes: settable properties, fields
        // and a get-only auto-property a constructor takes.
        var code = @"
using System;
using Nino.Core;

[NinoType]
public class Collected
{
    [NinoMember(0)] public static int {|#0:Shared|};
    [NinoMember(1)] public const int {|#1:Constant|} = 1;
    [NinoMember(2)] public int {|#2:Sum|} => 1;
    [NinoMember(3)] private static int {|#3:Hidden|};
    public int A { get; set; }
}

[NinoType(autoCollect: false)]
public partial class OptedIn
{
    [NinoMember(0)] public int A;
    [NinoMember(0)] public static int {|#4:Repeats|};
    [NinoMember(1)] public int {|#5:this|}[int i] => i;
    [NinoMember(2)] public int {|#6:GetOnly|} { get; }
    [field: NinoMember(3)] public int {|#7:Backed|} { get; set; }
    [NinoMember(4)] public int Taken { get; }
    [NinoMember(5)] public int Settable { get; private set; }

    public OptedIn(int taken) => Taken = taken;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO028").WithLocation(0).WithArguments("Shared", "Collected", "it is static"),
            Verify.Diagnostic("NINO028").WithLocation(1).WithArguments("Constant", "Collected", "it is a constant"),
            Verify.Diagnostic("NINO028").WithLocation(2)
                .WithArguments("Sum", "Collected", "it has no setter, and no constructor takes it"),
            Verify.Diagnostic("NINO028").WithLocation(3).WithArguments("Hidden", "Collected", "it is static"),
            Verify.Diagnostic("NINO028").WithLocation(4).WithArguments("Repeats", "OptedIn", "it is static"),
            Verify.Diagnostic("NINO028").WithLocation(5).WithArguments("this[]", "OptedIn", "it is an indexer"),
            Verify.Diagnostic("NINO028").WithLocation(6)
                .WithArguments("GetOnly", "OptedIn", "it has no setter, and no constructor takes it"),
            Verify.Diagnostic("NINO028").WithLocation(7).WithArguments("Backed", "OptedIn",
                "it is the backing field of a property or event; mark the property instead")).RunAsync();
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

        // A non-public [NinoMember] in an autoCollect type is not collected without includeNonPublicMembers:
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

[NinoType(includeNonPublicMembers: true)]
public partial class Base
{
    protected int {|#0:Value|};
    public string Name;
}

[NinoType(includeNonPublicMembers: true)]
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
                .WithArguments("Callback", "System.Action", "AllSkipped", "", "the member is skipped"),
            Verify.Diagnostic("NINO023").WithLocation(1).WithArguments("AllSkipped")).RunAsync();
    }

    /// <summary>The arguments of a NINO029 on member <paramref name="member"/> of <paramref name="type"/>.</summary>
    private static DiagnosticResult Nino029(int location, string member, string type, string memberType,
        string vector = null) =>
        Verify.Diagnostic("NINO029").WithLocation(location).WithArguments($"Member '{member}' of NinoType '{type}'",
            memberType, vector == null ? "is a Vector<T>" : $"holds '{vector}'");

    [TestMethod]
    public async Task NINO029_VectorTMembers()
    {
        // Vector<T> wherever Nino would write it: alone, as an element, a type argument a [NinoType] uses, a field at
        // any depth of a plain struct, a settable property. One diagnostic: no NINO023 for a struct of vectors only.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

public struct Raw { public int A; public Vector<int> V; }
public struct Outer { public byte B; public Raw Inner; }

[NinoType]
public class Box<T> { public T Value; }

// T reaches the base's member under the base's own parameter.
[NinoType]
public class ListBox<T> : Box<List<T>> { }

[NinoType]
public class Holder
{
    public Vector<float> {|#0:Alone|};
    public List<Vector<int>> {|#1:InList|};
    public Vector<int>[] {|#2:InArray|};
    public Outer {|#3:Deep|};
    public Box<Vector<int>> {|#4:InBox|};
    public Vector<int>? {|#5:Nullable|};
    public Dictionary<int, (byte, Vector<long>)> {|#6:InTuple|};
    public Vector<float> {|#7:Settable|} { get => default; set { } }
    public ListBox<Vector<short>> {|#10:InBaseOfBox|};
}

[NinoType]
public struct NinoStruct { public int A; public Vector<double> {|#8:V|}; }

[NinoType]
public struct OnlyVectors { public Vector<byte> {|#9:V|}; }
";

        await SetUpAnalyzerTest(code,
            Nino029(0, "Alone", "Holder", "System.Numerics.Vector<float>"),
            Nino029(1, "InList", "Holder", "System.Collections.Generic.List<System.Numerics.Vector<int>>",
                "System.Numerics.Vector<int>"),
            Nino029(2, "InArray", "Holder", "System.Numerics.Vector<int>[]", "System.Numerics.Vector<int>"),
            Nino029(3, "Deep", "Holder", "Outer", "System.Numerics.Vector<int>"),
            Nino029(4, "InBox", "Holder", "Box<System.Numerics.Vector<int>>", "System.Numerics.Vector<int>"),
            Nino029(5, "Nullable", "Holder", "System.Numerics.Vector<int>?", "System.Numerics.Vector<int>"),
            Nino029(6, "InTuple", "Holder",
                "System.Collections.Generic.Dictionary<int, (byte, System.Numerics.Vector<long>)>",
                "System.Numerics.Vector<long>"),
            Nino029(7, "Settable", "Holder", "System.Numerics.Vector<float>"),
            Nino029(8, "V", "NinoStruct", "System.Numerics.Vector<double>"),
            Nino029(9, "V", "OnlyVectors", "System.Numerics.Vector<byte>"),
            Nino029(10, "InBaseOfBox", "Holder", "ListBox<System.Numerics.Vector<short>>",
                "System.Numerics.Vector<short>")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_TopLevelCalls()
    {
        // The calls the generator would register or intercept; IsSupported only asks (it answers false).
        var code = @"
using System;
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

public struct Raw { public int A; public Vector<int> V; }

public static class Calls
{
    public static void Run()
    {
        var bytes = {|#0:NinoSerializer.Serialize(new Vector<int>(1))|};
        {|#1:NinoDeserializer.Deserialize<List<Raw>>(bytes)|};
        {|#2:NinoSerializer.SerializeBoxed(new Raw(), typeof(Raw))|};
        NinoSerializer.IsSupported(typeof(Raw));
        NinoSerializer.Serialize(new System.Runtime.Intrinsics.Vector128<int>());
    }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO029").WithLocation(0).WithArguments("The value of this NinoSerializer.Serialize call",
                "System.Numerics.Vector<int>", "is a Vector<T>"),
            Verify.Diagnostic("NINO029").WithLocation(1).WithArguments(
                "The value of this NinoDeserializer.Deserialize call", "System.Collections.Generic.List<Raw>",
                "holds 'System.Numerics.Vector<int>'"),
            Verify.Diagnostic("NINO029").WithLocation(2).WithArguments(
                "The value of this NinoSerializer.SerializeBoxed call", "Raw", "holds 'System.Numerics.Vector<int>'"))
            .RunAsync();
    }

    [TestMethod]
    public async Task NINO029_InBaseTypesAndCollections()
    {
        // A type that names no vector itself but whose base type or IEnumerable<T> holds one: NINO029 alone, not
        // NINO007 (it has no formatter for the same reason). A struct holding a list of itself is searched to the end.
        var code = @"
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

public class VectorList : List<Vector<int>> { }
public class Derived : VectorList { }
public class VectorMap : Dictionary<int, Vector<float>> { }
public class Bag : IEnumerable<Vector<long>>
{
    public void Add(Vector<long> item) { }
    public IEnumerator<Vector<long>> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
}
public struct Chain { public int A; public List<Chain> Rest; public Vector<int> V; }

[NinoType]
public class Holder
{
    public VectorList {|#0:InBase|};
    public Derived {|#1:InBaseOfBase|};
    public VectorMap {|#2:InMapBase|};
    public Bag {|#3:InEnumerable|};
    public List<VectorList> {|#4:Nested|};
    public Chain {|#5:SelfHolding|};
}

public static class Calls
{
    public static void Run() => {|#6:NinoSerializer.Serialize(new VectorList())|};
}
";

        await SetUpAnalyzerTest(code,
            Nino029(0, "InBase", "Holder", "VectorList", "System.Numerics.Vector<int>"),
            Nino029(1, "InBaseOfBase", "Holder", "Derived", "System.Numerics.Vector<int>"),
            Nino029(2, "InMapBase", "Holder", "VectorMap", "System.Numerics.Vector<float>"),
            Nino029(3, "InEnumerable", "Holder", "Bag", "System.Numerics.Vector<long>"),
            Nino029(4, "Nested", "Holder", "System.Collections.Generic.List<VectorList>", "System.Numerics.Vector<int>"),
            Nino029(5, "SelfHolding", "Holder", "Chain", "System.Numerics.Vector<int>"),
            Verify.Diagnostic("NINO029").WithLocation(6).WithArguments("The value of this NinoSerializer.Serialize call",
                "VectorList", "holds 'System.Numerics.Vector<int>'")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForBaseTypesWithoutVectors()
    {
        // Base types and collections without a Vector<T>, types that hold themselves (searched once, no stack
        // overflow), and a struct of itself without a formatter, which stays NINO007's.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

public class IntList : List<int> { }
public class Vector4Map : Dictionary<string, Vector4> { }
public class Tree : List<Tree> { public int Value; }
public struct Node { public int A; public List<Node> Children; }

[NinoType]
public class Holder
{
    public IntList Ints;
    public Vector4Map Map;
    public Tree Root;
    public Node {|#0:Graph|};
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO007").WithLocation(0)
                .WithArguments("Graph", "Node", "Holder", "", "the member is skipped")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForVectorsNinoNeverWrites()
    {
        // The documented workaround: fixed lanes stored, the Vector<T> exposed through [NinoIgnore] members. Neither
        // members Nino does not serialize (get-only, ref structs, [NinoIgnore]), nor fixed-size vectors, nor a type
        // argument no serialized member uses, nor a [NinoMemberFormatter] that writes the member its own way.
        var code = @"
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nino.Core;

[NinoType]
public class Particle
{
    public float[] Lanes = new float[16];

    [NinoIgnore]
    public Span<Vector<float>> Vectors => MemoryMarshal.Cast<float, Vector<float>>(Lanes.AsSpan());

    [NinoIgnore]
    public Vector<float> Head { get => Vectors[0]; set => Vectors[0] = value; }

    public Vector<float> First => new Vector<float>(Lanes);

    [NinoIgnore]
    public Vector<int> Scratch;
}

[NinoType]
public struct Packed
{
    public Vector128<float> Low;
    public Vector256<float> High;

    [NinoIgnore]
    public Vector<float> View { get => default; set { } }
}

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class UsesTag { public Tag<Vector<int>> Tagged; }

public struct VectorFormatter : INinoFormatter<Vector<int>>
{
    public void Serialize(Vector<int> value, ref Writer writer) { }
    public void Deserialize(ref Vector<int> value, ref Reader reader) { }
}

[NinoType]
public class Custom
{
    [NinoMemberFormatter(typeof(VectorFormatter))]
    public Vector<int> Mine;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForTypeArgumentsNeverWritten()
    {
        // A generic [NinoType] closed over Vector<T> where its formatter never writes the argument, decided by
        // substitution through the serialized members: T only in a [NinoIgnore] member, a method, a constraint or a
        // computed property; T only in a nested generic that does not write it (Outer<T> holding Tag<T>, also two
        // levels down); T reaching a base type that does not write it; a type holding itself over a bigger argument
        // that never writes it (Expanding<T>, whose closed member types grow without end).
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public class IgnoredOnly<T> { public int Id; [NinoIgnore] public T Scratch; [NinoIgnore] public List<T> Cache; }

[NinoType]
public class MethodOnly<T> where T : struct
{
    public int Id;
    public T Make() => default;
    public T Computed => default;
}

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class Outer<T> { public Tag<T> Inner; public List<Tag<T>> Many; }

[NinoType]
public class TwoLevels<T> { public Outer<List<T>> Inner; }

[NinoType]
public class TagBase<T> { public Tag<T> Inner; }

[NinoType]
public sealed class FromTagBase<T> : TagBase<T> { public int Id; }

[NinoType]
public class Expanding<T> { public int Id; public Expanding<List<T>> Next; }

[NinoType]
public class Holder
{
    public IgnoredOnly<Vector<int>> Ignored;
    public MethodOnly<Vector<int>> Methods;
    public Outer<Vector<int>> Nested;
    public TwoLevels<Vector<int>> Deeper;
    public FromTagBase<Vector<int>> ThroughBase;
    public Expanding<Vector<int>> Expanding;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NINO029_TypeArgumentsWritten()
    {
        // The argument a generic [NinoType]'s formatter writes holds a Vector<T>: directly, through List<T>, a
        // nested generic that writes it, a dictionary value, a base type, the elements of a collection class, a
        // generic subtype of a slot, and a type holding itself over a bigger argument (Growing<T>: decided on the
        // definitions, so it ends at once). A ring of 40 generic types over growing arguments ends fast too.
        const int count = 40;
        var ring = string.Join("\n", Enumerable.Range(0, count).Select(i => i == count - 1
            ? $"[NinoType] public class G{i}<T> {{ public T Value; public G0<T[]> Next; }}"
            : $"[NinoType] public class G{i}<T> {{ public int Id; public G{i + 1}<List<T>> Next; }}"));
        var code = $@"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public class Box<T> {{ public T Value; }}

[NinoType]
public class Listed<T> {{ public List<T> Items; }}

[NinoType]
public class Nested<T> {{ public int Id; public Box<T[]> Inner; }}

[NinoType]
public class Mapped<T> {{ public Dictionary<string, T> Map; }}

[NinoType]
public sealed class FromBase<T> : Box<T> {{ }}

[NinoType]
public class Bag<T> : List<T> {{ }}

[NinoType]
public class Growing<T> {{ public T Value; public Growing<List<T>> Next; }}

[NinoType]
public abstract class Shelf<T> {{ public int Id; }}

[NinoType]
public class ListShelf<T> : Shelf<T> {{ public List<T> Items; }}

{ring}

[NinoType]
public class Holder
{{
    public Box<Vector<int>> {{|#0:Direct|}};
    public Listed<Vector<int>> {{|#1:InList|}};
    public Nested<Vector<int>> {{|#2:InNested|}};
    public Mapped<Vector<int>> {{|#3:InDictionary|}};
    public FromBase<Vector<int>> {{|#4:InBase|}};
    public Bag<Vector<int>> {{|#5:InElements|}};
    public Growing<Vector<int>> {{|#6:InGrowing|}};
    public Shelf<Vector<int>> {{|#7:InSubtype|}};
    public G0<Vector<int>> {{|#8:InRing|}};
}}
";

        const string Vector = "System.Numerics.Vector<int>";
        await SetUpAnalyzerTest(code,
            Nino029(0, "Direct", "Holder", "Box<System.Numerics.Vector<int>>", Vector),
            Nino029(1, "InList", "Holder", "Listed<System.Numerics.Vector<int>>", Vector),
            Nino029(2, "InNested", "Holder", "Nested<System.Numerics.Vector<int>>", Vector),
            Nino029(3, "InDictionary", "Holder", "Mapped<System.Numerics.Vector<int>>", Vector),
            Nino029(4, "InBase", "Holder", "FromBase<System.Numerics.Vector<int>>", Vector),
            Nino029(5, "InElements", "Holder", "Bag<System.Numerics.Vector<int>>", Vector),
            Nino029(6, "InGrowing", "Holder", "Growing<System.Numerics.Vector<int>>", Vector),
            Nino029(7, "InSubtype", "Holder", "Shelf<System.Numerics.Vector<int>>", Vector),
            Nino029(8, "InRing", "Holder", "G0<System.Numerics.Vector<int>>", Vector)).RunAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NINO029_PartsOfArgumentsSubtypesWrite()
    {
        // A slot holds the generic subtypes whose base matches its argument, and they write what they write of it,
        // to any depth: TagShelf<U> : Shelf<Tag<U>> writes the X of a Shelf<Tag<X>> though Tag<T> writes nothing of
        // its own; through a base between them (Deep<U> : Middle<U> : Shelf<Tag<U[]>>), an interface, a type nested
        // in a generic one, a generic that passes its argument to the slot (Outer<T> { Shelf<T> S; }), and a subtype
        // holding its base over its own parameter, which matches Tag<Tag<...>> at every depth (every type inside the
        // argument then counts).
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public abstract class Shelf<T> { public int Id; }

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class TagShelf<U> : Shelf<Tag<U>> { public U Value; }

[NinoType]
public abstract class Middle<V> : Shelf<Tag<V[]>> { }

[NinoType]
public class Deep<U> : Middle<U> { public List<U> Items; }

[NinoType]
public interface ICarrier<T> { }

[NinoType]
public class Carrier<U> : ICarrier<Dictionary<int, Tag<U>>> { public U Value; }

public class Container<T>
{
    [NinoType]
    public class Inner : Shelf<KeyValuePair<string, T>> { public T Value; }
}

[NinoType]
public class Outer<T> { public Shelf<T> Inner; }

[NinoType]
public abstract class Rack<T> { public int Id; }

[NinoType]
public class SelfRack<U> : Rack<Tag<U>> { public U Value; public Rack<U> Next; }

[NinoType]
public class Holder
{
    public Shelf<Tag<Vector<int>>> {|#0:ThroughTag|};
    public Shelf<Tag<Vector<int>[]>> {|#1:ThroughMiddle|};
    public ICarrier<Dictionary<int, Tag<Vector<int>>>> {|#2:ThroughInterface|};
    public Shelf<KeyValuePair<string, Vector<int>>> {|#3:ThroughNested|};
    public Outer<Tag<Vector<int>>> {|#4:ThroughOuter|};
    public Rack<Tag<Tag<Tag<Tag<Tag<Tag<Tag<Tag<Vector<int>>>>>>>>>> {|#5:AtAnyDepth|};
}
";

        const string Vector = "System.Numerics.Vector<int>";
        await SetUpAnalyzerTest(code,
            Nino029(0, "ThroughTag", "Holder", "Shelf<Tag<System.Numerics.Vector<int>>>", Vector),
            Nino029(1, "ThroughMiddle", "Holder", "Shelf<Tag<System.Numerics.Vector<int>[]>>", Vector),
            Nino029(2, "ThroughInterface", "Holder",
                "ICarrier<System.Collections.Generic.Dictionary<int, Tag<System.Numerics.Vector<int>>>>", Vector),
            Nino029(3, "ThroughNested", "Holder",
                "Shelf<System.Collections.Generic.KeyValuePair<string, System.Numerics.Vector<int>>>", Vector),
            Nino029(4, "ThroughOuter", "Holder", "Outer<Tag<System.Numerics.Vector<int>>>", Vector),
            Nino029(5, "AtAnyDepth", "Holder",
                "Rack<Tag<Tag<Tag<Tag<Tag<Tag<Tag<Tag<System.Numerics.Vector<int>>>>>>>>>>", Vector)).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForPartsSubtypesNeverWrite()
    {
        // The parts of a slot's argument no subtype writes: a subtype that writes nothing of its parameter, an
        // argument of another shape than every subtype's base (no such subtype fits the slot), a subtype's
        // parameter in a position of its base's argument that is matched but not written (Pair<U, int>'s int),
        // and a sealed class, which has no subtypes.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class Box<T> { public T Value; }

[NinoType]
public abstract class Rack<T> { public int Id; }

[NinoType]
public class TagRack<U> : Rack<Tag<U>> { public U Value; }

[NinoType]
public class QuietRack<U> : Rack<Box<U>> { public int Count; }

[NinoType]
public abstract class Pairs<T> { public int Id; }

[NinoType]
public class FirstOnly<U, W> : Pairs<KeyValuePair<U, W>> { public U First; }

[NinoType]
public sealed class Sealed<T> { public int Id; }

[NinoType]
public class Holder
{
    public Rack<Box<Vector<int>>> Quiet;
    public Rack<List<Vector<int>>> OtherShape;
    public Rack<Tag<int>> NoVector;
    public Pairs<KeyValuePair<int, Vector<int>>> SecondNeverWritten;
    public Sealed<Tag<Vector<int>>> NoSubtypes;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_OnlyArgumentsASubtypeCanBe()
    {
        // A slot holds a subtype only when its argument can be what the subtype gives its base: a parameter the base
        // names twice (Same<U> : Shelf<KeyValuePair<U, U>>, TwinOf<U> : Twin<U, U>) stands for one type in every
        // place, and a fixed type (Fixed<U> : Shelf<KeyValuePair<int, U[]>>) must be that type. Arguments no
        // subtype can be are not searched.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public abstract class Shelf<T> { public int Id; }

[NinoType]
public class Same<U> : Shelf<KeyValuePair<U, U>> { public U Value; }

[NinoType]
public class Fixed<U> : Shelf<KeyValuePair<int, U[]>> { public U[] Values; }

[NinoType]
public abstract class Twin<A, B> { public int Id; }

[NinoType]
public class TwinOf<U> : Twin<U, U> { public U Value; }

[NinoType]
public class Holder
{
    public Shelf<KeyValuePair<Vector<int>, Vector<int>>> {|#0:SameFits|};
    public Shelf<KeyValuePair<int, Vector<int>[]>> {|#1:FixedFits|};
    public Twin<Vector<int>, Vector<int>> {|#2:TwinFits|};
    public Shelf<KeyValuePair<long, Vector<int>[]>> NeitherFits;
    public Shelf<KeyValuePair<int, Vector<int>>> NotTheSame;
    public Twin<int, Vector<int>> TwinNeverHolds;
}
";

        const string Vector = "System.Numerics.Vector<int>";
        await SetUpAnalyzerTest(code,
            Nino029(0, "SameFits", "Holder",
                "Shelf<System.Collections.Generic.KeyValuePair<System.Numerics.Vector<int>, System.Numerics.Vector<int>>>", Vector),
            Nino029(1, "FixedFits", "Holder",
                "Shelf<System.Collections.Generic.KeyValuePair<int, System.Numerics.Vector<int>[]>>", Vector),
            Nino029(2, "TwinFits", "Holder", "Twin<System.Numerics.Vector<int>, System.Numerics.Vector<int>>", Vector))
            .RunAsync();
    }

    [TestMethod]
    public void NINO029_AcrossAssemblies()
    {
        // A referenced class's private members are not in its symbols (a compilation imports only public and
        // protected members of referenced classes): its published layout names them, so Hidden<Vector<int>> is
        // reported and Quiet<Vector<int>>, whose private T is not serialized, is not. A subtype in another referenced
        // assembly that references the base's writes the part of the argument it matches.
        var contracts = CornerCompiler.Compile(@"
using Nino.Core;

namespace Contracts
{
    [NinoType] public abstract class Shelf<T> { public int Id; }
    [NinoType] public class Tag<T> { public int Id; }
    [NinoType(includeNonPublicMembers: true)] public partial class Hidden<T> { public int Id; private T _value; }
    [NinoType] public class Quiet<T> { public int Id; [NinoIgnore] private T _scratch; }
    [NinoType] public class Visible<T> { public T Value; }
}
", load: false);
        Assert.AreEqual("", contracts.ErrorText);
        var shelves = CornerCompiler.CompileAgainst(@"
using Nino.Core;
using Contracts;

namespace Shelves
{
    [NinoType] public class TagShelf<U> : Shelf<Tag<U>> { public U Value; }
}
", contracts);
        Assert.AreEqual("", shelves.ErrorText);
        var app = CornerCompiler.CompileAgainst(@"
using System.Numerics;
using Nino.Core;
using Contracts;

namespace App
{
    [NinoType]
    public class Holder
    {
        public Shelf<Tag<Vector<int>>> InSubtype;
        public Hidden<Vector<int>> InPrivate;
        public Visible<Vector<int>> InPublic;
        public Quiet<Vector<int>> NotWritten;
        public Tag<Vector<int>> NotWrittenEither;
    }
}
", contracts, shelves);
        var reported = app.AnalyzerDiagnostics.Where(d => d.Id == "NINO029")
            .Select(d => d.GetMessage().Split(' ')[1]).OrderBy(m => m).ToArray();
        CollectionAssert.AreEqual(new[] { "'InPrivate'", "'InPublic'", "'InSubtype'" }, reported,
            string.Join("\n", app.AnalyzerDiagnostics));
    }

    /// <summary>A NINO029 on a formatter or a call used by hand: its value <paramref name="value"/>.</summary>
    private static DiagnosticResult Nino029ByHand(int location, string what, string value, string vector = null) =>
        Verify.Diagnostic("NINO029").WithLocation(location).WithArguments(what, value,
            vector == null ? "is a Vector<T>" : $"holds '{vector}'");

    [TestMethod]
    public async Task NINO029_FormattersMadeByHand()
    {
        // Nino.Core's formatters and the Writer / Reader / NinoDirect generic methods used by hand refuse a value that
        // holds Vector<T> at run time: constructed, defaulted, inferred or explicit, nested in a collection formatter,
        // or named by [NinoMemberFormatter].
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;
using Nino.Core.CompilerServices;

public struct Raw { public int A; public Vector<int> V; }

[NinoType]
public class Holder
{
    [NinoMemberFormatter(typeof(UnmanagedFormatter<Vector<int>>))]
    public Vector<int> {|#0:Lanes|};
}

public static class ByHand
{
    public static void Run(ref Writer writer, ref Reader reader, Vector<int> v, Raw raw)
    {
        {|#1:new UnmanagedFormatter<Vector<int>>()|}.Serialize(v, ref writer);
        Take({|#2:default(UnmanagedFormatter<Raw>)|});
        {|#3:default(ArrayFormatter<Vector<int>, UnmanagedFormatter<Vector<int>>>)|}.Serialize(null, ref writer);
        Take({|#4:new UnmanagedListFormatter<List<Raw>, Raw>()|});
        {|#5:writer.Write(v)|};
        {|#6:writer.Write(new[] { raw })|};
        {|#7:reader.Read(out Vector<long> back)|};
        {|#8:reader.Read<Raw>()|};
        {|#9:NinoDirect.Serialize<Vector<int>, UnmanagedFormatter<Vector<int>>>(v)|};
    }

    private static void Take<T>(T formatter) { }
}
";

        await SetUpAnalyzerTest(code,
            Nino029(0, "Lanes", "Holder", "System.Numerics.Vector<int>"),
            Nino029ByHand(1, "The value of this UnmanagedFormatter<T>", "System.Numerics.Vector<int>"),
            Nino029ByHand(2, "The value of this UnmanagedFormatter<T>", "Raw", "System.Numerics.Vector<int>"),
            Nino029ByHand(3, "The value of this ArrayFormatter<T, TFormatter>", "System.Numerics.Vector<int>"),
            Nino029ByHand(4, "The value of this UnmanagedListFormatter<TList, T>", "System.Collections.Generic.List<Raw>",
                "System.Numerics.Vector<int>"),
            Nino029ByHand(5, "The value of this Writer.Write call", "System.Numerics.Vector<int>"),
            Nino029ByHand(6, "The value of this Writer.Write call", "Raw", "System.Numerics.Vector<int>"),
            Nino029ByHand(7, "The value of this Reader.Read call", "System.Numerics.Vector<long>"),
            Nino029ByHand(8, "The value of this Reader.Read call", "Raw", "System.Numerics.Vector<int>"),
            Nino029ByHand(9, "The value of this NinoDirect.Serialize call", "System.Numerics.Vector<int>")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_FormattersHeldInFieldsPropertiesAndLocals()
    {
        // A formatter struct is ready to use without a constructor call: a field, property or local of one is reported
        // at its declaration, once, whether or not a new or default fills it (that value is not reported again).
        var code = @"
using System.Numerics;
using Nino.Core;

public struct Raw { public int A; public Vector<int> V; }

[NinoType]
public class Stored
{
    [NinoIgnore] public UnmanagedFormatter<Vector<int>> {|#7:Ignored|};
    public UnmanagedFormatter<Vector<int>> {|#8:Written|};
}

public class Cache
{
    private static readonly UnmanagedFormatter<Vector<int>> {|#0:Lanes|};
    private UnmanagedFormatter<Raw> {|#1:Raws|} = new UnmanagedFormatter<Raw>();
    public static UnmanagedArrayFormatter<Vector<long>> {|#2:Arrays|} { get; } = default;
    private ArrayFormatter<Vector<int>, UnmanagedFormatter<Vector<int>>> {|#3:Nested|};

    public Cache() => Raws = default;

    public void Run(ref Writer writer, Vector<int> v)
    {
        Lanes.Serialize(v, ref writer);
        UnmanagedFormatter<Vector<short>> {|#4:local|};
        local.Serialize(default, ref writer);
        var {|#5:made|} = new UnmanagedFormatter<Vector<byte>>();
        made = new UnmanagedFormatter<Vector<byte>>();
        {|#6:new UnmanagedFormatter<Vector<byte>>()|}.Serialize(default, ref writer);
    }
}
";

        await SetUpAnalyzerTest(code,
            Nino029ByHand(0, "The UnmanagedFormatter<T> in 'Cache.Lanes'", "System.Numerics.Vector<int>"),
            Nino029ByHand(1, "The UnmanagedFormatter<T> in 'Cache.Raws'", "Raw", "System.Numerics.Vector<int>"),
            Nino029ByHand(2, "The UnmanagedArrayFormatter<T> in 'Cache.Arrays'", "System.Numerics.Vector<long>"),
            Nino029ByHand(3, "The ArrayFormatter<T, TFormatter> in 'Cache.Nested'", "System.Numerics.Vector<int>"),
            Nino029ByHand(4, "The UnmanagedFormatter<T> in 'local'", "System.Numerics.Vector<short>"),
            Nino029ByHand(5, "The UnmanagedFormatter<T> in 'made'", "System.Numerics.Vector<byte>"),
            Nino029ByHand(6, "The value of this UnmanagedFormatter<T>", "System.Numerics.Vector<byte>"),
            Nino029ByHand(7, "The UnmanagedFormatter<T> in 'Stored.Ignored'", "System.Numerics.Vector<int>"),
            // A serialized member: the member rule's diagnostic alone.
            Nino029(8, "Written", "Stored", "Nino.Core.UnmanagedFormatter<System.Numerics.Vector<int>>",
                "System.Numerics.Vector<int>")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForFormattersHeldOverOtherTypes()
    {
        // Fields, properties and locals of Nino.Core formatters over fixed-size vectors, of the user's own Vector<T>
        // formatter and of a collection formatter over it, and of a generic formatter over a type parameter.
        var code = @"
using System.Numerics;
using System.Runtime.Intrinsics;
using Nino.Core;

public struct VectorFormatter : INinoFormatter<Vector<int>>
{
    public void Serialize(Vector<int> value, ref Writer writer) { }
    public void Deserialize(ref Vector<int> value, ref Reader reader) { }
}

public class Cache<T> where T : unmanaged
{
    private static readonly UnmanagedFormatter<Vector128<int>> Fixed;
    private VectorFormatter Own;
    public ArrayFormatter<Vector<int>, VectorFormatter> OverOwn { get; set; }
    private UnmanagedFormatter<T> Open;
    private UnmanagedFormatter<Vector4> Four = new UnmanagedFormatter<Vector4>();

    public void Run(ref Writer writer)
    {
        UnmanagedFormatter<long> local;
        local.Serialize(1, ref writer);
        Vector<int> zero = default;
        Own.Serialize(zero, ref writer);
    }
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForOtherFormattersByHand()
    {
        // Fixed-size vectors and Vector4 through the same calls, a collection formatter over the user's own
        // Vector<T> formatter (which writes the lanes its own way), and unrelated generic types and methods that take
        // a Vector<T>.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Intrinsics;
using Nino.Core;
using Nino.Core.CompilerServices;

public struct VectorFormatter : INinoFormatter<Vector<int>>
{
    public void Serialize(Vector<int> value, ref Writer writer) { }
    public void Deserialize(ref Vector<int> value, ref Reader reader) { }
}

public struct Box<T> { public T Value; }

public static class Unrelated
{
    public static void Take<T>(T value) { }
}

public static class ByHand
{
    public static void Run(ref Writer writer, ref Reader reader, Vector<int> v)
    {
        new UnmanagedFormatter<Vector128<int>>().Serialize(Vector128<int>.Zero, ref writer);
        writer.Write(Vector256<float>.Zero);
        writer.Write(Vector4.One);
        reader.Read(out Vector128<byte> fixedLanes);
        default(ArrayFormatter<Vector<int>, VectorFormatter>).Serialize(null, ref writer);
        new VectorFormatter().Serialize(v, ref writer);
        NinoDirect.Serialize<Vector<int>, VectorFormatter>(v);
        var box = new Box<Vector<int>> { Value = v };
        Unrelated.Take(v);
        var list = new List<Vector<int>>();
        Vector<int> zero = default;
    }
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NINO029_TypesHoldingEachOtherAreSearchedOnce()
    {
        // A ring of collections, each derived from a list of the next: every class was searched again on each path to
        // it (its list's type argument, the list's IEnumerable<T>, its own IEnumerable<T>), three times more work per
        // class; 18 took two minutes, 40 would never end.
        const int count = 40;
        var ring = string.Join("\n", Enumerable.Range(0, count)
            .Select(i => $"public class C{i} : List<C{(i + 1) % count}> {{ }}"));
        var code = $@"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

{ring}
public class Last : List<C0> {{ public Vector<int> NotWritten; }}
public class Lanes : List<C{count / 2}>, IEnumerable<Vector<int>>
{{
    IEnumerator<Vector<int>> IEnumerable<Vector<int>>.GetEnumerator() => null;
}}

[NinoType]
public class Holder
{{
    public C0 Ring;
    public List<C7> Inside;
    public Last Fields;
    public Lanes {{|#0:Mixed|}};
}}
";
        await SetUpAnalyzerTest(code, Nino029(0, "Mixed", "Holder", "Lanes", "System.Numerics.Vector<int>")).RunAsync();
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
    public async Task NINO027_GenericTypeFormerName()
    {
        // Every instantiation of a generic type takes its id from the one former name: Box<int> and Box<string>
        // collide once both are written with their type id.
        var code = @"
using Nino.Core;

[NinoType] public interface IShape { }

[NinoType, {|#0:NinoFormerName(""Old.Box"")|}]
public class Box<T> { public T Value; }

[NinoType, {|#1:NinoFormerName(""Old.Leaf"")|}]
public sealed class Leaf<T> : IShape { public T Value; }

[NinoType, {|#2:NinoFormerName(""Old.Shape"")|}]
public interface IGenericShape<T> { }

public class Outer<T>
{
    [NinoType, {|#3:NinoFormerName(""Old.Inner"")|}]
    public abstract class Inner { public int A; }
}

// Never written with a type id: no collision.
[NinoType, NinoFormerName(""Old.Sealed"")]
public sealed class SealedBox<T> { public T Value; }

[NinoType, NinoFormerName(""Old.Pair"")]
public struct Pair<T> { public T Value; }

[NinoType, NinoFormerName(""Old.Plain"")]
public class Plain { public int A; }
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO027").WithLocation(0).WithArguments("Box<T>", "Old.Box"),
            Verify.Diagnostic("NINO027").WithLocation(1).WithArguments("Leaf<T>", "Old.Leaf"),
            Verify.Diagnostic("NINO027").WithLocation(2).WithArguments("IGenericShape<T>", "Old.Shape"),
            Verify.Diagnostic("NINO027").WithLocation(3).WithArguments("Outer<T>.Inner", "Old.Inner")).RunAsync();
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

    /// <summary>
    /// The release tracking files (AnalyzerReleases.*.md) list every rule with its severity and its title, as the
    /// descriptors declare them.
    /// </summary>
    [TestMethod]
    public void ReleaseTrackingMatchesTheDescriptors()
    {
        string dir = RepositoryFiles.Of(System.IO.Path.Combine("src", "Nino.Generator"));
        var listed = new System.Collections.Generic.Dictionary<string, (string Severity, string Title)>();
        foreach (string file in new[] { "AnalyzerReleases.Shipped.md", "AnalyzerReleases.Unshipped.md" })
        {
            foreach (string line in System.IO.File.ReadAllLines(System.IO.Path.Combine(dir, file)))
            {
                var cells = line.Split('|');
                if (cells.Length == 4 && cells[0].Trim().StartsWith("NINO", System.StringComparison.Ordinal))
                    listed.Add(cells[0].Trim(), (cells[2].Trim(), cells[3].Trim()));
            }
        }

        var descriptors = new Generator.Analysis.NinoAnalyzer().SupportedDiagnostics;
        foreach (var d in descriptors)
        {
            Assert.IsTrue(listed.TryGetValue(d.Id, out var entry), $"{d.Id} is not listed");
            Assert.AreEqual(d.DefaultSeverity.ToString(), entry.Severity, d.Id);
            Assert.AreEqual(d.Title.ToString(), entry.Title, d.Id);
        }
    }
}
