using System;

namespace Nino.Core
{
    public class NinoPrivateProxyAttribute : Attribute
    {
        public NinoPrivateProxyAttribute(string privateMemberName, bool isProperty)
        {
        }
    }
}