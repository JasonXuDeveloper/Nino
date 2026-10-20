using System;
using System.Linq;
using System.Reflection;
using System.Threading;

/// <summary>
/// Loaded into Nino.Benchmark through DOTNET_STARTUP_HOOKS by benchmarks/asm.py, so that it works on any commit's
/// benchmark: before Main, it calls every Nino case (those whose name contains NINO_JIT_FILTER) until the JIT has
/// compiled it at Tier1, then exits, so that DOTNET_JitDisasm prints the code BenchmarkDotNet times (the same code:
/// the cases have no profile data of their own there either). BenchmarkDotNet is found by attribute names only.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        var filter = Environment.GetEnvironmentVariable("NINO_JIT_FILTER");
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
            for (int round = 0; round < 8; round++)
            {
                foreach (var method in cases)
                    for (int i = 0; i < 40; i++)
                        method.Invoke(bench, null);
                Thread.Sleep(200);
            }

            foreach (var cleanup in type.GetMethods().Where(m => Has(m, "GlobalCleanupAttribute")))
                cleanup.Invoke(bench, null);
        }

        Environment.Exit(0);
    }

    private static bool Has(MethodInfo method, string attribute) =>
        method.GetCustomAttributes().Any(a => a.GetType().Name == attribute);
}
