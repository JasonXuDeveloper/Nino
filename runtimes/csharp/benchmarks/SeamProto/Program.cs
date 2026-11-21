// PROTOTYPE (#355): `verify` checks the flat kernels against the generated body; `bench [count] [rounds]` times
// List<T> and T[] of each shape, serialize and deserialize (NINO_SEAM=0: the generated body, the base).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nino.Core;
using NinoGen;

namespace Nino.SeamProto
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "verify";
            if (mode == "verify" || mode == "verify-untrusted")
            {
                // Untrusted mode: a type whose blocks hold a value the mode checks (SeamMany's DateTime) stays on its
                // generated body; the others keep the flat kernels.
                if (mode == "verify-untrusted")
                    NinoSettings.Untrusted = true;
                Console.WriteLine(
                    $"flatA={SeamCheck.Flat<Nino_SeamProto_SeamOneFormatter>()} flatB={SeamCheck.Flat<Nino_SeamProto_SeamManyFormatter>()}");
                var failures = SeamCheck.Run(mode == "verify-untrusted");
                foreach (var failure in failures)
                    Console.WriteLine(failure);
                Console.WriteLine(failures.Count == 0 ? "verify: ok" : $"verify: {failures.Count} failures");
                return failures.Count == 0 ? 0 : 1;
            }

            if (mode == "il")
            {
                // The IL of each formatter's methods (nested types too): the emitted size per type.
                foreach (var type in new[] { typeof(Nino_SeamProto_SeamOneFormatter), typeof(Nino_SeamProto_SeamManyFormatter) })
                    DumpIl(type, "");
                return 0;
            }

            int count = args.Length > 1 ? int.Parse(args[1]) : 1000;
            Console.WriteLine(
                $"seam={(Seam.On ? 1 : 0)} flatA={SeamCheck.Flat<Nino_SeamProto_SeamOneFormatter>()} flatB={SeamCheck.Flat<Nino_SeamProto_SeamManyFormatter>()} flatBTop={SeamCheck.Flat<Nino_SeamProto_SeamManyFormatter.__Top>()} count={count}");
            var ones = SeamData.Ones(count, false);
            var manys = SeamData.Manys(count, false);
            var oneList = new List<SeamOne>(ones);
            var manyList = new List<SeamMany>(manys);
            // Each call names its type (NativeAOT registers only the formatters of calls it sees).
            var buffer = new NinoArrayBufferWriter(1 << 20);
            byte[] aList = NinoSerializer.Serialize(oneList), aArray = NinoSerializer.Serialize(ones);
            byte[] bList = NinoSerializer.Serialize(manyList), bArray = NinoSerializer.Serialize(manys);
            Time("A.List.Serialize", () => { buffer.ResetWrittenCount(); NinoSerializer.Serialize(oneList, buffer); });
            Time("A.List.Deserialize", () => NinoDeserializer.Deserialize<List<SeamOne>>(aList));
            if (!Seam.On)
                BenchOwnTypeLoop(ones);
            Time("A.Array.Serialize", () => { buffer.ResetWrittenCount(); NinoSerializer.Serialize(ones, buffer); });
            Time("A.Array.Deserialize", () => NinoDeserializer.Deserialize<SeamOne[]>(aArray));
            Time("B.List.Serialize", () => { buffer.ResetWrittenCount(); NinoSerializer.Serialize(manyList, buffer); });
            Time("B.List.Deserialize", () => NinoDeserializer.Deserialize<List<SeamMany>>(bList));
            Time("B.Array.Serialize", () => { buffer.ResetWrittenCount(); NinoSerializer.Serialize(manys, buffer); });
            Time("B.Array.Deserialize", () => NinoDeserializer.Deserialize<SeamMany[]>(bArray));
            BenchOne(manys[0]);
            return 0;
        }

        /// <summary>
        /// The generated run loop of SeamOne as the dispatch would write it with the base's own type tested inline
        /// (today it reaches the registry, NinoSubtypes.Serialize, for every element of the base's own type).
        /// </summary>
        private static void BenchOwnTypeLoop(SeamOne[] values)
        {
            var buffer = new NinoArrayBufferWriter(1 << 20);
            Time("A.Array.Ser.ownloop", () =>
            {
                buffer.ResetWrittenCount();
                var writer = new Writer(buffer);
                writer.UnsafeWrite(values.Length);
                OwnTypeLoop(values, ref writer);
                writer.Flush();
            });
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OwnTypeLoop(SeamOne[] values, ref Writer target)
        {
            Writer writer = default;
            target.CopyTo(ref writer);
            foreach (var value in values)
            {
                if (value == null) { writer.UnsafeWrite(0); continue; }
                if (value.GetType() == typeof(SeamOne)) { Nino.Core.CompilerServices.NinoObject.WriteExactShallow<SeamOne, Nino_SeamProto_SeamOneFormatter>(value, ref writer); continue; }
                if (value.GetType() == typeof(SeamOneDerived)) { Nino.Core.CompilerServices.NinoObject.WriteExactShallow<SeamOneDerived, Nino_SeamProto_SeamOneDerivedFormatter>(value, ref writer); continue; }
                Nino.Core.CompilerServices.NinoSubtypes<SeamOne>.Serialize(value, ref writer);
            }

            writer.CopyTo(ref target);
        }

        /// <summary>
        /// One SeamMany body (no header), the generated body against the items' (what a formatter whose generated
        /// body is removed would run for a member or a top-level value).
        /// </summary>
        private static void BenchOne(SeamMany one)
        {
            var buffer = new NinoArrayBufferWriter(1 << 16);
            var w0 = new Writer(buffer);
            default(Nino_SeamProto_SeamManyFormatter).WriteBody(one, ref w0);
            w0.Flush();
            byte[] body = buffer.WrittenSpan.ToArray();
            Time("B.One.Ser.genbody", () =>
            {
                buffer.ResetWrittenCount();
                var writer = new Writer(buffer);
                default(Nino_SeamProto_SeamManyFormatter).WriteBody(one, ref writer);
                writer.Flush();
            });
            Time("B.One.Ser.flatbody", () =>
            {
                buffer.ResetWrittenCount();
                var writer = new Writer(buffer);
                Nino_SeamProto_SeamManyFormatter.FlatWriteBody(one, ref writer);
                writer.Flush();
            });
            Time("B.One.Deser.genbody", () =>
            {
                var reader = new Reader(body);
                SeamMany value = null;
                default(Nino_SeamProto_SeamManyFormatter).ReadBody(ref reader, ref value);
            });
            Time("B.One.Deser.flatbody", () =>
            {
                var reader = new Reader(body);
                var value = new SeamMany();
                Nino_SeamProto_SeamManyFormatter.FlatReadBody(value, ref reader);
            });
        }

        private static void DumpIl(Type type, string indent)
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
            int total = 0;
            foreach (var method in type.GetMethods(all).Cast<System.Reflection.MethodBase>()
                         .Concat(type.GetConstructors(all)))
            {
                int size = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
                total += size;
                Console.WriteLine($"{indent}  {size,5}  {method.Name}");
            }

            Console.WriteLine($"{indent}{type.Name}: {total} bytes of IL");
            foreach (var nested in type.GetNestedTypes(all))
                DumpIl(nested, indent + "    ");
        }

        private static void Time(string name, Action action)
        {
            // Warm up to Tier1 (tiered PGO), then 15 samples of about 100 ms each.
            var watch = Stopwatch.StartNew();
            int iterations = 0;
            while (watch.ElapsedMilliseconds < 1500)
            {
                action();
                iterations++;
            }

            int batch = Math.Max(1, (int)(iterations * 100L / watch.ElapsedMilliseconds));
            var samples = new List<double>();
            for (int s = 0; s < 15; s++)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < batch; i++)
                    action();
                samples.Add((Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / batch);
            }

            samples.Sort();
            Console.WriteLine(samples[0] < 1000
                ? $"{name,-22} min {samples[0],9:F2} ns  median {samples[7],9:F2} ns"
                : $"{name,-22} min {samples[0] / 1000,9:F2} us  median {samples[7] / 1000,9:F2} us");
        }
    }
}
