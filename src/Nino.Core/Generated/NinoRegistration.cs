using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Nino.Core.Generated
{
    /// <summary>
    /// Marks an assembly that contains generated Nino code. <see cref="Registry"/> is a static class whose static
    /// constructor registers the assembly's formatters.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public sealed class NinoGeneratedAttribute : Attribute
    {
        /// <summary>The generated registry type; its static constructor registers the assembly.</summary>
        // NonPublicConstructors keeps the static constructor that NinoRegistry.EnsureInitialized runs.
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        public Type Registry { get; }

        public NinoGeneratedAttribute(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type registry)
        {
            Registry = registry;
        }
    }

    /// <summary>
    /// The generated registry of one assembly. Registration is lazy: a lookup that misses asks every registry to
    /// register the type with that id (a switch in generated code), so startup does not pay for types never used.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public interface INinoRegistry
    {
        /// <summary>Registers the formatters of the types (and built-in closed generics) whose type id is <paramref name="id"/>.</summary>
        void Register(int id);
    }
}
