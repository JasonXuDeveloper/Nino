using System;

namespace Nino.Core
{
    /// <summary>
    /// On a type parameter of a formatter (<c>struct F&lt;[NinoCreated] TCollection, ...&gt;</c>): the formatter creates
    /// instances of the parameter through <see cref="NinoActivator{T}.Create"/>, so the bound type must be a non-abstract
    /// class with a public static <see cref="NinoFactoryAttribute"/> factory or a public constructor that is
    /// callable without arguments (every parameter optional). The generator and the runtime resolver only bind such
    /// types, and the generator registers a creator for each one it binds.
    /// <para>
    /// Use it instead of the <c>new()</c> constraint, which admits only a parameterless constructor (not
    /// <c>MultiMap(int maxPoolCount = 0)</c>, not a pooled <c>static X Create()</c>) and which generic code compiles
    /// to <c>Activator.CreateInstance&lt;T&gt;()</c>. A plain <c>new()</c> constraint is still honored.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.GenericParameter)]
    public sealed class NinoCreatedAttribute : Attribute
    {
    }
}
