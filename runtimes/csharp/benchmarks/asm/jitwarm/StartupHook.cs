using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

/// <summary>
/// Loaded into Nino.Benchmark through DOTNET_STARTUP_HOOKS by runtimes/csharp/benchmarks/asm/asm.py, so that it works on any commit's
/// benchmark: before Main, it calls every Nino case (those whose name contains NINO_JIT_FILTER) until the JIT has
/// compiled it at Tier1, then exits, so that DOTNET_JitDisasm prints the code BenchmarkDotNet times (the same code:
/// the cases have no profile data of their own there either). BenchmarkDotNet is found by attribute names only.
/// With DOTNET_TieredCompilation=0 every method is compiled once, fully optimized, on its first call: one round.
/// NINO_JIT_INLINE=FILE writes the inlines the JIT attempted and gave up (InlineFailures) to FILE.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        // First, so that it sees the compilations of everything below.
        var inlineFile = Environment.GetEnvironmentVariable("NINO_JIT_INLINE");
        var failures = string.IsNullOrEmpty(inlineFile) ? null : new InlineFailures();
        var filter = Environment.GetEnvironmentVariable("NINO_JIT_FILTER");
        bool tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0";
        var assembly = Assembly.GetEntryAssembly();
        foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName))
        {
            var cases = type.GetMethods()
                .Where(m => Has(m, "BenchmarkAttribute") && m.Name.StartsWith("Nino") &&
                            (string.IsNullOrEmpty(filter) || m.Name.Contains(filter)))
                .ToArray();
            if (cases.Length == 0)
                continue;

            var bench = Activator.CreateInstance(type);
            foreach (var setup in type.GetMethods().Where(m => Has(m, "GlobalSetupAttribute")))
                setup.Invoke(bench, null);
            // Tier0 first, then Tier1 once a method was called 30 times and the call counting delay (100 ms without
            // new Tier0 code) has passed: a few rounds with pauses reach it for every case.
            for (int round = 0; round < (tiered ? 8 : 1); round++)
            {
                foreach (var method in cases)
                    for (int i = 0; i < (tiered ? 40 : 1); i++)
                        method.Invoke(bench, null);
                if (tiered)
                    Thread.Sleep(200);
            }

            foreach (var cleanup in type.GetMethods().Where(m => Has(m, "GlobalCleanupAttribute")))
                cleanup.Invoke(bench, null);
        }

        failures?.Write(inlineFile);
        WaitForJit();
        Environment.Exit(0);
    }

    /// <summary>
    /// Waits until the JIT has finished no method for half a second (30 s at most), so that no compilation is still
    /// printing its listing when the process exits: the background tier-up of the last calls goes on after them (902
    /// methods in 1.7 s), and the runtime's shutdown closes DOTNET_JitStdOutFile under a thread still writing it, which
    /// aborted the process with glibc's "free(): invalid pointer" or "printf_buffer_to_file.c:86 ... assertion failed"
    /// (#413: 27 of 1,081 runs of a program without Nino that exits while the JIT prints, none of 400 with this wait).
    /// </summary>
    private static void WaitForJit()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long compiled = System.Runtime.JitInfo.GetCompiledMethodCount();
        for (int quiet = 0; quiet < 5 && DateTime.UtcNow < deadline;)
        {
            Thread.Sleep(100);
            long now = System.Runtime.JitInfo.GetCompiledMethodCount();
            quiet = now == compiled ? quiet + 1 : 0;
            compiled = now;
        }
    }

    private static bool Has(MethodInfo method, string attribute) =>
        method.GetCustomAttributes().Any(a => a.GetType().Name == attribute);
}

/// <summary>
/// The release runtime's own report of every inline the JIT gave up: MethodJitInliningFailed (keyword JitTracing,
/// 0x1000, at Verbose), with the inliner's reason ("inline exceeds budget", "too many locals", ...). Kept for the
/// methods asm.py lists (Nino's), as one line per root, inlinee and reason with how often the JIT reported it:
/// "root\tinlinee\treason\tcount". The JIT reports a method per compilation (Tier1 instrumented, Tier1, OSR).
/// </summary>
internal sealed class InlineFailures : EventListener
{
    // The base constructor calls OnEventSourceCreated for the sources that exist already, before this one's fields
    // are set: no instance state is touched there.
    private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
    private readonly object _gate = new object();
    private long _last = DateTime.UtcNow.Ticks;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1000);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        Interlocked.Exchange(ref _last, DateTime.UtcNow.Ticks);
        if (e.EventName == null || !e.EventName.StartsWith("MethodJitInliningFailed", StringComparison.Ordinal))
            return;
        string root = Name(e, "MethodBeingCompiled");
        if (!Listed(root))
            return;
        string key = root + "\t" + Name(e, "Inlinee") + "\t" + Get(e, "FailReason");
        lock (_gate)
            _counts[key] = _counts.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    /// <summary>The roots asm.py's METHODS lists: the benchmark's Nino cases, the generated code, Nino's.</summary>
    private static bool Listed(string root) =>
        root.StartsWith("NinoGen.", StringComparison.Ordinal) ||
        root.StartsWith("Nino.Core.", StringComparison.Ordinal) ||
        root.StartsWith("Nino.Immutable.", StringComparison.Ordinal) ||
        root.StartsWith("Nino.Benchmark.", StringComparison.Ordinal) && root.Contains(":Nino");

    private static string Name(EventWrittenEventArgs e, string prefix) =>
        Get(e, prefix + "Namespace") + ":" + Get(e, prefix + "Name");

    private static string Get(EventWrittenEventArgs e, string name)
    {
        int i = e.PayloadNames?.IndexOf(name) ?? -1;
        return i < 0 ? "" : (e.Payload[i]?.ToString() ?? "").Replace('\t', ' ').Replace('\n', ' ');
    }

    /// <summary>Waits until the events have stopped arriving (they come through a session buffer, a little late),
    /// then writes the table.</summary>
    public void Write(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _last)) < TimeSpan.FromSeconds(2) &&
               DateTime.UtcNow < deadline)
            Thread.Sleep(250);
        var text = new StringBuilder();
        lock (_gate)
            foreach (var pair in _counts.OrderBy(p => p.Key, StringComparer.Ordinal))
                text.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');
        File.WriteAllText(path, text.ToString());
    }
}
