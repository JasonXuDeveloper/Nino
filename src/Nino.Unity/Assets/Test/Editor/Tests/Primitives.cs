using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Nino.Core;

namespace Test.Editor.Tests
{
    public class Primitives
    {
        private static void RunTest<T>(T input, string inputName, Func<T, T, bool> equality = null)
        {
            var bytes = NinoSerializer.Serialize(input);
            var output = NinoDeserializer.Deserialize<T>(bytes);
            equality ??= (x, y) => x.Equals(y);
            Assert.True(equality(input, output), $"{inputName} ({typeof(T).Name}) did not round-trip: {output}");
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
            List<int> p = new List<int> { 1, 2, 3, 4, 5 };
            Dictionary<string, int> q = new Dictionary<string, int>()
                { { "a", 1 }, { "b", 2 }, { "c", 3 }, { "d", 4 }, { "e", 5 } };
            DateTime r = DateTime.Now;
            Guid s = Guid.NewGuid();

            RunTest(a, nameof(a));
            RunTest(b, nameof(b));
            RunTest(c, nameof(c));
            RunTest(d, nameof(d));
            RunTest(e, nameof(e));
            RunTest(f, nameof(f));
            RunTest(g, nameof(g));
            RunTest(h, nameof(h));
            RunTest(i, nameof(i));
            RunTest(j, nameof(j));
            RunTest(k, nameof(k));
            RunTest(l, nameof(l));
            RunTest(m, nameof(m));
            RunTest(n, nameof(n));
            RunTest(o, nameof(o), (input, output) => input.SequenceEqual(output));
            RunTest(p, nameof(p), (input, output) => input.SequenceEqual(output));
            RunTest(q, nameof(q), (input, output) =>
            {
                if (input.Count != output.Count)
                {
                    return false;
                }

                foreach (var kvp in input)
                {
                    if (!output.TryGetValue(kvp.Key, out var value) || value != kvp.Value)
                    {
                        return false;
                    }
                }

                return true;
            });
            RunTest(r, nameof(r));
            RunTest(s, nameof(s));
        }

        [Test]
        public void TestUnityPrimitiveTypes()
        {
            Vector2 a = new Vector2(1.5f, -2.25f);
            Vector3 b = new Vector3(1.5f, -2.25f, 3.125f);
            Vector4 c = new Vector4(1.5f, -2.25f, 3.125f, 4f);
            Quaternion d = Quaternion.Euler(1, 2, 3);
            Color e = Color.red;
            Color32 f = new Color32(1, 2, 3, 4);
            Rect g = new Rect(1, 2, 3, 4);
            Bounds h = new Bounds(Vector3.one, Vector3.one);
            LayerMask i = 1;
            GradientColorKey[] j = { new(Color.red, 0) };
            GradientAlphaKey[] k = { new(1, 0) };
            Keyframe[] l = { new(0, 0) };
            Matrix4x4 m = Matrix4x4.identity;
            Vector2Int n = new Vector2Int(-1, 2);
            Vector3Int o = new Vector3Int(-1, 2, 3);
            Vector3[] p = { Vector3.up, Vector3.down, b };

            RunTest(a, nameof(a));
            RunTest(b, nameof(b));
            RunTest(c, nameof(c));
            RunTest(d, nameof(d));
            RunTest(e, nameof(e));
            RunTest(f, nameof(f), (input, output) =>
                input.r == output.r && input.g == output.g && input.b == output.b && input.a == output.a);
            RunTest(g, nameof(g));
            RunTest(h, nameof(h));
            RunTest(i, nameof(i), (input, output) => input.value == output.value);
            RunTest(j, nameof(j), (input, output) =>
            {
                for (int x = 0; x < input.Length; x++)
                {
                    if (input[x].color != output[x].color || !Mathf.Approximately(input[x].time, output[x].time))
                    {
                        return false;
                    }
                }

                return input.Length == output.Length;
            });
            RunTest(k, nameof(k), (input, output) =>
            {
                for (int x = 0; x < input.Length; x++)
                {
                    if (!Mathf.Approximately(input[x].alpha, output[x].alpha) ||
                        !Mathf.Approximately(input[x].time, output[x].time))
                    {
                        return false;
                    }
                }

                return input.Length == output.Length;
            });
            RunTest(l, nameof(l), (input, output) =>
            {
                for (int x = 0; x < input.Length; x++)
                {
                    if (!Mathf.Approximately(input[x].time, output[x].time) ||
                        !Mathf.Approximately(input[x].value, output[x].value))
                    {
                        return false;
                    }
                }

                return input.Length == output.Length;
            });
            RunTest(m, nameof(m));
            RunTest(n, nameof(n));
            RunTest(o, nameof(o));
            RunTest(p, nameof(p), (input, output) => input.SequenceEqual(output));
        }
    }
}
