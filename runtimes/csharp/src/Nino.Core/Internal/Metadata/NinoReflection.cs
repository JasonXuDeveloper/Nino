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
        private static readonly HashSet<Type> ValueTuples = new()
        {
            typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
            typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>),
            typeof(ValueTuple<,,,,,,,>)
        };

        /// <summary>
        /// The type of a struct's field as its layout sees it: a function pointer (<c>delegate*</c>) as
        /// <see cref="UIntPtr"/>, a pointer zero-extended to 8 bytes on the wire like <c>T*</c> (spec §5.2). Which
        /// fields are function pointers comes from generated code (<see cref="NinoFunctionPointerFields"/>), never from
        /// reflection: Mono, IL2CPP and .NET Framework report such a field as an <see cref="IntPtr"/>, .NET 8 and later
        /// as a function pointer type, and neither ResolveSignature nor IsFunctionPointer is there on every runtime.
        /// Only a field that can be one is looked up: an <see cref="IntPtr"/>, or a class without a base type that is
        /// not <see cref="object"/>, an interface or a pointer (what .NET 8 and later report).
        /// </summary>
        public static Type LayoutFieldType(System.Reflection.FieldInfo field)
        {
            var type = field.FieldType;
            return (type == typeof(IntPtr) || type.BaseType == null && !type.IsValueType && !type.IsInterface &&
                       !type.IsPointer && type != typeof(object)) && NinoFunctionPointerFields.Holds(field)
                ? typeof(UIntPtr)
                : type;
        }

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
                    if (attribute.InheritedBySubtypes)
                        return true;
                    break;
                }
            }

            foreach (var i in type.GetInterfaces())
            {
                if (Declared(i) is { InheritedBySubtypes: true })
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
        /// a <c>[NinoType]</c>, or a field (at any depth) of a struct that is not one. <c>Vector&lt;T&gt;</c> is 16, 32
        /// or 64 bytes by the CPU (16 on Arm64, Mono, IL2CPP and x64 without AVX2, 32 with AVX2, 64 with
        /// <c>DOTNET_MaxVectorTBitWidth=512</c>), so no layout of it reads on every host, and Nino serializes no type
        /// that holds one (docs/spec/wire-format.md §3). <c>[NinoType]</c>s are left to their generated formatters,
        /// which skip such members (NINO029) and resolve the formatters of their type arguments, refused there, when
        /// first used. The generator's rule is <c>RawRules.HostSizedVectorIn</c>.
        /// </summary>
        public static Type HostSizedVectorIn(Type type) => HostSizedVectorIn(type, null);

        /// <summary>
        /// Throws <see cref="NotSupportedException"/> when <paramref name="type"/> is or holds <c>Vector&lt;T&gt;</c>
        /// (<see cref="HostSizedVectorIn(Type)"/>): called where a formatter is first resolved, and by the converted paths
        /// of <see cref="NinoLayout{T}"/> for such a type only (code that skipped the resolver); out of line.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RefuseHostSizedVector(Type type)
        {
            if (HostSizedVectorIn(type) is { } vector)
                NinoThrow.HostSizedVector(type, vector);
        }

        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = VectorJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = VectorJustification)]
        private static Type HostSizedVectorIn(Type type, HashSet<Type> seen)
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
                    if (HostSizedVectorIn(argument, seen ??= new HashSet<Type> { type }) is { } found)
                        return found;
                }
            }

            // What a collection formatter writes of a type that names no vector itself: its base type's
            // (class Lanes : List<Vector<int>>) and the elements of the IEnumerable<T> it implements.
            if (!type.IsValueType && type.BaseType is { } baseType && baseType != typeof(object) &&
                HostSizedVectorIn(baseType, seen ??= new HashSet<Type> { type }) is { } inBase)
                return inBase;
            foreach (var face in type.GetInterfaces())
            {
                if (face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                    HostSizedVectorIn(face.GetGenericArguments()[0], seen ??= new HashSet<Type> { type }) is { } inElements)
                    return inElements;
            }

            if (!type.IsValueType || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return null;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic))
            {
                if (HostSizedVectorIn(field.FieldType, seen ??= new HashSet<Type> { type }) is { } found)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// Whether this is IL2CPP: Mono's class libraries (their description is not ".NET ...") without dynamic code.
        /// </summary>
        internal static readonly bool IsIl2Cpp =
            !System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET",
                StringComparison.Ordinal) && !RuntimeFeature.IsDynamicCodeSupported;

        /// <summary>
        /// The generic struct, <c>T?</c> included, that <paramref name="type"/> is or holds (in its elements, type
        /// arguments or fields) whose fields IL2CPP places in two ways, or null. IL2CPP computes a generic instance's
        /// field offsets at run time (libil2cpp's <c>FieldLayout::LayoutFields</c>), which reflection and fully
        /// shared generic code use, while the code compiled for the type uses the C++ compiler's struct; the two differ
        /// where a field's struct is one whose <c>Pack</c> lowers the alignment of a struct field it holds, or a
        /// generic struct with <c>Pack</c> (unity.yml run 44102730242: <c>Around&lt;PackedIntDateTime4&gt;</c>'s field
        /// at 8 compiled and 4 by reflection; a <c>PackedPair1&lt;long&gt;?</c> read with its value at 8 of 10
        /// bytes, which wrote past values), and where the generic struct itself has a <c>Pack</c> below its fields'
        /// alignment (Unity 6000.6 took <c>PackedPair1&lt;long&gt;</c>'s 9 bytes as 16 in arrays and lists). A formatter Nino builds for such a type at run time could only read and
        /// write the wrong bytes: refused (<see cref="RefuseIl2CppSplitLayout"/>). Formatters the generator
        /// registered are compiled for their types, and lay them out from the offsets it registered
        /// (<see cref="CompilerServices.NinoLayoutOffsets"/>): those are right.
        /// </summary>
        public static Type Il2CppSplitLayoutIn(Type type) => IsIl2Cpp ? SplitLayoutIn(type, null) : null;

        /// <summary>
        /// Throws <see cref="NotSupportedException"/> on IL2CPP for <see cref="Il2CppSplitLayoutIn"/>'s types: called
        /// where a formatter is built at run time; out of line.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RefuseIl2CppSplitLayout(Type type)
        {
            if (Il2CppSplitLayoutIn(type) is { } generic)
                NinoThrow.Il2CppSplitLayout(type, generic);
        }

        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = VectorJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = VectorJustification)]
        internal static Type SplitLayoutIn(Type type, HashSet<Type> seen)
        {
            while (type.HasElementType)
            {
                if (type.IsPointer || type.IsByRef)
                    return null;
                type = type.GetElementType()!;
            }

            if (type.IsGenericParameter || type.IsPrimitive || type.IsEnum || type == typeof(string) || IsNinoType(type))
                return null;
            if (seen != null && !seen.Add(type))
                return null;
            // A generic struct with Pack is split itself: on Unity 6000.6 shared code took PackedPair1<long> (Pack 1,
            // 9 bytes) as 16, and wrote its arrays and lists with 16-byte elements (run 44108765619).
            if (IsSplitGeneric(type))
                return type;
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    if (SplitLayoutIn(argument, seen ??= new HashSet<Type> { type }) is { } found)
                        return found;
                }
            }

            if (!type.IsValueType)
                return null;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic))
            {
                if (SplitLayoutIn(field.FieldType, seen ??= new HashSet<Type> { type }) is { } found)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// Whether <paramref name="type"/> is a closed generic struct (<c>T?</c> included) whose fields IL2CPP places in
        /// two ways (<see cref="Il2CppSplitLayoutIn"/>): its <c>Pack</c> is below its fields' alignment, or it holds a
        /// struct whose <c>Pack</c> moves a struct field. The generator names the same structs (SplitLayouts) and
        /// registers their compiled offsets (<see cref="CompilerServices.NinoLayoutOffsets"/>).
        /// </summary>
        internal static bool IsSplitGeneric(Type type) =>
            type.IsValueType && type.IsGenericType && !type.ContainsGenericParameters &&
            (UnderPacked(type) || HoldsUnderPacked(type));

        /// <summary>
        /// Whether a field of the struct <paramref name="type"/>, at any depth of the structs it holds, is a struct
        /// that <see cref="UnderPacked"/>.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = VectorJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = VectorJustification)]
        private static bool HoldsUnderPacked(Type type, int depth = 0)
        {
            if (depth > 32)
                return false;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic))
            {
                var fieldType = field.FieldType;
                if (!IsPlainStruct(field, fieldType))
                    continue;
                if (UnderPacked(fieldType) || HoldsUnderPacked(fieldType, depth + 1))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="type"/>'s <c>Pack</c> lowers the alignment of a struct field it holds, or it is a
        /// generic struct with a <c>Pack</c> below its fields' alignment.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = VectorJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = VectorJustification)]
        private static bool UnderPacked(Type type)
        {
            var layout = type.StructLayoutAttribute;
            if (layout == null || layout.Value != System.Runtime.InteropServices.LayoutKind.Sequential ||
                layout.Pack <= 0)
                return false;
            if (type.IsGenericType && layout.Pack < AlignOf(type, 0))
                return true;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic))
            {
                if (IsPlainStruct(field, field.FieldType) && layout.Pack < AlignOf(field.FieldType, 0))
                    return true;
            }

            return false;
        }

        /// <summary>A struct field other than a primitive, an enum, a pointer or a fixed buffer.</summary>
        private static bool IsPlainStruct(System.Reflection.FieldInfo field, Type type) =>
            type.IsValueType && !type.IsPrimitive && !type.IsEnum &&
            !field.IsDefined(typeof(FixedBufferAttribute), false);

        /// <summary>The largest primitive a value of <paramref name="type"/> holds (its natural alignment, at most 8).</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = VectorJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = VectorJustification)]
        private static int AlignOf(Type type, int depth)
        {
            if (type.IsEnum)
                type = Enum.GetUnderlyingType(type);
            if (type.IsPointer)
                return IntPtr.Size;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean:
                case TypeCode.Byte:
                case TypeCode.SByte:
                    return 1;
                case TypeCode.Char:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                    return 2;
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Single:
                    return 4;
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Double:
                    return 8;
            }

            if (type.IsPrimitive)
                return IntPtr.Size;
            if (!type.IsValueType || depth > 32)
                return IntPtr.Size;
            int align = 1;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic))
                align = Math.Max(align, AlignOf(field.FieldType, depth + 1));
            return align;
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
