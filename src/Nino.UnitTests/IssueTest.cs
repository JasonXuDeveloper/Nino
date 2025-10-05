using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    [TestClass]
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "SpecifyACultureInStringConversionExplicitly")]
    public class IssueTest
    {
        [TestClass]
        public abstract class IssueTestTemplate
        {
            [TestMethod(nameof(Test))]
            [Timeout(1000)]
            public void Test()
            {
                RunTest();
            }

            protected abstract void RunTest();
        }

        [TestClass]
        public class Issue153:IssueTestTemplate
        {
            public class ListComponent<T>: List<T>, IDisposable
            {
                public ListComponent()
                {
                }
        
                public static ListComponent<T> Create()
                {
                    return new();
                }

                public void Dispose()
                {
                    if (Capacity > 64) 
                    {
                        return;
                    }
                    Clear();
                }
            }
            
            protected override void RunTest()
            {
                return;
                var list = ListComponent<int>.Create();
                list.Add(1);
                list.Add(2);
                list.Add(3);
                var bytes = NinoSerializer.Serialize(list);
                ListComponent<int> list2 = NinoDeserializer.Deserialize<ListComponent<int>>(bytes);
                Assert.AreEqual(list.Count, list2.Count);
                Assert.AreEqual(list[0], list2[0]);
                Assert.AreEqual(list[1], list2[1]);
                Assert.AreEqual(list[2], list2[2]);
            }
        }

        [TestClass]
#nullable disable
        public class Issue145 : IssueTestTemplate
        {
            protected override void RunTest()
            {
                TestB<float> testB = new TestB<float>(123.4f);
                var bytes = NinoSerializer.Serialize(testB);
                TestB<float> testB2 = NinoDeserializer.Deserialize<TestB<float>>(bytes);
                Assert.AreEqual(testB.GetType(), testB2.GetType());
                float input = testB;
                float output = testB2;
                Assert.AreEqual(input, output);
            }
        }

        [TestClass]
        public class ListTest : IssueTestTemplate
        {
            [NinoType]
            public class MyTest
            {
                public List<int> List = new List<int>();
            }

            protected override void RunTest()
            {
                var t = new MyTest();
                var bytes = NinoSerializer.Serialize(t);
                MyTest t2 = NinoDeserializer.Deserialize<MyTest>(bytes);
                Assert.AreEqual(t.List.Count, t2.List.Count);
            }
        }

        [TestClass]
        public class IssueValueTupleLayout : IssueTestTemplate
        {
            protected override void RunTest()
            {
                var data = new NinoTuple<byte, int>(4, 1920);
                var bytes = NinoSerializer.Serialize(data);

                var data2 = new NinoTuple<int, byte>(1920, 4);
                var bytes2 = NinoSerializer.Serialize(data2);

                Console.WriteLine(string.Join(",", bytes));
                Console.WriteLine(string.Join(",", bytes2));

                Assert.AreNotEqual(string.Join(",", bytes), string.Join(",", bytes2));
            }
        }

        [TestClass]
        public class Issue144 : IssueTestTemplate
        {
            /// <summary>
            /// Source https://github.com/Unity-Technologies/InputSystem/blob/develop/Packages/com.unity.inputsystem/InputSystem/Utilities/ReadOnlyArray.cs
            /// </summary>
            /// <typeparam name="TValue"></typeparam>
            public struct ReadOnlyArray<TValue> : IReadOnlyList<TValue>
            {
                internal TValue[] m_Array;
                internal int m_StartIndex;
                internal int m_Length;

                public ReadOnlyArray(TValue[] array)
                {
                    m_Array = array;
                    m_StartIndex = 0;
                    m_Length = array?.Length ?? 0;
                }

                public ReadOnlyArray(TValue[] array, int index, int length)
                {
                    m_Array = array;
                    m_StartIndex = index;
                    m_Length = length;
                }

                public TValue[] ToArray()
                {
                    var result = new TValue[m_Length];
                    if (m_Length > 0)
                        Array.Copy(m_Array, m_StartIndex, result, 0, m_Length);
                    return result;
                }

                public int IndexOf(Predicate<TValue> predicate)
                {
                    if (predicate == null)
                        throw new ArgumentNullException(nameof(predicate));

                    for (var i = 0; i < m_Length; ++i)
                        if (predicate(m_Array[m_StartIndex + i]))
                            return i;

                    return -1;
                }

                public Enumerator GetEnumerator()
                {
                    return new Enumerator(m_Array, m_StartIndex, m_Length);
                }

                IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
                {
                    return GetEnumerator();
                }

                IEnumerator IEnumerable.GetEnumerator()
                {
                    return GetEnumerator();
                }

                [SuppressMessage("Microsoft.Usage",
                    "CA2225:OperatorOverloadsHaveNamedAlternates",
                    Justification =
                        "`ToXXX` message only really makes sense as static, which is not recommended for generic types.")]
                public static implicit operator ReadOnlyArray<TValue>(TValue[] array)
                {
                    return new ReadOnlyArray<TValue>(array);
                }

                public int Count => m_Length;

                public TValue this[int index]
                {
                    get
                    {
                        if (index < 0 || index >= m_Length)
                            throw new ArgumentOutOfRangeException(nameof(index));
                        if (m_Array == null)
                            throw new InvalidOperationException();
                        return m_Array[m_StartIndex + index];
                    }
                }

                public struct Enumerator : IEnumerator<TValue>
                {
                    private readonly TValue[] m_Array;
                    private readonly int m_IndexStart;
                    private readonly int m_IndexEnd;
                    private int m_Index;

                    internal Enumerator(TValue[] array, int index, int length)
                    {
                        m_Array = array;
                        m_IndexStart = index - 1; // First call to MoveNext() moves us to first valid index.
                        m_IndexEnd = index + length;
                        m_Index = m_IndexStart;
                    }

                    public void Dispose()
                    {
                    }

                    public bool MoveNext()
                    {
                        if (m_Index < m_IndexEnd)
                            ++m_Index;
                        return m_Index != m_IndexEnd;
                    }

                    public void Reset()
                    {
                        m_Index = m_IndexStart;
                    }

                    public TValue Current
                    {
                        get
                        {
                            if (m_Index == m_IndexEnd)
                                throw new InvalidOperationException("Iterated beyond end");
                            return m_Array[m_Index];
                        }
                    }

                    object IEnumerator.Current => Current;
                }
            }

            protected override void RunTest()
            {
                return;
                var array = new int[] { 1, 2, 3, 4, 5 };
                var readOnlyArray = new ReadOnlyArray<int>(array);
                var bytes = NinoSerializer.Serialize(readOnlyArray);

                Assert.IsTrue(bytes.Length > 0);
                ReadOnlyArray<int> readOnlyArray2 = NinoDeserializer.Deserialize<ReadOnlyArray<int>>(bytes);
                for (var i = 0; i < readOnlyArray.Count; i++)
                {
                    Assert.AreEqual(readOnlyArray[i], readOnlyArray2[i]);
                }
            }
        }

        [TestClass]
        public class Issue141 : IssueTestTemplate
        {
            [NinoType]
            public class TestClass<T>
            {
                public Dictionary<TestEnum, T> Dict = new Dictionary<TestEnum, T>();
            }

            public enum TestEnum
            {
                A,
                B,
                C
            }


            protected override void RunTest()
            {
                var t = new TestClass<int>();
                t.Dict.Add(TestEnum.A, 1);
                t.Dict.Add(TestEnum.B, 2);
                t.Dict.Add(TestEnum.C, 3);

                var bytes = NinoSerializer.Serialize(t);
                TestClass<int> t2 = NinoDeserializer.Deserialize<TestClass<int>>(bytes);

                Assert.AreEqual(t.Dict.Count, t2.Dict.Count);
                Assert.AreEqual(t.Dict[TestEnum.A], t2.Dict[TestEnum.A]);
                Assert.AreEqual(t.Dict[TestEnum.B], t2.Dict[TestEnum.B]);
                Assert.AreEqual(t.Dict[TestEnum.C], t2.Dict[TestEnum.C]);

                var tt = new TestClass<string>();
                tt.Dict.Add(TestEnum.A, "1");
                tt.Dict.Add(TestEnum.B, "2");
                tt.Dict.Add(TestEnum.C, "3");

                bytes = NinoSerializer.Serialize(tt);
                TestClass<string> tt2 = NinoDeserializer.Deserialize<TestClass<string>>(bytes);

                Assert.AreEqual(tt.Dict.Count, tt2.Dict.Count);
                Assert.AreEqual(tt.Dict[TestEnum.A], tt2.Dict[TestEnum.A]);
                Assert.AreEqual(tt.Dict[TestEnum.B], tt2.Dict[TestEnum.B]);
                Assert.AreEqual(tt.Dict[TestEnum.C], tt2.Dict[TestEnum.C]);
            }
        }

        [TestClass]
        public class Issue137 : IssueTestTemplate
        {
            public class MultiMap<T, K> : SortedDictionary<T, List<K>>
            {
                public new List<K> this[T key]
                {
                    get
                    {
                        if (!TryGetValue(key, out List<K> value))
                        {
                            value = new List<K>();
                            Add(key, value);
                        }

                        return value;
                    }

                    set
                    {
                        if (value.Count == 0)
                        {
                            Remove(key);
                        }
                        else
                        {
                            base[key] = value;
                        }
                    }
                }
            }

            protected override void RunTest()
            {
                return;
                MultiMap<long, long> TimeId = new MultiMap<long, long>();
                TimeId[1].Add(1);
                TimeId[1].Add(2);
                TimeId[2].Add(3);

                var bytes = NinoSerializer.Serialize(TimeId);
                MultiMap<long, long> TimeId2 = NinoDeserializer.Deserialize<MultiMap<long, long>>(bytes);

                Assert.AreEqual(TimeId.Count, TimeId2.Count);
                Assert.AreEqual(TimeId[1].Count, TimeId2[1].Count);
                Assert.AreEqual(TimeId[2].Count, TimeId2[2].Count);

                Assert.AreEqual(TimeId[1][0], TimeId2[1][0]);
                Assert.AreEqual(TimeId[1][1], TimeId2[1][1]);
                Assert.AreEqual(TimeId[2][0], TimeId2[2][0]);

                MultiMap<long, string> dict = new MultiMap<long, string>();
                dict[1].Add("1");
                dict[1].Add("2");
                dict[2].Add("3");

                bytes = NinoSerializer.Serialize(dict);
                MultiMap<long, string> dict2 = NinoDeserializer.Deserialize<MultiMap<long, string>>(bytes);

                Assert.AreEqual(dict.Count, dict2.Count);
                Assert.AreEqual(dict[1].Count, dict2[1].Count);
                Assert.AreEqual(dict[2].Count, dict2[2].Count);

                Assert.AreEqual(dict[1][0], dict2[1][0]);
                Assert.AreEqual(dict[1][1], dict2[1][1]);
                Assert.AreEqual(dict[2][0], dict2[2][0]);
            }
        }

        [TestClass]
        public class IssueV3_0 : IssueTestTemplate
        {
            [NinoType]
            public class DiceData
            {
            }

            [NinoType(false)]
            public class DicePool : IEnumerable<DiceData>
            {
                [NinoMember(1)] public DicePoolType Type { get; set; }
                [NinoMember(2)] public List<DiceData> DiceDatas { get; set; } = new List<DiceData>();

                public IEnumerator<DiceData> GetEnumerator()
                {
                    return DiceDatas.GetEnumerator();
                }

                IEnumerator IEnumerable.GetEnumerator()
                {
                    return GetEnumerator();
                }

                public void Add(DiceData diceData)
                {
                    DiceDatas.Add(diceData);
                }

                public void Remove(DiceData diceData)
                {
                    DiceDatas.Remove(diceData);
                }

                public void Clear()
                {
                    DiceDatas.Clear();
                }

                public bool Contains(DiceData diceData)
                {
                    return DiceDatas.Contains(diceData);
                }
            }

            /// <summary>
            /// 骰子池类型
            /// </summary>
            public enum DicePoolType
            {
                /// <summary>
                /// 力量
                /// </summary>
                STR = 1,

                /// <summary>
                /// 体质
                /// </summary>
                CONS = 2,
            }

            protected override void RunTest()
            {
                var pools = new Dictionary<DicePoolType, DicePool>();
                pools.Add(DicePoolType.STR, new DicePool());
                pools.Add(DicePoolType.CONS, new DicePool());
                var bytes = NinoSerializer.Serialize(pools);
                Dictionary<DicePoolType, DicePool> pools2 = NinoDeserializer.Deserialize<Dictionary<DicePoolType, DicePool>>(bytes);
            }
        }

        [TestClass]
        public class Issue134 : IssueTestTemplate
        {
            [NinoType]
            public interface IBase
            {
                int A { get; set; }
            }

            [NinoType]
            public class Impl : IBase
            {
                public int A { get; set; }
            }

            protected override void RunTest()
            {
                var impl = new Impl { A = 10 };
                var bytes = NinoSerializer.Serialize(impl);
                Impl impl2 = NinoDeserializer.Deserialize<Impl>(bytes);
                Assert.AreEqual(impl.A, impl2.A);

                Dictionary<string, IBase> dict = new Dictionary<string, IBase>
                {
                    { "A", new Impl { A = 10 } }
                };
                bytes = NinoSerializer.Serialize(dict);
                Dictionary<string, IBase> dict2 = NinoDeserializer.Deserialize<Dictionary<string, IBase>>(bytes);
                Assert.AreEqual(dict["A"].A, dict2["A"].A);
            }
        }

        [TestClass]
        public class InheritanceTest : IssueTestTemplate
        {
            public class PackageBase
            {
            }

            [Nino.Core.NinoType]
            [Serializable]
            public sealed partial class MyPackPerson : PackageBase
            {
                public int P1 { get; set; }

                public string P2 { get; set; }

                public char P3 { get; set; }

                public double P4 { get; set; }

                public List<int> P5 { get; set; }

                public Dictionary<int, MyClassModel> P6 { get; set; }
            }

            [Nino.Core.NinoType]
            [Serializable]
            public sealed partial class MyClassModel : PackageBase
            {
                public DateTime P1 { get; set; }
            }

            protected override void RunTest()
            {
                MyPackPerson person = new MyPackPerson
                {
                    P1 = 1,
                    P2 = "Hello",
                    P3 = 'A',
                    P4 = 3.14,
                    P5 = new List<int> { 1, 2, 3 },
                    P6 = new Dictionary<int, MyClassModel>
                    {
                        { 1, new MyClassModel { P1 = DateTime.Now } }
                    }
                };
                var bytes = NinoSerializer.Serialize(person);
                MyPackPerson person2 = NinoDeserializer.Deserialize<MyPackPerson>(bytes);
                Assert.AreEqual(person.P1, person2.P1);
                Assert.AreEqual(person.P2, person2.P2);
                Assert.AreEqual(person.P3, person2.P3);
                Assert.AreEqual(person.P4, person2.P4);
                Assert.AreEqual(person.P5.Count, person2.P5.Count);
                Assert.AreEqual(person.P6.Count, person2.P6.Count);
                Assert.AreEqual(person.P6[1].P1, person2.P6[1].P1);
            }
        }


        [TestClass]
        public class IssueIgnore : IssueTestTemplate
        {
            [NinoType]
            public class Data
            {
                public int A;
                public int B;
                public CompA CompA;
            }

            [NinoType(false)]
            public class CompA
            {
                [NinoMember(0)] public int Aa;
                public int Ba;
            }

            protected override void RunTest()
            {
                Data data = new Data();
                data.A = 10;
                data.B = 20;
                data.CompA = new CompA();
                data.CompA.Aa = 30;
                data.CompA.Ba = 40;

                var bufForData = NinoSerializer.Serialize(data);
                Data data2 = NinoDeserializer.Deserialize<Data>(bufForData);

                Assert.IsTrue(data.A == data2.A);
                Assert.IsTrue(data.B == data2.B);
                Assert.IsTrue(data.CompA.Aa == data2.CompA.Aa);
                Assert.IsTrue(data2.CompA.Ba == 0);
            }
        }

        [TestClass]
        public class Issue52 : IssueTestTemplate
        {
            [NinoType]
            public class NinoTestData
            {
                [NinoMember(1)] public int X;
                [NinoMember(2)] public long Y;
            }

            protected override void RunTest()
            {
                var dt = new NinoTestData()
                {
                    X = -136, Y = 8
                };

                var buf = NinoSerializer.Serialize(dt);
                NinoTestData dt2 = NinoDeserializer.Deserialize<NinoTestData>(buf);

                Assert.IsTrue(dt.X == dt2.X);
                Assert.IsTrue(dt.Y == dt2.Y);

                dt = new NinoTestData()
                {
                    X = sbyte.MinValue,
                    Y = short.MinValue
                };

                buf = NinoSerializer.Serialize(dt);
                dt2 = NinoDeserializer.Deserialize<NinoTestData>(buf);

                Assert.IsTrue(dt.X == dt2.X);
                Assert.IsTrue(dt.Y == dt2.Y);

                dt = new NinoTestData()
                {
                    X = int.MinValue,
                    Y = long.MinValue
                };

                buf = NinoSerializer.Serialize(dt);
                dt2 = NinoDeserializer.Deserialize<NinoTestData>(buf);

                Assert.IsTrue(dt.X == dt2.X);
                Assert.IsTrue(dt.Y == dt2.Y);
            }
        }

        [TestClass]
        public class Issue41 : IssueTestTemplate
        {
            [NinoType]
            public class NinoTestData
            {
                public enum Sex
                {
                    Male,
                    Female
                }

                [NinoMember(1)] public string name;
                [NinoMember(2)] public int id;
                [NinoMember(3)] public bool isHasPet;
                [NinoMember(4)] public Sex sex;
            }

            protected override void RunTest()
            {
                var list = new List<NinoTestData>();
                list.Add(new NinoTestData
                {
                    sex = NinoTestData.Sex.Male,
                    name = "A",
                    id = -1,
                    isHasPet = false
                });
                list.Add(new NinoTestData
                {
                    sex = NinoTestData.Sex.Female,
                    name = "B",
                    id = 1,
                    isHasPet = true
                });

                var buf = NinoSerializer.Serialize(list);
                List<NinoTestData> list2 = NinoDeserializer.Deserialize<List<NinoTestData>>(buf);
                Assert.IsTrue(list2.Count == list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    Assert.IsTrue(list2[i].name == list[i].name);
                    Assert.IsTrue(list2[i].id == list[i].id);
                    Assert.IsTrue(list2[i].isHasPet == list[i].isHasPet);
                    Assert.IsTrue(list2[i].sex == list[i].sex);
                }

                var arr = new NinoTestData[2];
                arr[0] = new NinoTestData
                {
                    sex = NinoTestData.Sex.Male,
                    name = "C",
                    id = 2,
                    isHasPet = true
                };
                arr[1] = new NinoTestData
                {
                    sex = NinoTestData.Sex.Male,
                    name = "D",
                    id = 3,
                    isHasPet = false
                };

                buf = NinoSerializer.Serialize(arr);
                NinoTestData[] arr2 = NinoDeserializer.Deserialize<NinoTestData[]>(buf);
                Assert.IsTrue(arr2.Length == arr.Length);
                for (int i = 0; i < arr.Length; i++)
                {
                    Assert.IsTrue(arr2[i].name == arr[i].name);
                    Assert.IsTrue(arr2[i].id == arr[i].id);
                    Assert.IsTrue(arr2[i].isHasPet == arr[i].isHasPet);
                    Assert.IsTrue(arr2[i].sex == arr[i].sex);
                }
            }
        }

        [TestClass]
        public class Issue32 : IssueTestTemplate
        {
            protected override void RunTest()
            {
                MessagePackage package = new MessagePackage
                {
                    agreement = AgreementType.Move,
                    move = new Move(1, 2, 3, 4, 5, 6, 7)
                };


                var a = NinoSerializer.Serialize(package);
                Console.WriteLine(string.Join(",", a));
                MessagePackage b = NinoDeserializer.Deserialize<MessagePackage>(a);
                Assert.IsTrue(package.agreement == b.agreement);
                Assert.IsTrue(package.move.id == b.move.id);
                Assert.IsTrue(package.move.x.ToString() == b.move.x.ToString());
                Assert.IsTrue(package.move.y.ToString() == b.move.y.ToString());
                Assert.IsTrue(package.move.z.ToString() == b.move.z.ToString());
                Assert.IsTrue(package.move.eulerX.ToString() == b.move.eulerX.ToString());
                Assert.IsTrue(package.move.eulerY.ToString() == b.move.eulerY.ToString());
                Assert.IsTrue(package.move.eulerZ.ToString() == b.move.eulerZ.ToString());
            }

            public enum AgreementType : byte
            {
                Enter = 1,
                EnemyEnter = 2,
                List = 3,
                Move = 4,
                ReadyCreatEnemy = 5,
                OnGameEnd = 6,
                Attack = 7,
                Hit = 7,
                OnHit = 8,
                ReadyDTDown = 9,
                ReadyDTUp = 10,
                GetTime = 11,
                GetID = 12,
            }

            [NinoType()]
            public class MessagePackage
            {
                public AgreementType agreement;
                public Move move;
            }

            [NinoType]
            public class Move
            {
                [NinoMember(1)] public int id;
                [NinoMember(2)] public float x;
                [NinoMember(3)] public float y;
                [NinoMember(4)] public float z;
                [NinoMember(5)] public float eulerX;
                [NinoMember(6)] public float eulerY;
                [NinoMember(7)] public float eulerZ;

                public Move()
                {
                }

                public Move(int id, float x, float y, float z, float eulerX, float eulerY, float eulerZ)
                {
                    this.id = id;
                    this.x = x;
                    this.y = y;
                    this.z = z;
                    this.eulerX = eulerX;
                    this.eulerY = eulerY;
                    this.eulerX = eulerZ;
                }
            }
        }
    }
}