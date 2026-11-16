#if NET
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Tests;

#nullable enable
namespace Nino.UnitTests;

/// <summary>
/// The DLLs committed in the Unity package (src/Nino.Unity/Packages/com.jasonxudeveloper.nino/Runtime) have the public
/// API of this commit's builds. Unity compiles the package's own sources (Nino.Burst, Nino.WebGL), the code the
/// generator writes and the user's code against them, so a DLL left behind its sources breaks a project that installs
/// the package from the repository: after rc.7, Nino.Burst and Nino.WebGL named NinoKernels, which the committed
/// Nino.Core.dll (rc.7's) did not have. Bytes cannot be compared (each DLL carries the commit it was built from); the
/// public API can.
/// Refresh: src/Nino.Unity/AGENTS.md, "Rules for these files".
/// </summary>
[TestClass]
public class UnityPackageDllTests
{
    private static string Committed(string name) => RepositoryFiles.Of("src", "Nino.Unity", "Packages",
        "com.jasonxudeveloper.nino", "Runtime", name);

    [TestMethod]
    public void NinoCoreHasThePublicApiOfItsSources()
    {
        // Unity's Nino.Core is the netstandard2.1 build: compared where the tests run on it (-p:NinoCoreTfm=netstandard2.1).
        var built = typeof(NinoSerializer).Assembly;
        if (built.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName != ".NETStandard,Version=v2.1")
            Assert.Inconclusive("Nino.Core's netstandard2.1 build is compared in the -p:NinoCoreTfm=netstandard2.1 run");
        AssertSameApi(Committed("Nino.Core.dll"), built);
    }

    [TestMethod]
    public void NinoUnityTypesHasThePublicApiOfItsSources() =>
        AssertSameApi(Committed("Nino.UnityTypes.dll"), typeof(Nino.UnityTypes.AnimationCurveFormatter).Assembly);

    private static void AssertSameApi(string committedPath, Assembly built)
    {
        // Its own load context: the committed assembly has the built one's name.
        var context = new AssemblyLoadContext(nameof(UnityPackageDllTests), isCollectible: true);
        try
        {
            var committed = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(committedPath)));
            string[] expected = Api(built), actual = Api(committed);
            var missing = expected.Except(actual).ToArray();
            var extra = actual.Except(expected).ToArray();
            Assert.IsTrue(missing.Length == 0 && extra.Length == 0,
                $"{Path.GetFileName(committedPath)} is not this commit's build (refresh it: src/Nino.Unity/AGENTS.md).\n" +
                $"Missing from it:\n  {string.Join("\n  ", missing.Take(20))}\nOnly in it:\n  {string.Join("\n  ", extra.Take(20))}");
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Every public type and its public and protected members, as text.</summary>
    private static string[] Api(Assembly assembly) => assembly.GetExportedTypes()
        .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                            BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(Visible)
            .Select(member => $"{type.FullName} {member.MemberType} {member}")
            .Prepend($"{type.FullName} {(type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class")}" +
                     $"{(type.IsAbstract ? " abstract" : "")}{(type.IsSealed ? " sealed" : "")} : {type.BaseType}"))
        .OrderBy(line => line, StringComparer.Ordinal).ToArray();

    private static bool Visible(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        // Properties and events through their accessors.
        PropertyInfo property => property.GetAccessors(true).Any(Visible),
        EventInfo e => e.AddMethod is { } add && Visible(add),
        Type nested => nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem,
        _ => false,
    };
}
#endif
