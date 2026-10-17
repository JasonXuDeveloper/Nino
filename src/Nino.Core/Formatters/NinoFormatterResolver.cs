using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Builds struct formatter types for types without a registered formatter, by reflection, on first use of a
    /// type. Applies the <see cref="NinoFormatterAttribute"/> registrations with the same rules as the generator
    /// (strings, <c>object</c>, raw unmanaged values and <c>[NinoType]</c>s are built in), so both write the same
    /// bytes. Ahead-of-time platforms (IL2CPP) need the generated registrations for value-type instantiations; the
    /// generator emits them for every type it sees used.
    /// <para>
    /// Trimming and NativeAOT: everything here closes generic formatter definitions over runtime types, so it is
    /// <see cref="RequiresDynamicCodeAttribute"/>: where the runtime cannot compile code (NativeAOT, iOS), the callers
    /// (<see cref="NinoFormatterCache{T}"/>, <see cref="NinoPolymorphic.Register(Type)"/>, <see cref="BoxedFormatter"/>)
    /// check <c>RuntimeFeature.IsDynamicCodeSupported</c> and use only the generated registrations, so NativeAOT drops
    /// this class. A trimmed app that runs on a JIT keeps it; what the trimmer cannot see is justified at each
    /// suppression below: the formatter definitions come from <see cref="NinoFormatterAttribute"/> (annotated), the
    /// type arguments are types the program uses, and Nino's formatter type parameters ask the trimmer for nothing.
    /// </para>
    /// </summary>
    internal static class NinoFormatterResolver
    {
        internal const string DynamicCodeMessage =
            "Builds formatter types at run time (MakeGenericType); only for types without a generated registration.";
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
#if NET8_0_OR_GREATER
            // Without dynamic code the registrations are never bound: skip reading them (and this class).
            if (!RuntimeFeature.IsDynamicCodeSupported)
                return;
#endif
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
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = InterfacesJustification)]
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

        /// <summary>Why reading the interfaces of a runtime type is safe after trimming.</summary>
        internal const string InterfacesJustification =
            "Looks for interfaces of a type the program uses (it holds a value of it or names it). The trimmer keeps " +
            "an interface implementation on every instantiated type whose interface is kept; the interfaces that " +
            "matter here are kept: a [NinoType] interface by its generated formatter, the collection interfaces " +
            "that formatter constraints name by Nino.Core's formatters.";

        /// <summary>Why the struct and unmanaged constraints of Nino's own generic types are met after trimming.</summary>
        internal const string ValueTypeConstraintJustification =
            "The struct (or unmanaged) constraint makes the trimmer ask for the public parameterless constructor of " +
            "the type argument, a value type here. A value type is always creatable without one, and Nino only uses " +
            "these arguments as default(T) and as unmanaged values, never through a constructor.";

        private static NinoTypeAttribute Attribute(Type type) =>
            (NinoTypeAttribute)System.Attribute.GetCustomAttribute(type, typeof(NinoTypeAttribute), false);

        /// <summary>
        /// Returns a struct type implementing <c>INinoFormatter&lt;type&gt;</c>, or null if Nino cannot format the
        /// type.
        /// </summary>
        [RequiresDynamicCode(DynamicCodeMessage)]
        internal static Type GetFormatterType(Type type, bool topLevel = false)
        {
            if (!topLevel)
                return Cache.GetOrAdd(type, t => Build(t, false));
            return Build(type, true);
        }

        [RequiresDynamicCode(DynamicCodeMessage)]
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

        [RequiresDynamicCode(DynamicCodeMessage)]
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

        [RequiresDynamicCode(DynamicCodeMessage)]
        // IL2071; IL2070 from the .NET 8 analyzer, which reports the same thing on MakeGenericType itself.
        [UnconditionalSuppressMessage("Trimming", "IL2071", Justification = ValueTypeConstraintJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = ValueTypeConstraintJustification)]
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
#if NET8_0_OR_GREATER
            return array.IsSZArray ? 1 : -rank;
#else
            return rank == 1 && array == array.GetElementType()!.MakeArrayType() ? 1 : -rank;
#endif
        }

        [RequiresDynamicCode(DynamicCodeMessage)]
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
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
            private readonly Type _formatter;
            private readonly Type[] _parameters;
            public readonly Type Pattern;
            public readonly Type[] Targets;
            public readonly int Priority;
            public readonly string Name;

            private Rule([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type formatter,
                Type pattern, Type[] targets, int priority)
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
            [RequiresDynamicCode(DynamicCodeMessage)]
            [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = BindJustification)]
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

            internal const string BindJustification =
                "Closes a formatter definition registered with [assembly: NinoFormatter] (kept by the attribute) over " +
                "types the program uses and formatter types built the same way. The trimmer can only ask for what " +
                "the definition's type parameters declare: Nino's formatters declare no DynamicallyAccessedMembers, " +
                "their struct constraints are met by every value type (" + ValueTypeConstraintJustification + "), " +
                "and Satisfies checks a new() constraint (the constructor itself) before binding. A constraint that " +
                "still fails throws ArgumentException, caught below: the rule then does not apply.";

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
#if NET8_0_OR_GREATER
                    // Only reached from Bind (RequiresDynamicCode); the check states it for the analyzer.
                    if (!RuntimeFeature.IsDynamicCodeSupported)
                    {
                        formatter = null;
                        return false;
                    }
#endif
                    formatter = ElementFormatter(element);
                    return true;
                }

                public bool Satisfies(int index, Type bound, Type[] types) => Rule.Satisfies(_parameters[index], bound, types);
            }

            [UnconditionalSuppressMessage("Trimming", "IL2070", Justification =
                "Checks a new() constraint against a type the program uses. A parameterless constructor the trimmer " +
                "removed (nothing creates the type) reads as missing, so the formatter is not bound, which is what " +
                "MakeGenericType would decide; nothing is created through the constructor here.")]
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
                // [NinoCreated]: created through NinoActivator<T>, by a factory or constructor callable without arguments.
                if (parameter.IsDefined(typeof(NinoCreatedAttribute), false) && !NinoActivator.IsCreatable(type))
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

            [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = InterfacesJustification)]
            public IEnumerable<Type> Interfaces(Type type) => type.GetInterfaces();

            // MakeArray and Construct are only reached from Rule.Bind (RequiresDynamicCode); the checks state it for
            // the analyzer.
            public Type MakeArray(Type element, int key)
            {
#if NET8_0_OR_GREATER
                if (!RuntimeFeature.IsDynamicCodeSupported)
                    return null;
#endif
                return key == 1 ? element.MakeArrayType() : element.MakeArrayType(-key);
            }

            [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = Rule.BindJustification)]
            public Type Construct(Type definition, Type[] arguments)
            {
#if NET8_0_OR_GREATER
                if (!RuntimeFeature.IsDynamicCodeSupported)
                    return null;
#endif
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
