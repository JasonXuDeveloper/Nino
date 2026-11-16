using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nino.Core;
#if V3
using P3.NinoGen;
#endif

namespace Parts;

// The members of the benchmark's SimpleClass one at a time, plus struct and vector arrays. Built twice: against
// Nino 3.3.13 from NuGet (v3/, V3 defined) and against this repository (v4/).

[NinoType] public sealed partial class PHead { public int Id; public bool Tag; public Guid G; public DateTime T; }
[NinoType] public sealed partial class PStr { public string A; }
[NinoType] public sealed partial class PUtf8 { [NinoUtf8] public string A; }
[NinoType] public sealed partial class PInts { public int[] A; }
[NinoType] public sealed partial class PDates { public List<DateTime> A; }
[NinoType] public sealed partial class PMap1 { public Dictionary<int, string> A; }
[NinoType] public sealed partial class PMap2 { public Dictionary<int, int> A; }
[NinoType] public sealed partial class PEmpty { public int A; }
[NinoType, StructLayout(LayoutKind.Sequential, Pack = 4)] public partial struct PStruct { public int Id; public DateTime T; }
#if !V3
// Version tolerant, over two levels of a hierarchy (v4 only: v3 has no version tolerance).
[NinoType(format: NinoFormat.VersionTolerant)] public partial class PTolBase { public int Id; public bool Tag; }
[NinoType(format: NinoFormat.VersionTolerant)] public sealed partial class PTol : PTolBase { public Guid G; public DateTime T; public string S; }
#endif

[MemoryDiagnoser, GcServer(true)]
public class B
{
    static readonly Random R = new Random(1);
    public static readonly PHead Head = new PHead { Id = 5, Tag = true, G = Guid.NewGuid(), T = DateTime.Now };
    public static readonly PStr Str = new PStr { A = Guid.NewGuid().ToString() };
    public static readonly PUtf8 Utf8 = new PUtf8 { A = string.Concat(Enumerable.Range(0, 10).Select(_ => Guid.NewGuid().ToString())) };
    public static readonly PInts Ints = new PInts { A = Enumerable.Range(0, 100).Select(_ => R.Next()).ToArray() };
    public static readonly PDates Dates = new PDates { A = Enumerable.Range(0, 10).Select(_ => DateTime.Now.AddSeconds(R.Next())).ToList() };
    public static readonly PMap1 Map1 = new PMap1 { A = Enumerable.Range(0, 10).ToDictionary(n => n, n => n.ToString()) };
    public static readonly PMap2 Map2 = new PMap2 { A = Enumerable.Range(0, 10).ToDictionary(n => n, n => n * 2) };
    public static readonly PEmpty Empty = new PEmpty { A = 1 };
    public static readonly PStruct[] Structs = Enumerable.Range(0, 100).Select(i => new PStruct { Id = i, T = DateTime.Now }).ToArray();
    public static readonly Vector4[] Vectors = Enumerable.Range(0, 10000).Select(i => new Vector4(i, i, i, i)).ToArray();

#if V3
    readonly ArrayBufferWriter<byte> _w = new ArrayBufferWriter<byte>(1 << 20);
    static readonly byte[] bEmpty = Serializer.Serialize(Empty);
    [Benchmark] public PEmpty DEmpty() { Deserializer.Deserialize(bEmpty, out PEmpty v); return v; }
    static readonly byte[] bHead = Serializer.Serialize(Head);
    [Benchmark] public PHead DHead() { Deserializer.Deserialize(bHead, out PHead v); return v; }
    static readonly byte[] bStr = Serializer.Serialize(Str);
    [Benchmark] public PStr DStr() { Deserializer.Deserialize(bStr, out PStr v); return v; }
    static readonly byte[] bUtf8 = Serializer.Serialize(Utf8);
    [Benchmark] public PUtf8 DUtf8() { Deserializer.Deserialize(bUtf8, out PUtf8 v); return v; }
    static readonly byte[] bInts = Serializer.Serialize(Ints);
    [Benchmark] public PInts DInts() { Deserializer.Deserialize(bInts, out PInts v); return v; }
    static readonly byte[] bDates = Serializer.Serialize(Dates);
    [Benchmark] public PDates DDates() { Deserializer.Deserialize(bDates, out PDates v); return v; }
    static readonly byte[] bMap1 = Serializer.Serialize(Map1);
    [Benchmark] public PMap1 DMap1() { Deserializer.Deserialize(bMap1, out PMap1 v); return v; }
    static readonly byte[] bMap2 = Serializer.Serialize(Map2);
    [Benchmark] public PMap2 DMap2() { Deserializer.Deserialize(bMap2, out PMap2 v); return v; }
    static readonly byte[] bStructs = Serializer.Serialize(Structs);
    [Benchmark] public PStruct[] DStructs() { Deserializer.Deserialize(bStructs, out PStruct[] v); return v; }
    static readonly byte[] bVectors = Serializer.Serialize(Vectors);
    [Benchmark] public Vector4[] DVectors() { Deserializer.Deserialize(bVectors, out Vector4[] v); return v; }
    [Benchmark] public void SVectors() { _w.ResetWrittenCount(); Serializer.Serialize(Vectors, _w); }
    [Benchmark] public void SMap1() { _w.ResetWrittenCount(); Serializer.Serialize(Map1, _w); }
    [Benchmark] public void SUtf8() { _w.ResetWrittenCount(); Serializer.Serialize(Utf8, _w); }
    [Benchmark] public void SHead() { _w.ResetWrittenCount(); Serializer.Serialize(Head, _w); }
    [Benchmark] public byte[] SHeadArr() => Serializer.Serialize(Head);
#else
    readonly NinoArrayBufferWriter _w = new NinoArrayBufferWriter(1 << 20);
    static readonly byte[] bEmpty = NinoSerializer.Serialize(Empty);
    [Benchmark] public PEmpty DEmpty() => NinoDeserializer.Deserialize<PEmpty>(bEmpty);
    static readonly byte[] bHead = NinoSerializer.Serialize(Head);
    [Benchmark] public PHead DHead() => NinoDeserializer.Deserialize<PHead>(bHead);
    static readonly byte[] bStr = NinoSerializer.Serialize(Str);
    [Benchmark] public PStr DStr() => NinoDeserializer.Deserialize<PStr>(bStr);
    static readonly byte[] bUtf8 = NinoSerializer.Serialize(Utf8);
    [Benchmark] public PUtf8 DUtf8() => NinoDeserializer.Deserialize<PUtf8>(bUtf8);
    static readonly byte[] bInts = NinoSerializer.Serialize(Ints);
    [Benchmark] public PInts DInts() => NinoDeserializer.Deserialize<PInts>(bInts);
    static readonly byte[] bDates = NinoSerializer.Serialize(Dates);
    [Benchmark] public PDates DDates() => NinoDeserializer.Deserialize<PDates>(bDates);
    static readonly byte[] bMap1 = NinoSerializer.Serialize(Map1);
    [Benchmark] public PMap1 DMap1() => NinoDeserializer.Deserialize<PMap1>(bMap1);
    static readonly byte[] bMap2 = NinoSerializer.Serialize(Map2);
    [Benchmark] public PMap2 DMap2() => NinoDeserializer.Deserialize<PMap2>(bMap2);
    static readonly byte[] bStructs = NinoSerializer.Serialize(Structs);
    [Benchmark] public PStruct[] DStructs() => NinoDeserializer.Deserialize<PStruct[]>(bStructs);
    static readonly byte[] bVectors = NinoSerializer.Serialize(Vectors);
    [Benchmark] public Vector4[] DVectors() => NinoDeserializer.Deserialize<Vector4[]>(bVectors);
    [Benchmark] public void SVectors() { _w.ResetWrittenCount(); NinoSerializer.Serialize(Vectors, _w); }
    [Benchmark] public void SMap1() { _w.ResetWrittenCount(); NinoSerializer.Serialize(Map1, _w); }
    [Benchmark] public void SUtf8() { _w.ResetWrittenCount(); NinoSerializer.Serialize(Utf8, _w); }
    [Benchmark] public void SHead() { _w.ResetWrittenCount(); NinoSerializer.Serialize(Head, _w); }
    [Benchmark] public byte[] SHeadArr() => NinoSerializer.Serialize(Head);
    public static readonly PTol Tol = new PTol { Id = 5, Tag = true, G = Guid.NewGuid(), T = DateTime.Now, S = "version tolerant" };
    static readonly byte[] bTol = NinoSerializer.Serialize(Tol);
    [Benchmark] public PTol DTol() => NinoDeserializer.Deserialize<PTol>(bTol);
    [Benchmark] public void STol() { _w.ResetWrittenCount(); NinoSerializer.Serialize(Tol, _w); }
#endif

    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(B).Assembly).Run(args);
    }
}
