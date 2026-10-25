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

    /// <summary>A body that deserializes without allocating, so the tests see only what the integration allocates.</summary>
    [NinoType]
    public struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>A list whose factory returns null: reading it fails on the server's side, whatever the data.</summary>
    public class NullFactoryList : List<int>
    {
        [NinoFactory]
        public static NullFactoryList Create() => null;
    }

    /// <summary>A body that is valid data but that the server cannot build (its list's factory returns null).</summary>
    [NinoType]
    public sealed class Broken
    {
        public NullFactoryList Items;
    }
}
