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

// [NinoUtf8] (NINO030, NINO031).
public partial class AnalyzerTests
{
    [TestMethod]
    public async Task NINO030_Utf8WithoutEffect()
    {
        var code = @"
using System.Collections.Generic;
using Nino.Core;

[NinoType]
public class TestClass
{
    [{|#0:NinoUtf8|}] public int A;
    // A [NinoType] writes its own strings: the attribute does not reach them.
    [{|#1:NinoUtf8|}] public Inner B;
    [{|#2:NinoUtf8|}] [NinoMemberFormatter(typeof(Latin))] public string C;
    [{|#3:Nino.Core.NinoUtf8Attribute|}] public Dictionary<int, long> D;
}

[NinoType]
public class Inner
{
    public string S;
}

public struct Latin : INinoFormatter<string>
{
    public void Serialize(string value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref string value) { }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO030").WithLocation(0).WithArguments("A", "TestClass",
                "its type 'int' holds no string"),
            Verify.Diagnostic("NINO030").WithLocation(1).WithArguments("B", "TestClass",
                "it does not reach the strings a [NinoType], object or type parameter in its type 'Inner' writes"),
            Verify.Diagnostic("NINO030").WithLocation(2).WithArguments("C", "TestClass",
                "its [NinoMemberFormatter] writes it"),
            Verify.Diagnostic("NINO030").WithLocation(3).WithArguments("D", "TestClass",
                "its type 'System.Collections.Generic.Dictionary<int, long>' holds no string")).RunAsync();
    }

    [TestMethod]
    public async Task NINO030_NotWhereTheStringsAreWritten()
    {
        // Strings anywhere the built-in formatters write them: alone, in collections, dictionaries, tuples, arrays.
        var code = @"
using System.Collections.Generic;
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoUtf8] public string A;
    [NinoUtf8] public List<string> B;
    [NinoUtf8] public Dictionary<int, string[]> C;
    [NinoUtf8] public (int, string) D;
    [NinoUtf8] public string[][] E;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO030_NotReachingInside()
    {
        // A [NinoType], object or type parameter writes its strings with its own formatter, whatever the member says.
        var code = @"
using System.Collections.Generic;
using Nino.Core;

[NinoType]
public class TestClass
{
    [{|#0:NinoUtf8|}] public Box<string> A;
    [{|#1:NinoUtf8|}] public object B;
    [{|#2:NinoUtf8|}] public List<Inner> C;
    [{|#3:NinoUtf8|}] public char[] D;
}

[NinoType]
public class Box<T>
{
    [{|#4:NinoUtf8|}] public T Value;
}

[NinoType]
public class Inner
{
    [NinoUtf8] public string S;
}
";

        static string Inside(string type) =>
            $"it does not reach the strings a [NinoType], object or type parameter in its type '{type}' writes";
        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO030").WithLocation(0).WithArguments("A", "TestClass", Inside("Box<string>")),
            Verify.Diagnostic("NINO030").WithLocation(1).WithArguments("B", "TestClass", Inside("object")),
            Verify.Diagnostic("NINO030").WithLocation(2).WithArguments("C", "TestClass",
                Inside("System.Collections.Generic.List<Inner>")),
            Verify.Diagnostic("NINO030").WithLocation(3).WithArguments("D", "TestClass", "its type 'char[]' holds no string"),
            Verify.Diagnostic("NINO030").WithLocation(4).WithArguments("Value", "Box<T>", Inside("T"))).RunAsync();
    }

    [TestMethod]
    public async Task NINO030_MembersNeverSerialized()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [{|#0:NinoUtf8|}, NinoIgnore] public string A;
    [{|#1:NinoUtf8|}] private string B;
    [{|#2:NinoUtf8|}] public string C => ""c"";
    [{|#3:NinoUtf8|}] public static string D;
    [NinoUtf8] public string Written;
}

[NinoType(false)]
public class OptIn
{
    [{|#4:NinoUtf8|}] public string A;
    [NinoMember(0), NinoUtf8] public string Written;
    // Another finding about the member comes first: fixing it may make the member serialized (NINO009).
    [NinoMember(1), NinoUtf8] private string {|#5:_hidden|};
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO030").WithLocation(0).WithArguments("A", "TestClass", "it is [NinoIgnore]"),
            Verify.Diagnostic("NINO030").WithLocation(1).WithArguments("B", "TestClass",
                "it is not public, and the type does not set includeNonPublicMembers"),
            Verify.Diagnostic("NINO030").WithLocation(2).WithArguments("C", "TestClass",
                "it has no setter, and no constructor takes it"),
            Verify.Diagnostic("NINO030").WithLocation(3).WithArguments("D", "TestClass", "it is static"),
            Verify.Diagnostic("NINO030").WithLocation(4).WithArguments("A", "OptIn",
                "it has no [NinoMember], and the type collects only those (autoCollect: false)"),
            Verify.Diagnostic("NINO009").WithLocation(5).WithArguments("_hidden", "OptIn")).RunAsync();
    }

    [TestMethod]
    public async Task NINO030_NINO031_OncePerMember()
    {
        // Reported by the type declaring the member (an interface or abstract class too), however many types write it;
        // an interface's [NinoUtf8] is NINO033's.
        var code = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    [{|#0:NinoUtf8|}] int Sides { get; set; }
    // The implementing type's Name is written, with its attributes: one here changes nothing.
    [{|#4:NinoUtf8|}] string Name { get; set; }
}

[NinoType]
public abstract class Base
{
    [{|#1:NinoUtf8|}] public int A;
    [{|#2:NinoMemberFormatter(typeof(Utf8StringFormatter))|}] public string S;
}

public class Derived : Base
{
}

public sealed class MoreDerived : Derived, IShape
{
    public int Sides { get; set; }
    public string Name { get; set; }
}

[NinoType]
public struct Point
{
    [{|#3:NinoUtf8|}] public int X;
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO033").WithLocation(0).WithArguments("NinoUtf8", "Sides", "IShape"),
            Verify.Diagnostic("NINO033").WithLocation(4).WithArguments("NinoUtf8", "Name", "IShape"),
            Verify.Diagnostic("NINO030").WithLocation(1).WithArguments("A", "Base", "its type 'int' holds no string"),
            Verify.Diagnostic("NINO031").WithLocation(2).WithArguments("S", "Base"),
            Verify.Diagnostic("NINO030").WithLocation(3).WithArguments("X", "Point", "its type 'int' holds no string"))
            .RunAsync();
    }

    [TestMethod]
    public async Task NINO031_Utf8FormatterByHand()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [{|#0:NinoMemberFormatter(typeof(Utf8StringFormatter))|}] public string A;
    // With [NinoUtf8] already there, only the formatter is reported (removing it is the fix).
    [NinoUtf8] [{|#1:Nino.Core.NinoMemberFormatter(typeof(Nino.Core.Utf8StringFormatter))|}] public string B { get; set; }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO031").WithLocation(0).WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO031").WithLocation(1).WithArguments("B", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task NINO031_NotOnOtherMembers()
    {
        // Not a string member: Utf8StringFormatter cannot write it (NINO008), so it is not read as [NinoUtf8].
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoMemberFormatter(typeof(Utf8StringFormatter))] public int {|#0:A|};
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO008").WithLocation(0).WithArguments("Nino.Core.Utf8StringFormatter", "int", "A", "TestClass"))
            .RunAsync();
    }
}
