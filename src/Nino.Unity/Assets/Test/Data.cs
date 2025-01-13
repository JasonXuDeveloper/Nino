using System;
using ProtoBuf;
using System.Linq;
using UnityEngine;
using MessagePack;
using System.Collections.Generic;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using Nino.Core;

namespace Nino.Test
{
    [NinoType()]
    public class CollectionTest
    {
        [NinoMember(0)] public List<int> a = new List<int>();
        [NinoMember(1)] public List<string> b = new List<string>();
        [NinoMember(2)] public Dictionary<int, bool> c = new Dictionary<int, bool>();
        [NinoMember(3)] public Dictionary<string, bool> d = new Dictionary<string, bool>();
        [NinoMember(4)] public Dictionary<byte, string> e = new Dictionary<byte, string>();
    }
    
    [NinoType]
    public class ComplexData
    {
        [NinoMember(0)]
        public int[][] a;
        [NinoMember(1)]
        public List<int[]> b;
        [NinoMember(2)]
        public List<int>[] c;
        [NinoMember(3)]
        public Dictionary<string,Dictionary<string, int>> d;
        [NinoMember(4)]
        public Dictionary<string,Dictionary<string, int[][]>>[] e;
        [NinoMember(5)] 
        public Data[][] f;
        [NinoMember(6)]
        public List<Data[]> g;
        [NinoMember(7)]
        public Data[][][] h;
        [NinoMember(8)]
        public List<Data>[] i;
        [NinoMember(9)]
        public List<Data[]>[] j;
        public override string ToString()
        {
            return $"{string.Join(",", a.SelectMany(x => x).ToArray())},\n" +
                   $"{string.Join(",", b.SelectMany(x => x).ToArray())},\n" +
                   $"{string.Join(",", c.SelectMany(x => x).ToArray())},\n" +
                   $"{GetDictString(d)},\n" +
                   $"{string.Join(",\n", e.Select(GetDictString).ToArray())}\n" +
                   $"{string.Join(",\n", f.SelectMany(x => x).Select(x => x))}\n" +
                   $"{string.Join(",\n", g.SelectMany(x => x).Select(x => x))}\n" +
                   $"{string.Join(",\n", h.SelectMany(x => x).SelectMany(x => x).Select(x => x))}\n" +
                   $"{string.Join(",\n", i.SelectMany(x => x).Select(x => x))}\n" +
                   $"{string.Join(",\n", j.SelectMany(x => x).Select(x => x).SelectMany(x => x).Select(x => x))}\n";
        }

        private string GetDictString<K,V>(Dictionary<K,Dictionary<K,V>> ddd)
        {
            return $"{string.Join(",", ddd.Keys.ToList())},\n" +
                $"   {string.Join(",", ddd.Values.ToList().SelectMany(k=>k.Keys))},\n" +
                $"   {string.Join(",", ddd.Values.ToList().SelectMany(k=>k.Values))}";
        }
    }
    
    [Serializable]
    [ProtoContract]
    [NinoType]
    [MessagePackObject]
    public struct Data
    {
        [ProtoMember(1)] [NinoMember(1)] [BsonElement] [Key(1)]
        public int x;

        [ProtoMember(2)] [NinoMember(2)] [BsonElement] [Key(2)]
        public short y;

        [ProtoMember(3)] [NinoMember(3)] [BsonElement] [Key(3)]
        public long z;

        [ProtoMember(4)] [NinoMember(4)] [BsonElement] [Key(4)]
        public float f;

        [ProtoMember(5)] [NinoMember(5)] [BsonElement] [Key(5)]
        public decimal d;

        [ProtoMember(6)] [NinoMember(6)] [BsonElement] [Key(6)]
        public double db;

        [ProtoMember(7)] [NinoMember(7)] [BsonElement] [Key(7)]
        public bool bo;

        [ProtoMember(8)] [NinoMember(8)] [BsonElement] [Key(8)]
        public TestEnum en;

        public override string ToString()
        {
            return $"{x},{y},{z},{f},{d},{db},{bo},{en}";
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
    [NinoType]
    [MessagePackObject]
    public class NestedData
    {
        [ProtoMember(1)] [NinoMember(1)] [BsonElement] [Key(1)]
        public string name;

        [ProtoMember(2)] [NinoMember(2)] [BsonElement] [Key(2)]
        public Data[] ps;

        public override string ToString()
        {
            return $"{name},{ps[0]}";
        }
    }

    [Serializable]
    [ProtoContract]
    [NinoType]
    public class NestedData2
    {
        [ProtoMember(1)] [NinoMember(1)] public string name;

        [ProtoMember(2)] [NinoMember(2)] public Data[] ps;

        [ProtoMember(3)] [NinoMember(3)] public List<int> vs;

        public override string ToString()
        {
            return $"{name},{string.Join(",", vs)},{ps[0]}";
        }
    }

    [NinoType(false)]
    public partial class PrimitiveTypeTest
    {
        [NinoMember(1)] public Vector3 v3;

        [NinoMember(2)] private DateTime dt;
        
        [NinoIgnore]
        public DateTime Time
        {
            get => dt;
            set => dt = value;
        }

        [NinoMember(3)] public int? ni { get; set; }

        [NinoMember(4)] public List<Quaternion> qs;

        [NinoMember(5)] public Matrix4x4 m;

        [NinoMember(6)] public Dictionary<string, int> dict;

        [NinoMember(7)] public Dictionary<string, Data> dict2;

        public override string ToString()
        {
            return
                $"{v3}, {dt}, {ni}, {String.Join(",", qs)}, {m.ToString()}\n" +
                $"dict.keys: {string.Join(",", dict.Keys)},\ndict.values:{string.Join(",", dict.Values)}\n" +
                $"dict2.keys: {string.Join(",", dict2.Keys)},\ndict2.values:{string.Join(",", dict2.Values)}\n";
        }
    }

    [NinoType]
    public class IncludeAllClassCodeGen
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

    [Serializable]
    [ProtoContract]
    [NinoType(false)]
    [MessagePackObject]
    public class NotIncludeAllClass
    {
        [ProtoMember(10)] [BsonElement] [Key(0)] [NinoMember(0)]
        public int a;

        [ProtoMember(1)] [BsonElement] [Key(1)] [NinoMember(1)]
        public long b;

        [ProtoMember(2)] [BsonElement] [Key(2)] [NinoMember(2)]
        public float c;

        [ProtoMember(3)] [BsonElement] [Key(3)] [NinoMember(3)]
        public double d;

        public override string ToString()
        {
            return $"{a}, {b}, {c}, {d}";
        }
    }

    [Serializable]
    [ProtoContract]
    [NinoType]
    [MessagePackObject]
    public class BuildTestDataCodeGen
    {
        [ProtoMember(100)] [BsonElement] [Key(0)] [NinoMember(0)]
        public byte a;

        [ProtoMember(1)] [BsonElement] [Key(1)] [NinoMember(1)]
        public sbyte b;

        [ProtoMember(2)] [BsonElement] [Key(2)] [NinoMember(2)]
        public short c;

        [ProtoMember(3)] [BsonElement] [Key(3)] [NinoMember(3)]
        public ushort d;

        [ProtoMember(4)] [BsonElement] [Key(4)] [NinoMember(4)]
        public int e;

        [ProtoMember(5)] [BsonElement] [Key(5)] [NinoMember(5)]
        public uint f;

        [ProtoMember(6)] [BsonElement] [Key(6)] [NinoMember(6)]
        public long g;

        [ProtoMember(7)] [BsonElement] [Key(7)] [NinoMember(7)]
        public ulong h;

        [ProtoMember(8)] [BsonElement] [Key(8)] [NinoMember(8)]
        public float i;

        [ProtoMember(9)] [BsonElement] [Key(9)] [NinoMember(9)]
        public double j;

        [ProtoMember(10)] [BsonElement] [Key(10)] [NinoMember(10)]
        public decimal k;

        [ProtoMember(11)] [BsonElement] [Key(11)] [NinoMember(11)]
        public bool l;

        [ProtoMember(12)] [BsonElement] [Key(12)] [NinoMember(12)]
        public char m;

        [ProtoMember(13)] [BsonElement] [Key(13)] [NinoMember(13)]
        public string n;

        [ProtoMember(14)] [BsonElement] [Key(14)] [NinoMember(14)]
        public List<int> o = new List<int>();

        [ProtoMember(15)] [BsonElement] [Key(15)] [NinoMember(15)]
        public List<NotIncludeAllClass> p = new List<NotIncludeAllClass>();

        [ProtoMember(16)] [BsonElement] [Key(16)] [NinoMember(16)]
        public byte[] q = Array.Empty<byte>();

        [ProtoMember(17)] [BsonElement] [Key(17)] [NinoMember(17)]
        public NotIncludeAllClass[] r = Array.Empty<NotIncludeAllClass>();

        [Key(18)]
        [NinoMember(18)]
#if !IL2CPP || UNITY_EDITOR
        [ProtoMember(18)]
        [BsonElement]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfArrays)]
#endif
        public Dictionary<string, NotIncludeAllClass> s = new Dictionary<string, NotIncludeAllClass>();

        [Key(19)]
        [NinoMember(19)]

#if !IL2CPP || UNITY_EDITOR
        [ProtoMember(19)]
        [BsonElement]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfArrays)]
#endif
        public Dictionary<NotIncludeAllClass, int> t = new Dictionary<NotIncludeAllClass, int>();

        [Key(20)]
        [NinoMember(20)]

#if !IL2CPP || UNITY_EDITOR
        [ProtoMember(20)]
        [BsonElement]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfArrays)]
#endif
        public Dictionary<string, int> u = new Dictionary<string, int>();

        [Key(21)]
        [NinoMember(21)]

#if !IL2CPP || UNITY_EDITOR
        [ProtoMember(21)]
        [BsonElement]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfArrays)]
#endif
        public Dictionary<NotIncludeAllClass, NotIncludeAllClass> v =
            new Dictionary<NotIncludeAllClass, NotIncludeAllClass>();

        public override string ToString()
        {
            return $"{a},{b},{c},{d},{e},{f},{g},{h},{i},{j},{k},{l},{m},{n},{string.Join("/", o)}," +
                   $"{string.Join("/", p)},{string.Join("/", q)},{string.Join("/", r.ToList())}," +
                   $"{string.Join("/", s.Keys.ToList())}-{string.Join("/", s.Values.ToList())}," +
                   $"{string.Join("/", t.Keys.ToList())}-{string.Join("/", t.Values.ToList())}," +
                   $"{string.Join("/", u.Keys.ToList())}-{string.Join("/", v.Values.ToList())}," +
                   $"{string.Join("/", v.Keys.ToList())}-{string.Join("/", v.Values.ToList())}";
        }
    }
}