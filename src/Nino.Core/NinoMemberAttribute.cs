using System;

namespace Nino.Core
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
    public class NinoMemberAttribute : Attribute
    {
        public ushort Index;

        public NinoMemberAttribute(ushort index)
        {
            Index = index;
        }
    }
}