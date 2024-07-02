using System;
using System.Collections.Generic;
using Nino.Core;

namespace Nino.UnitTests
{
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
}