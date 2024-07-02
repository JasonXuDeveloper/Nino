using System;

namespace Nino.Core
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class NinoMemberAttribute : Attribute
    {
        public ushort Index;

        public NinoMemberAttribute(ushort index)
        {
            Index = index;
        }
    }
}