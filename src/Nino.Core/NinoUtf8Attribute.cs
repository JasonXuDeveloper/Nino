using System;

namespace Nino.Core
{
    /// <summary>
    /// Specify that the string should be encoded in UTF-8.
    /// <see href="https://nino.xgamedev.net/en/doc/advanced#string-encoding"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
    public class NinoUtf8Attribute : Attribute
    {
    }
}