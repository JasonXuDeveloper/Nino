// PROTOTYPE (#355): the flat kernels against the generated per-element body in one process: the bytes of a run of
// each shape written both ways, and read back both ways. Called by `verify` and by Nino.UnitTests' SeamProtoTests.
using System;
using System.Collections.Generic;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using NinoGen;

namespace Nino.SeamProto
{
    public static class SeamData
    {
        private const string Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ";

        private static string Text(Random random, int min, int max, bool wide = false)
        {
            int length = random.Next(min, max + 1);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = wide && i % 7 == 3 ? (char)(0x4E00 + random.Next(500)) : Chars[random.Next(Chars.Length)];
            return new string(chars);
        }

        /// <summary>Shape A: <paramref name="count"/> values; with <paramref name="mixed"/>, nulls, null and empty
        /// names, wide characters and subtypes among them.</summary>
        public static SeamOne[] Ones(int count, bool mixed, int seed = 1)
        {
            var random = new Random(seed);
            var values = new SeamOne[count];
            for (int i = 0; i < count; i++)
            {
                if (mixed && i % 11 == 5)
                    continue;
                var value = mixed && i % 13 == 7 ? new SeamOneDerived { Extra = i } : new SeamOne();
                value.Stamp = random.Next() * 1000L;
                value.Score = random.NextDouble();
                value.Id = random.Next();
                value.Kind = (SeamKind)(i % 3);
                value.Name = mixed && i % 17 == 3 ? null : mixed && i % 19 == 4 ? "" : Text(random, 8, 24, mixed);
                values[i] = value;
            }

            return values;
        }

        /// <summary>Shape B: as <see cref="Ones"/>, without subtypes (the class is sealed).</summary>
        public static SeamMany[] Manys(int count, bool mixed, int seed = 2)
        {
            var random = new Random(seed);
            var values = new SeamMany[count];
            for (int i = 0; i < count; i++)
            {
                if (mixed && i % 11 == 5)
                    continue;
                values[i] = new SeamMany
                {
                    Id = random.Next(),
                    Name = mixed && i % 17 == 3 ? null : Text(random, 6, 16, mixed),
                    Tag = mixed && i % 19 == 4 ? "" : Text(random, 4, 10, mixed),
                    At = new DateTime(2020, 1, 1).AddSeconds(random.Next()),
                    Flag = random.Next(2) == 0,
                    Text = Text(random, 20, 60, mixed),
                };
            }

            return values;
        }
    }

    public static class SeamCheck
    {
        /// <summary>The failures (none: the flat kernels write and read every run as the generated body does).</summary>
        public static List<string> Run(bool fallbackAllowed = false)
        {
            // The closed types the generic check below uses, named once (NativeAOT registers only what it sees).
            _ = NinoSerializer.Serialize(Array.Empty<SeamOne>());
            _ = NinoSerializer.Serialize(new List<SeamOne>());
            _ = NinoSerializer.Serialize(Array.Empty<SeamMany>());
            _ = NinoSerializer.Serialize(new List<SeamMany>());
            _ = NinoDeserializer.Deserialize<SeamOne[]>(new byte[] { 0, 0, 0, 0 });
            _ = NinoDeserializer.Deserialize<List<SeamOne>>(new byte[] { 0, 0, 0, 0 });
            _ = NinoDeserializer.Deserialize<SeamMany[]>(new byte[] { 0, 0, 0, 0 });
            _ = NinoDeserializer.Deserialize<List<SeamMany>>(new byte[] { 0, 0, 0, 0 });
            var failures = new List<string>();
            _fallbackAllowed = fallbackAllowed;
            foreach (int count in new[] { 1, 2, 7, 100, 1000 })
            foreach (bool mixed in new[] { false, true })
            {
                Check<SeamOne, Nino_SeamProto_SeamOneFormatter>("SeamOne", SeamData.Ones(count, mixed), Same,
                    failures);
                Check<SeamMany, Nino_SeamProto_SeamManyFormatter>("SeamMany", SeamData.Manys(count, mixed), Same,
                    failures);
            }

            // The intercepted top-level calls, whose element formatter is the generated top-level twin (__Top), with
            // the same hooks: concrete types, so the interceptors take them.
            foreach (int count in new[] { 3, 1000 })
            {
                var manys = SeamData.Manys(count, true);
                var buffer = new NinoArrayBufferWriter();
                var writer = new Writer(buffer);
                writer = NinoRuns.WriteEach<SeamMany, Nino_SeamProto_SeamManyFormatter>(NinoRun.Of<SeamMany>(manys),
                    manys.Length, writer);
                writer.Flush();
                byte[] expected = buffer.WrittenSpan.ToArray();
                byte[] array = NinoSerializer.Serialize(manys);
                byte[] list = NinoSerializer.Serialize(new List<SeamMany>(manys));
                var readArray = NinoDeserializer.Deserialize<SeamMany[]>(expected);
                var readList = NinoDeserializer.Deserialize<List<SeamMany>>(expected);
                bool same = expected.AsSpan().SequenceEqual(array) && expected.AsSpan().SequenceEqual(list);
                for (int i = 0; same && i < manys.Length; i++)
                    same = Same(manys[i], readArray[i]) && Same(manys[i], readList[i]);
                if (!same)
                    failures.Add($"SeamMany[{count}] top-level: differs from the generated body");
            }

            // One body through the items against the generated body.
            foreach (var value in SeamData.Manys(200, true))
            {
                if (value == null)
                    continue;
                var a = new NinoArrayBufferWriter();
                var wa = new Writer(a);
                default(Nino_SeamProto_SeamManyFormatter).WriteBody(value, ref wa);
                wa.Flush();
                var b = new NinoArrayBufferWriter();
                var wb = new Writer(b);
                Nino_SeamProto_SeamManyFormatter.FlatWriteBody(value, ref wb);
                wb.Flush();
                var read = new SeamMany();
                var reader = new Reader(a.WrittenSpan);
                Nino_SeamProto_SeamManyFormatter.FlatReadBody(read, ref reader);
                if (!a.WrittenSpan.SequenceEqual(b.WrittenSpan) || !Same(value, read) || reader.Remaining != 0)
                {
                    failures.Add("SeamMany body: the items differ from the generated body");
                    break;
                }
            }

            return failures;
        }

        private static bool _fallbackAllowed;

        public static bool Flat<TFormatter>() where TFormatter : struct, INinoFormatter => NinoKind<TFormatter>.Flat;

        private static void Check<T, TFormatter>(string name, T[] values, Func<T, T, bool> same, List<string> failures)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
        {
            string label = $"{name}[{values.Length}]{(Array.IndexOf(values, null) >= 0 ? " mixed" : "")}";
            if (!NinoKind<TFormatter>.Flat && !_fallbackAllowed)
            {
                failures.Add($"{label}: not on the flat kernels");
                return;
            }

            // The generated body: the one loop of values, element by element.
            var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            writer = NinoRuns.WriteEach<T, TFormatter>(NinoRun.Of<T>(values), values.Length, writer);
            writer.Flush();
            byte[] expected = buffer.WrittenSpan.ToArray();

            // The public paths, which take the flat kernels: an array and a list.
            byte[] array = NinoSerializer.Serialize(values);
            byte[] list = NinoSerializer.Serialize(new List<T>(values));
            if (!expected.AsSpan().SequenceEqual(array))
                failures.Add($"{label}: array bytes differ from the generated body's");
            if (!expected.AsSpan().SequenceEqual(list))
                failures.Add($"{label}: list bytes differ from the generated body's");

            // Reads: the flat kernels (array and list), and the generated body element by element.
            var readArray = NinoDeserializer.Deserialize<T[]>(expected);
            var readList = NinoDeserializer.Deserialize<List<T>>(expected);
            var each = new T[values.Length];
            var reader = new Reader(expected.AsSpan(4));
            NinoRuns.ReadLoop<T, TFormatter>(ref System.Runtime.CompilerServices.Unsafe.As<T, byte>(ref each[0]),
                each.Length, IntPtr.Size, ref reader);
            for (int i = 0; i < values.Length; i++)
            {
                if (!same(values[i], readArray[i]) || !same(values[i], readList[i]) || !same(values[i], each[i]))
                {
                    failures.Add($"{label}: element {i} read back wrong");
                    break;
                }
            }

            // Into an existing array (reuse): the elements that hold objects leave the run and keep their instances.
            var existing = NinoDeserializer.Deserialize<T[]>(expected);
            var kept = (T[])existing.Clone();
            NinoDeserializer.Deserialize(expected, ref existing);
            for (int i = 0; i < values.Length; i++)
            {
                if (!same(values[i], existing[i]) || (kept[i] != null && !ReferenceEquals(kept[i], existing[i])))
                {
                    failures.Add($"{label}: reuse of element {i} wrong");
                    break;
                }
            }

            // Truncated data: rejected as the generated body rejects it, never read past.
            for (int cut = 1; cut < Math.Min(expected.Length, 200); cut += 3)
            {
                try
                {
                    NinoDeserializer.Deserialize<T[]>(expected.AsSpan(0, expected.Length - cut));
                    failures.Add($"{label}: {cut} bytes short read without an error");
                    break;
                }
                catch (NinoDataException)
                {
                }
            }
        }

        private static bool Same(SeamOne a, SeamOne b) =>
            a == null ? b == null :
            b != null && a.GetType() == b.GetType() && a.Stamp == b.Stamp && a.Score.Equals(b.Score) && a.Id == b.Id &&
            a.Kind == b.Kind && a.Name == b.Name &&
            (a is not SeamOneDerived d || ((SeamOneDerived)b).Extra == d.Extra);

        private static bool Same(SeamMany a, SeamMany b) =>
            a == null ? b == null :
            b != null && a.Id == b.Id && a.Name == b.Name && a.Tag == b.Tag && a.At == b.At && a.Flag == b.Flag &&
            a.Text == b.Text;
    }
}
