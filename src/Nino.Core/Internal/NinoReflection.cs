using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// What Nino.Core reads about a runtime type by reflection, with the generator's rules: whether it is a
    /// <c>[NinoType]</c>, tracks references, or is written as its raw bytes. One place for each rule, so the paths that
    /// ask (the resolver, polymorphic registration, the nesting check, NativeAOT's formatters) cannot drift apart.
    /// </summary>
    internal static class NinoReflection
    {
        /// <summary>Why reading the interfaces of a runtime type is safe after trimming.</summary>
        internal const string InterfacesJustification =
            "Looks for interfaces of a type the program uses (it holds a value of it or names it). The trimmer keeps " +
            "an interface implementation on every instantiated type whose interface is kept; the interfaces that " +
            "matter here are kept: a [NinoType] interface by its generated formatter, the collection interfaces " +
            "that formatter constraints name by Nino.Core's formatters.";

        // Every ValueTuple definition (8 = with a nested TRest): tuples have their own format, never raw.
        private static readonly HashSet<Type> ValueTuples = new HashSet<Type>
        {
            typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
            typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>),
            typeof(ValueTuple<,,,,,,,>)
        };

        /// <summary>The <c>[NinoType]</c> declared on <paramref name="type"/> itself, or null.</summary>
        private static NinoTypeAttribute Declared(Type type) =>
            (NinoTypeAttribute)Attribute.GetCustomAttribute(type, typeof(NinoTypeAttribute), false);

        /// <summary>
        /// Whether <paramref name="type"/> is a <c>[NinoType]</c>, directly or through the nearest attributed base type
        /// or an attributed interface that allows inheritance (as in the generator).
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = InterfacesJustification)]
        public static bool IsNinoType(Type type)
        {
            if (Declared(type) != null)
                return true;
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (Declared(t) is { } attribute)
                {
                    if (attribute.AllowInheritance)
                        return true;
                    break;
                }
            }

            foreach (var i in type.GetInterfaces())
            {
                if (Declared(i) is { AllowInheritance: true })
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the type, a base class or an interface is marked <c>trackReferences</c> (as in the generator: an
        /// attribute counts whether or not it allows inheritance).
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = InterfacesJustification)]
        public static bool IsTracked(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (Declared(t) is { TrackReferences: true })
                    return true;
            }

            foreach (var i in type.GetInterfaces())
            {
                if (Declared(i) is { TrackReferences: true })
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether values of <paramref name="type"/> are written as their raw bytes, as the generator decides it
        /// (RawRules.IsRawUnmanaged): a formatter parameter marked <see cref="NinoRawAttribute"/> binds the same types
        /// here as in generated code, so both write the same bytes (a <c>T?[]</c> of a raw <c>[NinoType]</c> struct at
        /// its fixed stride, for one).
        /// </summary>
        public static bool IsRawUnmanaged(Type type)
        {
            if (type.IsPointer || type.IsGenericParameter || !NinoUnsafe.IsUnmanaged(type))
                return false;
            return IsNinoType(type) ? IsRawStruct(type) : IsRawShape(type);
        }

        /// <summary>
        /// <see cref="IsRawUnmanaged"/> of an unmanaged type that is not a <c>[NinoType]</c>, and false for a
        /// <c>[NinoType]</c>: the rule NativeAOT applies too (NinoFormatterCache's CreateWithoutDynamicCode), where an
        /// unmanaged type that generated code did not register can only be raw. Pointer-sized values are written as 8
        /// bytes, and Nullable, KeyValuePair, the value tuples and DateTimeOffset have formats of their own.
        /// </summary>
        public static bool IsRawShape(Type type)
        {
            if (type == typeof(IntPtr) || type == typeof(UIntPtr) || type == typeof(DateTimeOffset) || IsNinoType(type))
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

        /// <summary>
        /// Whether a <c>[NinoType]</c> struct is written as its raw memory: its generated formatter says so
        /// (<see cref="INinoRawFormatter"/>, which the generator declares for the structs it copies as one block).
        /// </summary>
        private static bool IsRawStruct(Type type)
        {
            if (!type.IsValueType)
                return false;
            NinoRegistry.RegisterType(type);
            var formatter = NinoFormatterCache.Get(type)?.StructType;
            return formatter != null && typeof(INinoRawFormatter).IsAssignableFrom(formatter);
        }
    }
}
