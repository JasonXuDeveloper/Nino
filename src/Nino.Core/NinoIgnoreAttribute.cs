using System;

namespace Nino.Core
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class NinoIgnoreAttribute : Attribute
    {
    }
}