using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCF = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Nino.Generator.NinoCodeFixProvider,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// Attributes on interface members (NINO033).
public partial class CodeFixTests
{
    private const string Compact = @"
public struct Compact : INinoFormatter<int>
{
    public void Serialize(int value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref int value) { }
}";

    [TestMethod]
    public async Task NINO033_MoveToTheImplementingMembers()
    {
        // To each member the generator writes: a class's, a struct's, a base class's for its subtypes. A member with
        // the attribute keeps its own.
        var code = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    [{|#0:NinoUtf8|}] string Name { get; set; }

    [{|#1:NinoMemberFormatter(typeof(Compact))|}]
    int Sides { get; set; }

    [{|#2:NinoIgnore|}] int Cache { get; set; }
}

public class Square : IShape
{
    public string Name { get; set; }
    public int Sides { get; set; }
    public int Cache { get; set; }
}

public struct Dot : IShape
{
    [NinoUtf8] public string Name { get; set; }
    public int Sides { get; set; }
    public int Cache { get; set; }
}

public abstract class ShapeBase : IShape
{
    public string Name { get; set; }
    public int Sides { get; set; }
    public int Cache { get; set; }
}

public sealed class Circle : ShapeBase
{
}

" + Compact;

        var fixedCode = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    string Name { get; set; }

    int Sides { get; set; }

    int Cache { get; set; }
}

public class Square : IShape
{
    [NinoUtf8]
    public string Name { get; set; }
    [NinoMemberFormatter(typeof(Compact))]
    public int Sides { get; set; }
    [NinoIgnore]
    public int Cache { get; set; }
}

public struct Dot : IShape
{
    [NinoUtf8] public string Name { get; set; }
    [NinoMemberFormatter(typeof(Compact))]
    public int Sides { get; set; }
    [NinoIgnore]
    public int Cache { get; set; }
}

public abstract class ShapeBase : IShape
{
    [NinoUtf8]
    public string Name { get; set; }
    [NinoMemberFormatter(typeof(Compact))]
    public int Sides { get; set; }
    [NinoIgnore]
    public int Cache { get; set; }
}

public sealed class Circle : ShapeBase
{
}

" + Compact;

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO033").WithLocation(0).WithArguments("NinoUtf8", "Name", "IShape"),
            VerifyCF.Diagnostic("NINO033").WithLocation(1).WithArguments("NinoMemberFormatter", "Sides", "IShape"),
            VerifyCF.Diagnostic("NINO033").WithLocation(2).WithArguments("NinoIgnore", "Cache", "IShape")).RunAsync();
    }

    [TestMethod]
    public async Task NINO033_NamesTheAttributeAsTheOtherFileCan()
    {
        // The implementation is in a file without using directives, and a record declares it by a parameter.
        var code = @"
using Formats;
using Nino.Core;

namespace Shapes
{
    [NinoType]
    public interface IShape
    {
        [{|#0:NinoMemberFormatter(typeof(Compact))|}] int Sides { get; init; }
        [{|#1:NinoUtf8|}, System.Obsolete] string Name { get; init; }
    }
}

namespace Formats
{
" + Compact + @"
}";
        const string Other = @"
namespace Shapes
{
    public class Square : IShape
    {
        public int Sides { get; init; }
        public string Name { get; init; }
    }

    public record Label(int Sides, string Name) : IShape;
}";

        var fixedCode = @"
using Formats;
using Nino.Core;

namespace Shapes
{
    [NinoType]
    public interface IShape
    {
        int Sides { get; init; }
        [System.Obsolete] string Name { get; init; }
    }
}

namespace Formats
{
" + Compact + @"
}";
        const string FixedOther = @"
namespace Shapes
{
    public class Square : IShape
    {
        [Nino.Core.NinoMemberFormatter(typeof(Formats.Compact))]
        public int Sides { get; init; }
        [Nino.Core.NinoUtf8]
        public string Name { get; init; }
    }

    public record Label([property: Nino.Core.NinoMemberFormatter(typeof(Formats.Compact))] int Sides, [property: Nino.Core.NinoUtf8] string Name) : IShape;
}";

        var test = SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO033").WithLocation(0).WithArguments("NinoMemberFormatter", "Sides", "Shapes.IShape"),
            VerifyCF.Diagnostic("NINO033").WithLocation(1).WithArguments("NinoUtf8", "Name", "Shapes.IShape"));
        test.TestState.Sources.Add(Other);
        test.FixedState.Sources.Add(FixedOther);
        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO033_KeptForAnExplicitImplementationElsewhere()
    {
        // Another project of the solution implements the member explicitly, so writes the interface's member with its
        // attribute: the attribute is copied to the implicit implementation and stays on the interface (still
        // reported in this project, which has no explicit implementation).
        var code = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    [{|#0:NinoUtf8|}] string Name { get; set; }
}

public class Square : IShape
{
    public string Name { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    [{|#0:NinoUtf8|}] string Name { get; set; }
}

public class Square : IShape
{
    [NinoUtf8]
    public string Name { get; set; }
}";
        const string Other = @"
public class Hidden : IShape
{
    string IShape.Name { get; set; }
}";

        var diagnostic = VerifyCF.Diagnostic("NINO033").WithLocation(0).WithArguments("NinoUtf8", "Name", "IShape");
        var test = SetUpCodeFixTest(code, fixedCode, diagnostic);
        test.FixedState.ExpectedDiagnostics.Add(diagnostic);
        foreach (var state in new[] { test.TestState, test.FixedState })
        {
            var other = state.AdditionalProjects["Other"];
            other.Sources.Add(Other);
            other.AdditionalProjectReferences.Add("TestProject");
            TestReferences.Apply(other);
        }

        await test.RunAsync();
    }

    [TestMethod]
    public async Task NINO033_RemovedWithoutImplementations()
    {
        var code = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    [{|#0:NinoUtf8|}]
    string Name { get; set; }
}";

        var fixedCode = @"
using Nino.Core;

[NinoType]
public interface IShape
{
    string Name { get; set; }
}";

        await SetUpCodeFixTest(code, fixedCode,
            VerifyCF.Diagnostic("NINO033").WithLocation(0).WithArguments("NinoUtf8", "Name", "IShape")).RunAsync();
    }
}
