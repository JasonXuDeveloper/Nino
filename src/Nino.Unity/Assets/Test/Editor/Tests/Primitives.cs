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
        private void Test<T>(T input, string inputName, Func<T, string> toString = null,
            Func<T, T, bool> equality = null)
        {
            var bytes = NinoSerializer.Serialize(input);
            var output = NinoDeserializer.Deserialize<T>(bytes);
            equality ??= (x, y) => x.Equals(y);
            toString ??= x => x.ToString();
            Assert.True(equality(input, output));
            Debug.Log(
                $"Serialized {inputName} as: {bytes.Length} bytes: {string.Join(",", bytes)}, deserialized as:{toString(output)}");
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

            Test(a, nameof(a));
            Test(b, nameof(b));
            Test(c, nameof(c));
            Test(d, nameof(d));
            Test(e, nameof(e));
            Test(f, nameof(f));
            Test(g, nameof(g));
            Test(h, nameof(h));
            Test(i, nameof(i));
            Test(j, nameof(j));
            Test(k, nameof(k));
            Test(l, nameof(l));
            Test(m, nameof(m));
            Test(n, nameof(n));
            Test(o, nameof(o), x => string.Join(",", x), (input, output) => input.SequenceEqual(output));
            Test(p, nameof(p), x => string.Join(",", x), (input, output) => input.SequenceEqual(output));
            Test(q, nameof(q),
                x =>
                    string.Join(",", x.ToList().SelectMany(kvp => $"{kvp.Key}-{kvp.Value}")),
                (input, output) =>
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
            Test(r, nameof(r));
            Test(s, nameof(s));
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
            GradientColorKey[] j = { new(Color.red, 0) };
            GradientAlphaKey[] k = { new(1, 0) };
            Keyframe[] l = { new(0, 0) };
            Matrix4x4 m = Matrix4x4.identity;

            // Test(a, nameof(a));
            // Test(b, nameof(b));
            // Test(c, nameof(c));
            // Test(d, nameof(d));
            // Test(e, nameof(e));
            // Test(f, nameof(f));
            // Test(g, nameof(g));
            // Test(h, nameof(h));
            // Test(i, nameof(i));
            Test(j, nameof(j),
                gradientColorKey =>
                    string.Join(",", gradientColorKey.Select(x => $"{x.color},{x.time}")),
                (input, output) =>
                {
                    for (int i = 0; i < input.Length; i++)
                    {
                        if (input[i].color != output[i].color || !Mathf.Approximately(input[i].time, output[i].time))
                        {
                            return false;
                        }
                    }

                    return true;
                });
            Test(k, nameof(k),
                gradientAlphaKey =>
                    string.Join(",", gradientAlphaKey.Select(x => $"{x.alpha},{x.time}")),
                (input, output) =>
                {
                    for (int i = 0; i < input.Length; i++)
                    {
                        if (!Mathf.Approximately(input[i].alpha, output[i].alpha) ||
                            !Mathf.Approximately(input[i].time, output[i].time))
                        {
                            return false;
                        }
                    }

                    return true;
                });
            Test(l, nameof(l), keyframe =>
                    string.Join(",", keyframe.Select(x => $"{x.time},{x.value}")),
                (input, output) =>
                {
                    for (int i = 0; i < input.Length; i++)
                    {
                        if (!Mathf.Approximately(input[i].time, output[i].time) ||
                            !Mathf.Approximately(input[i].value, output[i].value))
                        {
                            return false;
                        }
                    }

                    return true;
                });
            Test(m, nameof(m));
        }
    }
}