using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

[TestClass]
public class AnalyzerTest
{
    private CSharpAnalyzerTest<Generator.NinoAnalyzer, DefaultVerifier> SetUpAnalyzerTest(string code,
        params DiagnosticResult[] diagnostics)
    {
        var test = new CSharpAnalyzerTest<Generator.NinoAnalyzer, DefaultVerifier>
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

        var referenceAssembly = typeof(Core.Writer).Assembly;
        // reference this assembly
        test.TestState.AdditionalReferences.Add(referenceAssembly);
        // runtime version
#if NET8_0
        test.TestState.ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
#else
        test.TestState.ReferenceAssemblies = ReferenceAssemblies.NetStandard.NetStandard21;
#endif

        return test;
    }

    private CSharpSourceGeneratorTest<Generator.GlobalGenerator, DefaultVerifier> SetUpSourceGeneratorTest(
        string code, params DiagnosticResult[] diagnostics)
    {
        var test = new CSharpSourceGeneratorTest<Generator.GlobalGenerator, DefaultVerifier>
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

        var referenceAssembly = typeof(Core.Writer).Assembly;
        // reference this assembly
        test.TestState.AdditionalReferences.Add(referenceAssembly);
        // runtime version
#if NET8_0
        test.TestState.ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
#else
        test.TestState.ReferenceAssemblies = ReferenceAssemblies.NetStandard.NetStandard21;
#endif

        // Disable verification of generated sources - we only care about diagnostics
        test.TestBehaviors |= TestBehaviors.SkipGeneratedSourcesCheck;

        return test;
    }

    [TestMethod]
    public async Task TestNino002()
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
    private class NestedB
    {
        public int A;
        public string B;
    }

    [NinoType]
    private static class NestedC
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
        private class B
        {
            
        }
    }
    [NinoType]
    class C
    {
        [NinoType]
        public class D
        {
            
        }
    }
}
";

        await SetUpAnalyzerTest(code, Verify.Diagnostic("NINO002")
                .WithLocation(5, 16)
                .WithArguments("TestClass"),
            Verify.Diagnostic("NINO002")
                .WithLocation(14, 20)
                .WithArguments("NestedTestClass.NestedA"),
            Verify.Diagnostic("NINO002")
                .WithLocation(21, 19)
                .WithArguments("NestedTestClass.NestedB"),
            Verify.Diagnostic("NINO002")
                .WithLocation(28, 26)
                .WithArguments("NestedTestClass.NestedC"),
            Verify.Diagnostic("NINO002")
                .WithLocation(40, 23)
                .WithArguments("SomeContainer.A.B"),
            Verify.Diagnostic("NINO002")
                .WithLocation(46, 11)
                .WithArguments("SomeContainer.C"),
            Verify.Diagnostic("NINO002")
                .WithLocation(49, 22)
                .WithArguments("SomeContainer.C.D")).RunAsync();
    }

    [TestMethod]
    public async Task TestNino003()
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

public class TestClass2 : AbstractClass2
{
}

public class TestClass3 : TestClass2
{
}

public class TestClass4 : Something, IBase
{
}

public class Something
{
}
";

        await SetUpAnalyzerTest(code, Verify.Diagnostic("NINO003")
                .WithLocation(28, 14)
                .WithArguments("TestClass2", "AbstractClass2"),
            Verify.Diagnostic("NINO003")
                .WithLocation(32, 14)
                .WithArguments("TestClass3", "AbstractClass2")).RunAsync();
    }

    [TestMethod]
    public async Task TestNino004()
    {
        var code = @"
using Nino.Core;

[NinoType]
public class TestClass
{
    [NinoMember(1)]
    public int A;

    [NinoMember(2)]
    public string B;
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

        await SetUpAnalyzerTest(code, Verify.Diagnostic("NINO004")
                .WithSpan(8, 16, 8, 17)
                .WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO004")
                .WithSpan(11, 19, 11, 20)
                .WithArguments("B", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task TestNino005()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public class TestClass
{
    [NinoIgnore]
    public int A;

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

        await SetUpAnalyzerTest(code, Verify.Diagnostic("NINO005")
            .WithSpan(8, 16, 8, 17)
            .WithArguments("A", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task TestNino006()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public class TestClass
{
    [NinoIgnore]
    [NinoMember(2)]
    public int A;

    public string B;
}

[NinoType]
public class TestClass2
{
    [NinoIgnore]
    [NinoMember(2)]
    public int A;

    public string B;
}
";

        await SetUpAnalyzerTest(code, Verify.Diagnostic("NINO006")
                .WithSpan(9, 16, 9, 17)
                .WithArguments("A", "TestClass"),
            Verify.Diagnostic("NINO004")
                .WithSpan(19, 16, 19, 17)
                .WithArguments("A", "TestClass2")).RunAsync();
    }

    [TestMethod]
    public async Task TestNino007()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public class TestClass
{
    [NinoMember(1)]
    private int A;

    public string B;
}

[NinoType(false, true)]
public class TestClass2
{
    [NinoMember(1)]
    private int A;

    public string B;
}
";

        await SetUpAnalyzerTest(code, Verify.Diagnostic("NINO007")
            .WithSpan(8, 17, 8, 18)
            .WithArguments("A", "TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task TestNino008()
    {
        var code = @"
using Nino.Core;

public class TestNested
{
    [NinoType(false, true)]
    public class TestClass
    {
        [NinoMember(1)]
        private int A;
    }
}
";

        await SetUpAnalyzerTest(code, Verify.Diagnostic("NINO008")
            .WithSpan(7, 18, 7, 27)
            .WithArguments("TestClass")).RunAsync();
    }

    [TestMethod]
    public async Task TestNino009()
    {
        var code = @"
using Nino.Core;

[NinoType(false)]
public class TestClass : TestBase
{
    [NinoMember(3)]
    public float C;

    [NinoMember(3)]
    public double D;

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
            Verify.Diagnostic("NINO009")
                .WithSpan(11, 19, 11, 20)
                .WithArguments("TestClass", "D", "TestClass", "C")).RunAsync();
    }

    [TestMethod]
    public async Task TestNino011()
    {
        var code = @"
using System;
using System.Threading.Tasks;
using Nino.Core;

[NinoType]
public class TestClassWithDelegate
{
    // Delegates are not directly serializable and trigger NINO011 during source generation
    public Action UnsupportedDelegate;

    // Valid fields that will be serialized
    public int Id;
    public string Name;
}

[NinoType]
public class TestClassWithTask
{
    // Task types trigger NINO011 during source generation
    public Task<int> UnsupportedTask;

    // Valid fields
    public double Value;
    public bool Flag;
}
";

        await SetUpSourceGeneratorTest(code,
            Verify.Diagnostic("NINO011")
                .WithSpan(10, 19, 10, 38)
                .WithArguments("UnsupportedDelegate", "System.Action", "TestClassWithDelegate"),
            Verify.Diagnostic("NINO011")
                .WithSpan(21, 22, 21, 37)
                .WithArguments("UnsupportedTask", "System.Threading.Tasks.Task<int>", "TestClassWithTask")
        ).RunAsync();
    }
}