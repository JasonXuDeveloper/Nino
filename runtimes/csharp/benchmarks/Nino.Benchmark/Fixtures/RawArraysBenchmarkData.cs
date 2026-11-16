using LuminPack.Attribute;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark
{

    /// <summary>
    /// A raw struct with padding (a short, then a long at offset 8: 16 bytes in memory, 10 on the wire), which Nino packs
    /// through its permute kernels (RawArraysTest; Unity's BurstBenchmark PaddedRaw cases).
    /// </summary>
    [NinoType]
    [MemoryPackable, LuminPackable]
    [MessagePackObject]
    [ProtoContract]
    public partial struct PaddedRaw
    {
        [Key(0)] [ProtoMember(1)] public short A;
        [Key(1)] [ProtoMember(2)] public long B;
    }

    /// <summary>
    /// A generic [NinoType] (RawArraysTest's GenericBoxes): its members of type T are direct calls in the generated
    /// formatter, as <see cref="LongBox"/>'s are; the two should take the same time (Unity's IL2CPP once paid a virtual
    /// call per member of the generic one).
    /// </summary>
    [NinoType]
    [MemoryPackable, LuminPackable]
    [MessagePackObject]
    [ProtoContract]
    public sealed partial class Box<T>
    {
        [Key(0)] [ProtoMember(1)] public T A;
        [Key(1)] [ProtoMember(2)] public T B;
        [Key(2)] [ProtoMember(3)] public T C;
        [Key(3)] [ProtoMember(4)] public T D;
    }

    /// <summary>The non-generic twin of <see cref="Box{T}"/> over long (RawArraysTest's LongBoxes).</summary>
    [NinoType]
    [MemoryPackable, LuminPackable]
    [MessagePackObject]
    [ProtoContract]
    public sealed partial class LongBox
    {
        [Key(0)] [ProtoMember(1)] public long A;
        [Key(1)] [ProtoMember(2)] public long B;
        [Key(2)] [ProtoMember(3)] public long C;
        [Key(3)] [ProtoMember(4)] public long D;
    }
}
