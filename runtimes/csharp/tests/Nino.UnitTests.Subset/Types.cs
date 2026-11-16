using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Nino.Core;

namespace Nino.UnitTests.Subset;

[NinoType(false, true)]
public partial class SubsetClassWithPrivateField
{
    [NinoMember(1)] private int _id;

    [NinoMember(0)] public string Name;

    [NinoMember(2)] protected int Age;

    [NinoMember(3)] protected SomeCollections RandomCollections;

    public int Id
    {
        get => _id;
        set => _id = value;
    }
}

[NinoType(false)]
public class SomeCollections
{
    [NinoMember(0)] public string Name;
    public Task[] Tasks;
    public List<ValueTask> ValueTasks1;
    public List<ValueTask<int>> ValueTasks2;
    public List<ValueTask<string>> ValueTasks3;
    public List<UniTask> UniTasks1;
    public List<UniTask<int>> UniTasks2;
    public List<UniTask<string>> UniTasks3;
}
// Unmanaged, every field serialized and the same layout on every runtime: written as raw memory.
[NinoType]
public struct SubsetRawStruct
{
    public int X;
    public int W;
    public long Y;
}

// The long sits at offset 8 on 64-bit but at 4 on x86, so the struct is written member by member.
[NinoType]
public struct SubsetUnportableStruct
{
    public int X;
    public long Y;
}

[NinoType]
public struct SubsetRawOuter
{
    public SubsetRawStruct Inner;
    public long Z { get; set; }
}

// A field is not serialized, so the struct is written member by member.
[NinoType]
public struct SubsetPartialStruct
{
    public int X;
    [NinoIgnore] public int Ignored;
    public long Y;
}

// Version tolerant structs are never raw.
[NinoType(format: NinoFormat.VersionTolerant)]
public struct SubsetTolerantStruct
{
    public int X;
    public long Y;
}

[NinoType]
public struct SubsetPadded
{
    public byte A;
    public int B;
}
