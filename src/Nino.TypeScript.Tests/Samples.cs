using System;
using System.Collections.Generic;
using Nino.Core;

namespace Samples
{
    public enum Rank : byte { None, Bronze, Silver, Gold }

    [Flags]
    public enum Access { Read = 1, Write = 2, Admin = 4 }

    public enum Big : long { Small = 1, Huge = 1L << 40 }

    [NinoType]
    public struct Vec2
    {
        public float X;
        public float Y;
    }

    public readonly record struct PlayerId(long Value);

    /// <summary>A struct that is not raw (it holds a string): written member by member, without a header.</summary>
    [NinoType]
    public struct Tag
    {
        public string Name;
        public int Weight;
    }

    [NinoType]
    public sealed class Player
    {
        public PlayerId Id;
        public string Name;
        [NinoUtf8] public string Motto;
        public Rank Rank;
        public Access Access;
        public Big Big;
        public bool Online;
        public char Initial;
        public double Score;
        public decimal Balance;
        public Guid Session;
        public DateTime Joined;
        public TimeSpan Played;
        public List<Vec2> Path;
        public int[] Levels;
        public byte[] Avatar;
        public Dictionary<string, int[]> Inventory;
        public HashSet<int> Friends;
        public int? Maybe;
        public long?[] Gaps;
        public (int, string) Pair;
        public Tuple<int> Boxed;
        public KeyValuePair<string, Vec2> Spawn;
        public Tag Tag;
        public Animal Pet;
        public List<Animal> Zoo;
        public IShape Shape;
        public object Anything;
        public Saved Saved;
        public Tolerant Tolerant;
    }

    [NinoType]
    public abstract class Animal
    {
        public string Name;
    }

    [NinoType]
    public class Dog : Animal
    {
        public int Bones;
    }

    [NinoType]
    public sealed class Puppy : Dog
    {
        public bool Sleepy;
    }

    [NinoType]
    public sealed class Cat : Animal
    {
        public float Lives;
    }

    [NinoType]
    public interface IShape
    {
        double Area { get; set; }
    }

    [NinoType]
    public sealed class Square : IShape
    {
        public double Area { get; set; }
        public double Side;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Saved
    {
        [NinoFormerName("Hp")] public int Health;
        public string Note;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class Tolerant
    {
        public int A;
        public string B;
    }

    /// <summary>Left out of TypeScript (reference tracking), with a warning.</summary>
    [NinoType(trackReferences: true)]
    public sealed class Node
    {
        public Node Next;
    }
}
