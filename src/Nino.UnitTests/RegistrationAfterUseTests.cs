#nullable disable
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

// Each test owns its types: the first use of a type fixes its formatter for the whole process.
[NinoType] public sealed class RegisteredBeforeUse { public int A; }
[NinoType] public sealed class RegisteredAfterSerialize { public int A; }
[NinoType] public sealed class RegisteredAfterDeserialize { public int A; }
// Not a [NinoType]: an unmanaged struct Nino copies as its raw bytes.
public struct RegisteredRawAfterUse { public int A; }

/// <summary>Writes a marker byte and the value plus one, so a test can tell it from the generated formatter.</summary>
public struct MarkedIntHolderFormatter : INinoFormatter<RegisteredBeforeUse>, INinoFormatter<RegisteredAfterSerialize>,
    INinoFormatter<RegisteredAfterDeserialize>, INinoFormatter<RegisteredRawAfterUse>
{
    public const byte Marker = 0x5A;

    public void Serialize(RegisteredBeforeUse value, ref Writer writer) => Write(value?.A, ref writer);
    public void Serialize(RegisteredAfterSerialize value, ref Writer writer) => Write(value?.A, ref writer);
    public void Serialize(RegisteredAfterDeserialize value, ref Writer writer) => Write(value?.A, ref writer);
    public void Serialize(RegisteredRawAfterUse value, ref Writer writer) => Write(value.A, ref writer);

    public void Deserialize(ref RegisteredBeforeUse value, ref Reader reader) =>
        value = Read(ref reader) is { } a ? new RegisteredBeforeUse { A = a } : null;

    public void Deserialize(ref RegisteredAfterSerialize value, ref Reader reader) =>
        value = Read(ref reader) is { } a ? new RegisteredAfterSerialize { A = a } : null;

    public void Deserialize(ref RegisteredAfterDeserialize value, ref Reader reader) =>
        value = Read(ref reader) is { } a ? new RegisteredAfterDeserialize { A = a } : null;

    public void Deserialize(ref RegisteredRawAfterUse value, ref Reader reader) =>
        value = new RegisteredRawAfterUse { A = Read(ref reader) ?? 0 };

    private static void Write(int? value, ref Writer writer)
    {
        writer.Write(Marker);
        writer.Write(value.HasValue);
        writer.Write((value ?? 0) + 1);
    }

    private static int? Read(ref Reader reader)
    {
        Assert.AreEqual(Marker, reader.Read<byte>());
        reader.Read(out bool hasValue);
        int a = reader.Read<int>() - 1;
        return hasValue ? a : null;
    }
}

[TestClass]
public class RegistrationAfterUseTests
{
    [TestMethod]
    public void RegistrationBeforeFirstUseIsUsedByEveryTopLevelCall()
    {
        NinoFormatterCache<RegisteredBeforeUse>.Register<MarkedIntHolderFormatter>();

        // Intercepted (this project's calls with an exact type argument) and regular paths alike.
        var bytes = NinoSerializer.Serialize(new RegisteredBeforeUse { A = 41 });
        CollectionAssert.AreEqual(new byte[] { MarkedIntHolderFormatter.Marker, 1, 42, 0, 0, 0 }, bytes);
        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize((object)new RegisteredBeforeUse { A = 41 }));
        var buffer = new NinoArrayBufferWriter();
        NinoSerializer.Serialize(new RegisteredBeforeUse { A = 41 }, buffer);
        CollectionAssert.AreEqual(bytes, buffer.WrittenSpan.ToArray());
        Assert.AreEqual(41, NinoDeserializer.Deserialize<RegisteredBeforeUse>(bytes).A);
        RegisteredBeforeUse existing = null;
        NinoDeserializer.Deserialize(bytes, ref existing);
        Assert.AreEqual(41, existing.A);
        Assert.AreEqual(41, ((RegisteredBeforeUse)NinoDeserializer.Deserialize(bytes, typeof(RegisteredBeforeUse))).A);
        Assert.AreEqual(41, Generic<RegisteredBeforeUse>(new RegisteredBeforeUse { A = 41 }).A);

        // Used now: the formatter stays, and registering again throws.
        Assert.ThrowsException<InvalidOperationException>(() =>
            NinoFormatterCache<RegisteredBeforeUse>.Register<MarkedIntHolderFormatter>());
    }

    [TestMethod]
    public void RegistrationAfterSerializeThrows()
    {
        var generated = NinoSerializer.Serialize(new RegisteredAfterSerialize { A = 7 });
        var e = Assert.ThrowsException<InvalidOperationException>(() =>
            NinoFormatterCache<RegisteredAfterSerialize>.Register<MarkedIntHolderFormatter>());
        StringAssert.Contains(e.Message, typeof(RegisteredAfterSerialize).FullName);

        // Nothing changed: every path still writes and reads with the generated formatter.
        CollectionAssert.AreEqual(generated, NinoSerializer.Serialize(new RegisteredAfterSerialize { A = 7 }));
        CollectionAssert.AreEqual(generated, NinoSerializer.Serialize((object)new RegisteredAfterSerialize { A = 7 }));
        Assert.AreEqual(7, NinoDeserializer.Deserialize<RegisteredAfterSerialize>(generated).A);
        Assert.AreEqual(7, Generic<RegisteredAfterSerialize>(new RegisteredAfterSerialize { A = 7 }).A);
    }

    [TestMethod]
    public void RegistrationAfterDeserializeThrows()
    {
        // Bytes of the generated formatter, written without touching the top-level API of the type.
        var buffer = new NinoArrayBufferWriter();
        var writer = new Writer(buffer);
        default(NinoGen.Nino_UnitTests_RegisteredAfterDeserializeFormatter).Serialize(new RegisteredAfterDeserialize { A = 3 }, ref writer);
        writer.Flush();
        var bytes = buffer.WrittenSpan.ToArray();
        Assert.AreEqual(3, NinoDeserializer.Deserialize<RegisteredAfterDeserialize>(bytes).A);
        var e = Assert.ThrowsException<InvalidOperationException>(() =>
            NinoFormatterCache<RegisteredAfterDeserialize>.Register<MarkedIntHolderFormatter>());
        StringAssert.Contains(e.Message, typeof(RegisteredAfterDeserialize).FullName);
        Assert.AreEqual(3, NinoDeserializer.Deserialize<RegisteredAfterDeserialize>(bytes).A);
    }

    [TestMethod]
    public void RegistrationAfterRawCopyThrows()
    {
        // A raw struct is copied by the top-level calls without calling its formatter: still a use.
        var bytes = NinoSerializer.Serialize(new RegisteredRawAfterUse { A = 9 });
        CollectionAssert.AreEqual(BitConverter.GetBytes(9), bytes);
        Assert.ThrowsException<InvalidOperationException>(() =>
            NinoFormatterCache<RegisteredRawAfterUse>.Register<MarkedIntHolderFormatter>());
        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(new RegisteredRawAfterUse { A = 9 }));
    }

    // Shared generic code: not intercepted, through the cache.
    private static T Generic<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));
}
