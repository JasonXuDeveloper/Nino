
using System;

namespace Nino.Core
{
    /// <summary>
    /// Abstract base class for high-performance custom formatters.
    /// Inherit from this class and implement the abstract methods to create a custom formatter.
    /// The base class provides automatic singleton instance management via the Instance property.
    /// </summary>
    /// <typeparam name="T">The type to be formatted</typeparam>
    public abstract class NinoFormatter<T>
    {
        /// <summary>
        /// Serializes a value to the writer.
        /// </summary>
        /// <param name="value">The value to serialize</param>
        /// <param name="writer">The writer to serialize to</param>
        public abstract void Serialize(T value, ref Writer writer);
        
        /// <summary>
        /// Deserializes a value from the reader (out parameter variant).
        /// </summary>
        /// <param name="value">The deserialized value</param>
        /// <param name="reader">The reader to deserialize from</param>
        public abstract void Deserialize(out T value, ref Reader reader);
        
        /// <summary>
        /// Deserializes a value from the reader (ref parameter variant).
        /// Used for updating existing instances.
        /// </summary>
        /// <param name="value">The value to update</param>
        /// <param name="reader">The reader to deserialize from</param>
        public abstract void DeserializeRef(ref T value, ref Reader reader);
    }
    
    /// <summary>
    /// Provides singleton instance management for concrete formatter implementations.
    /// </summary>
    /// <typeparam name="TFormatter">The concrete formatter type</typeparam>
    /// <typeparam name="T">The type being formatted</typeparam>
    public static class NinoFormatterInstance<TFormatter, T> 
        where TFormatter : NinoFormatter<T>, new()
    {
        /// <summary>
        /// Thread-safe singleton instance of the concrete formatter.
        /// Users don't need to declare this - it's automatically provided.
        /// </summary>
        public static readonly Lazy<TFormatter> Instance = new(() => new TFormatter());
    }
}