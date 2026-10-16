using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Throw helpers. Kept out of line so callers stay small enough to inline.
    /// </summary>
    public static class NinoThrow
    {
#if NET8_0_OR_GREATER
        // Not NoInlining on CoreCLR: the JIT then looks at the body, sees that it only throws, does not inline it
        // and knows the call does not return. A NoInlining call is assumed to return: its caller keeps every live
        // value across it (spilling some on the hot path) and jumps back after it.
        private const MethodImplOptions Cold = 0;
#else
        private const MethodImplOptions Cold = MethodImplOptions.NoInlining;
#endif

        [MethodImpl(Cold)]
        public static void BigEndianHost() =>
            throw new PlatformNotSupportedException(
                "Nino: big-endian hosts are not supported. Nino writes little-endian data, and every platform .NET and Unity run on today (x86, x64, ARM, WebAssembly) is little-endian.");

        [MethodImpl(Cold)]
        public static void EndOfData() =>
            throw new InvalidOperationException("Nino: unexpected end of data.");

        [MethodImpl(Cold)]
        public static void InvalidLength(int length) =>
            throw new InvalidOperationException($"Nino: invalid length {length}.");

        [MethodImpl(Cold)]
        public static void InvalidData(string message) =>
            throw new InvalidOperationException("Nino: " + message);

        [MethodImpl(Cold)]
        public static void UnknownTypeId(Type declaredType, int id) =>
            throw new InvalidOperationException(
                $"Nino: type id {id} is not a registered subtype of {declaredType.FullName}.");

        [MethodImpl(Cold)]
        public static void UnregisteredType(Type declaredType, Type runtimeType) =>
            throw new InvalidOperationException(
                $"Nino: {runtimeType?.FullName} is not a registered subtype of {declaredType.FullName}. " +
                "Mark it with [NinoType].");

        [MethodImpl(Cold)]
        public static void NoFormatter(Type type) =>
            throw new InvalidOperationException(
                $"Nino: no formatter for {type.FullName}. Mark it with [NinoType], declare a struct formatter with " +
                "[assembly: NinoFormatter] or register one with NinoFormatterCache<T>.Register<TFormatter>().");

        [MethodImpl(Cold)]
        public static void FactoryReturnedNull(Type type) =>
            throw new InvalidOperationException($"Nino: the factory of {type.FullName} returned null.");

        [MethodImpl(Cold)]
        public static void CannotCreate(Type type, string reason) =>
            throw new InvalidOperationException($"Nino: cannot create an instance of {type.FullName}: {reason}.");

        [MethodImpl(Cold)]
        public static void ReadOnlyCollection(Type type) =>
            throw new InvalidOperationException(
                $"Nino: {type.FullName} is read-only (IsReadOnly is true), so it cannot be deserialized into.");

        [MethodImpl(Cold)]
        public static void DuplicateKey(Type type) =>
            throw new InvalidOperationException(
                $"Nino: the data holds the same key (or set element) twice for {type.FullName}, under its comparer.");

        [MethodImpl(Cold)]
        public static void InvalidReference(int index) =>
            throw new InvalidOperationException($"Nino: invalid object reference {index}.");

        [MethodImpl(Cold)]
        public static void IncompleteReference(int index) =>
            throw new InvalidOperationException(
                $"Nino: object reference {index} points at an object that is still being created. Cycles must " +
                "go through types marked [NinoType(trackReferences: true)].");

        [MethodImpl(Cold)]
        public static void NotASubtype(Type declaredType, Type actualType) =>
            throw new InvalidOperationException(
                $"Nino: the data holds a {actualType.FullName} where a {declaredType.FullName} is expected.");

        [MethodImpl(Cold)]
        public static void ReferenceTypeMismatch(Type expected, Type actual) =>
            throw new InvalidOperationException(
                $"Nino: object reference to a {actual?.FullName} where a {expected.FullName} is expected.");

        [MethodImpl(Cold)]
        public static void Cycle(Type type) =>
            throw new InvalidOperationException(
                $"Nino: the object graph has a cycle through a {type.FullName}, which would be written forever. " +
                "Mark the types on the cycle [NinoType(trackReferences: true)] to write each object once.");

        [MethodImpl(Cold)]
        public static void TooDeep(Type type) =>
            throw new InvalidOperationException(
                $"Nino: {type.FullName} is nested more than {NinoDepth.Max} objects deep. Objects deeper than " +
                $"{NinoDepth.Inline} are written later, from a shallow point, except those built by their constructor " +
                "or compared by value (records, types overriding GetHashCode or implementing IComparable), which " +
                "must be complete when their slot gets them. If the graph has a cycle, mark its types " +
                "[NinoType(trackReferences: true)].");
    }
}
