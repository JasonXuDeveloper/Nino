using System.ComponentModel;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// The generated registry of one assembly. Registration is lazy: a lookup that misses asks every registry to
    /// register the type with that id (a switch in generated code), so startup does not pay for types never used.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoRegistry
    {
        /// <summary>Registers the formatters of the types (and built-in closed generics) whose type id is <paramref name="id"/>.</summary>
        void Register(int id);
    }
}
