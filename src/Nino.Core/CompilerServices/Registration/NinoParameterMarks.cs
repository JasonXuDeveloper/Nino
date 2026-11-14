using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using Nino.Core.Internal;

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

        /// <summary>A mark a type parameter can carry: its bit in the marks of that parameter.</summary>
        internal enum Mark
        {
            /// <summary><c>[NinoRaw]</c>.</summary>
            Raw = 1,

            /// <summary>The <c>unmanaged</c> constraint.</summary>
            Unmanaged = 2,

            /// <summary><c>[NinoCreated]</c>.</summary>
            Created = 4,
        }

        /// <summary>A <c>[NinoRaw]</c> parameter, which is also <c>unmanaged</c>.</summary>
        private const byte RawParameter = (byte)(Mark.Raw | Mark.Unmanaged);

        /// <summary>A <c>[NinoCreated]</c> parameter.</summary>
        private const byte CreatedParameter = (byte)Mark.Created;

        /// <summary>
        /// The marks of each registered formatter definition, by type parameter position (entry <c>i</c> for its type
        /// parameter <c>i</c>, missing entries for parameters without marks), so any number of parameters is told apart.
        /// Entries of a removed assembly's formatters are dropped (<see cref="NinoRegistry.Remove"/>), so a collectible
        /// assembly whose generated registry registered marks can unload.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, byte[]> Marks = BuiltIn();

        /// <summary>Nino.Core's formatters whose parameters carry the marks.</summary>
        private static ConcurrentDictionary<Type, byte[]> BuiltIn()
        {
            var marks = NinoUnload.Cache<byte[]>();
            marks[typeof(UnmanagedFormatter<>)] = new[] { RawParameter };
            marks[typeof(UnmanagedArrayFormatter<>)] = new[] { RawParameter };
            marks[typeof(NullableArrayFormatter<>)] = new[] { RawParameter };
            marks[typeof(UnmanagedListFormatter<,>)] = new[] { CreatedParameter, RawParameter };
            marks[typeof(NullableListFormatter<,>)] = new[] { CreatedParameter, RawParameter };
            marks[typeof(NinoNullableFormatter<>)] = new[] { RawParameter };
            marks[typeof(NinoNullableArrayFormatter<>)] = new[] { RawParameter };
            marks[typeof(NinoNullableListFormatter<,>)] = new[] { CreatedParameter, RawParameter };
            marks[typeof(ListFormatter<,,>)] = new[] { CreatedParameter };
            marks[typeof(CollectionFormatter<,,>)] = new[] { CreatedParameter };
            marks[typeof(HashSetFormatter<,,>)] = new[] { CreatedParameter };
            marks[typeof(QueueFormatter<,,>)] = new[] { CreatedParameter };
            marks[typeof(StackFormatter<,,>)] = new[] { CreatedParameter };
            marks[typeof(ProducerConsumerFormatter<,,>)] = new[] { CreatedParameter };
            marks[typeof(DictionaryFormatter<,,,,>)] = new[] { CreatedParameter };
            marks[typeof(DictionaryLikeFormatter<,,,,>)] = new[] { CreatedParameter };
            return marks;
        }

        /// <summary>
        /// Registers the marks of a formatter definition's type parameters, one value per parameter position (the
        /// first for the first parameter; trailing parameters without marks may be left out), each the sum of 1 for
        /// <c>[NinoRaw]</c>, 2 for the <c>unmanaged</c> constraint and 4 for <c>[NinoCreated]</c>. Called by generated
        /// code.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Register(Type formatter, params byte[] marks)
        {
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));
            if (marks == null)
                throw new ArgumentNullException(nameof(marks));
            Marks[formatter] = (byte[])marks.Clone();
        }

        /// <summary>Whether <paramref name="parameter"/> is <c>[NinoRaw]</c>.</summary>
        internal static bool Raw(Type parameter) =>
            Visible ? parameter.IsDefined(typeof(NinoRawAttribute), false) : Registered(parameter, Mark.Raw);

        /// <summary>Whether <paramref name="parameter"/> is <c>unmanaged</c>.</summary>
        internal static bool Unmanaged(Type parameter) =>
            Visible ? NinoActivator.HasAttribute(parameter, UnmanagedAttribute) : Registered(parameter, Mark.Unmanaged);

        /// <summary>Whether <paramref name="parameter"/> is <c>[NinoCreated]</c>.</summary>
        internal static bool Created(Type parameter) =>
            Visible ? parameter.IsDefined(typeof(NinoCreatedAttribute), false) : Registered(parameter, Mark.Created);

        /// <summary>
        /// Whether the registered marks give <paramref name="parameter"/> <paramref name="mark"/>; false for a formatter
        /// nothing registered.
        /// </summary>
        internal static bool Registered(Type parameter, Mark mark)
        {
            if (parameter.DeclaringType == null || !Marks.TryGetValue(parameter.DeclaringType, out var marks))
                return false;
            int position = parameter.GenericParameterPosition;
            return position < marks.Length && (marks[position] & (int)mark) != 0;
        }
    }
}
