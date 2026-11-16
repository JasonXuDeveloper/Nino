using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nino.Fuzz.Launcher
{
    public static class Launcher
    {
        public static unsafe int Main(string[] args)
        {
            SharpFuzz.Common.Trace.SharedMem = (byte*)Marshal.AllocHGlobal(1 << 16);
            new Span<byte>(SharpFuzz.Common.Trace.SharedMem, 1 << 16).Clear();
            var path = Path.Combine(AppContext.BaseDirectory, "Nino.Fuzz.dll");
            var main = Assembly.LoadFrom(path).GetType("Nino.Fuzz.Program", throwOnError: true)!.GetMethod("Main")!;
            try
            {
                return (int)main.Invoke(null, new object[] { args })!;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
