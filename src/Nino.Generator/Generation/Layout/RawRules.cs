using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>Which values are written as their raw bytes, memoized for one compilation.</summary>
internal sealed class RawRules
{
    private readonly ConcurrentDictionary<ISymbol, bool> _rawStructs = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, ITypeSymbol?> _vectors = new(SymbolEqualityComparer.Default);
    private readonly Func<ISymbol, bool> _computeRawStruct;
    private readonly NinoSymbols _symbols;
    private readonly MemberCache _members;
    private readonly WrittenParameters _written;

    public RawRules(NinoSymbols symbols, MemberCache members, WrittenParameters written)
    {
        _symbols = symbols;
        _members = members;
        _written = written;
        _computeRawStruct = s => ComputeIsRawStruct((INamedTypeSymbol)s);
    }

    /// <summary>Whether values of <paramref name="type"/> are written as their raw bytes.</summary>
    public bool IsRawUnmanaged(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol || !type.IsUnmanagedType || HostSizedVectorIn(type) != null)
            return false;
        if (type.IsNinoType())
            return type is INamedTypeSymbol ninoStruct && IsRawStruct(ninoStruct);
        if (type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr ||
            type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
            return false; // pointer-sized: written as 8 bytes
        if (type is INamedTypeSymbol named)
        {
            // Tuples of items have formats of their own; System.ValueTuple, of none, is raw as in Nino.Core.
            if (named.IsTupleType && named.Arity != 0)
                return false;
            var definition = named.OriginalDefinition;
            // Nullable and NinoNullable (written as Nullable) have a format of their own.
            if (definition.SpecialType == SpecialType.System_Nullable_T ||
                TypeNames.HasMetadataName(definition, NinoNames.NinoNullable))
                return false;
            if (TypeNames.HasMetadataName(definition, "System.Collections.Generic.KeyValuePair`2") ||
                TypeNames.IsValueTuple(definition) || TypeNames.HasMetadataName(definition, "System.DateTimeOffset"))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The <c>System.Numerics.Vector&lt;T&gt;</c> that <paramref name="type"/> is or holds, or null: as an array
    /// element, a type argument, in a base type or the element of an <c>IEnumerable&lt;T&gt;</c> it implements
    /// (<c>class Lanes : List&lt;Vector&lt;int&gt;&gt;</c>), a field at any depth of a struct that is not a
    /// <c>[NinoType]</c>, or a type argument a generic <c>[NinoType]</c>'s formatter writes
    /// (<see cref="WrittenParameters"/>: <c>Box&lt;Vector&lt;int&gt;&gt;</c> of <c>Box&lt;T&gt; { T Value; }</c>, not
    /// <c>Tag&lt;Vector&lt;int&gt;&gt;</c> of <c>Tag&lt;T&gt; { int Id; }</c> nor <c>Outer&lt;Vector&lt;int&gt;&gt;</c> of
    /// <c>Outer&lt;T&gt; { Tag&lt;T&gt; Inner; }</c>; its other members are checked on their own declaration).
    /// <c>Vector&lt;T&gt;</c> is as wide as the CPU's vectors (16, 32 or 64 bytes), so no layout of it reads on every
    /// host: such a type has no formatter (<see cref="FormatterResolver"/>), is never raw, and a member or top-level
    /// call with one is NINO029 (not NINO007). Nino.Core refuses the same types at run time
    /// (<c>NinoReflection.HostSizedVectorIn</c>).
    /// </summary>
    public ITypeSymbol? HostSizedVectorIn(ITypeSymbol type)
    {
        if (_vectors.TryGetValue(type, out var found))
            return found;
        HashSet<ITypeSymbol>? searched = null;
        found = ComputeHostSizedVector(type, ref searched);
        // A search returns the first vector it finds through every step up to here, so a query that found none
        // searched every type it met to the end: none of them holds one.
        if (found == null && searched != null)
        {
            foreach (var t in searched)
                _vectors.TryAdd(t, null);
        }

        return _vectors.GetOrAdd(type, found);
    }

    /// <summary>
    /// The <c>Vector&lt;T&gt;</c> that one of Nino.Core's own generic formatters, used by hand
    /// (<c>new UnmanagedFormatter&lt;Vector&lt;int&gt;&gt;()</c>, <c>default(...)</c> or a
    /// <c>[NinoMemberFormatter]</c>), would write, or null; <paramref name="value"/> is the type argument that holds
    /// it. Nino.Core refuses it at run time like <see cref="HostSizedVectorIn(ITypeSymbol)"/>'s types; a user's own
    /// formatter writes a <c>Vector&lt;T&gt;</c> its own way and is never searched.
    /// </summary>
    public ITypeSymbol? HostSizedVectorWrittenBy(INamedTypeSymbol formatter, out ITypeSymbol? value)
    {
        value = null;
        return formatter.IsGenericType && _symbols.IFormatter is { } core &&
               SymbolEqualityComparer.Default.Equals(formatter.ContainingAssembly, core.ContainingAssembly) &&
               _symbols.IsFormatter(formatter)
            ? HostSizedVectorWrittenBy(formatter.TypeParameters, formatter.TypeArguments, out value)
            : null;
    }

    /// <summary>
    /// <see cref="HostSizedVectorWrittenBy(INamedTypeSymbol, out ITypeSymbol?)"/> for a generic method of Nino.Core's
    /// <c>Writer</c>, <c>Reader</c> or <c>NinoDirect</c> (<c>writer.Write(vector)</c>, <c>reader.Read(out T[] v)</c>),
    /// with its type arguments as the call has them, inferred or not.
    /// </summary>
    public ITypeSymbol? HostSizedVectorWrittenBy(IMethodSymbol method, out ITypeSymbol? value) =>
        HostSizedVectorWrittenBy(method.TypeParameters, method.TypeArguments, out value);

    // Where a type parameter is a formatter (ArrayFormatter<T, TFormatter>, NinoDirect.Serialize<T, TFormatter>), that
    // formatter writes the values of the others: only it is searched, and only if it is Nino.Core's. Otherwise every
    // type argument is a value written (UnmanagedFormatter<T>, Writer.Write<T>, UnmanagedListFormatter<TList, T>).
    private ITypeSymbol? HostSizedVectorWrittenBy(ImmutableArray<ITypeParameterSymbol> parameters,
        ImmutableArray<ITypeSymbol> arguments, out ITypeSymbol? value)
    {
        value = null;
        bool delegates = parameters.Any(p => p.ConstraintTypes.Any(_symbols.IsFormatter));
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!delegates)
            {
                if (HostSizedVectorIn(arguments[i]) is { } vector)
                {
                    value = arguments[i];
                    return vector;
                }
            }
            else if (arguments[i] is INamedTypeSymbol formatter &&
                     parameters[i].ConstraintTypes.Any(_symbols.IsFormatter) &&
                     HostSizedVectorWrittenBy(formatter, out value) is { } inFormatter)
                return inFormatter;
        }

        return null;
    }

    /// <summary>
    /// <see cref="HostSizedVectorIn(ITypeSymbol)"/> of <paramref name="child"/>, met while searching
    /// <paramref name="parent"/>; <paramref name="searched"/> holds every type this query met (created at the first
    /// step down). A type met again (<c>class Tree : List&lt;Tree&gt;</c>, <c>struct Node { List&lt;Node&gt; C; }</c>,
    /// or a type two others hold) is not searched again: it is being searched further up, or was searched without a
    /// vector, since a vector found ends the query. Each type is searched once per query: searching a type again on
    /// every path to it was exponential (a ring of 18 collection classes each derived from a list of the next took
    /// two minutes, three times longer per class).
    /// </summary>
    private ITypeSymbol? HostSizedVectorIn(ITypeSymbol parent, ITypeSymbol child, ref HashSet<ITypeSymbol>? searched)
    {
        if (_vectors.TryGetValue(child, out var found))
            return found;
        if (!(searched ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { parent }).Add(child))
            return null;
        found = ComputeHostSizedVector(child, ref searched);
        // Every type on the way to a vector holds it.
        if (found != null)
            _vectors.TryAdd(child, found);
        return found;
    }

    private ITypeSymbol? ComputeHostSizedVector(ITypeSymbol type, ref HashSet<ITypeSymbol>? searched)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return HostSizedVectorIn(type, array.ElementType, ref searched);
            case INamedTypeSymbol named:
                var definition = named.OriginalDefinition;
                if (definition.Arity == 1 && TypeNames.HasMetadataName(definition, "System.Numerics.Vector`1"))
                    return named;
                // A ref struct (Span<Vector<T>>) is never serialized: NINO007 says so, without a formatter for it.
                if (named.TypeKind == TypeKind.Enum || named.IsRefLikeType ||
                    named.SpecialType is not (SpecialType.None or SpecialType.System_Nullable_T))
                    return null;
                var arguments = TypeNames.AllTypeArguments(named);
                if (definition.GetNinoTypeOptions() != null)
                {
                    // Its formatter writes the values WrittenParameters finds in its arguments (an argument, or a
                    // part of one a subtype writes: the X of Shelf<Tag<X>> with S<U> : Shelf<Tag<U>> { U V; }),
                    // searched as closed types; the closed member types are never built, which grow without end for
                    // a type holding itself over a bigger argument (Expanding<T> holding Expanding<List<T>>). An
                    // argument it never writes (Tag<T> { int Id; }, Outer<T> { Tag<T> X; }) is never searched, so a
                    // vector found always reaches the query.
                    foreach (var (value, inside) in _written.ValuesOf(named))
                    {
                        if ((inside ? VectorInside(type, value, ref searched) : HostSizedVectorIn(type, value, ref searched))
                            is { } inValue)
                            return inValue;
                    }

                    return null;
                }

                foreach (var argument in arguments)
                {
                    if (HostSizedVectorIn(type, argument, ref searched) is { } inArgument)
                        return inArgument;
                }

                // What a collection formatter writes of a type that names no vector itself: its base type's
                // (class Lanes : List<Vector<int>>) and the elements of the IEnumerable<T> it implements.
                if (named.BaseType is { SpecialType: SpecialType.None } baseType &&
                    HostSizedVectorIn(type, baseType, ref searched) is { } inBase)
                    return inBase;
                foreach (var face in named.AllInterfaces)
                {
                    if (face.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                        HostSizedVectorIn(type, face.TypeArguments[0], ref searched) is { } inElements)
                        return inElements;
                }

                if (named.TypeKind != TypeKind.Struct)
                    return null;
                foreach (var member in named.GetMembers())
                {
                    if (member is IFieldSymbol { IsStatic: false, IsConst: false } field &&
                        HostSizedVectorIn(type, field.Type, ref searched) is { } inField)
                        return inField;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The <c>Vector&lt;T&gt;</c> that <paramref name="value"/> or any type inside it (an element, a type argument, at
    /// any depth) holds by <see cref="HostSizedVectorIn(ITypeSymbol)"/>'s rules: what a value
    /// <see cref="WrittenParameters"/> could not follow (<see cref="PatternKind.Inside"/>) may write.
    /// </summary>
    private ITypeSymbol? VectorInside(ITypeSymbol parent, ITypeSymbol value, ref HashSet<ITypeSymbol>? searched)
    {
        if (HostSizedVectorIn(parent, value, ref searched) is { } found)
            return found;
        switch (value)
        {
            case IArrayTypeSymbol array:
                return VectorInside(value, array.ElementType, ref searched);
            case INamedTypeSymbol named:
                foreach (var argument in TypeNames.AllTypeArguments(named))
                {
                    if (VectorInside(value, argument, ref searched) is { } inArgument)
                        return inArgument;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a <c>[NinoType]</c> struct is written as its raw memory: unmanaged, not version tolerant, and every
    /// instance field is serialized (nested <c>[NinoType]</c> structs must qualify too). Such structs are copied as
    /// one block, and arrays/lists of them with a single memory copy.
    /// </summary>
    public bool IsRawStruct(INamedTypeSymbol type)
    {
        if (!type.IsValueType || type.TypeKind != TypeKind.Struct)
            return false;
        return _rawStructs.GetOrAdd(type, _computeRawStruct);
    }

    private bool ComputeIsRawStruct(INamedTypeSymbol type)
    {
        // A generic struct has one formatter, generated for its definition: an instance is raw exactly when that
        // formatter is (INinoRawFormatter, which Nino.Core reads), never by its type arguments. Pad<int> written raw
        // in one place and by its formatter in another (a Box<T> of it, its own registration) were two encodings.
        if (!SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition))
            return IsRawStruct(type.OriginalDefinition);
        if (!type.IsUnmanagedType)
            return false;
        var attr = type.GetNinoTypeOptions();
        if (attr == null || attr.Format is NinoFormat.VersionTolerant or NinoFormat.Keyed)
            return false;
        if (RawLayout.KindOf(type) != System.Runtime.InteropServices.LayoutKind.Sequential)
            return false;

        var members = new HashSet<ISymbol>(_members.Of(type.OriginalDefinition, attr),
            SymbolEqualityComparer.Default);
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            // The type is a definition: its own members are theirs, an interface's are in its terms.
            if (MemberCollector.StoredMember(field, members.Contains) is not { } member ||
                _symbols.HasCustomFormatter(member))
                return false;
            // Every field must itself be raw: Nullable, tuples, nint or DateTimeOffset have their own formats.
            if (!IsRawUnmanaged(field.Type))
                return false;
        }

        return true;
    }
}
