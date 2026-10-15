using System;

namespace Nino.Core
{
    /// <summary>
    /// Registers a struct formatter for every type it can format, for generated code and for runtime resolution
    /// alike. Applied to the assembly that declares (or uses) the formatter:
    /// <code>[assembly: NinoFormatter(typeof(MyDequeFormatter&lt;,&gt;))]</code>
    /// <para>
    /// The formatter implements <see cref="INinoFormatter{T}"/> for exactly one pattern <c>P</c>, written in terms of
    /// its type parameters. A type <c>X</c> uses the formatter when its type parameters can be bound so that
    /// <c>P</c> is <c>X</c> and every constraint holds:
    /// <list type="bullet">
    /// <item>parameters that occur in <c>P</c> are bound by matching <c>P</c> against <c>X</c>;</item>
    /// <item>a type constraint (<c>where TList : List&lt;T&gt;</c>) binds the parameters it mentions, by matching it
    /// against the bound type, its base types or its interfaces;</item>
    /// <item>a parameter constrained to <c>INinoFormatter&lt;E&gt;</c> is bound to the formatter Nino resolves for
    /// <c>E</c>, so element formatters compose (and inline: they are structs);</item>
    /// <item>constraints keep their C# meaning (<c>class</c>, <c>struct</c>, <c>unmanaged</c>, <c>new()</c>);</item>
    /// <item><see cref="NinoRawAttribute"/> on a parameter (<c>[NinoRaw] T</c>, with <c>where T : unmanaged</c>): Nino
    /// copies the values as raw memory, so the bound type must be one Nino writes as raw bytes (portable unmanaged
    /// types: no <c>Nullable</c>, tuples, <c>nint</c> or <c>DateTimeOffset</c>);</item>
    /// <item><see cref="NinoCreatedAttribute"/> on a parameter (<c>[NinoCreated] TCollection</c>): the formatter
    /// creates instances through <see cref="NinoActivator{T}.Create"/>, so the bound type must be a non-abstract class
    /// with a public static <see cref="NinoFactoryAttribute"/> factory or a public constructor callable
    /// without arguments. Use it instead of <c>new()</c>, which admits only parameterless constructors and compiles to
    /// <c>Activator.CreateInstance&lt;T&gt;()</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// A formatter whose pattern is a bare type parameter (<c>INinoFormatter&lt;TCollection&gt;</c>) applies to
    /// every type its constraints admit, or only to the generic type definitions listed in <see cref="Targets"/>.
    /// Formatters of a specific type definition (and of arrays) are tried before anything else, in
    /// <see cref="Priority"/> order; formatters of a bare type parameter are tried after <c>[NinoType]</c>s.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class NinoFormatterAttribute : Attribute
    {
        /// <summary>Registers <paramref name="formatterType"/> (an open generic definition, or a non-generic struct).</summary>
        /// <param name="formatterType">The struct implementing <see cref="INinoFormatter{T}"/>.</param>
        /// <param name="targets">
        /// For a formatter of a bare type parameter: the type definitions it applies to (e.g.
        /// <c>typeof(IList&lt;&gt;)</c>). Empty: any type its constraints admit.
        /// </param>
        public NinoFormatterAttribute(Type formatterType, params Type[] targets)
        {
            FormatterType = formatterType;
            Targets = targets ?? Array.Empty<Type>();
        }

        /// <summary>The registered formatter.</summary>
        public Type FormatterType { get; }

        /// <summary>The type definitions a formatter of a bare type parameter is restricted to.</summary>
        public Type[] Targets { get; }

        /// <summary>
        /// Order among formatters that can format the same type: higher first. Ties go to the formatter with the
        /// ordinally smaller full name.
        /// </summary>
        public int Priority { get; set; }
    }
}
