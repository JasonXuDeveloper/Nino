using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Core;
using UnityEngine;

namespace Nino.Test
{
    [NinoType]
    public class ComplexData
    {
        public int[][] a;
        public List<int[]> b;
        public List<int>[] c;
        public Dictionary<string, Dictionary<string, int>> d;
        public Dictionary<string, Dictionary<string, int[][]>>[] e;
        public Data[][] f;
        public List<Data[]> g;
        public Data[][][] h;
        public List<Data>[] i;
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

        private string GetDictString<K, V>(Dictionary<K, Dictionary<K, V>> ddd)
        {
            return $"{string.Join(",", ddd.Keys.ToList())},\n" +
                   $"   {string.Join(",", ddd.Values.ToList().SelectMany(k => k.Keys))},\n" +
                   $"   {string.Join(",", ddd.Values.ToList().SelectMany(k => k.Values))}";
        }
    }

    [NinoType]
    public struct Data
    {
        public int x;
        public short y;
        public long z;
        public float f;
        public decimal d;
        public double db;
        public bool bo;
        public TestEnum en;

        public override string ToString()
        {
            return $"{x},{y},{z},{f},{d},{db},{bo},{en}";
        }
    }

    public enum TestEnum : byte
    {
        A = 1,
        B = 2
    }

    [NinoType]
    public class NestedData
    {
        public string name;
        public Data[] ps;

        public override string ToString()
        {
            return $"{name},{ps[0]}";
        }
    }

    // Only the [NinoMember]s, one of them private: the generator reaches it through an accessor inside the type,
    // which is why the type is partial (NINO002; Unity has no [UnsafeAccessor]).
    [NinoType(autoCollect: false, containNonPublicMembers: true)]
    public partial class PrimitiveTypeTest
    {
        [NinoMember(1)] public Vector3 v3;

        [NinoMember(2)] private DateTime dt;

        public DateTime Dt
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

    [NinoType(false)]
    public class NotIncludeAllClass
    {
        [NinoMember(0)] public int a;

        [NinoMember(1)] public long b;

        [NinoMember(2)] public float c;

        [NinoMember(3)] public double d;

        public override string ToString()
        {
            return $"{a}, {b}, {c}, {d}";
        }
    }

    /// <summary>Every primitive, strings, and collections of a class, including class-keyed dictionaries.</summary>
    [NinoType]
    public class MixedData
    {
        public byte a;
        public sbyte b;
        public short c;
        public ushort d;
        public int e;
        public uint f;
        public long g;
        public ulong h;
        public float i;
        public double j;
        public decimal k;
        public bool l;
        public char m;
        public string n;
        public List<int> o = new List<int>();
        public List<NotIncludeAllClass> p = new List<NotIncludeAllClass>();
        public byte[] q = Array.Empty<byte>();
        public NotIncludeAllClass[] r = Array.Empty<NotIncludeAllClass>();
        public Dictionary<string, NotIncludeAllClass> s = new Dictionary<string, NotIncludeAllClass>();
        public Dictionary<NotIncludeAllClass, int> t = new Dictionary<NotIncludeAllClass, int>();
        public Dictionary<string, int> u = new Dictionary<string, int>();

        public Dictionary<NotIncludeAllClass, NotIncludeAllClass> v =
            new Dictionary<NotIncludeAllClass, NotIncludeAllClass>();

        public override string ToString()
        {
            return $"{a},{b},{c},{d},{e},{f},{g},{h},{i},{j},{k},{l},{m},{n},{string.Join("/", o)}," +
                   $"{string.Join("/", p)},{string.Join("/", q)},{string.Join("/", r.ToList())}," +
                   $"{string.Join("/", s.Keys.ToList())}-{string.Join("/", s.Values.ToList())}," +
                   $"{string.Join("/", t.Keys.ToList())}-{string.Join("/", t.Values.ToList())}," +
                   $"{string.Join("/", u.Keys.ToList())}-{string.Join("/", u.Values.ToList())}," +
                   $"{string.Join("/", v.Keys.ToList())}-{string.Join("/", v.Values.ToList())}";
        }
    }

    // Polymorphism: written and read through the abstract base, each instance comes back as its own subtype.
    [NinoType]
    public abstract class Shape
    {
        public string Name;
    }

    [NinoType]
    public class Circle : Shape
    {
        public float Radius;
    }

    [NinoType]
    public sealed class Box : Shape
    {
        public Vector3 Size;
        public Quaternion Rotation;
    }

    // Deserialization takes instances from the pool, and fills them in place.
    [NinoType, NinoPooled]
    public sealed partial class PooledMessage
    {
        public static int Returns;

        public int Id;
        public List<int> Values;

        private static partial IEnumerable<PooledMessage> OnNinoPoolWarmup() => null;

        private partial void OnNinoPoolReturn() => Returns++;
    }

    // Two versions of one keyed type, as data authored in the editor evolves: V2 reorders the members, renames
    // Speed (it reads the old name through [NinoFormerName]), drops Tag and adds Color.
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedSettingsV1
    {
        public int Id;
        public Vector3 Position;
        public float Speed;
        public string Tag;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedSettingsV2
    {
        public Color Color = Color.white;
        public Vector3 Position;
        [NinoFormerName("Speed")] public float MoveSpeed;
        public int Id;
    }
}
