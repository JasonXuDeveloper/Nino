using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nino.Core;

#nullable disable

[NinoType(false, true)]
public partial class HierarchicalBase
{
    [NinoMember(0)] protected int A;
    [NinoMember(1)] public string B;
}

[NinoType(false, true)]
public partial class HierarchicalSub1 : HierarchicalBase
{
    [NinoMember(0)] protected bool C;
    [NinoMember(1)] public float D;
}

[NinoType(false, true)]
public sealed partial class HierarchicalSub2 : HierarchicalSub1
{
    [NinoMember(0)] protected bool E;
    [NinoMember(1)] public List<int> F;
}

[NinoType]
public sealed class SomeNestedPrivateEnum
{
    public int Id;

    [NinoIgnore] private PrivateEnum _someEnum = PrivateEnum.B;

    [NinoIgnore]
    public int EnumVal
    {
        get => (int)_someEnum;
        set => _someEnum = (PrivateEnum)value;
    }

    [Flags]
    private enum PrivateEnum
    {
        A = 1,
        B = 2
    }
}

[NinoType]
public sealed class Move
{
    public ushort ClientX;

    public ushort ClientY;

    private Move()
    {
    }

    [NinoConstructor(nameof(ClientX), nameof(ClientY))]
    public static Move Create(ushort clientX, ushort clientY)
    {
        return new Move
        {
            ClientX = clientX,
            ClientY = clientY,
        };
    }
}

[NinoType(false, true)]
public sealed partial class TestMethodCtor
{
    [NinoMember(0)] public int A;
    [NinoMember(1)] private string _b;

    public string B
    {
        get => _b;
        set => _b = value ?? string.Empty;
    }

    [NinoConstructor(nameof(A), nameof(_b))]
    public static TestMethodCtor Create(int a = 0, string b = null)
    {
        Console.WriteLine(111);
        return new TestMethodCtor()
        {
            A = a,
            _b = b ?? string.Empty
        };
    }
}

[NinoType(false, true)]
public partial class TestA<T>
{
    [NinoMember(0)] protected T Value;

    [NinoConstructor(nameof(Value))]
    public TestA(T val = default) => Value = val;

    public static implicit operator T(TestA<T> val) => val.Value;
}

[NinoType(false, true)]
public sealed partial class TestB<T> : TestA<T>
{
    [NinoConstructor(nameof(Value))]
    public TestB(T val = default) : base(val)
    {
    }
}

[NinoType]
public sealed class CursedGeneric<T>
{
    public ConcurrentDictionary<string, T[]> field;
}

[NinoType]
public sealed class PrivateNestedCollection
{
    private struct MyStruct
    {
        public int X;
        public string Y;
    }

    private class MyClass
    {
        public int X;
        public string Y;
    }

    public void Func()
    {
        //ensure no code is generated for this
        List<MyStruct> list = new List<MyStruct>();
        Dictionary<MyStruct, MyStruct> dict = new Dictionary<MyStruct, MyStruct>();
        List<MyClass> list2 = new List<MyClass>();
        Dictionary<MyClass, MyClass> dict2 = new Dictionary<MyClass, MyClass>();
        Dictionary<MyStruct, MyClass> dict3 = new Dictionary<MyStruct, MyClass>();
    }
}

[NinoType]
public interface IListElementClass
{
}

[NinoType]
public sealed class ListElementClass : IListElementClass
{
    public int Id;
    public string Name;
    public DateTime CreateTime;
    public bool Extra;
}

[NinoType]
[NinoFormerName("global::ListElementClass2")]
public sealed class ListElementClass2Renamed : IListElementClass
{
    public int Id;
    public string Name;
    public DateTime CreateTime;
    public string Extra;
}


[NinoType(containNonPublicMembers: true)]
public
#if !NET8_0_OR_GREATER
    partial
#endif
    class ProtectedShouldInclude
{
    protected int _id;

    [NinoIgnore]
    public int Id
    {
        get => _id;
        set => _id = value;
    }
}

[NinoType]
public
#if !NET8_0_OR_GREATER
    partial
#endif
    class ShouldIgnorePrivate
{
    private int _id;

    [NinoIgnore]
    public int Id
    {
        get => _id;
        set => _id = value;
    }

    public string Name;
    public DateTime CreateTime;
}

//We make it no namespace on purpose to see if it still works
[NinoType(false, true)]
public
#if !NET8_0_OR_GREATER
    partial
#endif
    class Bindable<T>
{
    [NinoMember(0)] private T mValue;

    public T Value
    {
        get => mValue;
    }

    [NinoConstructor(nameof(mValue))]
    public Bindable(T value)
    {
        mValue = value;
    }
}

namespace Nino.UnitTests
{
    [NinoType(containNonPublicMembers: true)]
    public
#if !NET8_0_OR_GREATER
        partial
#endif
        class
        TestPrivateMemberClass : Base
    {
        private int Id { get; set; }

        public int ReadonlyId => Id;

        public TestPrivateMemberClass()
        {
#if NET6_0_OR_GREATER
            Id = Random.Shared.Next();
#else
            Id = new Random().Next();
#endif
        }
    }

    [NinoType(containNonPublicMembers: true)]
    public
#if !NET8_0_OR_GREATER
        partial
#endif
        record RecordWithPrivateMember(string Name)
    {
        private int Id { get; set; }

        public int ReadonlyId => Id;
    }

    [NinoType(containNonPublicMembers: true)]
    public
#if !NET8_0_OR_GREATER
        partial
#endif
        record struct RecordWithPrivateMember2(string Name)
    {
        private int Id { get; set; } = 0;

        public int ReadonlyId => Id;
    }

    [NinoType(containNonPublicMembers: true)]
    public
#if !NET8_0_OR_GREATER
        partial
#endif
        struct StructWithPrivateMember
    {
        public int Id;
        private string Name;

        public string GetName() => Name;
        public void SetName(string name) => Name = name;
    }

    [NinoType(containNonPublicMembers: true)]
    public
#if !NET8_0_OR_GREATER
        partial
#endif
        class ClassWithPrivateMember<T>
    {
        public int Id;
        [NinoIgnore] public bool Flag;

        private string _name;
        private List<T> _list;

        private bool _flagProperty
        {
            get => Flag;
            set => Flag = value;
        }

        public string Name => _name;

        [NinoIgnore]
        public List<T> List
        {
            get => _list;
            set => _list = value;
        }

        public ClassWithPrivateMember()
        {
            Id = 0;
            _name = Guid.NewGuid().ToString();
        }
    }

    [NinoType]
    public interface ISerializable
    {
    }

    [NinoType]
    public struct Struct1 : ISerializable
    {
        public int A;
        public DateTime B;
        public Guid C;
    }

    [NinoType]
    public sealed class Class1 : ISerializable
    {
        public int A;
        public DateTime B;
        public Guid C;
        public ISerializable D;
    }

    [NinoType]
    public struct Struct2 : ISerializable
    {
        public int A;
        public DateTime B;
        public string C;
        public Class1 D;
    }

    [NinoType]
    public sealed class StringData
    {
        [NinoUtf8] public string Str;
        [NinoUtf8] public bool ShouldHaveNoEffect;
    }

    [NinoType]
    public sealed class StringData2
    {
        public string Str;
        [NinoUtf8] public bool ShouldHaveNoEffect;
    }

    [NinoType(false)]
    public sealed class SaveData
    {
        [NinoMember(1)] public int Id;
        [NinoMember(2)] [NinoUtf8] public string Name;
        [NinoMember(3)] public DateTime NewField1;
        [NinoMember(4)] public Generic<int> NewField2;
    }

    [NinoType]
    public struct GenericStruct<T>
    {
        public T Val;
    }

    [NinoType]
    public sealed class Generic<T>
    {
        public T Val;
    }

    [NinoType]
    public sealed class ComplexGeneric<T> where T : IList
    {
        public T Val;
    }

    [NinoType]
    public sealed class ComplexGeneric2<T>
    {
        public Generic<T> Val;
    }

    [NinoType]
    public abstract class Base
    {
        public int A;
    }

    [NinoType]
    public sealed class Sub1 : Base
    {
        public int B;
    }

    public sealed class Nested
    {
        [NinoType]
        public abstract class Sub2 : Base
        {
            public int C;
        }

        [NinoType]
        public class Sub2Impl : Sub2
        {
            public int D;
        }
    }

    [NinoType]
    public sealed class Sub3 : Nested.Sub2Impl
    {
        public int E;
    }

    [NinoType(false)]
    public class TestClass
    {
        [NinoMember(1)] public int A;

        [NinoMember(2)] public string B;
    }

    [NinoType(false)]
    public class TestClass2 : TestClass
    {
        [NinoMember(3)] public int C;
    }

    [NinoType]
    public sealed class TestClass3 : TestClass2
    {
        public bool D;
        public TestClass E;
        public TestStruct F;
        public TestStruct? G;
        public IList<TestStruct2> H;
        public List<TestStruct2?> I;
        public TestStruct3[] J;
        public Dictionary<int, int> K;
        public Dictionary<int, TestClass3> L;
        public TestClass3 M;
    }

    [NinoType]
    public struct TestStruct
    {
        public int A;
        public string B;
    }

    [NinoType]
    public struct TestStruct2
    {
        public int A;
        public bool B;
        public TestStruct3 C;
    }

    public struct TestStruct3 : IComparable<TestStruct3>, IEquatable<TestStruct3>
    {
        public byte A;
        public float B;

        public int CompareTo(TestStruct3 other)
        {
            int cmp = A.CompareTo(other.A);
            if (cmp != 0) return cmp;
            return B.CompareTo(other.B);
        }

        public bool Equals(TestStruct3 other)
        {
            return A == other.A && B.Equals(other.B);
        }

        public override bool Equals(object obj)
        {
            return obj is TestStruct3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(A, B);
        }
    }

    [NinoType]
    public sealed class SimpleClass
    {
        public int Id;
        public string Name;
        public DateTime CreateTime;
    }

    [NinoType]
    public record struct SimpleRecordStruct(int Id, string Name, DateTime CreateTime);

    [NinoType]
    public record struct SimpleRecordStruct2(int Id, DateTime CreateTime);

    [NinoType]
    public record struct SimpleRecordStruct2<T>(int Id, T Data);

    [NinoType]
    public record SimpleRecord
    {
        public int Id;
        public string Name;
        public DateTime CreateTime;

        public SimpleRecord()
        {
            Id = 0;
            Name = string.Empty;
            CreateTime = DateTime.MinValue;
        }

        [NinoConstructor(nameof(Id), nameof(Name))]
        public SimpleRecord(int id, string name)
        {
            Id = id;
            Name = name;
            CreateTime = DateTime.Now;
        }
    }

    [NinoType]
    public record SimpleRecord2(int Id, string Name, DateTime CreateTime);

    [NinoType(false)]
    public record SimpleRecord3(
        [property: NinoMember(3)] int Id,
        [property: NinoMember(2)] string Name,
        [property: NinoMember(1)] DateTime CreateTime)
    {
        [NinoMember(4)] public bool Flag;

        public int Ignored;
    }


    [NinoType]
    public record SimpleRecord4(int Id, string Name, DateTime CreateTime)
    {
        [NinoIgnore] public bool Flag;

        public int ShouldNotIgnore;

        // Should not use this
        public SimpleRecord4() : this(0, "", DateTime.MinValue)
        {
        }
    }


    [NinoType]
    public record SimpleRecord5(int Id, string Name, DateTime CreateTime)
    {
        [NinoIgnore] public bool Flag;

        public int ShouldNotIgnore;

        // Not good since we will discard the primary constructor values when deserializing
        [NinoConstructor]
        public SimpleRecord5() : this(0, "", DateTime.MinValue)
        {
        }
    }

    [NinoType]
    public record SimpleRecord6<T>(int Id, T Data);


    [NinoType]
    public struct SimpleStruct
    {
        public int Id;
        public string Name;
        public DateTime CreateTime;

        [NinoConstructor(nameof(Id), nameof(Name), nameof(CreateTime))]
        public SimpleStruct(int a, string b, DateTime c)
        {
            Id = a;
            Name = b;
            CreateTime = c;
        }
    }

    [NinoType]
    public sealed class SimpleClassWithConstructor
    {
        public int Id;
        public string Name;
        public DateTime CreateTime;

        // [NinoConstructor(nameof(Id), nameof(Name), nameof(CreateTime))] - we try not to use this and test if it still works
        // should automatically use this constructor since this is the only public constructor

        public SimpleClassWithConstructor(int id, string name, DateTime createTime)
        {
            Id = id;
            Name = name;
            CreateTime = createTime;
        }
    }

    [NinoType]
    public class UnboundGenericTest<T>
    {
        public T Data;
    }

    [NinoType]
    public class ClassWithUnboundGenericType
    {
        [NinoIgnore]
        public Type UnboundType = typeof(UnboundGenericTest<>);
        public int OtherData = 1;
    }
}
