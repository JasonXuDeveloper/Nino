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

            int count = args.Length > 1 ? int.Parse(args[1]) : 1000;
            Console.WriteLine(
                $"seam={(Seam.On ? 1 : 0)} flatA={SeamCheck.Flat<Nino_SeamProto_SeamOneFormatter>()} flatB={SeamCheck.Flat<Nino_SeamProto_SeamManyFormatter>()} count={count}");
            var ones = SeamData.Ones(count, false);
            var manys = SeamData.Manys(count, false);
            var oneList = new List<SeamOne>(ones);
            var manyList = new List<SeamMany>(manys);
            Bench("A.List", oneList);
            if (!Seam.On)
                BenchOwnTypeLoop(ones);
            Bench("A.Array", ones);
            Bench("B.List", manyList);
            Bench("B.Array", manys);
            return 0;
        }

        private static void Bench<T>(string name, T value)
        {
            var buffer = new NinoArrayBufferWriter(1 << 20);
            byte[] bytes = NinoSerializer.Serialize(value);
            Time(name + ".Serialize", () =>
            {
                buffer.ResetWrittenCount();
                NinoSerializer.Serialize(value, buffer);
            });
            Time(name + ".Deserialize", () => NinoDeserializer.Deserialize<T>(bytes));
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
            Console.WriteLine($"{name,-22} min {samples[0] / 1000,9:F2} us  median {samples[7] / 1000,9:F2} us");
        }
    }
}
