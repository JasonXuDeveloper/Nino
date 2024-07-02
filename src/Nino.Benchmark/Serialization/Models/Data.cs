using System;
using ProtoBuf;
using MessagePack;
using Nino.Core;

namespace Nino.Benchmark.Models
{

    [Serializable]
    [ProtoContract]
    [NinoType]
    [MessagePackObject]
    [System.Runtime.Serialization.DataContract]
    public partial struct Data
    {
        [ProtoMember(1)]
        [NinoMember(1)]
        [Key(1)]
        [System.Runtime.Serialization.DataMember]
        public int X;

        [ProtoMember(2)]
        [NinoMember(2)]
        [Key(2)]
        [System.Runtime.Serialization.DataMember]
        public short Y;

        [ProtoMember(3)]
        [NinoMember(3)]
        [Key(3)]
        [System.Runtime.Serialization.DataMember]
        public long Z;

        [ProtoMember(4)]
        [NinoMember(4)]
        [Key(4)]
        [System.Runtime.Serialization.DataMember]
        public float F;

        [ProtoMember(5)]
        [NinoMember(5)]
        [Key(5)]
        [System.Runtime.Serialization.DataMember]
        public decimal D;

        [ProtoMember(6)]
        [NinoMember(6)]
        [Key(6)]
        [System.Runtime.Serialization.DataMember]
        public double Db;

        [ProtoMember(7)]
        [NinoMember(7)]
        [Key(7)]
        [System.Runtime.Serialization.DataMember]
        public bool Bo;

        [ProtoMember(8)]
        [NinoMember(8)]
        [Key(8)]
        [ProtoEnum]
        [System.Runtime.Serialization.EnumMember]
        public TestEnum En;

        public override string ToString()
        {
            return $"{X},{Y},{Z},{F},{D},{Db},{Bo},{En}";
        }
    }

    [Serializable]
    [ProtoContract]
    [System.Runtime.Serialization.DataContract]
    public enum TestEnum : byte
    {
        A = 1,
        B = 2
    }

    [Serializable]
    [ProtoContract]
    [NinoType]
    [MessagePackObject]
    [System.Runtime.Serialization.DataContract]
    public partial class NestedData
    {
        [ProtoMember(1)]
        [NinoMember(1)]
        [Key(1)]
        [System.Runtime.Serialization.DataMember]
        public string Name = "";

        [ProtoMember(2)] [NinoMember(2)] [Key(2)] [System.Runtime.Serialization.DataMember]
        public Data[] Ps = Array.Empty<Data>();

        public override string ToString()
        {
            return $"{Name},{Ps[0]}";
        }
    }
}