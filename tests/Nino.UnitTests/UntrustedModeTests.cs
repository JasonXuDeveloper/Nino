#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>Unmanaged, every field serialized: written as its memory. A and the time mark where its fields are.</summary>
[NinoType]
public struct UntrustedRaw
{
    public int A;
    public bool B;
    public DateTime When;
}

/// <summary>Not version tolerant: past the inline depth its bodies are deferred outside any region.</summary>
[NinoType]
public sealed class UntrustedChain
{
    public int V;
    public UntrustedChain Next;

    public static UntrustedChain Of(int depth)
    {
        UntrustedChain node = null;
        for (int i = depth; i > 0; i--)
            node = new UntrustedChain { V = i, Next = node };
        return node;
    }
}

/// <summary>Raw members of types with values to check, each found in the data by its value.</summary>
[NinoType]
public sealed class UntrustedHolder
{
    public int Id;
    public DateTime When;
    public decimal Price;
    public UntrustedRaw Raw;
    public (bool, int) Pair;
    public string Name;
}

/// <summary>
/// <see cref="NinoSettings.Untrusted"/>: seeded comparers, values checked as their constructors would, and no bytes
/// after the value. Each case runs in a copy of Nino of its own (<see cref="SettingsTests"/>), where it turns the mode
/// on before anything is read.
/// </summary>
[TestClass]
public class UntrustedModeTests
{
    [TestMethod]
    public void TrustedModeCopiesValues()
    {
        // The default mode: what the raw paths copy is not checked (a bool alone is: StrictReadTests).
        Assert.IsFalse(NinoSettings.Untrusted);
        var bad = BitConverter.GetBytes(SettingsProbes.BadTicks);
        _ = NinoDeserializer.Deserialize<DateTime>(bad);
        Assert.AreEqual(5, NinoDeserializer.Deserialize<int>(new byte[] { 5, 0, 0, 0, 9 }));
        Assert.AreSame(EqualityComparer<int>.Default,
            NinoDeserializer.Deserialize<Dictionary<int, int>>(NinoSerializer.Serialize(new Dictionary<int, int> { [1] = 2 }))
                .Comparer);
    }

    [TestMethod]
    public void SeededComparers() => SettingsTests.RunProbe(nameof(SettingsProbes.UntrustedComparers));

    [TestMethod]
    public void ValuesAreChecked() => SettingsTests.RunProbe(nameof(SettingsProbes.UntrustedValues));

    [TestMethod]
    public void TrailingBytesAreInvalid() => SettingsTests.RunProbe(nameof(SettingsProbes.UntrustedTrailing));
}

public static partial class SettingsProbes
{
    /// <summary>DateTime.MaxValue's ticks + 1, of kind Utc.</summary>
    public static readonly ulong BadTicks = (ulong)(DateTime.MaxValue.Ticks + 1) | 1UL << 62;

    private static string Fails<T>(byte[] data, string what)
    {
        try
        {
            NinoDeserializer.Deserialize<T>(data);
        }
        catch (NinoDataException)
        {
            return null;
        }
        catch (Exception e)
        {
            return $"{what}: {e.GetType().Name}: {e.Message}";
        }

        return $"{what}: read";
    }

    private static int Find(byte[] data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                return i;
        throw new InvalidOperationException("pattern not found");
    }

    private static byte[] With(byte[] data, int at, params byte[] bytes)
    {
        var copy = (byte[])data.Clone();
        bytes.CopyTo(copy, at);
        return copy;
    }

    public static string UntrustedComparers(byte[] _)
    {
        NinoSettings.Untrusted = true;
        // Longs whose halves cancel out: one bucket under the default comparer, more of them than the limit allows.
        var keys = Enumerable.Range(1, NinoSettings.DefaultMaxHashCollisions + 50).Select(i => (long)i << 32 | (uint)i)
            .ToArray();
        var map = NinoDeserializer.Deserialize<Dictionary<long, int>>(
            NinoSerializer.Serialize(keys.ToDictionary(k => k, k => (int)k)));
        var set = NinoDeserializer.Deserialize<HashSet<long>>(NinoSerializer.Serialize(keys.ToHashSet()));
        var names = NinoDeserializer.Deserialize<Dictionary<string, int>>(
            NinoSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }));
        var guids = NinoDeserializer.Deserialize<HashSet<Guid>>(NinoSerializer.Serialize(new HashSet<Guid> { Guid.Empty }));
        var objects = NinoDeserializer.Deserialize<Dictionary<FloodKey, int>>(
            NinoSerializer.Serialize(new Dictionary<FloodKey, int> { [new FloodKey { Id = 1 }] = 1 }));
        var lookup = NinoDeserializer.Deserialize<ILookup<int, string>>(
            NinoSerializer.Serialize(new[] { "a", "bb" }.ToLookup(s => s.Length)));
        if (map.Count != keys.Length || keys.Any(k => map[k] != (int)k) || set.Count != keys.Length ||
            !keys.All(set.Contains) || names["b"] != 2 || !guids.Contains(Guid.Empty) || lookup[2].Single() != "bb")
            return "contents";
        if (ReferenceEquals(map.Comparer, EqualityComparer<long>.Default) ||
            ReferenceEquals(set.Comparer, EqualityComparer<long>.Default) ||
            ReferenceEquals(names.Comparer, EqualityComparer<string>.Default) ||
            ReferenceEquals(guids.Comparer, EqualityComparer<Guid>.Default))
            return "default comparer";
        // Other keys keep the default one, with the collision limit; equality is the default one.
        if (!ReferenceEquals(objects.Comparer, EqualityComparer<FloodKey>.Default) || !map.Comparer.Equals(5, 5) ||
            map.Comparer.Equals(5, 6) || !names.Comparer.Equals("a", new string('a', 1)))
            return "comparer";
        // An existing dictionary keeps its own.
        var existing = new Dictionary<long, int>();
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new Dictionary<long, int> { [1] = 1 }), ref existing);
        return ReferenceEquals(existing.Comparer, EqualityComparer<long>.Default) && existing[1] == 1 ? "ok" : "existing";
    }

    public static string UntrustedValues(byte[] _)
    {
        NinoSettings.Untrusted = true;
        byte[] Bytes<T>(T value) => NinoSerializer.Serialize(value);
        var badTicks = BitConverter.GetBytes(BadTicks);
        var when = new DateTime(0x0123456789ABCDEF, DateTimeKind.Utc);
        var whenBytes = BitConverter.GetBytes(when.ToBinary());
        var price = new decimal(0x11223344, 0x55667788, 0x0A0B0C0D, false, 3);
        var holder = new UntrustedHolder
        {
            Id = 1, When = when, Price = price, Raw = new UntrustedRaw { A = 0x5A5A5A5A, B = true, When = when },
            Pair = (true, 0x6B6B6B6B), Name = "n",
        };
        var data = Bytes(holder);
        var back = NinoDeserializer.Deserialize<UntrustedHolder>(data);
        if (back.When != when || back.Price != price || back.Raw.When != when || !back.Raw.B || back.Pair != (true, 0x6B6B6B6B))
            return "holder";
        // Local times in the repeated hour of a daylight saving change keep a fourth kind: valid.
        ulong localAmbiguous = (ulong)when.Ticks | 3UL << 62;
        if (NinoDeserializer.Deserialize<DateTime>(BitConverter.GetBytes(localAmbiguous)).Ticks != when.Ticks)
            return "kind 3";

        int whenAt = Find(data, whenBytes);
        int priceAt = Find(data, new byte[] { 0x0D, 0x0C, 0x0B, 0x0A }) - 4; // flags, then hi
        int rawAt = Find(data, BitConverter.GetBytes(0x5A5A5A5A));
        int pairAt = Find(data, BitConverter.GetBytes(0x6B6B6B6B)) - 1; // the wire packs (bool, int): 5 bytes
        var scale29 = BitConverter.GetBytes(29 << 16);
        var dateOnly = BitConverter.GetBytes(DateOnly.MaxValue.DayNumber + 1);
        var timeOnly = BitConverter.GetBytes(TimeOnly.MaxValue.Ticks + 1);
        var flagged = BitConverter.GetBytes(3 << 16 | 1);
        // UntrustedRaw.B follows A on the wire.
        byte[] BoolAfterA(byte[] bytes) => With(bytes, Find(bytes, BitConverter.GetBytes(0x5A5A5A5A)) + 4, 2);

        return Fails<UntrustedHolder>(With(data, whenAt, badTicks), "DateTime member") ??
               Fails<UntrustedHolder>(With(data, priceAt, scale29), "decimal member (scale)") ??
               Fails<UntrustedHolder>(With(data, priceAt, flagged), "decimal member (flags)") ??
               Fails<UntrustedHolder>(With(data, rawAt + 4, 2), "bool in a raw struct member") ??
               Fails<UntrustedHolder>(With(data, rawAt + 8, badTicks), "DateTime in a raw struct member") ??
               Fails<UntrustedHolder>(With(data, pairAt, 2), "bool in a tuple member") ??
               Fails<DateTime>(badTicks, "DateTime") ??
               Fails<decimal>(With(Bytes(1.5m), 0, scale29), "decimal") ??
               Fails<DateOnly>(dateOnly, "DateOnly") ??
               Fails<TimeOnly>(timeOnly, "TimeOnly") ??
               Fails<DateTime?>(With(Bytes((DateTime?)when), 1, badTicks), "DateTime?") ??
               Fails<UntrustedRaw>(BoolAfterA(Bytes(holder.Raw)), "raw struct") ??
               Fails<DateTime[]>(With(Bytes(new[] { when, when }), 12, badTicks), "DateTime[]") ??
               Fails<List<DateTime>>(With(Bytes(new List<DateTime> { when }), 4, badTicks), "List<DateTime>") ??
               Fails<DateTime?[]>(With(Bytes(new DateTime?[] { when }), 4 + 8, badTicks), "DateTime?[]") ??
               Fails<(bool, int)[]>(With(Bytes(new[] { (true, 1), (false, 2) }), 4, 2), "(bool, int)[]") ??
               Fails<UntrustedRaw[]>(BoolAfterA(Bytes(new[] { holder.Raw })), "UntrustedRaw[]") ??
               Fails<HashSet<DateTime>>(With(Bytes(new HashSet<DateTime> { when }), 4, badTicks), "HashSet<DateTime>") ??
               Fails<Dictionary<int, DateTime>>(With(Bytes(new Dictionary<int, DateTime> { [1] = when }), 8, badTicks),
                   "Dictionary<int, DateTime>") ??
               Fails<Dictionary<DateTime, string>>(
                   With(Bytes(new Dictionary<DateTime, string> { [when] = "a" }), 4, badTicks), "Dictionary<DateTime, string>") ??
               Fails<KeyValuePair<int, DateTime>[]>(
                   With(Bytes(new[] { new KeyValuePair<int, DateTime>(1, when) }), 8, badTicks), "KeyValuePair[]") ??
               "ok";
    }

    public static string UntrustedTrailing(byte[] _)
    {
        NinoSettings.Untrusted = true;
        byte[] Plus(byte[] data) => data.Concat(new byte[] { 0 }).ToArray();
        var holder = NinoSerializer.Serialize(new UntrustedHolder { Name = "x" });
        var chain = NinoSerializer.Serialize(StrictChain.Of(300));
        var deep = NinoSerializer.Serialize(Chain(300));
        var plain = NinoSerializer.Serialize(UntrustedChain.Of(300));
        var ints = NinoSerializer.Serialize(new[] { 1, 2 });
        // Exactly the value: read, also with its bodies deferred past the inline depth (in a region and outside).
        if (NinoDeserializer.Deserialize<UntrustedHolder>(holder).Name != "x" ||
            NinoDeserializer.Deserialize<StrictChain>(chain).Depth() != 300 ||
            NinoDeserializer.Deserialize<UntrustedStructNode>(deep).V != 1 ||
            NinoDeserializer.Deserialize<UntrustedChain>(plain).Next.Next.V != 3 ||
            NinoDeserializer.Deserialize<int[]>(ints).Length != 2 ||
            NinoDeserializer.Deserialize<int>(new byte[4]) != 0)
            return "exact";
        UntrustedHolder into = null;
        object boxed = null;
        return Fails<int>(new byte[5], "int") ??
               Fails<int[]>(Plus(ints), "int[]") ??
               Fails<UntrustedHolder>(Plus(holder), "class") ??
               Fails<StrictChain>(Plus(chain), "deferred in a region") ??
               Fails<UntrustedChain>(Plus(plain), "deferred outside regions") ??
               Fails<string>(Plus(NinoSerializer.Serialize("s")), "string") ??
               Throws<NinoDataException>(() => NinoDeserializer.Deserialize(Plus(holder), ref into), "into") ??
               Throws<NinoDataException>(() => NinoDeserializer.DeserializeBoxed(Plus(holder), ref boxed,
                   typeof(UntrustedHolder)), "boxed") ??
               "ok";
    }
}
#endif
