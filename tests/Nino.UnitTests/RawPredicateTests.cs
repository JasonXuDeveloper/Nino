using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

public struct PlainGenericPair<T> where T : unmanaged
{
    public T A;
    public T B;
}

[NinoType]
public struct PredicateNinoStruct
{
    public int X;
}

/// <summary>
/// NativeAOT decides which unregistered unmanaged types are raw with the resolver's own rule
/// (NinoFormatterResolver.IsRawShape), so a type is raw with or without dynamic code.
/// </summary>
[TestClass]
public class RawPredicateTests
{
    [TestMethod]
    public void NativeAotAndTheResolverAgreeOnRawTypes()
    {
        var raw = new[] { typeof(int), typeof(DateTime), typeof(Guid), typeof(PlainGenericPair<int>), typeof(decimal) };
        var notRaw = new[]
        {
            typeof(int?), typeof(KeyValuePair<int, int>), typeof(ValueTuple<int, int>), typeof(DateTimeOffset),
            typeof(IntPtr), typeof(UIntPtr), typeof(PredicateNinoStruct)
        };
        foreach (var type in raw)
        {
            Assert.IsTrue(NinoFormatterResolver.IsRawShape(type), type.Name);
            Assert.IsTrue(NinoFormatterResolver.IsRawUnmanaged(type), type.Name);
        }

        foreach (var type in notRaw)
            Assert.IsFalse(NinoFormatterResolver.IsRawShape(type), type.Name);
        // Generic structs that are not [NinoType]s were left out under NativeAOT only; the resolver writes them raw.
        var pair = new PlainGenericPair<int> { A = 1, B = 2 };
        CollectionAssert.AreEqual(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 },
            NinoSerializer.SerializeBoxed(pair, typeof(PlainGenericPair<int>)));
    }
}
