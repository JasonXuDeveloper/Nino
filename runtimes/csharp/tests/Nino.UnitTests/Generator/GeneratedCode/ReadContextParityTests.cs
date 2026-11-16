#if NET8_0_OR_GREATER
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Parity of the generator's <c>FormatterTraits.PreservesReadContext</c> (<c>FormatterTraitsOf.Of</c>, a hand list of
/// Nino.Core formatters) with what those formatters do at run time. A member read through such a formatter is
/// assumed never to register a back reference or defer a body, so the emitted reads skip <c>Reader.EndRegion</c> at
/// the end of a version-tolerant region and keep only the length check of <c>Reader.EndMember</c> in a keyed one.
/// Both hand the region's read context (references, deferred bodies) back to the outer reader: if one of these
/// formatters started creating that state, it would be lost at the end of the region (a back reference that does not
/// resolve, a deferred body never read) and nothing else would notice.
/// <para>
/// The check is at the reader: the read context is rented from a one-slot cache per thread
/// (<c>NinoReadContext.Rent</c>) the first time a reader needs one, so each read starts with a sentinel in that slot
/// and passes when the sentinel is still there afterwards. Every formatter the generator marks, and every composition
/// of them it marks (arrays, lists, memory, segments, sequences, nullables, pairs and dictionaries over such elements),
/// has a case here, read fresh and into the value it returned; a control case that tracks references proves the probe
/// sees a context being created. A non-generic Nino.Core formatter the generator newly marks fails the test until it has
/// a case; a generic one is checked through the compositions listed (add a case with each new composition).
/// </para>
/// </summary>
[TestClass]
public class ReadContextParityTests
{
    private interface IProbe
    {
        Type Formatter { get; }

        /// <summary>Whether reading the value back (fresh, then into the instance read) created a read context.</summary>
        bool ReadCreatesContext();
    }

    private sealed class Probe<T, TFormatter> : IProbe where TFormatter : struct, INinoFormatter<T>
    {
        private readonly T _value;

        public Probe(T value) => _value = value;

        public Type Formatter => typeof(TFormatter);

        public bool ReadCreatesContext()
        {
            byte[] bytes = NinoDirect.Serialize<T, TFormatter>(_value);
            T value = default;
            bool fresh = Read(bytes, ref value);
            bool reused = Read(bytes, ref value);
            return fresh || reused;
        }

        private static bool Read(byte[] bytes, ref T value)
        {
            // The thread's cached context is now the sentinel: a reader that needs a context takes it.
            var sentinel = NinoReadContext.Rent();
            NinoReadContext.Return(sentinel);
            var reader = new Reader(bytes);
            default(TFormatter).Deserialize(ref reader, ref value);
            Assert.IsTrue(reader.Eof, typeof(TFormatter).Name + " left bytes unread");
            var cached = NinoReadContext.Rent();
            NinoReadContext.Return(cached);
            reader.ReleaseReferences();
            return !ReferenceEquals(sentinel, cached);
        }
    }

    private static IProbe P<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T> =>
        new Probe<T, TFormatter>(value);

    /// <summary>The formatters the generator marks <c>PreservesReadContext</c>, each with a value to read.</summary>
    private static IEnumerable<IProbe> Preserving()
    {
        var uris = new[] { new Uri("https://example.com/a?b=1"), null, new Uri("file:///tmp/x") };
        var versions = new[] { new Version(1, 2, 3, 4), null, new Version(5, 6) };
        var strings = new[] { "a", null, "héllo 你好" };

        yield return P<string, StringFormatter>("héllo 你好");
        yield return P<string, Utf8StringFormatter>("héllo 你好");
        yield return P<DateTimeOffset, DateTimeOffsetFormatter>(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)));
        yield return P<BigInteger, BigIntegerFormatter>(BigInteger.Parse("-123456789012345678901234567890"));
        yield return P<Uri, UriFormatter>(uris[0]);
        yield return P<Uri, UriFormatter>(null);
        yield return P<Version, VersionFormatter>(versions[0]);
        yield return P<StringBuilder, StringBuilderFormatter>(new StringBuilder("héllo"));
        yield return P<CultureInfo, CultureInfoFormatter>(CultureInfo.InvariantCulture);
        yield return P<TimeZoneInfo, TimeZoneInfoFormatter>(TimeZoneInfo.Utc);
        yield return P<BitArray, BitArrayFormatter>(new BitArray(new[] { true, false, true, true }));

        yield return P<int[], UnmanagedArrayFormatter<int>>(new[] { 1, 2, 3 });
        yield return P<List<int>, UnmanagedListFormatter<List<int>, int>>(new List<int> { 1, 2, 3 });
        yield return P<int?, NullableFormatter<int, UnmanagedFormatter<int>>>(5);
        yield return P<int?, NullableFormatter<int, UnmanagedFormatter<int>>>(null);
        yield return P<DateTimeOffset?, NullableFormatter<DateTimeOffset, DateTimeOffsetFormatter>>(DateTimeOffset.UnixEpoch);
        yield return P<int?[], NullableArrayFormatter<int>>(new int?[] { 1, null, 3 });
        yield return P<List<int?>, NullableListFormatter<List<int?>, int>>(new List<int?> { null, 2 });

        yield return P<Uri[], ArrayFormatter<Uri, UriFormatter>>(uris);
        yield return P<string[], ArrayFormatter<string, StringFormatter>>(strings);
        yield return P<Version[][], ArrayFormatter<Version[], ArrayFormatter<Version, VersionFormatter>>>(
            new[] { versions, null, new Version[0] });
        yield return P<Memory<Version>, MemoryFormatter<Version, VersionFormatter>>(versions);
        yield return P<ReadOnlyMemory<string>, ReadOnlyMemoryFormatter<string, StringFormatter>>(strings);
        yield return P<ArraySegment<Uri>, ArraySegmentFormatter<Uri, UriFormatter>>(new ArraySegment<Uri>(uris, 1, 2));
        yield return P<ReadOnlySequence<string>, ReadOnlySequenceFormatter<string, StringFormatter>>(
            new ReadOnlySequence<string>(strings));
        yield return P<List<Uri>, ListFormatter<List<Uri>, Uri, UriFormatter>>(uris.ToList());
        yield return P<KeyValuePair<int, string>, KeyValuePairFormatter<int, string, UnmanagedFormatter<int>, StringFormatter>>(
            new KeyValuePair<int, string>(1, "a"));
        yield return P<KeyValuePair<Version, Uri>, KeyValuePairFormatter<Version, Uri, VersionFormatter, UriFormatter>>(
            new KeyValuePair<Version, Uri>(versions[0], uris[0]));
        yield return P<Dictionary<int, string>, DictionaryFormatter<int, string, UnmanagedFormatter<int>, StringFormatter>>(
            new Dictionary<int, string> { [1] = "a", [2] = null });
        yield return P<Dictionary<int, long>,
            DictionaryFormatter<Dictionary<int, long>, int, long, UnmanagedFormatter<int>, UnmanagedFormatter<long>>>(
            new Dictionary<int, long> { [1] = 2, [3] = 4 });
    }

    [TestMethod]
    public void FormattersMarkedPreservingCreateNoReadContext()
    {
        var compilation = TestCompilation.Create("ReadContext", Array.Empty<string>());
        foreach (var probe in Preserving())
        {
            string name = Display(probe.Formatter);
            Assert.IsTrue(Preserves(compilation, probe.Formatter), $"the generator no longer marks {name}: drop its case");
            Assert.IsFalse(probe.ReadCreatesContext(), $"{name} creates reference or deferral state when read");
        }
    }

    [TestMethod]
    public void TheProbeSeesAReadContext()
    {
        // A tracked class registers its reference: the control the probe must catch.
        var tracked = new DeepTracked { Value = 1 };
        Assert.IsTrue(P<DeepTracked, CachedFormatter<DeepTracked>>(tracked).ReadCreatesContext());
        Assert.IsTrue(P<DeepTracked[], ArrayFormatter<DeepTracked, CachedFormatter<DeepTracked>>>(new[] { tracked, tracked })
            .ReadCreatesContext());
    }

    [TestMethod]
    public void EveryNonGenericFormatterMarkedPreservingHasACase()
    {
        var compilation = TestCompilation.Create("ReadContext", Array.Empty<string>());
        var covered = new HashSet<Type>(Preserving().Select(p => p.Formatter));
        var missing = typeof(NinoDirect).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.IsValueType && !t.IsGenericTypeDefinition && t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INinoFormatter<>)))
            .Where(t => !covered.Contains(t) && Preserves(compilation, t))
            .Select(Display)
            .ToList();
        Assert.AreEqual(0, missing.Count, "marked PreservesReadContext without a case here: " + string.Join(", ", missing));
    }

    private static readonly Assembly GeneratorAssembly = typeof(Generator.Generation.NinoGenerator).Assembly;

    /// <summary>Whether the generator's <c>FormatterTraitsOf.Of</c> gives <paramref name="formatter"/> PreservesReadContext.</summary>
    private static bool Preserves(Compilation compilation, Type formatter)
    {
        var of = GeneratorAssembly.GetType("Nino.Generator.Generation.FormatterTraitsOf", true)!
            .GetMethod("Of", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(INamedTypeSymbol) }, null)!;
        var traits = GeneratorAssembly.GetType("Nino.Generator.Generation.FormatterTraits", true)!;
        int flag = Convert.ToInt32(Enum.Parse(traits, "PreservesReadContext"));
        int value = Convert.ToInt32(of.Invoke(null, new object[] { (INamedTypeSymbol)Symbol(compilation, formatter) }));
        return (value & flag) != 0;
    }

    /// <summary>The symbol of <paramref name="type"/> (closed generics and arrays included) in <paramref name="compilation"/>.</summary>
    private static ITypeSymbol Symbol(Compilation compilation, Type type)
    {
        if (type.IsArray)
            return compilation.CreateArrayTypeSymbol(Symbol(compilation, type.GetElementType()), type.GetArrayRank());
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var definition = (INamedTypeSymbol)Symbol(compilation, type.GetGenericTypeDefinition());
            return definition.Construct(type.GetGenericArguments().Select(a => Symbol(compilation, a)).ToArray());
        }

        // By assembly: GetTypeByMetadataName on the compilation gives null for a name two references define.
        string assembly = type.Assembly.GetName().Name;
        foreach (var referenced in compilation.SourceModule.ReferencedAssemblySymbols)
            if (referenced.Name == assembly && referenced.GetTypeByMetadataName(type.FullName!) is { } found)
                return found;
        Assert.Fail($"{type.FullName} not found in {assembly}");
        return null;
    }

    private static string Display(Type type) => type.IsGenericType
        ? type.Name.Substring(0, type.Name.IndexOf('`')) + "<" + string.Join(", ", type.GetGenericArguments().Select(Display)) + ">"
        : type.Name;
}
#endif
