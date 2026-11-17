using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core.Internal;

namespace Nino.UnitTests;

// The structs below exist for their layout only: their fields are never assigned.
#pragma warning disable CS0649

/// <summary>
/// Which types Nino refuses to build a formatter for on IL2CPP (NinoReflection.Il2CppSplitLayoutIn): generic structs,
/// T? included, holding a struct whose Pack moves a struct field, or a generic struct with Pack. IL2CPP places such a
/// generic instance's fields in one way for reflection and shared generic code and in another for compiled code
/// (unity.yml run 44102730242). The rule is checked here on every runtime; Unity's StructLayoutsWriteCoreClrBytes checks
/// that the player refuses exactly where it read and wrote wrong bytes.
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
}
