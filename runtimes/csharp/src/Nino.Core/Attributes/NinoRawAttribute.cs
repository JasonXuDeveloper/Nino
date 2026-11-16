using System;

namespace Nino.Core
{
    /// <summary>
    /// On a type parameter of a formatter (<c>struct F&lt;[NinoRaw] T&gt; ... where T : unmanaged</c>): the formatter
    /// copies values of the parameter as raw memory, so the bound type must be one Nino writes as its raw bytes (a
    /// portable unmanaged type: no <c>Nullable</c>, tuples, <c>nint</c>, <c>DateTimeOffset</c> or <c>[NinoType]</c>
    /// structs that are not raw). The generator and the runtime resolver only bind such types.
    /// <para>
    /// A plain <c>unmanaged</c> constraint keeps its C# meaning (no references), so formatters that merely carry it,
    /// such as the generated formatter of <c>[NinoType] class Box&lt;T&gt; where T : unmanaged</c>, bind to any
    /// unmanaged type.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.GenericParameter)]
    public sealed class NinoRawAttribute : Attribute
    {
    }
}
