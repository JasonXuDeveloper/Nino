using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// A formatter as generated code names it, kept in parts so that what the generator asks of it (the twins of the
/// generated formatters it is composed of, whether it names a type parameter) needs no parsing of C#; it is
/// rendered (<see cref="ToString"/>) only when written. A type argument of a formatter is one too, a leaf named by the
/// type (<see cref="Of"/>). Equatable, so models holding it cache.
/// </summary>
/// <param name="Name">The formatter's (or the type's) name without type arguments: <c>global::Nino.Core.ListFormatter</c>.</param>
/// <param name="Args">Its type arguments, types and formatters, in order.</param>
/// <param name="Generated">A formatter this generator emits (<c>global::NinoGen.*</c>), which may have twins.</param>
/// <param name="Open">Whether it names a type parameter anywhere (it can only be named where that parameter is).</param>
/// <param name="Twin">The twin it names: <see cref="TypeNames.Top"/>, <see cref="TypeNames.Shallow"/> or "".</param>
internal sealed record FormatterRef(string Name, EquatableArray<FormatterRef> Args, bool Generated, bool Open,
    string Twin = "")
{
    /// <summary>A formatter without type arguments (<c>global::Nino.Core.StringFormatter</c>), or a custom one.</summary>
    public static FormatterRef Named(string name) => new(name, EquatableArray<FormatterRef>.Empty, false, false);

    /// <summary>The type <paramref name="type"/> as a type argument.</summary>
    public static FormatterRef Of(ITypeSymbol type) =>
        new(TypeNames.Global(type), EquatableArray<FormatterRef>.Empty, false, TypeNames.ContainsTypeParameter(type));

    /// <summary><paramref name="name"/> with the type arguments <paramref name="args"/> (none: just the name).</summary>
    public static FormatterRef Generic(string name, IEnumerable<FormatterRef> args, bool generated = false)
    {
        var list = new EquatableArray<FormatterRef>(args);
        return new FormatterRef(name, list, generated, list.Any(static a => a.Open));
    }

    /// <summary>
    /// This formatter with the generated formatters in <paramref name="tops"/> replaced by their <c>__Top</c> twins
    /// and those in <paramref name="shallow"/> by their <c>__Shallow</c> twins (by <see cref="Name"/>): the formatter
    /// itself, or the element formatters it is composed of (the type arguments of built-in collection, nullable and
    /// tuple formatters). A generic generated formatter has no twins, and its formatter arguments keep theirs.
    /// </summary>
    public FormatterRef WithTwins(HashSet<string> tops, HashSet<string> shallow)
    {
        if (tops.Count == 0 && shallow.Count == 0)
            return this;
        if (Generated && Args.Length == 0)
        {
            string twin = tops.Contains(Name) ? TypeNames.Top : shallow.Contains(Name) ? TypeNames.Shallow : "";
            return twin.Length == 0 ? this : this with { Twin = twin };
        }

        if (Generated || Args.Length == 0)
            return this;
        var args = Args.Select(a => a.WithTwins(tops, shallow)).ToArray();
        return args.SequenceEqual(Args) ? this : this with { Args = new EquatableArray<FormatterRef>(args) };
    }

    /// <summary>The C# source of the formatter.</summary>
    public override string ToString()
    {
        if (Args.Length == 0)
            return Twin.Length == 0 ? Name : Name + Twin;
        var sb = new StringBuilder(64);
        Append(sb);
        return sb.ToString();
    }

    private void Append(StringBuilder sb)
    {
        sb.Append(Name);
        if (Args.Length != 0)
        {
            sb.Append('<');
            for (int i = 0; i < Args.Length; i++)
            {
                if (i != 0)
                    sb.Append(", ");
                Args[i].Append(sb);
            }

            sb.Append('>');
        }

        sb.Append(Twin);
    }
}
