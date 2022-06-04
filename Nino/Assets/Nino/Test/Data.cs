using Nino.Serialization;
using ProtoBuf;

namespace Nino.Test
{
    [ProtoContract]
    [NinoSerialize]
    public partial struct Data
    {
        [ProtoMember(1)] [NinoMember(1)] public int x;

        [ProtoMember(2)] [NinoMember(2)] public short y;

        [ProtoMember(3)] [NinoMember(3)] public long z;

        [ProtoMember(4)] [NinoMember(4)] public float f;

        [ProtoMember(5)] [NinoMember(5)] public decimal d;

        [ProtoMember(6)] [NinoMember(6)] public double db;

        [ProtoMember(7)] [NinoMember(7)] public bool bo;

        [ProtoMember(8)] [NinoMember(8)] public TestEnum en;

        [ProtoMember(9)] [NinoMember(9)] public string name;
    }

    [ProtoContract]
    public enum TestEnum : byte
    {
        A = 1,
        B = 2
    }

    [ProtoContract]
    [NinoSerialize]
    public partial class NestedData
    {
        [ProtoMember(1)] [NinoMember(1)] public string name;

        [ProtoMember(2)] [NinoMember(2)] public Data[] ps;
    }

}