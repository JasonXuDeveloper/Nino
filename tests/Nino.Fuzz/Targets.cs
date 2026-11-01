using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Core;
using Nino.Fuzz.Golden;

namespace Nino.Fuzz
{
    public delegate object ReadFn(ReadOnlySpan<byte> data);
    public delegate object ReadIntoFn(ReadOnlySpan<byte> data, object existing);

    /// <summary>One root type the fuzzer reads: its seed inputs and how to read, reuse and write it.</summary>
    public sealed class Target
    {
        public string Name;
        /// <summary>What the type covers, for the list command and the README.</summary>
        public string Covers;
        public Func<List<byte[]>> Seeds;
        public ReadFn Read;
        /// <summary>Reads into an existing value (the reuse overload); null when the type has no reuse path worth fuzzing.</summary>
        public ReadIntoFn ReadInto;
        /// <summary>A new value to read into.</summary>
        public Func<object> Fresh;
        public Func<object, byte[]> Write;
        /// <summary>
        /// Every input is read into an existing value, and one Nino rejects is read again over poisoned freed memory:
        /// the value must not hold any of it (<see cref="StaleMemory"/>). Costs a collection per rejected input.
        /// </summary>
        public bool Poisoned;
        /// <summary>Read with <see cref="NinoSettings.Untrusted"/> on (set by <see cref="Configure"/>, before Nino's first use in the process).</summary>
        public bool Untrusted;

        /// <summary>Sets what the target reads with; called once per process, before anything is read.</summary>
        public void Configure()
        {
            if (Untrusted) NinoSettings.Untrusted = true;
        }
    }

    /// <summary>
    /// Every root type the fuzzer runs. To add one: declare the type in Types.cs, a sample value in Samples.cs, and a
    /// line here (README.md, "Adding a root type").
    /// </summary>
    public static class Targets
    {
        public static readonly IReadOnlyList<Target> All = new List<Target>
        {
            Golden(),
            Make("Prims", "primitives, enums, BCL value types, Nullable, BigInteger, Uri, Version, CultureInfo, StringBuilder, BitArray, Lazy, Half, Int128, Rune",
                Samples.Prims, Samples.PrimsSparse),
            Make("Strings", "ASCII, Latin-1, UTF-16 and [NinoUtf8] strings, string collections, chars", Samples.Strings),
            Make("Raws", "raw (unmanaged) structs, arrays and lists of them, blittable tuples, Guid and decimal arrays", Samples.RawRoot),
            Make("Colls", "List, arrays, multi-dimensional arrays, HashSet, Dictionary, Sorted*, LinkedList, Queue, Stack, Concurrent*, ILookup, interfaces, Memory, ArraySegment",
                Samples.Colls, Samples.CollsLarge),
            Make("Imm", "Nino.Immutable: ImmutableArray, List, HashSet, Dictionary, SortedSet, SortedDictionary, Queue, Stack",
                Samples.Imm, Samples.ImmLarge),
            Make("Holder", "polymorphism (abstract class, interface, object) mixed with reference tracking", Samples.Holder),
            Make("Keyed", "NinoFormat.Keyed classes and structs", Samples.Keyed),
            Make("Node", "deep nesting: a positional chain 300 deep", () => Samples.Chain(300)),
            Make("VNode", "deep nesting: a version-tolerant chain", () => Samples.VChain(300)),
            Make("KNode", "deep nesting: a keyed chain", () => Samples.KChain(300)),
            Make<VBase>("VBase", "version tolerance with inheritance (tolerant subtypes)", Samples.VTree),
            Make<Shape>("Shape", "polymorphic chain of an abstract base", Samples.Shapes),
            Make<TNode>("TNode", "reference tracking: cycles and shared nodes", Samples.Tracked),
            Make("Nest", "nesting through lists, arrays, dictionaries and structs", Samples.Nest),
            Stale("Stale", false),
            Stale("StaleUntrusted", true),
        };

        public static Target Find(string name) =>
            SelfTest.All.Concat(All).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"unknown target '{name}'; targets: {string.Join(", ", All.Select(t => t.Name))}");

        static Target Make<T>(string name, string covers, params Func<T>[] samples) where T : class => new Target
        {
            Name = name,
            Covers = covers,
            Seeds = () => samples.Select(s => NinoSerializer.Serialize(s())).ToList(),
            Read = data => NinoDeserializer.Deserialize<T>(data),
            ReadInto = (data, existing) =>
            {
                var value = (T)existing;
                NinoDeserializer.Deserialize(data, ref value);
                return value;
            },
            Fresh = () => samples[0](),
            Write = value => NinoSerializer.Serialize((T)value),
        };

        /// <summary>
        /// Large arrays allocated uninitialized whose read can reject the data after the allocation, read into a value
        /// of small ones over poisoned memory (<see cref="Target.Poisoned"/>).
        /// </summary>
        static Target Stale(string name, bool untrusted)
        {
            var target = Make(name, untrusted
                    ? "Stale in Untrusted mode, which also rejects DateTime and decimal values"
                    : "rejected reads into an existing value leave no freed memory in it: blocks with a bool, T?, bools, raw structs with a bool, dictionary and set entries",
                () => Samples.Stale(11_000));
            target.Fresh = Samples.StaleExisting;
            target.Poisoned = true;
            target.Untrusted = untrusted;
            return target;
        }

        static Target Golden() => new Target
        {
            Name = "Golden",
            Covers = "the pinned bytes of the unit tests' GoldenBytesTests (an assembly built with WEAK_VERSION_TOLERANCE)",
            Seeds = () => new List<byte[]> { GoldenCorpus.Bytes() },
            Read = data => GoldenCorpus.Read(data),
            ReadInto = (data, existing) => GoldenCorpus.ReadInto(data, (Nino.UnitTests.GoldenData)existing),
            Fresh = () => GoldenCorpus.Sample(),
            Write = value => GoldenCorpus.Write((Nino.UnitTests.GoldenData)value),
        };
    }
}
