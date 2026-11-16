// Generic polymorphic bases need a generator with the fix for #203 (rc.7): left out of A/B runs against older releases.
#if !NINO_BENCH_LEGACY
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

// GenericPolymorphicTest's types, shared with its v3 port (V3/GenericPolymorphicTest.cs).

[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeSealedItem { [ProtoMember(1)] public int X; [ProtoMember(2)] public long Y; }
[NinoType, MemoryPackable, ProtoContract] public partial class ProbeOpenItem { [ProtoMember(1)] public int X; [ProtoMember(2)] public long Y; }

[NinoType, MemoryPackable]
[MemoryPackUnion(0, typeof(ProbeA))]
[MemoryPackUnion(1, typeof(ProbeB))]
[MemoryPackUnion(2, typeof(ProbeC))]
[MemoryPackUnion(3, typeof(ProbeD))]
[MemoryPackUnion(4, typeof(ProbeE))]
[MemoryPackUnion(5, typeof(ProbeF))]
[MemoryPackUnion(6, typeof(ProbeG))]
[MemoryPackUnion(7, typeof(ProbeH))]
[MessagePackObject]
[Union(0, typeof(ProbeA))]
[Union(1, typeof(ProbeB))]
[Union(2, typeof(ProbeC))]
[Union(3, typeof(ProbeD))]
[Union(4, typeof(ProbeE))]
[Union(5, typeof(ProbeF))]
[Union(6, typeof(ProbeG))]
[Union(7, typeof(ProbeH))]
[ProtoContract]
[ProtoInclude(10, typeof(ProbeA))]
[ProtoInclude(11, typeof(ProbeB))]
[ProtoInclude(12, typeof(ProbeC))]
[ProtoInclude(13, typeof(ProbeD))]
[ProtoInclude(14, typeof(ProbeE))]
[ProtoInclude(15, typeof(ProbeF))]
[ProtoInclude(16, typeof(ProbeG))]
[ProtoInclude(17, typeof(ProbeH))]
public abstract partial class ProbeBase { [Key(0)] [ProtoMember(1)] public int V; }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeA : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeB : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeC : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeD : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeE : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeF : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeG : ProbeBase { }
[NinoType, MemoryPackable, MessagePackObject, ProtoContract] public sealed partial class ProbeH : ProbeBase { }

// protobuf-net: the subtypes of each closed ProbeBox<T> are added to its model at startup (RegisterUnions).
[NinoType, ProtoContract] public abstract class ProbeBox<T> { [ProtoMember(1)] public T V; }
// PBN0013 (ProbeBox<T> does not declare them): protobuf-net's reflection model takes them from RegisterUnions.
#pragma warning disable PBN0013
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxA : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxB : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxC : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxD : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxE : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxF : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxG : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeBoxH : ProbeBox<int> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupA : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupB : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupC : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupD : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupE : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupF : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupG : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeTupH : ProbeBox<(int, long)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrA : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrB : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrC : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrD : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrE : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrF : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrG : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeStrH : ProbeBox<(int, string)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScA : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScB : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScC : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScD : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScE : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScF : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScG : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeScH : ProbeBox<(ProbeSealedItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcA : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcB : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcC : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcD : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcE : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcF : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcG : ProbeBox<(ProbeOpenItem, int)> { }
[NinoType, MemoryPackable, ProtoContract] public sealed partial class ProbeOcH : ProbeBox<(ProbeOpenItem, int)> { }
#pragma warning restore PBN0013
#endif
