using System;

namespace Nino.Core
{
    /// <summary>
    /// Specify the order of a field or property when serializing or deserializing
    /// <remarks>This requires the type is annotated using <c>[NinoType(false)]</c></remarks>
    /// <see href="https://nino.xgamedev.net/en/doc/types#custom-types"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
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