// Runs in the WebGL player Test.Editor.WebGL.WebGLBenchBuild builds (NINO_WEBGL_BENCH), in headless Chrome (unity.yml's
// WebGL job, .github/scripts/webgl_run.mjs): checks Nino.WebGL's kernels against Nino.Core's scalar references, checks
// that serializing with and without them writes the same bytes, and times both. Logs NINO_WEBGL lines.
#if UNITY_WEBGL && !UNITY_EDITOR && NINO_WEBGL_BENCH
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nino.Core;
using Nino.WebGL;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = System.Random;

namespace Test.WebGL
{
    public struct Padded
    {
        public short A;
        public long B;
    }

    /// <summary>A <c>[NinoUtf8]</c> string, which the UTF-8 kernels (NarrowAscii, WidenAscii) serve.</summary>
    [NinoType]
    public partial class Utf8Text
    {
        [NinoUtf8] public string Text;
    }

    // The WebGL player collects garbage only between frames: the benchmark is a coroutine that yields after each
    // timed round, or the heap fills within one frame.
    public sealed class WebGLBench : MonoBehaviour
    {
        private static int _failures;

        private static void Log(string line) => Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", line);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Run() => new GameObject("WebGLBench").AddComponent<WebGLBench>();

        private IEnumerator Start()
        {
            Log($"NINO_WEBGL|unity={Application.unityVersion}|simd={NinoWasm.Simd}|registered={NinoWasm.Registered}");
            if (NinoWasm.Registered != NinoWasm.Simd)
                Fail("registration");
            try
            {
                if (NinoWasm.Registered)
                    CheckKernels();
            }
            catch (Exception e)
            {
                Fail("check " + e);
            }

            var bench = Bench();
            while (true)
            {
                try
                {
                    if (!bench.MoveNext())
                        break;
                }
                catch (Exception e)
                {
                    Fail("bench " + e);
                    break;
                }

                yield return null;
            }

            Log($"NINO_WEBGL_DONE|failures={_failures}");
        }

        private static void Fail(string what)
        {
            _failures++;
            Log("NINO_WEBGL_FAIL|" + what);
        }

        private static unsafe void CheckKernels()
        {
            var k = NinoKernels.Current;
            var random = new Random(1);
            for (int trial = 0; trial < 4000; trial++)
            {
                int most = trial % 2 == 0 ? 17 : 65;
                int from = random.Next(1, most), to = random.Next(1, most);
                int count = trial % 4 == 0 ? random.Next(0, 1200) : random.Next(0, 70);
                var table = new byte[to];
                for (int j = 0; j < to; j++)
                    table[j] = random.Next(5) == 0 ? (byte)0xFF : (byte)random.Next(from);
                var source = new byte[from * count + 1];
                random.NextBytes(source);
                var a = new byte[to * count + 64];
                var b = new byte[to * count + 64];
                fixed (byte* s = source, t = table, x = a, y = b)
                {
                    k.Permute(s, from, x, to, t, count);
                    NinoKernels.PermuteScalar(s, from, y, to, t, count);
                }

                if (!a.SequenceEqual(b))
                {
                    Fail($"permute {from} {to} {count}");
                    return;
                }
            }

            for (int len = 0; len < 300; len++)
            {
                var chars = Enumerable.Range(0, len).Select(_ => (char)random.Next(256)).ToArray();
                if (len > 0 && len % 3 == 0)
                    chars[random.Next(len)] = (char)random.Next(0x100, 0xFFFF);
                var a = new byte[len + 16];
                var b = new byte[len + 16];
                bool ra, rb;
                fixed (char* c = chars)
                fixed (byte* x = a, y = b)
                {
                    ra = k.Narrow((ushort*)c, x, len) != 0;
                    rb = NinoKernels.NarrowScalar(c, y, len);
                }

                if (ra != rb || ra && !a.SequenceEqual(b))
                    Fail($"narrow {len}");
                var bytes = new byte[len];
                random.NextBytes(bytes);
                var wa = new char[len + 8];
                var wb = new char[len + 8];
                fixed (byte* s = bytes)
                fixed (char* x = wa, y = wb)
                {
                    k.Widen(s, (ushort*)x, len);
                    NinoKernels.WidenScalar(s, y, len);
                }

                if (!wa.SequenceEqual(wb))
                    Fail($"widen {len}");

                // The ASCII kernels: the same count, and the same chars or bytes up to it.
                var text = Enumerable.Range(0, len).Select(_ => (char)random.Next(128)).ToArray();
                if (len > 0 && len % 2 == 0)
                    text[random.Next(len)] = (char)random.Next(0x80, 0x800);
                var raw = Enumerable.Range(0, len).Select(_ => (byte)random.Next(128)).ToArray();
                if (len > 0 && len % 2 == 0)
                    raw[random.Next(len)] = (byte)random.Next(0x80, 0x100);
                int na, nb, wka, wkb;
                fixed (char* c = text)
                fixed (byte* x = a, y = b, r = raw)
                fixed (char* u = wa, v = wb)
                {
                    na = k.NarrowAscii((ushort*)c, x, len);
                    nb = NinoKernels.NarrowAsciiScalar(c, y, len);
                    wka = k.WidenAscii(r, (ushort*)u, len);
                    wkb = NinoKernels.WidenAsciiScalar(r, v, len);
                }

                if (na != nb || !a.Take(nb).SequenceEqual(b.Take(nb)))
                    Fail($"narrow ASCII {len}");
                if (wka != wkb || !wa.Take(wkb).SequenceEqual(wb.Take(wkb)))
                    Fail($"widen ASCII {len}");
            }

            Log("NINO_WEBGL|kernels checked");
        }

        private sealed class Case
        {
            public string Name;
            public Func<byte[]> Serialize;
            public Action<NinoArrayBufferWriter> SerializeTo;
            public Action Deserialize;
            public Func<bool> ReadsBack;
        }

        private static Case Values<T>(string name, T[] values)
        {
            var bytes = NinoSerializer.Serialize(values);
            var into = new T[values.Length];
            return new Case
            {
                Name = name, Serialize = () => NinoSerializer.Serialize(values),
                SerializeTo = w => NinoSerializer.Serialize(values, w),
                Deserialize = () => NinoDeserializer.Deserialize(bytes, ref into),
            };
        }

        private static Case Text(string name, string value)
        {
            var bytes = NinoSerializer.Serialize(value);
            return new Case
            {
                Name = name, Serialize = () => NinoSerializer.Serialize(value),
                SerializeTo = w => NinoSerializer.Serialize(value, w),
                Deserialize = () => NinoDeserializer.Deserialize<string>(bytes),
            };
        }

        private static Case Utf8(string name, string value)
        {
            var text = new Utf8Text { Text = value };
            var bytes = NinoSerializer.Serialize(text);
            return new Case
            {
                Name = name, Serialize = () => NinoSerializer.Serialize(text),
                SerializeTo = w => NinoSerializer.Serialize(text, w),
                Deserialize = () => NinoDeserializer.Deserialize<Utf8Text>(bytes),
                ReadsBack = () => NinoDeserializer.Deserialize<Utf8Text>(bytes).Text == value,
            };
        }

        private static IEnumerator Bench()
        {
            var random = new Random(5);
            var cases = new List<Case>();
            foreach (int n in new[] { 4, 16, 32, 1000, 10000 })
            {
                cases.Add(Values($"(int, long)[{n}]",
                    Enumerable.Range(0, n).Select(_ => (random.Next(), (long)random.Next() << 13)).ToArray()));
                cases.Add(Values($"(byte, int)[{n}]",
                    Enumerable.Range(0, n).Select(_ => ((byte)random.Next(), random.Next())).ToArray()));
                cases.Add(Values($"PaddedRaw[{n}]",
                    Enumerable.Range(0, n).Select(i => new Padded { A = (short)i, B = random.Next() }).ToArray()));
            }

            foreach (int n in new[] { 16, 32, 64, 1000, 10000 })
            {
                cases.Add(Text($"ASCII ({n})",
                    new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(32, 127)).ToArray())));
                cases.Add(Text($"CJK ({n})",
                    new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(0x4E00, 0x9FA6)).ToArray())));
                cases.Add(Utf8($"[NinoUtf8] ASCII ({n})",
                    new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(32, 127)).ToArray())));
            }

            // Below the threshold, and ASCII text with one other char in the middle (the kernel's prefix, then the
            // encoder): neither may get slower.
            cases.Add(Utf8("[NinoUtf8] ASCII (15)",
                new string(Enumerable.Range(0, 15).Select(_ => (char)random.Next(32, 127)).ToArray())));
            cases.Add(Utf8("[NinoUtf8] ASCII + é (1000)",
                new string(Enumerable.Range(0, 1000).Select(i => i == 500 ? '\u00E9' : (char)random.Next(32, 127)).ToArray())));
            cases.Add(Utf8("[NinoUtf8] CJK (1000)",
                new string(Enumerable.Range(0, 1000).Select(_ => (char)random.Next(0x4E00, 0x9FA6)).ToArray())));

            bool simd = NinoWasm.Simd;
            foreach (var c in cases)
            {
                NinoWasm.Unregister();
                var expected = c.Serialize();
                if (simd)
                {
                    NinoWasm.Register();
                    if (!c.Serialize().SequenceEqual(expected))
                        Fail("bytes " + c.Name);
                    if (c.ReadsBack != null && !c.ReadsBack())
                        Fail("read back " + c.Name);
                }

#if NINO_WEBGL_TIMING
                var buffer = new NinoArrayBufferWriter(1 << 20);
                var ops = new[]
                {
                    ("Serialize", (Action)(() => c.Serialize())),
                    ("SerializeTo", () =>
                    {
                        buffer.ResetWrittenCount();
                        c.SerializeTo(buffer);
                    }),
                    ("Deserialize", c.Deserialize),
                };
                foreach (var (op, action) in ops)
                {
                    // Scalar, kernels, kernels, scalar: the lower of each.
                    var times = new double[4];
                    for (int g = 0; g < 4; g++)
                    {
                        bool kernels = g == 1 || g == 2;
                        if (kernels && !simd)
                        {
                            times[g] = double.NaN;
                            continue;
                        }

                        if (kernels) NinoWasm.Register(); else NinoWasm.Unregister();
                        var t = Time(action);
                        double last = double.NaN;
                        while (t.MoveNext())
                        {
                            last = t.Current;
                            yield return null;
                        }

                        times[g] = last;
                    }

                    Log($"NINO_WEBGL_ROW|{c.Name}|{op}|{Math.Min(times[0], times[3]):F1}|{Math.Min(times[1], times[2]):F1}");
                }
#else
                yield return null;
#endif
            }

            NinoWasm.Register();
        }

        // Median ns per call of 7 rounds of about 30 ms each, after warm-up rounds; a frame after each round (the
        // collector runs between frames). The last value yielded is the result.
        private static IEnumerator<double> Time(Action action)
        {
            var watch = new Stopwatch();
            long n = 1;
            while (true)
            {
                watch.Restart();
                for (long i = 0; i < n; i++)
                    action();
                watch.Stop();
                yield return double.NaN;
                if (watch.Elapsed.TotalMilliseconds >= 15)
                    break;
                n *= 2;
            }

            n = Math.Max(1, (long)(n * 30 / watch.Elapsed.TotalMilliseconds));
            var times = new double[7];
            for (int r = 0; r < times.Length; r++)
            {
                watch.Restart();
                for (long i = 0; i < n; i++)
                    action();
                watch.Stop();
                times[r] = watch.Elapsed.TotalMilliseconds * 1e6 / n;
                yield return double.NaN;
            }

            Array.Sort(times);
            yield return times[times.Length / 2];
        }
    }
}
#endif
