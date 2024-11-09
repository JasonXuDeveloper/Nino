using System;

namespace Nino.Core
{
    /// <summary>
    /// Mark a constructor to be used when deserializing
    /// <see href="https://nino.xgamedev.net/en/doc/advanced#custom-constructors"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor)]
    public class NinoConstructorAttribute : Attribute
    {
        public string[] Parameters;

        public NinoConstructorAttribute(params string[] parameters)
        {
            Parameters = parameters;
        }
    }
}