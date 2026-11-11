using System;
using System.Collections.Generic;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[NinoType(format: NinoFormat.VersionTolerant)]
public struct PaddedItem
{
    public int Id;
    public short Count;
    public bool Bound;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public struct NamedItem
{
    public int Id;
    public string Name;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public struct WideItem
{
    public long A, B, C, D;
    public string Name { get; set; }
    public int E;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class InlineNote
{
    public int Id;
    public string Text;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class TaggedNote : InlineNote
{
    public string Tag;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class NoteBook
{
    public string Title;
    public List<InlineNote> Notes;
}

/// <summary>Keys and items shared by the PriorityQueue and SortedList fast-path tests.</summary>
internal static class CollectionFastPathFixtures
{
    internal enum Int8Key : sbyte { }

    internal readonly record struct ReverseIndex(int Value) : IComparable<ReverseIndex>
    {
        public int CompareTo(ReverseIndex other) => other.Value.CompareTo(Value);
    }
}

