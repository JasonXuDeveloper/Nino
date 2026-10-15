using System;

namespace Nino.Core
{
    /// <summary>
    /// Serializes the listed members first, in the given order (by name); the others follow in declaration order.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
    public sealed class NinoExplicitOrderAttribute : Attribute
    {
        public NinoExplicitOrderAttribute(params string[] order)
        {
            Order = order;
        }

        /// <summary>Member names, in serialization order.</summary>
        public string[] Order { get; }
    }
}
