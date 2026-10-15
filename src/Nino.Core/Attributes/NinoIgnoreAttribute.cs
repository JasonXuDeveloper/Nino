using System;

namespace Nino.Core
{
    /// <summary>
    /// Mark a field or property to be ignored when serializing or deserializing
    /// <remarks>Only has an effect when the type collects its members automatically (<c>[NinoType]</c>, the
    /// default); with <c>[NinoType(false)]</c> unmarked members are already skipped.</remarks>
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#ignoring-a-member"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class NinoIgnoreAttribute : Attribute
    {
    }
}