using System;

namespace Nino.Core
{
    /// <summary>
    /// Attribute for specifying custom formatters by type.
    /// The formatter will be instantiated as a singleton for optimal performance.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class NinoCustomFormatterAttribute : Attribute
    {
        /// <summary>
        /// The formatter type that implements NinoFormatter{T}.
        /// </summary>
        public readonly Type FormatterType;
        
        /// <summary>
        /// Initializes a new instance of the custom formatter attribute.
        /// </summary>
        /// <param name="formatterType">The formatter type that inherits from NinoFormatter{T}</param>
        public NinoCustomFormatterAttribute(Type formatterType)
        {
            FormatterType = formatterType;
        }
    }
}