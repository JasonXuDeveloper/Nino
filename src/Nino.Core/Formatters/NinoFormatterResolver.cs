using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Builds struct formatter types for types without a registered formatter, by reflection, on first use of a
    /// type. Applies the <see cref="NinoFormatterAttribute"/> registrations with the same rules as the generator
    /// (strings, <c>object</c>, raw unmanaged values and <c>[NinoType]</c>s are built in), so both write the same
    /// bytes. Ahead-of-time platforms (IL2CPP) need the generated registrations for value-type instantiations; the
    /// generator emits them for every type it sees used.
    /// </summary>
    internal static class NinoFormatterResolver
    {
        private static readonly ConcurrentDictionary<Type, Type> Cache = new ConcurrentDictionary<Type, Type>();
        private static readonly object Lock = new object();
        private static readonly HashSet<Assembly> Scanned = new HashSet<Assembly>();
        private static Rules _rules = Rules.Empty.With(typeof(NinoFormatterResolver).Assembly);

        // Every ValueTuple definition (8 = with a nested TRest): tuples have their own format, never raw.
        private static readonly HashSet<Type> ValueTuples = new HashSet<Type>
        {
            typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
            typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>),
            typeof(ValueTuple<,,,,,,,>)
        };

        /// <summary>Adds the formatter registrations of <paramref name="assembly"/> (once).</summary>
        internal static void AddAssembly(Assembly assembly)
        {
            lock (Lock)
            {
                if (Scanned.Contains(assembly))
                    return;
                Scanned.Add(assembly);
                _rules = _rules.With(assembly);
            }
        }

        /// <summary>
        /// Whether <paramref name="type"/> is a <c>[NinoType]</c>, directly or through the nearest attributed base type
        /// or an attributed interface that allows inheritance (as in the generator).
        /// </summary>
        internal static bool IsNinoType(Type type)
        {
            if (Attribute(type) != null)
                return true;
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (Attribute(t) is { } attribute)
                {
                    if (attribute.AllowInheritance)
                        return true;
                    break;
                }
            }

            foreach (var i in type.GetInterfaces())
            {
                if (Attribute(i) is { AllowInheritance: true })
                    return true;
            }

            return false;
        }

        private static NinoTypeAttribute Attribute(Type type) =>
            (NinoTypeAttribute)System.Attribute.GetCustomAttribute(type, typeof(NinoTypeAttribute), false);

        /// <summary>
        /// Returns a struct type implementing <c>INinoFormatter&lt;type&gt;</c>, or null if Nino cannot format the
        /// type.
        /// </summary>
        internal static Type GetFormatterType(Type type, bool topLevel = false)
        {
            if (!topLevel)
                return Cache.GetOrAdd(type, t => Build(t, false));
            return Build(type, true);
        }

        private static Type ElementFormatter(Type type)
        {
            NinoRegistry.RegisterType(type);
            if (NinoFormatterCache.IsRegistered(type) || IsNinoType(type))
                return typeof(CachedFormatter<>).MakeGenericType(type);
            return GetFormatterType(type) ?? typeof(CachedFormatter<>).MakeGenericType(type);
        }

        /// <summary>Whether values of <paramref name="type"/> are written as their raw bytes.</summary>
        internal static bool IsRawUnmanaged(Type type)
        {
            if (type.IsPointer || type.IsGenericParameter || !NinoUnsafe.IsUnmanaged(type) || IsNinoType(type) ||
                type == typeof(IntPtr) || type == typeof(UIntPtr) || type == typeof(DateTimeOffset))
                return false;
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(Nullable<>) || definition == typeof(KeyValuePair<,>) ||
                    ValueTuples.Contains(definition))
                    return false;
            }

            return true;
        }

        private static Type Build(Type type, bool topLevel)
        {
            var result = TryBuild(type, topLevel);
            if (result != null)
                return result;
            // The registration may live in an assembly that has not been looked at yet.
            var before = _rules;
            NinoRegistry.EnsureAllInitialized();
            return ReferenceEquals(before, _rules) ? null : TryBuild(type, topLevel);
        }

        private static Type TryBuild(Type type, bool topLevel)
        {
            if (type == typeof(string))
                return typeof(StringFormatter);
            if (type == typeof(object))
                return typeof(ObjectFormatter);
            if (type.IsPointer || type.IsByRef || typeof(Delegate).IsAssignableFrom(type))
                return null;

            var rules = _rules;
            if (type.IsArray)
                return FirstMatch(rules.ForArray(ArrayKey(type)), type);

            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            var registered = FirstMatch(rules.ForDefinition(definition), type);
            if (registered != null)
                return registered;

            if (IsRawUnmanaged(type))
                return typeof(UnmanagedFormatter<>).MakeGenericType(type);
            if (IsNinoType(type))
                return null; // use the registered formatter through CachedFormatter<T>
            return FirstMatch(rules.Open, type);
        }

        private static int ArrayKey(Type array)
        {
            int rank = array.GetArrayRank();
            return rank == 1 && array == array.GetElementType()!.MakeArrayType() ? 1 : -rank;
        }

        private static Type FirstMatch(List<Rule> rules, Type type)
        {
            if (rules == null)
                return null;
            foreach (var rule in rules)
            {
                var formatter = rule.Bind(type);
                if (formatter != null)
                    return formatter;
            }

            return null;
        }

        /// <summary>One <see cref="NinoFormatterAttribute"/> registration.</summary>
        private sealed class Rule
        {
            private readonly Type _formatter;
            private readonly Type[] _parameters;
            public readonly Type Pattern;
            public readonly Type[] Targets;
            public readonly int Priority;
            public readonly string Name;

            private Rule(Type formatter, Type pattern, Type[] targets, int priority)
            {
                _formatter = formatter;
                _parameters = formatter.IsGenericTypeDefinition ? formatter.GetGenericArguments() : Type.EmptyTypes;
                Pattern = pattern;
                Targets = targets;
                Priority = priority;
                Name = formatter.FullName ?? formatter.Name;
            }

            public static Rule Read(NinoFormatterAttribute attribute)
            {
                var formatter = attribute.FormatterType;
                if (formatter == null || !formatter.IsValueType ||
                    (formatter.IsGenericType && !formatter.IsGenericTypeDefinition))
                    return null;
                Type pattern = null;
                foreach (var i in formatter.GetInterfaces())
                {
                    if (!i.IsGenericType || i.GetGenericTypeDefinition() != typeof(INinoFormatter<>))
                        continue;
                    if (pattern != null)
                        return null; // ambiguous; the analyzer reports it
                    pattern = i.GetGenericArguments()[0];
                }

                if (pattern == null)
                    return null;
                var targets = new Type[attribute.Targets.Length];
                for (int i = 0; i < targets.Length; i++)
                {
                    var target = attribute.Targets[i];
                    targets[i] = target.IsGenericType ? target.GetGenericTypeDefinition() : target;
                }

                return new Rule(formatter, pattern, targets, attribute.Priority);
            }

            /// <summary>The closed formatter type for <paramref name="target"/>, or null if the rule does not apply.</summary>
            public Type Bind(Type target)
            {
                if (!NinoFormatterBinding.Bind<Type, Type, TypeSystem, Parameters>(default, new Parameters(_parameters),
                        Pattern, target, out var types, out var formatters))
                    return null;
                if (_parameters.Length == 0)
                    return _formatter;
                for (int i = 0; i < types.Length; i++)
                    types[i] ??= formatters[i];
                try
                {
                    return _formatter.MakeGenericType(types);
                }
                catch (ArgumentException)
                {
                    return null; // a constraint the rules above do not model
                }
            }

            /// <summary>The formatter's type parameters, for <see cref="NinoFormatterBinding"/>.</summary>
            private readonly struct Parameters : INinoFormatterParameters<Type, Type>
            {
                private readonly Type[] _parameters;

                public Parameters(Type[] parameters) => _parameters = parameters;

                public int Count => _parameters.Length;

                public IEnumerable<Type> Constraints(int index) => _parameters[index].GetGenericParameterConstraints();

                public Type FormatterElement(int index)
                {
                    foreach (var constraint in _parameters[index].GetGenericParameterConstraints())
                    {
                        if (constraint.IsGenericType && constraint.GetGenericTypeDefinition() == typeof(INinoFormatter<>))
                            return constraint.GetGenericArguments()[0];
                    }

                    return null;
                }

                public bool ResolveElement(Type element, out Type formatter)
                {
                    formatter = ElementFormatter(element);
                    return true;
                }

                public bool Satisfies(int index, Type bound, Type[] types) => Rule.Satisfies(_parameters[index], bound, types);
            }

            internal static bool Satisfies(Type parameter, Type type, Type[] args)
            {
                var attributes = parameter.GenericParameterAttributes;
                if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && type.IsValueType)
                    return false;
                if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 &&
                    (!type.IsValueType || Nullable.GetUnderlyingType(type) != null))
                    return false;
                // `unmanaged` is C#'s; [NinoRaw] means "copied as raw memory", which is stricter.
                if (IsUnmanagedConstrained(parameter) && (!type.IsValueType || !NinoUnsafe.IsUnmanaged(type)))
                    return false;
                if (parameter.IsDefined(typeof(NinoRawAttribute), false) && !IsRawUnmanaged(type))
                    return false;
                if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                    !type.IsValueType && (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null))
                    return false;
                // [NinoCreated]: created through NinoFactory<T>, by a factory or constructor callable without arguments.
                if (parameter.IsDefined(typeof(NinoCreatedAttribute), false) && !NinoFactory.IsCreatable(type))
                    return false;
                foreach (var constraint in parameter.GetGenericParameterConstraints())
                {
                    if (constraint == typeof(ValueType))
                        continue;
                    if (!NinoFormatterBinding.UnifyWithSupertype<Type, TypeSystem>(default, constraint, type, args))
                        return false;
                }

                return true;
            }

            private static bool IsUnmanagedConstrained(Type parameter)
            {
                foreach (var data in parameter.CustomAttributes)
                {
                    if (data.AttributeType.FullName == "System.Runtime.CompilerServices.IsUnmanagedAttribute")
                        return true;
                }

                return false;
            }
        }

        /// <summary>The registrations seen so far, indexed like the generator's; replaced as a whole when one is added.</summary>
        private sealed class Rules
        {
            public static readonly Rules Empty = new Rules(new List<Rule>());

            private readonly List<Rule> _all;
            private readonly Dictionary<Type, List<Rule>> _byDefinition = new Dictionary<Type, List<Rule>>();
            private readonly Dictionary<int, List<Rule>> _byArrayRank = new Dictionary<int, List<Rule>>();
            public readonly List<Rule> Open = new List<Rule>();

            private Rules(List<Rule> all)
            {
                _all = all;
                foreach (var rule in all)
                {
                    if (rule.Pattern.IsGenericParameter)
                    {
                        if (rule.Targets.Length == 0)
                            Open.Add(rule);
                        foreach (var target in rule.Targets)
                            Key(_byDefinition, target).Add(rule);
                    }
                    else if (rule.Pattern.IsArray)
                    {
                        Key(_byArrayRank, ArrayKey(rule.Pattern)).Add(rule);
                    }
                    else
                    {
                        Key(_byDefinition,
                            rule.Pattern.IsGenericType ? rule.Pattern.GetGenericTypeDefinition() : rule.Pattern).Add(rule);
                    }
                }

                foreach (var list in _byDefinition.Values)
                    list.Sort(Compare);
                foreach (var list in _byArrayRank.Values)
                    list.Sort(Compare);
                Open.Sort(Compare);
            }

            public List<Rule> ForDefinition(Type definition) =>
                _byDefinition.TryGetValue(definition, out var list) ? list : null;

            public List<Rule> ForArray(int key) => _byArrayRank.TryGetValue(key, out var list) ? list : null;

            public Rules With(Assembly assembly)
            {
                NinoFormatterAttribute[] attributes;
                try
                {
                    attributes = (NinoFormatterAttribute[])assembly.GetCustomAttributes(typeof(NinoFormatterAttribute),
                        false);
                }
                catch
                {
                    return this;
                }

                if (attributes.Length == 0)
                    return this;
                var all = new List<Rule>(_all);
                foreach (var attribute in attributes)
                {
                    var rule = Rule.Read(attribute);
                    if (rule != null)
                        all.Add(rule);
                }

                return new Rules(all);
            }

            private static int Compare(Rule a, Rule b) => a.Priority != b.Priority
                ? b.Priority.CompareTo(a.Priority)
                : string.CompareOrdinal(a.Name, b.Name);

            private static List<Rule> Key<TKey>(Dictionary<TKey, List<Rule>> map, TKey key)
            {
                if (!map.TryGetValue(key, out var list))
                    map[key] = list = new List<Rule>();
                return list;
            }
        }

        /// <summary><see cref="Type"/> for <see cref="NinoFormatterBinding"/>.</summary>
        private readonly struct TypeSystem : INinoTypeSystem<Type>
        {
            public bool IsTypeParameter(Type type, out int ordinal)
            {
                bool result = type.IsGenericParameter && type.DeclaringMethod == null;
                ordinal = result ? type.GenericParameterPosition : -1;
                return result;
            }

            public bool IsArray(Type type, out int key, out Type element)
            {
                if (!type.IsArray)
                {
                    key = 0;
                    element = null;
                    return false;
                }

                key = ArrayKey(type);
                element = type.GetElementType();
                return true;
            }

            public bool IsConstructed(Type type, out Type definition, out IReadOnlyList<Type> arguments)
            {
                if (!type.IsGenericType || type.IsGenericTypeDefinition)
                {
                    definition = null;
                    arguments = null;
                    return false;
                }

                definition = type.GetGenericTypeDefinition();
                arguments = type.GetGenericArguments();
                return true;
            }

            public bool IsInterface(Type type) => type.IsInterface;

            public bool IsPointerLike(Type type) => type.IsPointer || type.IsByRef;

            public bool Same(Type a, Type b) => a == b;

            public Type Bound(Type type) => type;

            public Type BaseType(Type type) => type.BaseType;

            public IEnumerable<Type> Interfaces(Type type) => type.GetInterfaces();

            public Type MakeArray(Type element, int key) =>
                key == 1 ? element.MakeArrayType() : element.MakeArrayType(-key);

            public Type Construct(Type definition, Type[] arguments)
            {
                try
                {
                    return definition.MakeGenericType(arguments);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
        }
    }
}
