using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Throw helpers. Kept out of line so callers stay small enough to inline.
    /// </summary>
    public static class NinoThrow
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void EndOfData() =>
            throw new InvalidOperationException("Nino: unexpected end of data.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void InvalidLength(int length) =>
            throw new InvalidOperationException($"Nino: invalid length {length}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void InvalidData(string message) =>
            throw new InvalidOperationException("Nino: " + message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void UnknownTypeId(Type declaredType, int id) =>
            throw new InvalidOperationException(
                $"Nino: type id {id} is not a registered subtype of {declaredType.FullName}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void UnregisteredType(Type declaredType, Type runtimeType) =>
            throw new InvalidOperationException(
                $"Nino: {runtimeType?.FullName} is not a registered subtype of {declaredType.FullName}. " +
                "Mark it with [NinoType].");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void NoFormatter(Type type) =>
            throw new InvalidOperationException(
                $"Nino: no formatter for {type.FullName}. Mark it with [NinoType] or register a formatter with " +
                "NinoFormatterCache<T>.Register.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void InvalidReference(int index) =>
            throw new InvalidOperationException($"Nino: invalid object reference {index}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void IncompleteReference(int index) =>
            throw new InvalidOperationException(
                $"Nino: object reference {index} points at an object that is still being created. Cycles must " +
                "go through types marked [NinoType(trackReferences: true)].");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReferenceTypeMismatch(Type expected, Type actual) =>
            throw new InvalidOperationException(
                $"Nino: object reference to a {actual?.FullName} where a {expected.FullName} is expected.");
    }
}
