using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

        /// <summary>Why reading the fields and interfaces of a runtime type is safe after trimming.</summary>
        private const string VectorJustification =
            "Looks for System.Numerics.Vector<T> in the instance fields of a struct the program uses (the trimmer " +
            "keeps every instance field of a sequential or explicit struct) and in the IEnumerable<T> a collection " +
            "implements. Where that metadata is missing (a trimmed interface, a struct generated code never named " +
            "under NativeAOT), the type is not refused here: a collection's elements are refused when their formatter " +
            "is resolved, and a struct's layout is copied as it lies in memory, as on any reflection failure of " +
            "NinoLayout.";

        /// <summary>
        /// The <c>System.Numerics.Vector&lt;T&gt;</c> that <paramref name="type"/> is or holds, or null: as an array
        /// element, a type argument, a base type or the element of an <c>IEnumerable&lt;T&gt;</c> of a type that is not
        /// a <c>[NinoType]</c>, or a field (at any depth) of a struct that is not one. <c>Vector&lt;T&gt;</c> is as wide
        /// as the CPU's vectors (16 bytes on Arm64, Mono, IL2CPP and x64 without AVX2, 32 with AVX2, 64 with
        /// <c>DOTNET_MaxVectorTBitWidth=512</c>), so no layout of it reads on every host, and Nino serializes no type
        /// that holds one (docs/spec/wire-format.md §3). <c>[NinoType]</c>s are left to their generated formatters,
        /// which skip such members (NINO029) and resolve the formatters of their type arguments, refused there, when
        /// first used. The generator's rule is <c>RawRules.VariableVectorIn</c>.
        /// </summary>
        public static Type VariableVectorIn(Type type) => VariableVectorIn(type, null);

        /// <summary>
        /// Throws <see cref="NotSupportedException"/> when <paramref name="type"/> is or holds <c>Vector&lt;T&gt;</c>
        /// (<see cref="VariableVectorIn(Type)"/>): called where a formatter is first resolved, and by the converted paths
        /// of <see cref="NinoLayout{T}"/> for such a type only (code that skipped the resolver); out of line.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RefuseVariableVector(Type type)
        {
            if (VariableVectorIn(type) is { } vector)
                NinoThrow.VariableVector(type, vector);
        }

        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = VectorJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = VectorJustification)]
        private static Type VariableVectorIn(Type type, HashSet<Type> seen)
        {
            while (type.HasElementType)
            {
                if (type.IsPointer || type.IsByRef)
                    return null;
                type = type.GetElementType()!;
            }

            if (type.IsGenericParameter)
                return null;
            // By name: Unity's Mono and IL2CPP have a Vector<T> in mscorlib and another in System.Numerics.Vectors.
            if (type.IsGenericType && type.Namespace == "System.Numerics" && type.Name == "Vector`1")
                return type;
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || IsNinoType(type))
                return null;
            if (seen != null && !seen.Add(type))
                return null;
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    if (VariableVectorIn(argument, seen ??= new HashSet<Type> { type }) is { } found)
                        return found;
                }
            }

            // What a collection formatter writes of a type that names no vector itself: its base type's
            // (class Lanes : List<Vector<int>>) and the elements of the IEnumerable<T> it implements.
            if (!type.IsValueType && type.BaseType is { } baseType && baseType != typeof(object) &&
                VariableVectorIn(baseType, seen ??= new HashSet<Type> { type }) is { } inBase)
                return inBase;
            foreach (var face in type.GetInterfaces())
            {
                if (face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                    VariableVectorIn(face.GetGenericArguments()[0], seen ??= new HashSet<Type> { type }) is { } inElements)
                    return inElements;
            }

            if (!type.IsValueType || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return null;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic))
            {
                if (VariableVectorIn(field.FieldType, seen ??= new HashSet<Type> { type }) is { } found)
                    return found;
            }

            return null;
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
