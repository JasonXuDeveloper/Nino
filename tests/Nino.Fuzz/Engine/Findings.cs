using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nino.Fuzz
{
    /// <summary>One input as the worker ran it: enough to run it again (Program.cs: repro).</summary>
    public sealed class Case
    {
        public string Target;
        public int Seed;
        public long Iteration;
        public byte[] Data;
        public Placement Placement;
        public bool Reuse;
        public byte[] Prime;
    }

    /// <summary>
    /// The input being run, written to a file before each read (one positioned write, so it survives the process
    /// being killed by a stack overflow or an access violation: the supervisor reads it back).
    /// </summary>
    public sealed class CurrentCase : IDisposable
    {
        const int Magic = 0x315A464E; // "NFZ1"
        readonly SafeFileHandle _file;
        byte[] _buffer = new byte[4096];

        public CurrentCase(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _file = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }

        public void Record(Case c)
        {
            int primeLength = c.Prime?.Length ?? -1;
            int size = 4 + 8 + 1 + 1 + 4 + Math.Max(0, primeLength) + 4 + c.Data.Length;
            if (_buffer.Length < size) _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
            var w = _buffer.AsSpan();
            BitConverter.TryWriteBytes(w, Magic);
            BitConverter.TryWriteBytes(w.Slice(4), c.Iteration);
            w[12] = (byte)c.Placement;
            w[13] = c.Reuse ? (byte)1 : (byte)0;
            BitConverter.TryWriteBytes(w.Slice(14), primeLength);
            int at = 18;
            if (c.Prime != null) { c.Prime.CopyTo(w.Slice(at)); at += c.Prime.Length; }
            BitConverter.TryWriteBytes(w.Slice(at), c.Data.Length);
            c.Data.CopyTo(w.Slice(at + 4));
            RandomAccess.Write(_file, _buffer.AsSpan(0, size), 0);
        }

        public static Case Load(string path, string target, int seed)
        {
            if (!File.Exists(path)) return null;
            var b = File.ReadAllBytes(path);
            if (b.Length < 22 || BitConverter.ToInt32(b, 0) != Magic) return null;
            var c = new Case { Target = target, Seed = seed, Iteration = BitConverter.ToInt64(b, 4), Placement = (Placement)b[12], Reuse = b[13] != 0 };
            int primeLength = BitConverter.ToInt32(b, 14);
            int at = 18;
            if (primeLength >= 0) { c.Prime = b.AsSpan(at, primeLength).ToArray(); at += primeLength; }
            int length = BitConverter.ToInt32(b, at);
            c.Data = b.AsSpan(at + 4, length).ToArray();
            return c;
        }

        public void Dispose() => _file.Dispose();
    }

    /// <summary>The findings folder: one folder per target and signature, with the input, a minimized one and info.txt.</summary>
    public static class Findings
    {
        /// <summary>Whether a finding with this signature was already saved for the case's target.</summary>
        public static bool Saved(string root, Case c, string kind, string signature) =>
            File.Exists(Path.Combine(root, c.Target, kind + "-" + Hash(signature), "info.txt"));

        /// <summary>Saves a finding; returns its folder, or null when this signature was already saved.</summary>
        public static string Save(string root, Case c, string kind, string signature, string details, byte[] minimized, string minimizeNote)
        {
            string dir = Path.Combine(root, c.Target, kind + "-" + Hash(signature));
            if (Saved(root, c, kind, signature))
            {
                File.AppendAllText(Path.Combine(dir, "seen.txt"), $"seed {c.Seed} iteration {c.Iteration} ({c.Data.Length} bytes)\n");
                return null;
            }
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "input.bin"), c.Data);
            if (c.Prime != null) File.WriteAllBytes(Path.Combine(dir, "prime.bin"), c.Prime);
            if (minimized != null) File.WriteAllBytes(Path.Combine(dir, "min.bin"), minimized);

            var repro = minimized != null ? "min.bin" : "input.bin";
            var sb = new StringBuilder();
            sb.AppendLine($"signature: {signature}");
            sb.AppendLine($"target:    {c.Target}");
            sb.AppendLine($"found:     seed {c.Seed}, iteration {c.Iteration}, {DateTime.UtcNow:u}");
            sb.AppendLine($"placement: {c.Placement.ToString().ToLowerInvariant()}{(c.Reuse ? ", reuse (read into " + (c.Prime != null ? "the value of prime.bin" : "a fresh sample") + ")" : "")}");
            sb.AppendLine($"input:     {c.Data.Length} bytes; minimized: {(minimized != null ? minimized.Length + " bytes" : "no")} {minimizeNote}");
            sb.AppendLine($"machine:   {Environment()}");
            sb.AppendLine();
            sb.AppendLine("Reproduce (from the repository root, with the same DOTNET_* variables as above):");
            sb.AppendLine($"  dotnet run -c Release -f net{System.Environment.Version.Major}.0 --project tests/Nino.Fuzz -- repro {c.Target} {Path.Combine(dir, repro)} {ReproArgs(c, dir)}");
            sb.AppendLine();
            var shown = minimized ?? c.Data;
            if (shown.Length <= 4096) { sb.AppendLine($"{repro} as hex (for a unit test):"); sb.AppendLine(Convert.ToHexString(shown)); sb.AppendLine(); }
            if (c.Prime != null && c.Prime.Length <= 4096) { sb.AppendLine("prime.bin as hex:"); sb.AppendLine(Convert.ToHexString(c.Prime)); sb.AppendLine(); }
            sb.AppendLine("details:");
            sb.AppendLine(details);
            File.WriteAllText(Path.Combine(dir, "info.txt"), sb.ToString());
            return dir;
        }

        public static string ReproArgs(Case c, string dir) =>
            $"--placement {c.Placement.ToString().ToLowerInvariant()}" + (c.Reuse ? " --reuse" : "") +
            (c.Prime != null ? " --prime " + Path.Combine(dir, "prime.bin") : "");

        public static string Environment()
        {
            var vars = new[] { "DOTNET_EnableHWIntrinsic", "DOTNET_EnableAVX512", "DOTNET_EnableAVX512F", "DOTNET_EnableAVX2", "DOTNET_TieredCompilation", "DOTNET_TieredPGO" }
                .Select(v => (v, value: System.Environment.GetEnvironmentVariable(v))).Where(x => x.value != null).Select(x => $"{x.v}={x.value}");
            return $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.ProcessArchitecture}, " +
                   $"vectors 128:{Vector128.IsHardwareAccelerated} 256:{Vector256.IsHardwareAccelerated} 512:{Vector512.IsHardwareAccelerated}, " +
                   $"inputs in {Guard.Mechanism}" + (vars.Any() ? ", " + string.Join(" ", vars) : "");
        }

        public static string Hash(string s)
        {
            ulong h = 14695981039346656037;
            foreach (char ch in s) { h ^= ch; h *= 1099511628211; }
            return h.ToString("x16").Substring(0, 10);
        }

        /// <summary>
        /// Shrinks an input while <paramref name="fails"/> still holds: removes ever smaller chunks, then sets bytes to
        /// zero. Bounded by <paramref name="budget"/> and <paramref name="maxTries"/>.
        /// </summary>
        public static byte[] Minimize(byte[] input, Func<byte[], bool> fails, TimeSpan budget, int maxTries, out string note)
        {
            var sw = Stopwatch.StartNew();
            int tries = 0;
            bool Try(byte[] candidate)
            {
                tries++;
                try { return fails(candidate); }
                catch { return false; }
            }
            bool Out() => sw.Elapsed > budget || tries >= maxTries;

            var best = input;
            for (int chunk = Math.Max(1, best.Length / 2); chunk >= 1 && !Out(); chunk /= 2)
            {
                for (int at = 0; at < best.Length && !Out();)
                {
                    int len = Math.Min(chunk, best.Length - at);
                    var candidate = new byte[best.Length - len];
                    Array.Copy(best, 0, candidate, 0, at);
                    Array.Copy(best, at + len, candidate, at, best.Length - at - len);
                    if (Try(candidate)) best = candidate; else at += len;
                }
                if (chunk == 1) break;
            }
            if (best.Length <= 4096)
            {
                for (int i = 0; i < best.Length && !Out(); i++)
                {
                    if (best[i] == 0) continue;
                    var candidate = (byte[])best.Clone();
                    candidate[i] = 0;
                    if (Try(candidate)) best = candidate;
                }
            }
            note = $"({tries} tries, {sw.Elapsed.TotalSeconds:F1} s{(Out() ? ", budget reached" : "")})";
            return best;
        }
    }
}
