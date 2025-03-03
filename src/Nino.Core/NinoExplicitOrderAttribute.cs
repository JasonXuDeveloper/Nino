using System;

namespace Nino.Core
{
    public class NinoExplicitOrderAttribute : Attribute
    {
        public NinoExplicitOrderAttribute(params string[] order)
        {
        }
    }
}