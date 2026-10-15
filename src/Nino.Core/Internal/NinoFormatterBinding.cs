#nullable disable
using System.Collections.Generic;

// Linked into Nino.Generator, where a Nino.Core namespace would shadow its TypeNames.Core constant.
#if NINO_GENERATOR
namespace Nino.Generator.Generation
#else
namespace Nino.Core.Internal
#endif
{
    /// <summary>
    /// What binding a <c>[NinoFormatter]</c> registration needs to know about types: <c>System.Type</c> at run time
    /// (<c>NinoFormatterResolver</c>), <c>ITypeSymbol</c> in the generator (<c>FormatterResolver</c>). Patterns are
    /// written in the formatter's type parameters, which are identified by ordinal.
    /// </summary>
    internal interface INinoTypeSystem<TType> where TType : class
    {
        /// <summary>Whether <paramref name="type"/> is a type parameter of a type (not of a method).</summary>
        bool IsTypeParameter(TType type, out int ordinal);

        /// <summary>Whether the type is an array; key 1 for single-dimensional zero-based ones, else -rank.</summary>
        bool IsArray(TType type, out int key, out TType element);

        /// <summary>Whether the type is a constructed generic type (tuples as their <c>ValueTuple</c>).</summary>
        bool IsConstructed(TType type, out TType definition, out IReadOnlyList<TType> arguments);

        bool IsInterface(TType type);

        /// <summary>Pointers and other types that can never bind a type parameter.</summary>
        bool IsPointerLike(TType type);

        bool Same(TType a, TType b);

        /// <summary>The type as bound to a parameter (without nullable annotations).</summary>
        TType Bound(TType type);

        /// <summary>The base type, or null.</summary>
        TType BaseType(TType type);

        /// <summary>Every interface the type implements.</summary>
        IEnumerable<TType> Interfaces(TType type);

        /// <summary>An array of <paramref name="element"/> with the key of <see cref="IsArray"/>, or null.</summary>
        TType MakeArray(TType element, int key);

        /// <summary>The generic definition constructed with <paramref name="arguments"/>, or null.</summary>
        TType Construct(TType definition, TType[] arguments);
    }

    /// <summary>
    /// What binding one registration needs to know about its formatter: the constraints of each type parameter, which
    /// parameters are element formatters, how to resolve those, and the checks that are specific to each side.
    /// </summary>
    internal interface INinoFormatterParameters<TType, TFormatter> where TType : class
    {
        /// <summary>The formatter's type parameters.</summary>
        int Count { get; }

        /// <summary>The type constraints of parameter <paramref name="index"/> (class, interface and parameter types).</summary>
        IEnumerable<TType> Constraints(int index);

        /// <summary>For a parameter constrained to <c>INinoFormatter&lt;E&gt;</c>, <c>E</c>; else null.</summary>
        TType FormatterElement(int index);

        /// <summary>The formatter of an element type, or false when there is none.</summary>
        bool ResolveElement(TType element, out TFormatter formatter);

        /// <summary>Whether a bound type satisfies the parameter's other constraints (class, struct, [NinoRaw]...).</summary>
        bool Satisfies(int index, TType bound, TType[] types);
    }

    /// <summary>
    /// The registration binding shared by the runtime and the generator, so both pick the same formatter for a type
    /// (see <c>NinoFormatterAttribute</c>). This file is compiled into Nino.Core and linked into Nino.Generator.
    /// </summary>
    internal static class NinoFormatterBinding
    {
        /// <summary>
        /// Binds the formatter's type parameters for <paramref name="target"/>: match the pattern, infer from type
        /// constraints, resolve element formatters, then check every constraint. On success each parameter is in
        /// <paramref name="types"/> (a type) or <paramref name="formatters"/> (an element formatter).
        /// </summary>
        public static bool Bind<TType, TFormatter, TSystem, TParameters>(TSystem system, TParameters parameters,
            TType pattern, TType target, out TType[] types, out TFormatter[] formatters)
            where TType : class
            where TSystem : INinoTypeSystem<TType>
            where TParameters : INinoFormatterParameters<TType, TFormatter>
        {
            int count = parameters.Count;
            types = new TType[count];
            formatters = new TFormatter[count];
            var resolved = new bool[count];
            if (!Unify(system, pattern, target, types))
                return false;

            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int i = 0; i < count; i++)
                {
                    if (types[i] is { } bound)
                    {
                        foreach (var constraint in parameters.Constraints(i))
                        {
                            if (IsBound(system, constraint, types))
                                continue;
                            if (!UnifyWithSupertype(system, constraint, bound, types))
                                return false;
                            progress = true;
                        }
                    }
                    else if (!resolved[i] && parameters.FormatterElement(i) is { } element &&
                             IsBound(system, element, types))
                    {
                        var elementType = Substitute(system, element, types);
                        if (elementType == null || !parameters.ResolveElement(elementType, out formatters[i]))
                            return false;
                        resolved[i] = true;
                        progress = true;
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (types[i] is { } bound ? !parameters.Satisfies(i, bound, types) : !resolved[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Matches <paramref name="pattern"/> against <paramref name="actual"/>, binding type parameters by ordinal; a
        /// bound parameter must match exactly. Bindings are only committed when the whole match succeeds.
        /// </summary>
        public static bool Unify<TType, TSystem>(TSystem system, TType pattern, TType actual, TType[] types)
            where TType : class where TSystem : INinoTypeSystem<TType>
        {
            var trial = (TType[])types.Clone();
            if (!Match(system, pattern, actual, trial))
                return false;
            trial.CopyTo(types, 0);
            return true;
        }

        /// <summary>
        /// Unifies <paramref name="pattern"/> with <paramref name="actual"/> or the base type or interface of the same
        /// definition (for type constraints such as <c>where TList : List&lt;T&gt;</c>). A type implementing an
        /// interface for two different arguments (<c>ICollection&lt;int&gt;</c> and <c>ICollection&lt;string&gt;</c>)
        /// is ambiguous: it matches neither, whatever order the interfaces are listed in.
        /// </summary>
        public static bool UnifyWithSupertype<TType, TSystem>(TSystem system, TType pattern, TType actual,
            TType[] types)
            where TType : class where TSystem : INinoTypeSystem<TType>
        {
            if (Unify(system, pattern, actual, types))
                return true;
            if (system.IsTypeParameter(pattern, out _) || system.IsArray(pattern, out _, out _) ||
                system.IsTypeParameter(actual, out _))
                return false;
            var definition = system.IsConstructed(pattern, out var d, out _) ? d : pattern;
            if (system.IsInterface(pattern))
            {
                TType[] found = null;
                foreach (var i in system.Interfaces(actual))
                {
                    var trial = (TType[])types.Clone();
                    if (!SameDefinition(system, i, definition) || !Unify(system, pattern, i, trial))
                        continue;
                    if (found != null && !SameBindings(system, found, trial))
                        return false;
                    found = trial;
                }

                if (found == null)
                    return false;
                found.CopyTo(types, 0);
                return true;
            }

            for (var b = system.BaseType(actual); b != null; b = system.BaseType(b))
            {
                if (SameDefinition(system, b, definition))
                    return Unify(system, pattern, b, types);
            }

            return false;
        }

        /// <summary>Whether every type parameter in <paramref name="type"/> is bound.</summary>
        public static bool IsBound<TType, TSystem>(TSystem system, TType type, TType[] types)
            where TType : class where TSystem : INinoTypeSystem<TType>
        {
            if (system.IsTypeParameter(type, out int ordinal))
                return ordinal < types.Length && types[ordinal] != null;
            if (system.IsArray(type, out _, out var element))
                return IsBound(system, element, types);
            if (system.IsConstructed(type, out _, out var arguments))
            {
                foreach (var argument in arguments)
                {
                    if (!IsBound(system, argument, types))
                        return false;
                }
            }

            return true;
        }

        /// <summary>Replaces the type parameters of a fully bound <paramref name="type"/>; null if it cannot be built.</summary>
        public static TType Substitute<TType, TSystem>(TSystem system, TType type, TType[] types)
            where TType : class where TSystem : INinoTypeSystem<TType>
        {
            if (system.IsTypeParameter(type, out int ordinal))
                return types[ordinal];
            if (system.IsArray(type, out int key, out var element))
                return Substitute(system, element, types) is { } e ? system.MakeArray(e, key) : null;
            if (!system.IsConstructed(type, out var definition, out var arguments))
                return type;
            var args = new TType[arguments.Count];
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] = Substitute(system, arguments[i], types)) == null)
                    return null;
            }

            return system.Construct(definition, args);
        }

        private static bool Match<TType, TSystem>(TSystem system, TType pattern, TType actual, TType[] types)
            where TType : class where TSystem : INinoTypeSystem<TType>
        {
            if (system.IsTypeParameter(pattern, out int ordinal) && ordinal < types.Length)
            {
                if (types[ordinal] is { } bound)
                    return system.Same(bound, actual);
                if (system.IsPointerLike(actual))
                    return false;
                types[ordinal] = system.Bound(actual);
                return true;
            }

            if (system.IsArray(pattern, out int key, out var element))
                return system.IsArray(actual, out int actualKey, out var actualElement) && key == actualKey &&
                       Match(system, element, actualElement, types);

            if (system.IsConstructed(pattern, out var definition, out var arguments))
            {
                if (!system.IsConstructed(actual, out var actualDefinition, out var actualArguments) ||
                    !system.Same(definition, actualDefinition))
                    return false;
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (!Match(system, arguments[i], actualArguments[i], types))
                        return false;
                }

                return true;
            }

            return system.Same(pattern, actual);
        }

        private static bool SameDefinition<TType, TSystem>(TSystem system, TType type, TType definition)
            where TType : class where TSystem : INinoTypeSystem<TType> =>
            system.Same(system.IsConstructed(type, out var d, out _) ? d : type, definition);

        private static bool SameBindings<TType, TSystem>(TSystem system, TType[] a, TType[] b)
            where TType : class where TSystem : INinoTypeSystem<TType>
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == null ? b[i] != null : b[i] == null || !system.Same(a[i], b[i]))
                    return false;
            }

            return true;
        }
    }
}
