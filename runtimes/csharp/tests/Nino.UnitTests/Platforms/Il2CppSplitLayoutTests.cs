using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.UnitTests;

// The structs below exist for their layout only: their fields are never assigned.
#pragma warning disable CS0649

/// <summary>
/// Generic structs IL2CPP lays out in two ways. Which types Nino refuses to build a formatter for on IL2CPP (NinoReflection.Il2CppSplitLayoutIn): generic structs,
/// T? included, holding a struct whose Pack moves a struct field, or a generic struct with Pack. IL2CPP places such a
/// generic instance's fields in one way for reflection and shared generic code and in another for compiled code
/// (unity.yml run 44102730242). The rule is checked here on every runtime; Unity's StructLayoutsWriteCoreClrBytes checks
/// that the player refuses exactly where it read and wrote wrong bytes. The generator registers the compiled offsets of
/// those it sees (SplitLayouts, NinoLayoutOffsets), and the formatters of raw values holding one that top-level calls
/// name: laid out from those offsets, a value is written as reflection lays it out on CoreCLR.
/// </summary>
[TestClass]
public class Il2CppSplitLayoutTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct IntDateTime4
    {
        public int Id;
        public DateTime At;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ByteLong1
    {
        public byte A;
        public long B;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Pair1<T>
    {
        public byte A;
        public T B;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct Fixed1
    {
        public byte A;
        public fixed int B[3];
    }

    private struct HoldsPacked
    {
        public byte A;
        public IntDateTime4 P;
    }

    private struct Around<T>
    {
        public byte A;
        public T B;
        public int C;
    }

    private static Type SplitIn(Type type) => NinoReflection.SplitLayoutIn(type, null);

    [TestMethod]
    public void GenericStructsHoldingAStructPackMovesAreRefused()
    {
        Assert.AreEqual(typeof(Around<IntDateTime4>), SplitIn(typeof(Around<IntDateTime4>)));
        Assert.AreEqual(typeof(Around<HoldsPacked>), SplitIn(typeof(Around<HoldsPacked>)));
        Assert.AreEqual(typeof(HoldsPacked?), SplitIn(typeof(HoldsPacked?)));
        Assert.AreEqual(typeof(IntDateTime4?), SplitIn(typeof(IntDateTime4?[])));
        Assert.AreEqual(typeof(Pair1<long>?), SplitIn(typeof(Pair1<long>?)));
        Assert.AreEqual(typeof(Around<Pair1<long>>), SplitIn(typeof(Around<Pair1<long>>)));
        Assert.AreEqual(typeof(Around<IntDateTime4>), SplitIn(typeof(List<Around<IntDateTime4>>)));
        // A generic struct with Pack, alone and in arrays and lists (Unity 6000.6 wrote 16-byte elements of 9).
        Assert.AreEqual(typeof(Pair1<long>), SplitIn(typeof(Pair1<long>)));
        Assert.AreEqual(typeof(Pair1<long>), SplitIn(typeof(Pair1<long>[])));
        Assert.AreEqual(typeof(Pair1<long>), SplitIn(typeof(List<Pair1<long>>)));
    }

    [TestMethod]
    public void StructsIl2CppLaysOutOneWayAreKept()
    {
        // The packed structs alone, in arrays and lists, and generic structs over Pack that moves only primitives
        // (fixed buffers included): the player read and wrote them right.
        Assert.IsNull(SplitIn(typeof(IntDateTime4)));
        Assert.IsNull(SplitIn(typeof(IntDateTime4[])));
        Assert.IsNull(SplitIn(typeof(List<IntDateTime4>)));
        Assert.IsNull(SplitIn(typeof(HoldsPacked)));
        Assert.IsNull(SplitIn(typeof(Around<ByteLong1>)));
        Assert.IsNull(SplitIn(typeof(ByteLong1?)));
        Assert.IsNull(SplitIn(typeof(Fixed1?)));
        Assert.IsNull(SplitIn(typeof(Around<int>)));
        Assert.IsNull(SplitIn(typeof(DateTime?)));
        Assert.IsNull(SplitIn(typeof(KeyValuePair<int, DateTime>)));
    }

    [TestMethod]
    public void NothingIsRefusedOutsideIl2Cpp()
    {
        Assert.IsFalse(NinoReflection.IsIl2Cpp);
        Assert.IsNull(NinoReflection.Il2CppSplitLayoutIn(typeof(Around<IntDateTime4>)));
        NinoReflection.RefuseIl2CppSplitLayout(typeof(HoldsPacked?));
    }

    // Types of their own for the tests below, which register their offsets: nothing else measures them.
    private struct TabledAround<T>
    {
        public byte A;
        public T B;
        public int C;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TabledPair1<T>
    {
        public byte A;
        public T B;
    }

    private struct TabledHolds
    {
        public byte A;
        public TabledAround<IntDateTime4> B;
        public TabledPair1<long>? C;
    }

    [TestMethod]
    public void RegisteredOffsetsLayOutAsReflectionDoes()
    {
        var types = new[]
        {
            typeof(TabledAround<IntDateTime4>), typeof(TabledPair1<long>), typeof(TabledPair1<long>?),
            typeof(TabledAround<TabledPair1<long>>), typeof(TabledHolds),
        };
        var sizes = new[]
        {
            Unsafe.SizeOf<TabledAround<IntDateTime4>>(), Unsafe.SizeOf<TabledPair1<long>>(),
            Unsafe.SizeOf<TabledPair1<long>?>(), Unsafe.SizeOf<TabledAround<TabledPair1<long>>>(),
            Unsafe.SizeOf<TabledHolds>(),
        };
        var measured = types.Select((t, i) => NinoLayout.Compute(t, sizes[i], NinoLayoutModel.Canonical, true)).ToArray();

        var around = default(TabledAround<IntDateTime4>);
        NinoLayoutOffsets.Register<TabledAround<IntDateTime4>>(NinoLayoutOffsets.Of(ref around, ref around.A),
            NinoLayoutOffsets.Of(ref around, ref around.B), NinoLayoutOffsets.Of(ref around, ref around.C));
        var pair = default(TabledPair1<long>);
        NinoLayoutOffsets.Register<TabledPair1<long>>(NinoLayoutOffsets.Of(ref pair, ref pair.A),
            NinoLayoutOffsets.Of(ref pair, ref pair.B));
        NinoLayoutOffsets.RegisterNullable<TabledPair1<long>>();
        var nested = default(TabledAround<TabledPair1<long>>);
        NinoLayoutOffsets.Register<TabledAround<TabledPair1<long>>>(NinoLayoutOffsets.Of(ref nested, ref nested.A),
            NinoLayoutOffsets.Of(ref nested, ref nested.B), NinoLayoutOffsets.Of(ref nested, ref nested.C));

        for (int i = 0; i < types.Length; i++)
        {
            var tabled = NinoLayout.Compute(types[i], sizes[i], NinoLayoutModel.Canonical, true);
            string name = types[i].ToString();
            Assert.AreEqual(measured[i].Size, tabled.Size, name);
            Assert.AreEqual(measured[i].Canonical, tabled.Canonical, name);
            CollectionAssert.AreEqual(measured[i].Mask, tabled.Mask, name);
            CollectionAssert.AreEqual(measured[i].Map, tabled.Map, name);
            CollectionAssert.AreEqual(measured[i].Resized, tabled.Resized, name);
            CollectionAssert.AreEqual(measured[i].Absent, tabled.Absent, name);
            Assert.IsTrue(tabled.Size > 0, name);
        }
    }

    [TestMethod]
    public void TheGeneratorRegistersSplitStructsItSees()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Runtime.InteropServices;
            using Nino.Core;

            namespace Split;

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            public struct IntDateTime4 { public int Id; public DateTime At; }

            public struct HoldsPacked { public byte A; public IntDateTime4 P; }

            public struct Around<T> { public byte A; public T B; public int C; }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            public struct Pair1<T> { public byte A; public T B; }

            public struct Hidden<T> { private byte _a; public T B; }

            [NinoType(format: NinoFormat.VersionTolerant)]
            public class Holder
            {
                public Around<HoldsPacked> Around;
                public List<Pair1<long>> Pairs;
                public HoldsPacked? Maybe;
                public Around<int> Plain;
                public Hidden<HoldsPacked> Hidden;
            }

            public static class Calls
            {
                public static byte[] Write(Around<IntDateTime4> value) => NinoSerializer.Serialize(value);
                public static byte[] WritePlain(Around<int> value) => NinoSerializer.Serialize(value);
            }
            """;
        var compilation = TestCompilation.Create("Split", new[] { source });
        CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        TestCompilation.EmitAndLoad(output);
        string usages = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("NinoGen.Usages.g.cs", StringComparison.Ordinal))
            .ToString();
        string registry = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("NinoGen.Registry.g.cs", StringComparison.Ordinal))
            .ToString();

        StringAssert.Contains(registry, "NinoGenUsages.RegisterLayouts();");
        // Members, their elements and T?, and a top-level call's raw value, whose formatter is registered too.
        StringAssert.Contains(usages, "Register<global::Split.Around<global::Split.HoldsPacked>>(");
        StringAssert.Contains(usages, "Register<global::Split.Pair1<long>>(");
        StringAssert.Contains(usages, "RegisterNullable<global::Split.HoldsPacked>();");
        StringAssert.Contains(usages, "Register<global::Split.Around<global::Split.IntDateTime4>>(");
        StringAssert.Contains(usages, "NinoFormatterCache<global::Split.Around<global::Split.IntDateTime4>>.TryRegister<");
        // A generic struct laid out one way, and one whose fields generated code cannot all name: neither.
        Assert.IsFalse(usages.Contains("Register<global::Split.Around<int>>"), usages);
        Assert.IsFalse(usages.Contains("NinoFormatterCache<global::Split.Around<int>>"), usages);
        Assert.IsFalse(usages.Contains("Register<global::Split.Hidden<"), usages);
    }
}
