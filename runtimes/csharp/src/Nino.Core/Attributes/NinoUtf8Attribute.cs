using System;

namespace Nino.Core
{
    /// <summary>
    /// Specify that the string should be encoded in UTF-8.
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#string-encoding"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class NinoUtf8Attribute : Attribute
    {
    }
}