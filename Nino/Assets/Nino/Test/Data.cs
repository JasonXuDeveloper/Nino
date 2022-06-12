using System;
using ProtoBuf;
using UnityEngine;
using Nino.Serialization;
using System.Collections.Generic;

namespace Nino.Test
{
    [Serializable]
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

        public override string ToString()
        {
            return $"{x},{y},{z},{f},{d},{db},{bo},{en},{name}";
        }
    }

    [Serializable]
    [ProtoContract]
    public enum TestEnum : byte
    {
        A = 1,
        B = 2
    }

    [Serializable]
    [ProtoContract]
    [NinoSerialize]
    public partial class NestedData
    {
        [ProtoMember(1)] [NinoMember(1)] public string name;

        [ProtoMember(2)] [NinoMember(2)] public Data[] ps;
        
        public override string ToString()
        {
            return $"{name},{ps[0]}";
        }
    }

    [Serializable]
    [ProtoContract]
    [NinoSerialize]
    public partial class NestedData2
    {
        [ProtoMember(1)] [NinoMember(1)] public string name;

        [ProtoMember(2)] [NinoMember(2)] public Data[] ps;
        
        [ProtoMember(3)] [NinoMember(3)] public List<int> vs;

        public override string ToString()
        {
            return $"{name},{string.Join(",",vs)},{ps[0]}";
        }
    }

    [NinoSerialize]
    public partial class CustomTypeTest
    {
        [NinoMember(1)]
        public Vector3 v3;

        [NinoMember(2)]
        public DateTime dt;

        [NinoMember(3)]
        public int? ni;

        [NinoMember(4)]
        public List<Quaternion> qs;

        [NinoMember(5)]
        public Matrix4x4 m;

        [NinoMember(6)] public Dictionary<string, int> dict;

        public override string ToString()
        {
            return
                $"{v3}, {dt}, {ni}, {String.Join(",", qs)}, {m.ToString()}\n" +
                $"dict.keys: {string.Join(",", dict.Keys)},\ndict.values:{string.Join(",", dict.Values)}";
        }
    }

    [NinoSerialize(true)]
    public partial class IncludeAllClass
    {
        public int a;
        public long b;
        public float c;
        public double d;

        public override string ToString()
        {
            return $"{a}, {b}, {c}, {d}";
        }
    }

    [NinoSerialize()]
    public partial class NotIncludeAllClass
    {
        [NinoMember(1)]
        public int a;
        [NinoMember(2)]
        public long b;
        [NinoMember(3)]
        public float c;
        [NinoMember(4)]
        public double d;

        public override string ToString()
        {
            return $"{a}, {b}, {c}, {d}";
        }
    }
}