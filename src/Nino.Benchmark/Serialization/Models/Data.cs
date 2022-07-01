using System;
using ProtoBuf;
using MessagePack;
using Nino.Serialization;

namespace Nino.Benchmark.Models
{

    [Serializable]
    [ProtoContract]
    [NinoSerialize]
    [MessagePackObject]
    [System.Runtime.Serialization.DataContract]
    public partial class Data
    {
        [ProtoMember(1)]
        [NinoMember(1)]
        [Key(1)]
        [System.Runtime.Serialization.DataMember]
        public int x;

        [ProtoMember(2)]
        [NinoMember(2)]
        [Key(2)]
        [System.Runtime.Serialization.DataMember]
        public short y;

        [ProtoMember(3)]
        [NinoMember(3)]
        [Key(3)]
        [System.Runtime.Serialization.DataMember]
        public long z;

        [ProtoMember(4)]
        [NinoMember(4)]
        [Key(4)]
        [System.Runtime.Serialization.DataMember]
        public float f;

        [ProtoMember(5)]
        [NinoMember(5)]
        [Key(5)]
        [System.Runtime.Serialization.DataMember]
        public decimal d;

        [ProtoMember(6)]
        [NinoMember(6)]
        [Key(6)]
        [System.Runtime.Serialization.DataMember]
        public double db;

        [ProtoMember(7)]
        [NinoMember(7)]
        [Key(7)]
        [System.Runtime.Serialization.DataMember]
        public bool bo;

        [ProtoMember(8)]
        [NinoMember(8)]
        [Key(8)]
        [ProtoEnum]
        [System.Runtime.Serialization.EnumMember]
        public TestEnum en;

        [ProtoMember(9)]
        [NinoMember(9)]
        [Key(9)]
        [System.Runtime.Serialization.DataMember]
        public string name = "";

        public override string ToString()
        {
            return $"{x},{y},{z},{f},{d},{db},{bo},{en},{name}";
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
    [NinoSerialize]
    [MessagePackObject]
    [System.Runtime.Serialization.DataContract]
    public partial class NestedData
    {
        [ProtoMember(1)]
        [NinoMember(1)]
        [Key(1)]
        [System.Runtime.Serialization.DataMember]
        public string name;

        [ProtoMember(2)]
        [NinoMember(2)]
        [Key(2)]
        [System.Runtime.Serialization.DataMember]
        public Data[] ps;

        public override string ToString()
        {
            return $"{name},{ps[0]}";
        }
    }
}