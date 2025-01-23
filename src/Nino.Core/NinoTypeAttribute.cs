using System;

namespace Nino.Core
{
    /// <summary>
    /// Define a custom type to be serialized or deserialized by Nino
    /// <see href="https://nino.xgamedev.net/en/doc/types#custom-types"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
                    AttributeTargets.Interface
        , Inherited = false)]
    public class NinoTypeAttribute : Attribute
    {
        public NinoTypeAttribute(bool autoCollect = true, bool containNonPublicMembers = false)
        {
        }
    }
}