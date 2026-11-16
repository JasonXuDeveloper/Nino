using System;

namespace Nino.Core
{
    /// <summary>
    /// The data being deserialized is not valid Nino data for the type it is read as: it ends early, holds a length,
    /// count, type id, reference or value that is invalid or out of range, a key twice, nests too deep, or holds bytes Nino
    /// never writes (a <c>bool</c> or presence byte other than 0 or 1, a deferred section that does not match its
    /// region). Every check that rejects data throws this type, before anything the data claims is allocated, so a
    /// caller reading untrusted input catches this one exception.
    /// <para>
    /// Derives from <see cref="InvalidOperationException"/>, which Nino threw for invalid data before; mistakes of the
    /// caller (a null argument, a type with no formatter, a read-only collection to read into) keep their own types,
    /// and so does a failure while serializing (an object graph with a cycle, or nested too deep).
    /// </para>
    /// </summary>
    public sealed class NinoDataException : InvalidOperationException
    {
        /// <summary>Creates the exception with the default message.</summary>
        public NinoDataException() : base("Nino: invalid data.")
        {
        }

        /// <summary>Creates the exception with <paramref name="message"/>.</summary>
        public NinoDataException(string message) : base(message)
        {
        }

        /// <summary>Creates the exception with <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
        public NinoDataException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
