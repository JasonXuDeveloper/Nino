using System.ComponentModel;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// How a generated registry registers a concrete <c>[NinoType]</c>: its formatter for by-type lookups
    /// (<see cref="NinoFormatterCache{T}"/>, unless one is already registered), then its handler for writes and reads by
    /// type id (<see cref="NinoSubtypes{TBase}"/> of <see cref="object"/>, idempotent; every base type finds it there).
    /// The id is the wire id, also under the case of a <c>[NinoFormerName]</c>'s name id. Abstract types and interfaces
    /// have no handler: their registries call <see cref="NinoFormatterCache{T}.TryRegister{TFormatter}"/>. One call
    /// does both, so the registry names the type and its formatter once. Each method builds the formatter's wrapper and
    /// the handler itself, calling only methods that are not generic over the formatter: for a class, the code shared
    /// over its type then looks up no more than the two calls it replaces did, and a registration compiles one method
    /// per type instead of two (a call to a generic helper costs a lookup and a compile per type at startup).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoTypes
    {
        /// <summary>Registers <typeparamref name="T"/> with its generated formatter under type id <paramref name="id"/>.</summary>
        public static void Register<T, TFormatter>(int id) where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (!NinoFormatterCache<T>.IsRegistered)
                NinoFormatterCache<T>.TryRegister(new StructFormatter<T, TFormatter>());
            NinoSubtypes<object>.Register(new SubtypeHandler<T, TFormatter>(id));
        }

        /// <summary>
        /// <see cref="Register{T,TFormatter}"/> for a formatter of fixed size, which the regular byte array call uses
        /// to write into an array of that size.
        /// </summary>
        public static void RegisterFixed<T, TFormatter>(int id)
            where TFormatter : struct, INinoBodyFormatter<T>, INinoFixedSizeFormatter<T>
        {
            if (!NinoFormatterCache<T>.IsRegistered)
                NinoFormatterCache<T>.TryRegister(new FixedStructFormatter<T, TFormatter>());
            NinoSubtypes<object>.Register(new SubtypeHandler<T, TFormatter>(id));
        }

        /// <summary>
        /// <see cref="Register{T,TFormatter}"/> for a formatter with a <c>__Top</c> twin, which the regular top-level
        /// calls write through (see <see cref="INinoTopFormatter{T}"/>).
        /// </summary>
        public static void RegisterTop<T, TFormatter, TTop>(int id)
            where TFormatter : struct, INinoBodyFormatter<T>
            where TTop : struct, INinoTopFormatter<T>
        {
            if (!NinoFormatterCache<T>.IsRegistered)
                NinoFormatterCache<T>.TryRegister(new TopStructFormatter<T, TFormatter, TTop>());
            NinoSubtypes<object>.Register(new SubtypeHandler<T, TFormatter>(id));
        }

        /// <summary>
        /// <see cref="Register{T,TFormatter}"/> for a class marked <c>trackReferences</c> (or deriving from one):
        /// written through a declared type that is not tracked, its reference header follows the type id.
        /// </summary>
        public static void RegisterTracked<T, TFormatter>(int id)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (!NinoFormatterCache<T>.IsRegistered)
                NinoFormatterCache<T>.TryRegister(new StructFormatter<T, TFormatter>());
            NinoSubtypes<object>.Register(new TrackedSubtypeHandler<T, TFormatter>(id));
        }
    }
}
