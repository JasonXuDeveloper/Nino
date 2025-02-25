using System;

namespace Nino.Core
{
    /// <summary>
    /// This attribute is used to mark the former name of a type.
    /// <see href="https://nino.xgamedev.net/en/doc/advanced#former-name"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
                    AttributeTargets.Interface
        , Inherited = false)]
    public class NinoFormerNameAttribute : Attribute
    {
        public NinoFormerNameAttribute(string formerName)
        {
        }
    }
}