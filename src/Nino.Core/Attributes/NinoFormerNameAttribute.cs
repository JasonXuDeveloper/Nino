using System;

namespace Nino.Core
{
    /// <summary>
    /// A former name of the type or member this is applied to.
    /// <para>On a type: its former name in runtime form (e.g. <c>Old.Namespace.Outer+Type</c>). The type id is computed
    /// from this name, so data written before the type was renamed or moved can still be read. Give it once (a second one is error NINO026).</para>
    /// <para>On a field or property of a keyed type (<c>[NinoType(format: NinoFormat.Keyed)]</c>): a name the member was written
    /// under before it was renamed, once per earlier name. Reading accepts the current name and every former name;
    /// writing always uses the current name. Positional and version-tolerant types write no member names, so there
    /// it has no effect (warning NINO024).</para>
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types#former-name"/>
    /// <see href="https://nino.xgamedev.net/en/docs/v4/evolving-data#renaming-keyed-members"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface |
                    AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
    public sealed class NinoFormerNameAttribute : Attribute
    {
        public string FormerName { get; }

        public NinoFormerNameAttribute(string formerName)
        {
            FormerName = formerName;
        }
    }
}
