using System;

namespace Nino.Core
{
    /// <summary>
    /// Marks a type to be serialized by Nino. The source generator emits its formatter.
    /// <see href="https://nino.xgamedev.net/en/doc/types#custom-types"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
    public sealed class NinoTypeAttribute : Attribute
    {
        /// <param name="autoCollect">Serialize every public field and property (otherwise only <c>[NinoMember]</c>s).</param>
        /// <param name="containNonPublicMembers">With <paramref name="autoCollect"/>, also serialize non-public members.</param>
        /// <param name="allowInheritance">Whether subtypes are serializable through this type.</param>
        /// <param name="versionTolerant">
        /// Prefix the body with its byte length, so members can be appended later without breaking existing data.
        /// Defining <c>WEAK_VERSION_TOLERANCE</c> in the project turns this on for every type.
        /// </param>
        /// <param name="keyed">
        /// Write each member with a key (the hash of its name) and its length, so members can be removed,
        /// reordered or inserted without breaking existing data. Unknown members are skipped and missing ones keep
        /// their current value. Costs 8 bytes per member and a key dispatch per member when reading.
        /// </param>
        /// <param name="trackReferences">
        /// Classes only. An object referenced several times in one call is written once and read back as one
        /// instance, so shared references and cycles round-trip. Costs 4 bytes and a hash lookup per object.
        /// Types that must be re-created from constructor arguments cannot be tracked.
        /// </param>
        public NinoTypeAttribute(bool autoCollect = true, bool containNonPublicMembers = false,
            bool allowInheritance = true, bool versionTolerant = false, bool keyed = false,
            bool trackReferences = false)
        {
        }
    }
}
