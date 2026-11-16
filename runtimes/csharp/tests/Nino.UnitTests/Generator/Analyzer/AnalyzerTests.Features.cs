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

// Features (NINO014-NINO017, NINO024, NINO026).
public partial class AnalyzerTests
{
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
}
