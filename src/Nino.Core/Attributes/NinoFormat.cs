// Linked into Nino.Generator (as the internal Nino.Generator.Common.NinoFormat), which reads [NinoType(format: ...)]
// as these values: the two cannot drift apart.
#if NINO_GENERATOR
namespace Nino.Generator.Common
#else
namespace Nino.Core
#endif
{
    /// <summary>
    /// How a <c>[NinoType]</c> writes its members, and so which changes to the type existing data survives.
    /// <see href="https://nino.xgamedev.net/en/docs/v4/evolving-data"/>
    /// </summary>
#if NINO_GENERATOR
    internal enum NinoFormat
#else
    public enum NinoFormat
#endif
    {
        /// <summary>
        /// The members, one after another, with nothing around them: the smallest and fastest format. Changing the
        /// members (adding, removing, reordering, changing a type) breaks existing data. Defining
        /// <c>WEAK_VERSION_TOLERANCE</c> in the project makes the positional types of its assembly
        /// <see cref="VersionTolerant"/>.
        /// </summary>
        Positional = 0,

        /// <summary>
        /// The body (the members of the whole hierarchy, root first) is prefixed with its byte length, so members can
        /// be appended to this type later without breaking existing data. Members appended to a base class shift the
        /// derived members and are not tolerated.
        /// </summary>
        VersionTolerant = 1,

        /// <summary>
        /// Each member is written with a key (the hash of its name) and its length, so members can be removed,
        /// reordered or inserted without breaking existing data. Unknown members are skipped and missing ones keep
        /// their current value. Costs 8 bytes per member and a key dispatch per member when reading.
        /// </summary>
        Keyed = 2,
    }
}
