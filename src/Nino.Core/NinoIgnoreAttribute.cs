using System;

namespace Nino.Core
{
    /// <summary>
    /// Mark a field or property to be ignored when serializing or deserializing
    /// <remarks>This requires the type is annotated using <c>[NinoType(false)]</c></remarks>
    /// <see href="https://nino.xgamedev.net/en/doc/types#custom-types"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class NinoIgnoreAttribute : Attribute
    {
    }
}