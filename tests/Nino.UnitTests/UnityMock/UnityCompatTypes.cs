using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Core;

// Unity-compatible data types for .NET Core testing
namespace Nino.UnitTests.UnityMock
{
    // Simple Vector3 replacement for Unity Vector3
    [NinoType]
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;
        
        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
        
        public static Vector3 one => new Vector3(1, 1, 1);
        
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }
    
    // Simple Quaternion replacement for Unity Quaternion
    [NinoType]
    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;
        
        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }
        
        public override string ToString() => $"({x:F5}, {y:F5}, {z:F5}, {w:F5})";
    }
    
    // Simple Matrix4x4 replacement for Unity Matrix4x4
    [NinoType]
    public struct Matrix4x4
    {
        public float m00, m01, m02, m03;
        public float m10, m11, m12, m13; 
        public float m20, m21, m22, m23;
        public float m30, m31, m32, m33;
        
        public static Matrix4x4 identity => new Matrix4x4
        {
            m00 = 1, m01 = 0, m02 = 0, m03 = 0,
            m10 = 0, m11 = 1, m12 = 0, m13 = 0,
            m20 = 0, m21 = 0, m22 = 1, m23 = 0,
            m30 = 0, m31 = 0, m32 = 0, m33 = 1
        };
        
        public override string ToString() =>
            $"{m00:F5}    {m01:F5}    {m02:F5}    {m03:F5}\n" +
            $"{m10:F5}    {m11:F5}    {m12:F5}    {m13:F5}\n" +
            $"{m20:F5}    {m21:F5}    {m22:F5}    {m23:F5}\n" +
            $"{m30:F5}    {m31:F5}    {m32:F5}    {m33:F5}";
    }

    public enum TestEnum : byte
    {
        A = 1,
        B = 2
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
            return $"{string.Join(",", a?.SelectMany(x => x).ToArray() ?? new int[0])},\n" +
                   $"{string.Join(",", b?.SelectMany(x => x).ToArray() ?? new int[0])},\n" +
                   $"{string.Join(",", c?.SelectMany(x => x).ToArray() ?? new int[0])},\n" +
                   $"{GetDictString(d)},\n" +
                   $"{string.Join(",\n", e?.Select(GetDictString).ToArray() ?? new string[0])}\n" +
                   $"{string.Join(",\n", f?.SelectMany(x => x).Select(x => x) ?? new Data[0])}\n" +
                   $"{string.Join(",\n", g?.SelectMany(x => x).Select(x => x) ?? new Data[0])}\n" +
                   $"{string.Join(",\n", h?.SelectMany(x => x).SelectMany(x => x).Select(x => x) ?? new Data[0])}\n" +
                   $"{string.Join(",\n", i?.SelectMany(x => x).Select(x => x) ?? new Data[0])}\n" +
                   $"{string.Join(",\n", j?.SelectMany(x => x).Select(x => x).SelectMany(x => x).Select(x => x) ?? new Data[0])}\n";
        }

        private string GetDictString<K, V>(Dictionary<K, Dictionary<K, V>> ddd)
        {
            if (ddd == null) return "";
            var keys = string.Join(",", ddd.Keys.ToList());
            var subKeys = string.Join(",", ddd.Values.ToList().SelectMany(k => k?.Keys ?? Enumerable.Empty<K>()));
            var subValues = string.Join(",", ddd.Values.ToList().SelectMany(k => k?.Values ?? Enumerable.Empty<V>()));
            return $"{keys},\n" +
                $"   {subKeys},\n" +
                $"   {subValues}";
        }
    }

    [NinoType(false, true)]
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
            var qsStr = qs != null ? string.Join(",", qs.Select(q => q.ToString())) : "";
            var dictKeys = dict != null ? string.Join(",", dict.Keys) : "";
            var dictValues = dict != null ? string.Join(",", dict.Values) : "";
            var dict2Keys = dict2 != null ? string.Join(",", dict2.Keys) : "";
            var dict2Values = dict2 != null ? string.Join(",", dict2.Values) : "";
            
            return
                $"{v3}, {dt}, {ni}, {qsStr}, {m.ToString()}\n" +
                $"dict.keys: {dictKeys},\n" +
                $"dict.values:{dictValues}\n" +
                $"dict2.keys: {dict2Keys},\n" +
                $"dict2.values:{dict2Values}\n";
        }
    }

    [NinoType] // Automatically includes all fields - no explicit NinoMember attributes
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

    [NinoType(false)] // Does NOT automatically include - requires explicit NinoMember attributes
    public class NotIncludeAllClass
    {
        [NinoMember(0)]
        public int a;

        [NinoMember(1)]
        public long b;

        [NinoMember(2)]
        public float c;

        [NinoMember(3)]
        public double d;

        public override string ToString()
        {
            return $"{a}, {b}, {c}, {d}";
        }
    }
}