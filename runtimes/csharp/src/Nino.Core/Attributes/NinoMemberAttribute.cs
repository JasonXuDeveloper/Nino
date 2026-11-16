using System;

namespace Nino.Core
{
    /// <summary>
    /// Specify the order of a field or property when serializing or deserializing
    /// <remarks>Only has an effect with <c>[NinoType(false)]</c> (<c>autoCollect: false</c>), where it selects the
    /// member; with the default <c>[NinoType]</c> every public member is collected in declaration order.</remarks>
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#choosing-members-explicitly"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class NinoMemberAttribute : Attribute
    {
        /// <summary>Position of the member in the serialized data.</summary>
        public ushort Index { get; }

        public NinoMemberAttribute(ushort index)
        {
            Index = index;
        }
    }
}