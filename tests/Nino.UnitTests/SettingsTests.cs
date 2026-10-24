#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>
/// The limits of <see cref="NinoSettings"/> are frozen the first time Nino uses them, so each case runs in a load
/// context of its own: a fresh copy of Nino.Core and of this assembly (its generated formatters register with that
/// copy), where the case sets the limits before anything reads them. Cases are the static methods of
/// <see cref="SettingsProbes"/>, called by name; they return "ok" or what went wrong.
/// </summary>
[TestClass]
public class SettingsTests
{
    private sealed class Isolated : AssemblyLoadContext
    {
        private readonly string _directory = Path.GetDirectoryName(typeof(SettingsTests).Assembly.Location);

        protected override Assembly Load(AssemblyName name) =>
            name.Name is "Nino.Core" or "Nino.UnitTests" or "Nino.Immutable"
                ? LoadFromAssemblyPath(Path.Combine(_directory, name.Name + ".dll"))
                : null;
    }

    private static void Run(string probe, byte[] data = null)
    {
        var copy = new Isolated().LoadFromAssemblyPath(typeof(SettingsTests).Assembly.Location);
        Assert.AreNotSame(typeof(NinoSettings).Assembly,
            copy.GetType(typeof(SettingsProbes).FullName)!.GetMethod(nameof(SettingsProbes.CoreAssembly))!.Invoke(null, null));
        var result = (string)copy.GetType(typeof(SettingsProbes).FullName)!.GetMethod(probe)!.Invoke(null, new object[] { data });
        Assert.AreEqual("ok", result, probe);
    }

    [TestMethod]
    public void DefaultsAreTheLimitsOfBefore()
    {
        // Read in this process (which has used Nino already): the defaults (DefaultMaxDepth is 512), and frozen.
        Assert.AreEqual(NinoSettings.DefaultMaxDepth, NinoSettings.MaxDepth);
        Assert.AreEqual(int.MaxValue, NinoSettings.MaxCollectionLength);
    }

    [TestMethod]
    public void SettingALimitAfterFirstUseThrows() => Run(nameof(SettingsProbes.SetAfterUse));

    [TestMethod]
    public void OutOfRangeLimitsAreRejected() => Run(nameof(SettingsProbes.OutOfRange));

    [TestMethod]
    public void CollectionLengthLimit() => Run(nameof(SettingsProbes.CollectionLength));

    [TestMethod]
    public void DepthLimit()
    {
        // Written here, under the default limit: 300 structs, each holding the next through its list.
        Run(nameof(SettingsProbes.Depth), NinoSerializer.Serialize(SettingsProbes.Chain(300)));
    }
}

/// <summary>The cases of <see cref="SettingsTests"/>, run in a copy of Nino of their own.</summary>
public static class SettingsProbes
{
    public static Assembly CoreAssembly() => typeof(NinoSettings).Assembly;

    public static UntrustedStructNode Chain(int depth)
    {
        var node = new UntrustedStructNode { V = depth, Kids = new List<UntrustedStructNode>() };
        for (int i = depth - 1; i > 0; i--)
            node = new UntrustedStructNode { V = i, Kids = new List<UntrustedStructNode> { node } };
        return node;
    }

    private static string Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            return e.GetType() == typeof(TException) ? null : $"{what}: {e.GetType().Name}: {e.Message}";
        }

        return $"{what}: nothing thrown";
    }

    public static string SetAfterUse(byte[] _)
    {
        NinoSettings.MaxDepth = 300;
        NinoSettings.MaxCollectionLength = 1000;
        NinoDeserializer.Deserialize<List<int>>(NinoSerializer.Serialize(new List<int> { 1 }));
        return Throws<InvalidOperationException>(() => NinoSettings.MaxDepth = 400, "MaxDepth") ??
               Throws<InvalidOperationException>(() => NinoSettings.MaxCollectionLength = 10, "MaxCollectionLength") ??
               (NinoSettings.MaxDepth == 300 && NinoSettings.MaxCollectionLength == 1000 ? "ok" : "changed");
    }

    public static string OutOfRange(byte[] _) =>
        Throws<ArgumentOutOfRangeException>(() => NinoSettings.MaxDepth = 127, "MaxDepth 127") ??
        Throws<ArgumentOutOfRangeException>(() => NinoSettings.MaxCollectionLength = -1, "MaxCollectionLength -1") ??
        "ok";

    public static string CollectionLength(byte[] _)
    {
        NinoSettings.MaxCollectionLength = 3;
        byte[] Bytes<T>(T value) => NinoSerializer.Serialize(value);
        // Written before any read: the writer has no length limit.
        var list4 = Bytes(new List<int> { 1, 2, 3, 4 });
        var array4 = Bytes(new[] { 1, 2, 3, 4 });
        var strings4 = Bytes(new[] { "a", "b", "c", "d" });
        var map4 = Bytes(new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 3, [4] = 4 });
        var grid = Bytes(new int[2, 2]);
        var holder = Bytes(new UntrustedStructNode { Kids = new List<UntrustedStructNode> { new(), new(), new(), new() } });
        if (NinoDeserializer.Deserialize<List<int>>(Bytes(new List<int> { 1, 2, 3 })).Count != 3 ||
            NinoDeserializer.Deserialize<int[]>(Bytes(new[] { 1, 2, 3 })).Length != 3 ||
            NinoDeserializer.Deserialize<int[]>(Bytes((int[])null)) != null ||
            NinoDeserializer.Deserialize<List<string>>(Bytes((List<string>)null)) != null)
            return "a length within the limit was not read";
        int[] existing = new int[1];
        return Throws<NinoDataException>(() => NinoDeserializer.Deserialize<List<int>>(list4), "List<int>") ??
               Throws<NinoDataException>(() => NinoDeserializer.Deserialize<int[]>(array4), "int[]") ??
               Throws<NinoDataException>(() => NinoDeserializer.Deserialize(array4, ref existing), "int[] into one") ??
               Throws<NinoDataException>(() => NinoDeserializer.Deserialize<string[]>(strings4), "string[]") ??
               Throws<NinoDataException>(() => NinoDeserializer.Deserialize<Dictionary<int, int>>(map4), "Dictionary") ??
               Throws<NinoDataException>(() => NinoDeserializer.Deserialize<int[,]>(grid), "int[,]") ??
               Throws<NinoDataException>(() => NinoDeserializer.Deserialize<UntrustedStructNode>(holder), "member") ??
               "ok";
    }

    public static string Depth(byte[] deep)
    {
        NinoSettings.MaxDepth = 200;
        if (NinoDeserializer.Deserialize<UntrustedStructNode>(NinoSerializer.Serialize(Chain(150))).Kids.Count != 1)
            return "150 levels were not read";
        return Throws<NinoDataException>(() => NinoDeserializer.Deserialize<UntrustedStructNode>(deep), "read 300") ??
               "ok";
    }
}
#endif
