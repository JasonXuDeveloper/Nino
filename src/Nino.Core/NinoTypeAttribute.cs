using System;

namespace Nino.Core
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
                    AttributeTargets.Interface
        , Inherited = false)]
    public class NinoTypeAttribute : Attribute
    {
        public bool AutoCollect;

        public NinoTypeAttribute(bool autoCollect = true)
        {
            AutoCollect = autoCollect;
        }
    }
}