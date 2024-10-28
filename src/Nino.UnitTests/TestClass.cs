using System;
using System.Collections.Generic;
using Nino.Core;

namespace Nino.UnitTests
{
    [NinoType]
    public abstract class Base
    {
        public int A;
    }

    [NinoType]
    public class Sub1 : Base
    {
        public int B;
    }

    public class Nested
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
    public class Sub3 : Nested.Sub2Impl
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
    public class TestClass3 : TestClass2
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

    public struct TestStruct3
    {
        public byte A;
        public float B;
    }

    [NinoType]
    public class SimpleClass
    {
        public int Id;
        public string Name;
        public DateTime CreateTime;
    }

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
        [NinoMember(3)] int Id,
        [NinoMember(2)] string Name,
        [NinoMember(1)] DateTime CreateTime)
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
    public class SimpleClassWithConstructor
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
}