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

// Formatter registrations (NINO020).
public partial class AnalyzerTests
{
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
    public void Deserialize(ref Reader reader, ref int value) { }
}

public struct NotAFormatter { }

public struct BoxFormatter<T, TF> : INinoFormatter<Box<T>> where TF : struct, INinoFormatter<T>
{
    public void Serialize(Box<T> value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref Box<T> value) { }
}

public struct Uninferable<T, TExtra> : INinoFormatter<Box<T>>
{
    public void Serialize(Box<T> value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref Box<T> value) { }
}

public struct AnyListFormatter<TList, T, TF> : INinoFormatter<TList>
    where TList : class, IEnumerable<T> where TF : struct, INinoFormatter<T>
{
    public void Serialize(TList value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref TList value) { }
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
}
