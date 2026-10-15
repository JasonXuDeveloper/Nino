// Linked into Nino.Generator (as Nino.Generator.Generation.NinoWire), so the constants generated code relies on cannot
// drift from the runtime's.
#if NINO_GENERATOR
namespace Nino.Generator.Generation
#else
namespace Nino.Core
#endif
{
    /// <summary>
    /// Constants of the Nino wire format. Every multi-byte value is little-endian and unaligned; there is no
    /// header, so the declared type determines how the bytes are read.
    /// <list type="bullet">
    /// <item>Strings, arrays and collections: an <c>int32</c> element count (<see cref="NullLength"/> for null),
    /// then the elements.</item>
    /// <item>Sealed classes, <c>Tuple</c>s and nullable values: a presence byte (<see cref="Null"/> or
    /// <see cref="NotNull"/>), then the value if present.</item>
    /// <item>Non-sealed classes, interfaces and <c>object</c>: an <c>int32</c> type id (<see cref="NullTypeId"/>
    /// for null, see <c>NinoTypeId</c>), then the body.</item>
    /// <item>Types marked <c>trackReferences</c>: an <c>int32</c> reference header in front of the above
    /// (<see cref="NullReference"/>, <see cref="NewReference"/>, or the 1-based index of an object already
    /// written).</item>
    /// <item>Keyed members: an <c>int32</c> key (<see cref="KeySize"/>), an <c>int32</c> length, then the value.</item>
    /// </list>
    /// </summary>
    internal static class NinoWire
    {
        /// <summary>Size of a length (or count) prefix: an <c>int32</c>.</summary>
        public const int LengthSize = sizeof(int);

        /// <summary>Size of a keyed member's key: an <c>int32</c>.</summary>
        public const int KeySize = sizeof(int);

        /// <summary>Length written for a null string, array or collection.</summary>
        public const int NullLength = -1;

        /// <summary>Presence byte of a null sealed class, <c>Tuple</c> or nullable value.</summary>
        public const byte Null = 0;

        /// <summary>Presence byte of a present sealed class, <c>Tuple</c> or nullable value.</summary>
        public const byte NotNull = 1;

        /// <summary>Type id written for a null non-sealed class, interface or <c>object</c>; never a real id.</summary>
        public const int NullTypeId = 0;

        /// <summary>Reference header of a null tracked object.</summary>
        public const int NullReference = 0;

        /// <summary>Reference header of a tracked object written for the first time; its body follows.</summary>
        public const int NewReference = -1;

        /// <summary><c>DateTimeOffset</c>: <c>int64</c> UTC ticks, then <c>int16</c> offset minutes.</summary>
        public const int DateTimeOffsetSize = sizeof(long) + sizeof(short);

        /// <summary>Position of the offset minutes inside a <c>DateTimeOffset</c>.</summary>
        public const int DateTimeOffsetMinutesPosition = sizeof(long);
    }
}
