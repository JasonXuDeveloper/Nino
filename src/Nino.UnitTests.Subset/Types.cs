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