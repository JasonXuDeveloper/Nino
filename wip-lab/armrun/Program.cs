using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

// Runs the [TestMethod]s of the classes whose name contains one of the filters, by reflection (no test host):
// armrun <Nino.UnitTests.dll> <filter>...
string dll = Path.GetFullPath(args[0]);
string dir = Path.GetDirectoryName(dll)!;
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string path = Path.Combine(dir, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
Console.WriteLine($"arch {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, AdvSimd {System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported}, Ssse3 {System.Runtime.Intrinsics.X86.Ssse3.IsSupported}, Avx2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}, V128 {System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}");
int passed = 0, failed = 0;
foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && args.Skip(1).Any(f => t.Name.Contains(f)))
             .OrderBy(t => t.FullName))
{
    if (!type.GetCustomAttributes().Any(a => a.GetType().Name == "TestClassAttribute")) continue;
    Console.WriteLine(type.FullName);
    object instance = Activator.CreateInstance(type)!;
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                 .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "TestMethodAttribute") && m.GetParameters().Length == 0))
    {
        try
        {
            object result = method.Invoke(instance, null);
            if (result is System.Threading.Tasks.Task task) task.GetAwaiter().GetResult();
            passed++;
        }
        catch (TargetInvocationException e)
        {
            failed++;
            Console.WriteLine($"  FAILED {method.Name}: {e.InnerException!.GetType().Name}: {e.InnerException.Message.Split('\n')[0]}");
        }
    }
}
Console.WriteLine($"passed {passed}, failed {failed}");
return failed == 0 ? 0 : 1;
