using System.Collections.Generic;
using Nino.Core;

namespace Nino.AspNetCore.Tests
{
    [NinoType]
    public class Order
    {
        public int Id;
        public string Customer;
        public List<int> Items;
    }

    [NinoType]
    public abstract class Shape
    {
        public string Name;
    }

    [NinoType]
    public class Circle : Shape
    {
        public double Radius;
    }

    [NinoType]
    public class Square : Shape
    {
        public double Side;
    }
}
