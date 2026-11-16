using System;

namespace Nino.Core
{
    /// <summary>
    /// Marks a public static method, callable without arguments (parameterless, or every parameter optional), that
    /// returns an instance of its declaring type. Nino calls it whenever it has to create an instance (the slot is
    /// <c>null</c>), for example to take it from an object pool, and then populates the instance in place:
    /// <list type="bullet">
    /// <item>on a <c>[NinoType]</c>: generated code calls it instead of the constructor;</item>
    /// <item>on any collection type (a <c>List&lt;T&gt;</c> subclass, an <c>IDictionary&lt;TKey, TValue&gt;</c>...):
    /// <see cref="NinoActivator{T}"/> uses it, as ET's pooled <c>static ListComponent&lt;T&gt; Create()</c>. It may
    /// return an instance that still holds elements; they are dropped (and recycled) before the data is read.</item>
    /// </list>
    /// The method must be public, static, callable without arguments and return exactly the declaring type (NINO017
    /// otherwise), and must not return <c>null</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NinoFactoryAttribute : Attribute
    {
    }
}
