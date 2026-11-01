using System;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// What the type parameters of a formatter require of the types bound to them, as their attributes say
    /// (<see cref="NinoRawAttribute"/>, <see cref="NinoCreatedAttribute"/>, C#'s <c>unmanaged</c>, which is the
    /// attribute <c>IsUnmanagedAttribute</c>), for <see cref="NinoFormatterResolver"/>.
    /// <para>
    /// IL2CPP keeps no custom attributes on generic parameters: there every one of them reads as absent, and the
    /// resolver bound <c>UnmanagedListFormatter</c> to a <c>List</c> of value tuples (raw memory instead of their items,
    /// other bytes than every other runtime's). Where the runtime shows none on <c>UnmanagedFormatter&lt;T&gt;</c>'s
    /// <c>T</c>, the marks come from <see cref="Register"/> instead: Nino.Core registers its own formatters' below, and
    /// the generated registry of each assembly registers those of the formatters its <c>[assembly: NinoFormatter]</c>
    /// attributes name, in its static constructor, before the resolver reads those attributes. A formatter no
    /// generated code registered keeps its attributes' absence there. <c>NinoParameterMarksTests</c> checks Nino.Core's
    /// entries against the attributes, and the generator's against the declarations.
    /// </para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoParameterMarks
    {
        private const string UnmanagedAttribute = "System.Runtime.CompilerServices.IsUnmanagedAttribute";

        /// <summary>Whether this runtime shows the custom attributes of generic parameters.</summary>
        internal static readonly bool Visible =
            typeof(UnmanagedFormatter<>).GetGenericArguments()[0].IsDefined(typeof(NinoRawAttribute), false);

        /// <summary>The marks of each registered formatter definition: bit <c>i</c> for its type parameter <c>i</c>.</summary>
        private static readonly ConcurrentDictionary<Type, (int Raw, int Unmanaged, int Created)> Marks = BuiltIn();

        /// <summary>
        /// Nino.Core's formatters whose parameters carry the marks (a <c>[NinoRaw]</c> parameter is also
        /// <c>unmanaged</c>).
        /// </summary>
        private static ConcurrentDictionary<Type, (int, int, int)> BuiltIn()
        {
            var marks = new ConcurrentDictionary<Type, (int, int, int)>();
            marks[typeof(UnmanagedFormatter<>)] = (1, 1, 0);
            marks[typeof(UnmanagedArrayFormatter<>)] = (1, 1, 0);
            marks[typeof(NullableArrayFormatter<>)] = (1, 1, 0);
            marks[typeof(UnmanagedListFormatter<,>)] = (2, 2, 1);
            marks[typeof(NullableListFormatter<,>)] = (2, 2, 1);
            marks[typeof(ListFormatter<,,>)] = (0, 0, 1);
            marks[typeof(CollectionFormatter<,,>)] = (0, 0, 1);
            marks[typeof(HashSetFormatter<,,>)] = (0, 0, 1);
            marks[typeof(QueueFormatter<,,>)] = (0, 0, 1);
            marks[typeof(StackFormatter<,,>)] = (0, 0, 1);
            marks[typeof(ProducerConsumerFormatter<,,>)] = (0, 0, 1);
            marks[typeof(DictionaryFormatter<,,,,>)] = (0, 0, 1);
            marks[typeof(DictionaryLikeFormatter<,,,,>)] = (0, 0, 1);
            return marks;
        }

        /// <summary>
        /// Registers the marks of a formatter definition's type parameters, one bit per parameter position (bit 0 for
        /// the first): <paramref name="raw"/> for <c>[NinoRaw]</c>, <paramref name="unmanaged"/> for the
        /// <c>unmanaged</c> constraint, <paramref name="created"/> for <c>[NinoCreated]</c>. Called by generated code.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Register(Type formatter, int raw, int unmanaged, int created)
        {
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));
            Marks[formatter] = (raw, unmanaged, created);
        }

        /// <summary>Whether <paramref name="parameter"/> is <c>[NinoRaw]</c>.</summary>
        internal static bool Raw(Type parameter) =>
            Visible ? parameter.IsDefined(typeof(NinoRawAttribute), false) : Registered(parameter, 0);

        /// <summary>Whether <paramref name="parameter"/> is <c>unmanaged</c>.</summary>
        internal static bool Unmanaged(Type parameter) =>
            Visible ? NinoActivator.HasAttribute(parameter, UnmanagedAttribute) : Registered(parameter, 1);

        /// <summary>Whether <paramref name="parameter"/> is <c>[NinoCreated]</c>.</summary>
        internal static bool Created(Type parameter) =>
            Visible ? parameter.IsDefined(typeof(NinoCreatedAttribute), false) : Registered(parameter, 2);

        /// <summary>
        /// The registered answer for <paramref name="parameter"/>: <paramref name="mark"/> 0 for <c>[NinoRaw]</c>, 1 for
        /// <c>unmanaged</c>, 2 for <c>[NinoCreated]</c>; false for a formatter nothing registered.
        /// </summary>
        internal static bool Registered(Type parameter, int mark)
        {
            if (parameter.DeclaringType == null || !Marks.TryGetValue(parameter.DeclaringType, out var marks))
                return false;
            int bits = mark == 0 ? marks.Raw : mark == 1 ? marks.Unmanaged : marks.Created;
            return (bits >> parameter.GenericParameterPosition & 1) != 0;
        }
    }
}
