using System;

namespace Nino.Core
{
    /// <summary>
    /// Marks a type to be serialized by Nino. The source generator emits its formatter.
    /// <see href="https://nino.xgamedev.net/en/doc/types#custom-types"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
    public class NinoTypeAttribute : Attribute
    {
        /// <param name="autoCollect">Serialize every public field and property (otherwise only <c>[NinoMember]</c>s).</param>
        /// <param name="containNonPublicMembers">With <paramref name="autoCollect"/>, also serialize non-public members.</param>
        /// <param name="allowInheritance">Whether subtypes are serializable through this type.</param>
        /// <param name="versionTolerant">
        /// Prefix the body with its byte length, so members can be appended later without breaking existing data.
        /// Defining <c>WEAK_VERSION_TOLERANCE</c> in the project turns this on for every type.
        /// </param>
        public NinoTypeAttribute(bool autoCollect = true, bool containNonPublicMembers = false,
            bool allowInheritance = true, bool versionTolerant = false)
        {
        }
    }
}
