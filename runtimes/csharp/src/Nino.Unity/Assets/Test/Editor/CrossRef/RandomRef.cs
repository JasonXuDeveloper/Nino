using System.Collections.Generic;
using Nino.Core;
using Test.Editor.Model;

namespace Test.Editor.CrossRef
{
    [NinoType]
    public class RandomRef
    {
        public int Id;
    }

    public class Redundant
    {
        public IList<RandomData> SomeData;
    }
}