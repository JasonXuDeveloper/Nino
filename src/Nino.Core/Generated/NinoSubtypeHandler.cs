using System;

namespace Nino.Core.Generated
{
    /// <summary>
    /// Writes and reads one registered type. Also the registry entry: the registry stores handler references, so
    /// a reader always sees a complete (type, id, code) triple. One handler serves every base type of its type.
    /// Only Nino.Core creates handlers (from generated registrations): a read trusts that a handler's type is what it
    /// reads.
    /// </summary>
    public abstract class NinoSubtypeHandler
    {
        /// <summary>The exact runtime type this handler writes and reads.</summary>
        public Type Type { get; }

        /// <summary>The type id written before the body.</summary>
        public int Id { get; }

        private protected NinoSubtypeHandler(Type type, int id)
        {
            Type = type;
            Id = id;
        }

        /// <summary>
        /// Writes the type id and the body of <paramref name="value"/> (an instance of exactly <see cref="Type"/>).
        /// For a type marked <c>trackReferences</c>, its reference header comes between the two, since the declared
        /// type (<c>object</c>, or a base class or interface that is not tracked) wrote none.
        /// </summary>
        public abstract void Serialize(object value, ref Writer writer);

        /// <summary>
        /// Reads what <see cref="Serialize"/> wrote after the type id (which has been consumed) into
        /// <paramref name="value"/>, which is null or an instance of exactly <see cref="Type"/> to reuse.
        /// </summary>
        public abstract void Deserialize(ref object value, ref Reader reader);

        /// <summary>
        /// Writes the type id and the body without a reference header: called through a tracked declared type,
        /// whose formatter wrote the header before the type id.
        /// </summary>
        internal virtual void SerializeReferenced(object value, ref Writer writer) => Serialize(value, ref writer);

        /// <summary>Reads what <see cref="SerializeReferenced"/> wrote after the type id.</summary>
        internal virtual void DeserializeReferenced(ref object value, ref Reader reader) =>
            Deserialize(ref value, ref reader);
    }
}
