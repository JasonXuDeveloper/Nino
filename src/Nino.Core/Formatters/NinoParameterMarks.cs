using System;

namespace Nino.Core
{
    /// <summary>
    /// What the type parameters of a formatter require of the types bound to them, as their attributes say
    /// (<see cref="NinoRawAttribute"/>, <see cref="NinoCreatedAttribute"/>, C#'s <c>unmanaged</c>, which is the
    /// attribute <c>IsUnmanagedAttribute</c>), for <see cref="NinoFormatterResolver"/>.
    /// <para>
    /// IL2CPP keeps no custom attributes on generic parameters: there every one of them reads as absent, and the
    /// resolver bound <c>UnmanagedListFormatter</c> to a <c>List</c> of value tuples (raw memory instead of their items,
    /// other bytes than every other runtime's). Where the runtime shows none on <c>UnmanagedFormatter&lt;T&gt;</c>'s
    /// <c>T</c>, the marks of Nino.Core's own formatters come from the table below instead (a formatter of another
    /// assembly keeps its own attributes' absence there). <c>NinoParameterMarksTests</c> checks the table against the
    /// attributes.
    /// </para>
    /// </summary>
    internal static class NinoParameterMarks
    {
        private const string UnmanagedAttribute = "System.Runtime.CompilerServices.IsUnmanagedAttribute";

        /// <summary>Whether this runtime shows the custom attributes of generic parameters.</summary>
        internal static readonly bool Visible =
            typeof(UnmanagedFormatter<>).GetGenericArguments()[0].IsDefined(typeof(NinoRawAttribute), false);

        /// <summary>
        /// Nino.Core's formatters whose parameters carry the marks: the definition, then the position of each parameter
        /// that is <c>[NinoRaw]</c> (also <c>unmanaged</c>), and of each that is <c>[NinoCreated]</c> (-1 for none).
        /// </summary>
        internal static readonly (Type Formatter, int Raw, int Created)[] Table =
        {
            (typeof(UnmanagedFormatter<>), 0, -1),
            (typeof(UnmanagedArrayFormatter<>), 0, -1),
            (typeof(NullableArrayFormatter<>), 0, -1),
            (typeof(UnmanagedListFormatter<,>), 1, 0),
            (typeof(NullableListFormatter<,>), 1, 0),
            (typeof(ListFormatter<,,>), -1, 0),
            (typeof(CollectionFormatter<,,>), -1, 0),
            (typeof(HashSetFormatter<,,>), -1, 0),
            (typeof(QueueFormatter<,,>), -1, 0),
            (typeof(StackFormatter<,,>), -1, 0),
            (typeof(ProducerConsumerFormatter<,,>), -1, 0),
            (typeof(DictionaryFormatter<,,,,>), -1, 0),
            (typeof(DictionaryLikeFormatter<,,,,>), -1, 0),
        };

        /// <summary>Whether <paramref name="parameter"/> is <c>[NinoRaw]</c>.</summary>
        public static bool Raw(Type parameter) =>
            Visible ? parameter.IsDefined(typeof(NinoRawAttribute), false) : InTable(parameter, true);

        /// <summary>Whether <paramref name="parameter"/> is <c>unmanaged</c>.</summary>
        public static bool Unmanaged(Type parameter) =>
            Visible ? NinoActivator.HasAttribute(parameter, UnmanagedAttribute) : InTable(parameter, true);

        /// <summary>Whether <paramref name="parameter"/> is <c>[NinoCreated]</c>.</summary>
        public static bool Created(Type parameter) =>
            Visible ? parameter.IsDefined(typeof(NinoCreatedAttribute), false) : InTable(parameter, false);

        /// <summary>The table's answer: [NinoRaw] (and unmanaged) with <paramref name="raw"/>, else [NinoCreated].</summary>
        internal static bool InTable(Type parameter, bool raw)
        {
            var formatter = parameter.DeclaringType;
            foreach (var entry in Table)
                if (entry.Formatter == formatter)
                    return (raw ? entry.Raw : entry.Created) == parameter.GenericParameterPosition;
            return false;
        }
    }
}
