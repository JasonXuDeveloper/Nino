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
    private readonly IAssemblySymbol _assembly;
    private readonly bool _weakTolerance;

    /// <param name="weakTolerance">The assembly defines WEAK_VERSION_TOLERANCE (its own structs are never raw).</param>
    public RawRules(NinoSymbols symbols, MemberCache members, IAssemblySymbol assembly, bool weakTolerance)
    {
        _symbols = symbols;
        _members = members;
        _assembly = assembly;
        _weakTolerance = weakTolerance;
        _computeRawStruct = s => ComputeIsRawStruct((INamedTypeSymbol)s);
    }

    /// <summary>Whether values of <paramref name="type"/> are written as their raw bytes.</summary>
    public bool IsRawUnmanaged(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol || !type.IsUnmanagedType || VariableVectorIn(type) != null)
            return false;
        if (type.IsNinoType())
            return type is INamedTypeSymbol ninoStruct && IsRawStruct(ninoStruct);
        if (type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr || type is IPointerTypeSymbol)
            return false; // pointer-sized: written as 8 bytes
        if (type is INamedTypeSymbol named)
        {
            // Tuples of items have formats of their own; System.ValueTuple, of none, is raw as in Nino.Core.
            if (named.IsTupleType && named.Arity != 0)
                return false;
            var definition = named.OriginalDefinition;
            if (definition.SpecialType == SpecialType.System_Nullable_T)
                return false;
            if (TypeNames.HasMetadataName(definition, "System.Collections.Generic.KeyValuePair`2") ||
                TypeNames.IsValueTuple(definition) || TypeNames.HasMetadataName(definition, "System.DateTimeOffset"))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The <c>System.Numerics.Vector&lt;T&gt;</c> that <paramref name="type"/> is or holds, or null: as an array
    /// element, a type argument (of a <c>[NinoType]</c>, only one its serialized members use), in a base type or the
    /// element of an <c>IEnumerable&lt;T&gt;</c> it implements (<c>class Lanes : List&lt;Vector&lt;int&gt;&gt;</c>), or
    /// a field at any depth of a struct that is not a <c>[NinoType]</c> (whose members are checked on their own
    /// declaration). <c>Vector&lt;T&gt;</c> is as wide as the CPU's vectors (16 or 32 bytes), so no layout of it reads
    /// on every host: such a type has no formatter (<see cref="FormatterResolver"/>), is never raw, and a member or
    /// top-level call with one is NINO029 (not NINO007). Nino.Core refuses the same types at run time
    /// (<c>NinoReflection.VariableVectorIn</c>).
    /// </summary>
    public ITypeSymbol? VariableVectorIn(ITypeSymbol type)
    {
        if (_vectors.TryGetValue(type, out var found))
            return found;
        HashSet<ITypeSymbol>? open = null;
        bool cut = false;
        return _vectors.GetOrAdd(type, ComputeVariableVector(type, ref open, ref cut));
    }

    /// <summary>
    /// The <c>Vector&lt;T&gt;</c> that one of Nino.Core's own generic formatters, used by hand
    /// (<c>new UnmanagedFormatter&lt;Vector&lt;int&gt;&gt;()</c>, <c>default(...)</c> or a
    /// <c>[NinoMemberFormatter]</c>), would write, or null; <paramref name="value"/> is the type argument that holds
    /// it. Nino.Core refuses it at run time like <see cref="VariableVectorIn(ITypeSymbol)"/>'s types; a user's own
    /// formatter writes a <c>Vector&lt;T&gt;</c> its own way and is never searched.
    /// </summary>
    public ITypeSymbol? VariableVectorWrittenBy(INamedTypeSymbol formatter, out ITypeSymbol? value)
    {
        value = null;
        return formatter.IsGenericType && _symbols.IFormatter is { } core &&
               SymbolEqualityComparer.Default.Equals(formatter.ContainingAssembly, core.ContainingAssembly) &&
               _symbols.IsFormatter(formatter)
            ? VariableVectorWrittenBy(formatter.TypeParameters, formatter.TypeArguments, out value)
            : null;
    }

    /// <summary>
    /// <see cref="VariableVectorWrittenBy(INamedTypeSymbol, out ITypeSymbol?)"/> for a generic method of Nino.Core's
    /// <c>Writer</c>, <c>Reader</c> or <c>NinoDirect</c> (<c>writer.Write(vector)</c>, <c>reader.Read(out T[] v)</c>),
    /// with its type arguments as the call has them, inferred or not.
    /// </summary>
    public ITypeSymbol? VariableVectorWrittenBy(IMethodSymbol method, out ITypeSymbol? value) =>
        VariableVectorWrittenBy(method.TypeParameters, method.TypeArguments, out value);

    // Where a type parameter is a formatter (ArrayFormatter<T, TFormatter>, NinoDirect.Serialize<T, TFormatter>), that
    // formatter writes the values of the others: only it is searched, and only if it is Nino.Core's. Otherwise every
    // type argument is a value written (UnmanagedFormatter<T>, Writer.Write<T>, UnmanagedListFormatter<TList, T>).
    private ITypeSymbol? VariableVectorWrittenBy(ImmutableArray<ITypeParameterSymbol> parameters,
        ImmutableArray<ITypeSymbol> arguments, out ITypeSymbol? value)
    {
        value = null;
        bool delegates = parameters.Any(p => p.ConstraintTypes.Any(_symbols.IsFormatter));
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!delegates)
            {
                if (VariableVectorIn(arguments[i]) is { } vector)
                {
                    value = arguments[i];
                    return vector;
                }
            }
            else if (arguments[i] is INamedTypeSymbol formatter &&
                     parameters[i].ConstraintTypes.Any(_symbols.IsFormatter) &&
                     VariableVectorWrittenBy(formatter, out value) is { } inFormatter)
                return inFormatter;
        }

        return null;
    }

    /// <summary>
    /// <see cref="VariableVectorIn(ITypeSymbol)"/> of <paramref name="child"/>, met while searching
    /// <paramref name="parent"/>. <paramref name="open"/> holds the types being searched (created at the first step
    /// down): a type that holds itself (<c>class Tree : List&lt;Tree&gt;</c>, <c>struct Node { List&lt;Node&gt; C; }</c>)
    /// is not searched again (<paramref name="cut"/>), and a null found under such a cut is not memoized, since the
    /// rest of that type may still hold a vector.
    /// </summary>
    private ITypeSymbol? VariableVectorIn(ITypeSymbol parent, ITypeSymbol child, ref HashSet<ITypeSymbol>? open,
        ref bool cut)
    {
        if (_vectors.TryGetValue(child, out var found))
            return found;
        var searching = open ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { parent };
        if (!searching.Add(child))
        {
            cut = true;
            return null;
        }

        bool childCut = false;
        found = ComputeVariableVector(child, ref open, ref childCut);
        searching.Remove(child);
        if (found != null || !childCut)
            _vectors.TryAdd(child, found);
        cut |= childCut;
        return found;
    }

    private ITypeSymbol? ComputeVariableVector(ITypeSymbol type, ref HashSet<ITypeSymbol>? open, ref bool cut)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return VariableVectorIn(type, array.ElementType, ref open, ref cut);
            case INamedTypeSymbol named:
                var definition = named.OriginalDefinition;
                if (definition.Arity == 1 && TypeNames.HasMetadataName(definition, "System.Numerics.Vector`1"))
                    return named;
                // A ref struct (Span<Vector<T>>) is never serialized: NINO007 says so, without a formatter for it.
                if (named.TypeKind == TypeKind.Enum || named.IsRefLikeType ||
                    named.SpecialType is not (SpecialType.None or SpecialType.System_Nullable_T))
                    return null;
                var arguments = TypeNames.AllTypeArguments(named);
                if (definition.GetNinoTypeOptions() is { } options)
                {
                    if (arguments.Count == 0)
                        return null;
                    var parameters = TypeNames.AllTypeParameters(definition);
                    var members = _members.Of(definition, options);
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        if (VariableVectorIn(type, arguments[i], ref open, ref cut) is { } inArgument &&
                            members.Any(m => MemberCollector.MemberType(m) is { } t && TypeNames.Mentions(t, parameters[i])))
                            return inArgument;
                    }

                    return null;
                }

                foreach (var argument in arguments)
                {
                    if (VariableVectorIn(type, argument, ref open, ref cut) is { } inArgument)
                        return inArgument;
                }

                // What a collection formatter writes of a type that names no vector itself: its base type's
                // (class Lanes : List<Vector<int>>) and the elements of the IEnumerable<T> it implements.
                if (named.BaseType is { SpecialType: SpecialType.None } baseType &&
                    VariableVectorIn(type, baseType, ref open, ref cut) is { } inBase)
                    return inBase;
                foreach (var face in named.AllInterfaces)
                {
                    if (face.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                        VariableVectorIn(type, face.TypeArguments[0], ref open, ref cut) is { } inElements)
                        return inElements;
                }

                if (named.TypeKind != TypeKind.Struct)
                    return null;
                foreach (var member in named.GetMembers())
                {
                    if (member is IFieldSymbol { IsStatic: false, IsConst: false } field &&
                        VariableVectorIn(type, field.Type, ref open, ref cut) is { } inField)
                        return inField;
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
        // WEAK_VERSION_TOLERANCE only applies to the types of the assembly that defines it.
        if (_weakTolerance && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _assembly))
            return false;
        var attr = type.GetNinoTypeOptions();
        if (attr == null || attr.VersionTolerant || attr.Keyed)
            return false;
        if (RawLayout.KindOf(type) != System.Runtime.InteropServices.LayoutKind.Sequential)
            return false;

        var members = new HashSet<ISymbol>(_members.Of(type.OriginalDefinition, attr),
            SymbolEqualityComparer.Default);
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            var member = field.AssociatedMember() ?? field;
            if (!members.Contains(member.OriginalDefinition))
                return false;
            if (NinoSymbols.Has(member, _symbols.CustomFormatter))
                return false;
            // Every field must itself be raw: Nullable, tuples, nint or DateTimeOffset have their own formats.
            if (!IsRawUnmanaged(field.Type))
                return false;
        }

        return true;
    }
}
