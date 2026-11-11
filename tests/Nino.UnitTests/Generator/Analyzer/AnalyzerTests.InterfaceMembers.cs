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

// Attributes on interface members (NINO033).
public partial class AnalyzerTests
{
    [TestMethod]
    public async Task NINO033_InterfaceMemberAttributes()
    {
        // Every implementation writes its own member with its own attributes: a class, a struct, a base class
        // implementing it for its subtypes. Reported once, where the attribute is, implemented or not.
        var code = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    [{|#0:NinoUtf8|}] string Name { get; set; }
    [{|#1:NinoMemberFormatter(typeof(Compact))|}] int Sides { get; set; }
    [{|#2:NinoIgnore|}] int Cache { get; set; }
    [{|#3:Nino.Core.NinoIgnoreAttribute|}, {|#4:NinoUtf8|}] string Label { get; set; }
}

public class Square : IShape
{
    public string Name { get; set; }
    public int Sides { get; set; }
    public int Cache { get; set; }
    public string Label { get; set; }
}

public struct Dot : IShape
{
    public string Name { get; set; }
    public int Sides { get; set; }
    public int Cache { get; set; }
    public string Label { get; set; }
}

public abstract class ShapeBase : IShape
{
    public string Name { get; set; }
    public int Sides { get; set; }
    public int Cache { get; set; }
    public string Label { get; set; }
}

public sealed class Circle : ShapeBase
{
    public double Radius;
}

[NinoType]
public interface IUnimplemented
{
    [{|#5:NinoUtf8|}] string Name { get; set; }
}

public struct Compact : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref int value) { }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO033").WithLocation(0).WithArguments("NinoUtf8", "Name", "IShape"),
            Verify.Diagnostic("NINO033").WithLocation(1).WithArguments("NinoMemberFormatter", "Sides", "IShape"),
            Verify.Diagnostic("NINO033").WithLocation(2).WithArguments("NinoIgnore", "Cache", "IShape"),
            Verify.Diagnostic("NINO033").WithLocation(3).WithArguments("NinoIgnore", "Label", "IShape"),
            Verify.Diagnostic("NINO033").WithLocation(4).WithArguments("NinoUtf8", "Label", "IShape"),
            Verify.Diagnostic("NINO033").WithLocation(5).WithArguments("NinoUtf8", "Name", "IUnimplemented"))
            .RunAsync();
    }

    [TestMethod]
    public async Task NINO033_NotForAttributesThatTakeEffect()
    {
        // An interface's [NinoMember(index)] places the implementing member; attributes on the implementing members,
        // an explicit implementation's or a plain interface's are not NINO033's; a member with a finding of its own
        // ([NinoIgnore] in an autoCollect: false interface, NINO011; an invalid formatter, NINO008) is left to it.
        var code = @"
using Nino.Core;

[NinoType(autoCollect: false)]
public interface IIndexed
{
    [NinoMember(1)] int Second { get; set; }
    [NinoMember(0)] int First { get; set; }
    [NinoIgnore] int {|#0:Cache|} { get; set; }
    [NinoMember(2)] [NinoMemberFormatter(typeof(Compact))] string {|#1:Text|} { get; set; }
}

public class Indexed : IIndexed
{
    public int Second { get; set; }
    public int First { get; set; }
    public int Cache { get; set; }
    public string Text { get; set; }
}

[NinoType]
public interface IExplicit
{
    int Count { get; set; }
}

public class Explicit : IExplicit
{
    [NinoMemberFormatter(typeof(Compact))] int IExplicit.Count { get; set; }
}

public interface IPlain
{
    [NinoUtf8] string Name { get; set; }
}

public struct Compact : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref int value) { }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO011").WithLocation(0).WithArguments("Cache", "IIndexed"),
            Verify.Diagnostic("NINO008").WithLocation(1)
                .WithArguments("Compact", "string", "Text", "IIndexed")).RunAsync();
    }

    [TestMethod]
    public async Task NINO033_NotWhereTheInterfaceMemberIsWritten()
    {
        // A type implementing a member explicitly, or by its default implementation, writes the interface's member
        // with its attributes: those take effect, whatever the other implementations do. Members implemented only
        // implicitly are still reported.
        var code = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    [NinoUtf8] string Name { get; set; }
    [NinoMemberFormatter(typeof(Compact))] int Sides { get; set; }
    [NinoIgnore] int Cache { get; set; }
    [{|#0:NinoUtf8|}] string Label { get; set; }
}

public class Square : IShape
{
    public string Name { get; set; }
    public int Sides { get; set; }
    public int Cache { get; set; }
    public string Label { get; set; }
}

public struct Hidden : IShape
{
    string IShape.Name { get; set; }
    int IShape.Sides { get; set; }
    int IShape.Cache { get; set; }
    public string Label { get; set; }
}

[NinoType]
public interface IDefault
{
    [NinoUtf8] string Name { get => null; set { } }
}

public class UsesDefault : IDefault
{
}

public struct Compact : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref int value) { }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO033").WithLocation(0).WithArguments("NinoUtf8", "Label", "IShape")).RunAsync();
    }
}
