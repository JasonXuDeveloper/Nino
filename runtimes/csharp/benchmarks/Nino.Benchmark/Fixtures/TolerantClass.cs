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

// VersionTolerantTest's type, shared with its v3 port (V3/VersionTolerantTest.cs), which builds it in the project
// that turns on v3's version tolerance (V3/Versioned).

/// <summary>
/// The members of <see cref="SimpleClass"/> in a version-tolerant type: members can be appended later and data written
/// before still reads. Nino prefixes the body with its length (<see cref="NinoFormat.VersionTolerant"/>); MemoryPack
/// writes a member count and the length of every member (<see cref="GenerateType.VersionTolerant"/>, which needs an
/// explicit <see cref="MemoryPackOrderAttribute"/> on each member). MessagePack (integer keys: an array that later
/// versions extend) and protobuf (numbered fields) are version tolerant as they are.
/// </summary>
#if NINO_BENCH_V3
// v3's version tolerance is WEAK_VERSION_TOLERANCE, which its project (V3/Versioned) defines.
[NinoType]
#else
[NinoType(format: NinoFormat.VersionTolerant)]
#endif
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[ProtoContract]
public sealed partial class TolerantClass
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public int Id;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public bool Tag;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public Guid Guid;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public DateTime CreateTime;
    [MemoryPackOrder(4)] [Key(4)] [ProtoMember(5)] public string Name { get; set; }
    [MemoryPackOrder(5)] [Key(5)] [ProtoMember(6)] [NinoUtf8] public string Desc;
    [MemoryPackOrder(6)] [Key(6)] [ProtoMember(7)] public int[] Numbers { get; set; }
    [MemoryPackOrder(7)] [Key(7)] [ProtoMember(8)] public List<DateTime> Dates { get; set; }
    [MemoryPackOrder(8)] [Key(8)] [ProtoMember(9)] public Dictionary<int, string> Map1;
    [MemoryPackOrder(9)] [Key(9)] [ProtoMember(10)] public Dictionary<int, int> Map2 { get; set; }

#if !NINO_BENCH_V3
    // The v3 project of this type has no SimpleClass (it would build in v3's version-tolerant format there too):
    // V3/VersionTolerantTest.cs copies the members itself.
    public static TolerantClass From(SimpleClass c) => new()
    {
        Id = c.Id, Tag = c.Tag, Guid = c.Guid, CreateTime = c.CreateTime, Name = c.Name, Desc = c.Desc,
        Numbers = c.Numbers, Dates = c.Dates, Map1 = c.Map1, Map2 = c.Map2,
    };
#endif
}
