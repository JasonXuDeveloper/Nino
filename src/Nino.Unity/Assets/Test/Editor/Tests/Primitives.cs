using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Nino.Core;
using Editor.Tests.NinoGen;

namespace Test.Editor.Tests
{
    public class Primitives
    {
        T Deserialize<T>(T org, byte[] bytes) where T : unmanaged
        {
            Deserializer.Deserialize(bytes, out T value);
            Assert.AreEqual(org, value);
            return value;
        }

        T[] DeserializeArray<T>(T[] org, byte[] bytes) where T : unmanaged
        {
            Deserializer.Deserialize(bytes, out T[] value);
            for (int index = 0; index < org.Length; index++)
            {
                Assert.AreEqual(org[index], value[index]);
            }

            return value;
        }

        List<T> DeserializeList<T>(List<T> org, byte[] bytes) where T : unmanaged
        {
            Deserializer.Deserialize(bytes, out List<T> value);
            for (int index = 0; index < org.Count; index++)
            {
                Assert.AreEqual(org[index], value[index]);
            }

            return value;
        }

        string DeserializeString(string org, byte[] bytes)
        {
            Deserializer.Deserialize(bytes, out string value);
            Assert.AreEqual(org, value);
            return value;
        }

        [Test]
        public void TestCSharpPrimitives()
        {
            int a = int.MaxValue;
            uint b = uint.MaxValue;
            long c = long.MaxValue;
            ulong d = ulong.MaxValue;
            float e = float.MaxValue;
            double f = double.MaxValue;
            decimal g = decimal.MaxValue;
            string h = "Hello World";
            bool i = true;
            char j = 'a';
            byte k = byte.MaxValue;
            sbyte l = sbyte.MaxValue;
            ushort m = ushort.MaxValue;
            short n = short.MaxValue;
            int[] o = { 1, 2, 3, 4, 5 };
            List<int> p = new List<int>() { 1, 2, 3, 4, 5 };
            Dictionary<string, int> q = new Dictionary<string, int>()
                { { "a", 1 }, { "b", 2 }, { "c", 3 }, { "d", 4 }, { "e", 5 } };
            DateTime r = DateTime.Now;
            Guid s = Guid.NewGuid();

            Debug.Log(
                $"Serialized a as: {a.Serialize().Length} bytes: {string.Join(",", a.Serialize())}, deserialized as:{Deserialize(a, a.Serialize())}");
            Debug.Log(
                $"Serialized b as: {b.Serialize().Length} bytes: {string.Join(",", b.Serialize())}, deserialized as:{Deserialize(b, b.Serialize())}");
            Debug.Log(
                $"Serialized c as: {c.Serialize().Length} bytes: {string.Join(",", c.Serialize())}, deserialized as:{Deserialize(c, c.Serialize())}");
            Debug.Log(
                $"Serialized d as: {d.Serialize().Length} bytes: {string.Join(",", d.Serialize())}, deserialized as:{Deserialize(d, d.Serialize())}");
            Debug.Log(
                $"Serialized e as: {e.Serialize().Length} bytes: {string.Join(",", e.Serialize())}, deserialized as:{Deserialize(e, e.Serialize())}");
            Debug.Log(
                $"Serialized f as: {f.Serialize().Length} bytes: {string.Join(",", f.Serialize())}, deserialized as:{Deserialize(f, f.Serialize())}");
            Debug.Log(
                $"Serialized g as: {g.Serialize().Length} bytes: {string.Join(",", g.Serialize())}, deserialized as:{Deserialize(g, g.Serialize())}");
            Debug.Log(
                $"Serialized h as: {h.Serialize().Length} bytes: {string.Join(",", h.Serialize())}, deserialized as:{DeserializeString(h, h.Serialize())}");
            Debug.Log(
                $"Serialized i as: {i.Serialize().Length} bytes: {string.Join(",", i.Serialize())}, deserialized as:{Deserialize(i, i.Serialize())}");
            Debug.Log(
                $"Serialized j as: {j.Serialize().Length} bytes: {string.Join(",", j.Serialize())}, deserialized as:{Deserialize(j, j.Serialize())}");
            Debug.Log(
                $"Serialized k as: {k.Serialize().Length} bytes: {string.Join(",", k.Serialize())}, deserialized as:{Deserialize(k, k.Serialize())}");
            Debug.Log(
                $"Serialized l as: {l.Serialize().Length} bytes: {string.Join(",", l.Serialize())}, deserialized as:{Deserialize(l, l.Serialize())}");
            Debug.Log(
                $"Serialized m as: {m.Serialize().Length} bytes: {string.Join(",", m.Serialize())}, deserialized as:{Deserialize(m, m.Serialize())}");
            Debug.Log(
                $"Serialized n as: {n.Serialize().Length} bytes: {string.Join(",", n.Serialize())}, deserialized as:{Deserialize(n, n.Serialize())}");
            Debug.Log(
                $"Serialized o as: {o.Serialize().Length} bytes: {string.Join(",", o.Serialize())}, deserialized as:{DeserializeArray(o, o.Serialize())}");
            Debug.Log(
                $"Serialized p as: {p.Serialize().Length} bytes: {string.Join(",", p.Serialize())}, deserialized as:{string.Join(",", DeserializeList(p, p.Serialize()))}");
            Deserializer.Deserialize(q.Serialize(), out Dictionary<string, int> dict);
            Debug.Log(
                $"Serialized q as: {q.Serialize().Length} bytes: {string.Join(",", q.Serialize())}, " +
                $"deserialized as:{string.Join(",", dict.ToList().SelectMany(kvp => $"{kvp.Key}-{kvp.Value}"))}");
            Debug.Log(
                $"Serialized r as: {r.Serialize().Length} bytes: {string.Join(",", r.Serialize())}, deserialized as:{Deserialize(r, r.Serialize())}");
            Debug.Log(
                $"Serialized s as: {s.Serialize().Length} bytes: {string.Join(",", s.Serialize())}, deserialized as:{Deserialize(s, s.Serialize())}");
        }

        [Test]
        public void TestUnityPrimitiveTypes()
        {
            Vector2 a = Vector2.one;
            Vector3 b = Vector3.one;
            Vector4 c = Vector4.one;
            Quaternion d = Quaternion.Euler(1, 2, 3);
            Color e = Color.red;
            Color32 f = new Color32(1, 2, 3, 4);
            Rect g = new Rect(1, 2, 3, 4);
            Bounds h = new Bounds(Vector3.one, Vector3.one);
            LayerMask i = 1;
            //not supported yet
            // AnimationCurve j = AnimationCurve.Linear(0, 0, 1, 1);
            // Gradient k = new Gradient();
            GradientColorKey[] l = { new(Color.red, 0) };
            GradientAlphaKey[] m = { new(1, 0) };
            // k.SetKeys(l, m);
            Keyframe[] n = { new(0, 0) };
            Matrix4x4 o = Matrix4x4.identity;
            Debug.Log(
                $"Serialized a as: {a.Serialize().Length} bytes: {string.Join(",", a.Serialize())}, deserialized as:{Deserialize(a, a.Serialize())}");
            Debug.Log(
                $"Serialized b as: {b.Serialize().Length} bytes: {string.Join(",", b.Serialize())}, deserialized as:{Deserialize(b, b.Serialize())}");
            Debug.Log(
                $"Serialized c as: {c.Serialize().Length} bytes: {string.Join(",", c.Serialize())}, deserialized as:{Deserialize(c, c.Serialize())}");
            Debug.Log(
                $"Serialized d as: {d.Serialize().Length} bytes: {string.Join(",", d.Serialize())}, deserialized as:{Deserialize(d, d.Serialize())}");
            Debug.Log(
                $"Serialized e as: {e.Serialize().Length} bytes: {string.Join(",", e.Serialize())}, deserialized as:{Deserialize(e, e.Serialize())}");
            Debug.Log(
                $"Serialized f as: {f.Serialize().Length} bytes: {string.Join(",", f.Serialize())}, deserialized as:{Deserialize(f, f.Serialize())}");
            Debug.Log(
                $"Serialized g as: {g.Serialize().Length} bytes: {string.Join(",", g.Serialize())}, deserialized as:{Deserialize(g, g.Serialize())}");
            Debug.Log(
                $"Serialized h as: {h.Serialize().Length} bytes: {string.Join(",", h.Serialize())}, deserialized as:{Deserialize(h, h.Serialize())}");
            Debug.Log(
                $"Serialized i as: {i.Serialize().Length} bytes: {string.Join(",", i.Serialize())}, deserialized as:{Deserialize(i, i.Serialize())}");
            // Debug.Log(
            //     $"Serialized j as: {(((j)).Serialize().Length)} bytes: {(string.Join(",", ((j)).Serialize()))}, deserialized as:{Deserialize<AnimationCurve>(j, j.Serialize())}");
            // Debug.Log(
            //     $"Serialized k as: {(((k)).Serialize().Length)} bytes: {(string.Join(",", ((k)).Serialize()))}, deserialized as:{Deserialize<Gradient>(k, k.Serialize())}");
            Debug.Log(
                $"Serialized l as: {l.Serialize().Length} bytes: {string.Join(",", l.Serialize())}, deserialized as:{string.Join(",", DeserializeArray(l, l.Serialize()))}");
            Debug.Log(
                $"Serialized m as: {m.Serialize().Length} bytes: {string.Join(",", m.Serialize())}, deserialized as:{string.Join(",", DeserializeArray(m, m.Serialize()))}");
            Debug.Log(
                $"Serialized n as: {n.Serialize().Length} bytes: {string.Join(",", n.Serialize())}, deserialized as:{string.Join(",", DeserializeArray(n, n.Serialize()))}");
            Debug.Log(
                $"Serialized o as: {o.Serialize().Length} bytes: {string.Join(",", o.Serialize())}, deserialized as:{Deserialize(o, o.Serialize())}");
        }
    }
}