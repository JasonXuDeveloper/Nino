using System;

namespace Nino.Core
{
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