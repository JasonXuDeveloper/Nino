using System.Runtime.CompilerServices;

// Locals are not zeroed on entry, as in Nino.Core: every local is assigned before it is read, and zeroing the struct
// copies of a Reader/Writer and the formatters' locals costs stores on each read and write.
[module: SkipLocalsInit]

#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct |
                    AttributeTargets.Interface | AttributeTargets.Constructor | AttributeTargets.Method |
                    AttributeTargets.Property | AttributeTargets.Event, Inherited = false)]
    internal sealed class SkipLocalsInitAttribute : Attribute
    {
    }
}
#endif
