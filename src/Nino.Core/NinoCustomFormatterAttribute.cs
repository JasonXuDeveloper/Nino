using System;

namespace Nino.Core
{
    /// <summary>
    /// Serializes the member with <see cref="FormatterType"/> instead of the formatter Nino would choose:
    /// <code>[NinoCustomFormatter(typeof(ZigZagFormatter))] public long Delta;</code>
    /// The formatter is a <c>struct</c> implementing <see cref="INinoFormatter{T}"/> where <c>T</c> is exactly the
    /// member type (NINO008 otherwise). Generated code calls it as <c>default(F).Serialize(...)</c>, so it is inlined
    /// like the built-in formatters; it has no instance, so state it needs lives in static fields.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class NinoCustomFormatterAttribute : Attribute
    {
        /// <summary>The struct implementing <see cref="INinoFormatter{T}"/> for the member type.</summary>
        public Type FormatterType { get; }

        /// <summary>Uses <paramref name="formatterType"/> for the member.</summary>
        /// <param name="formatterType">A struct implementing <see cref="INinoFormatter{T}"/> for the member type.</param>
        public NinoCustomFormatterAttribute(Type formatterType)
        {
            FormatterType = formatterType;
        }
    }
}
