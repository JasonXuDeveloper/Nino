using System;

namespace Nino.Core
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public class NinoTypeAttribute : Attribute
    {
        public bool AutoCollect;

        public NinoTypeAttribute(bool autoCollect = true)
        {
            AutoCollect = autoCollect;
        }
    }
}