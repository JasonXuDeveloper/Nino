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

// Members (NINO007-NINO013, NINO022, NINO023, NINO028).
public partial class AnalyzerTests
{
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

public class Other : AbstractBase
{
}

public sealed class OtherDerived : Derived
{
}

[NinoType]
public class Box<T>
{
    public T {|#2:Value|};
}

public class ActionBox : Box<Action>
{
}
";

        // One diagnostic per member, by the type declaring it (abstract or not), however many subtypes write it; a
        // closed generic base writes what its definition does not, so each subtype closing it reports.
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO007").WithLocation(0).WithArguments("Callback", "System.Action", "AbstractBase", "", "the member is skipped"),
            Verify.Diagnostic("NINO007").WithLocation(1).WithArguments("Factory", "System.Func<int>", "Derived", "", "the member is skipped"),
            Verify.Diagnostic("NINO007").WithLocation(2).WithArguments("Value", "System.Action", "ActionBox", "", "the member is skipped"))
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
    public void Deserialize(ref Reader reader, ref string value) { }
}

public struct IntFormatter : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref int value) { }
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
    public void Deserialize(ref Reader reader, ref int value) { }
}

public struct NullableIntFormatter : INinoFormatter<int?>
{
    public void Serialize(int? value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref int? value) { }
}

// A class implementing the interface is not accepted: custom formatters are inlined structs.
public sealed class ClassIntFormatter : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref int value) { }
}

public struct GenericFormatter<T> : INinoFormatter<T>
{
    public void Serialize(T value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref T value) { }
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
}
