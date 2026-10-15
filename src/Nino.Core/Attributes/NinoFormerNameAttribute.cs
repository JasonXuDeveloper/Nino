using System;

namespace Nino.Core
{
    /// <summary>
    /// The type's former name (runtime form, e.g. <c>Old.Namespace.Outer+Type</c>). The type id is computed
    /// from this name, so data written before the type was renamed or moved can still be read.
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#former-name"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
    public sealed class NinoFormerNameAttribute : Attribute
    {
        public string FormerName { get; }

        public NinoFormerNameAttribute(string formerName)
        {
            FormerName = formerName;
        }
    }
}
