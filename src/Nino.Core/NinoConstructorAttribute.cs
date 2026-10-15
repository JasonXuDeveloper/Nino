using System;

namespace Nino.Core
{
    /// <summary>
    /// Mark a constructor to be used when deserializing
    /// <see href="https://nino.xgamedev.net/en/doc/advanced#custom-constructors"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
    public sealed class NinoConstructorAttribute : Attribute
    {
        /// <summary>The serialized members passed as the arguments, in order (empty: matched by parameter name).</summary>
        public string[] Parameters { get; }

        public NinoConstructorAttribute(params string[] parameters)
        {
            Parameters = parameters;
        }
    }
}