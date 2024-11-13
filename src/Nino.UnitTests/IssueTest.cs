using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.UnitTests.NinoGen;
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
            [TestMethod]
            public abstract void RunTest();
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

            [TestMethod]
            public override void RunTest()
            {
                var pools = new Dictionary<DicePoolType, DicePool>();
                pools.Add(DicePoolType.STR, new DicePool());
                pools.Add(DicePoolType.CONS, new DicePool());
                var bytes = Serializer.Serialize(pools);
                Deserializer.Deserialize(bytes, out Dictionary<DicePoolType, DicePool> pools2);
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

            [TestMethod]
            public override void RunTest()
            {
                var impl = new Impl { A = 10 };
                var bytes = impl.Serialize();
                Deserializer.Deserialize(bytes, out Impl impl2);
                Assert.AreEqual(impl.A, impl2.A);

                Dictionary<string, IBase> dict = new Dictionary<string, IBase>
                {
                    { "A", new Impl { A = 10 } }
                };
                bytes = dict.Serialize();
                Deserializer.Deserialize(bytes, out Dictionary<string, IBase> dict2);
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

            [TestMethod]
            public override void RunTest()
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
                var bytes = person.Serialize();
                Deserializer.Deserialize(bytes, out MyPackPerson person2);
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

            [TestMethod]
            public override void RunTest()
            {
                Data data = new Data();
                data.A = 10;
                data.B = 20;
                data.CompA = new CompA();
                data.CompA.Aa = 30;
                data.CompA.Ba = 40;

                var bufForData = data.Serialize();
                Deserializer.Deserialize(bufForData, out Data data2);

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

            [TestMethod]
            public override void RunTest()
            {
                var dt = new NinoTestData()
                {
                    X = -136, Y = 8
                };

                var buf = dt.Serialize();
                Deserializer.Deserialize(buf, out NinoTestData dt2);

                Assert.IsTrue(dt.X == dt2.X);
                Assert.IsTrue(dt.Y == dt2.Y);

                dt = new NinoTestData()
                {
                    X = sbyte.MinValue,
                    Y = short.MinValue
                };

                buf = dt.Serialize();
                Deserializer.Deserialize(buf, out dt2);

                Assert.IsTrue(dt.X == dt2.X);
                Assert.IsTrue(dt.Y == dt2.Y);

                dt = new NinoTestData()
                {
                    X = int.MinValue,
                    Y = long.MinValue
                };

                buf = dt.Serialize();
                Deserializer.Deserialize(buf, out dt2);

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

            [TestMethod]
            public override void RunTest()
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

                var buf = list.Serialize();
                Deserializer.Deserialize(buf, out List<NinoTestData> list2);
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

                buf = arr.Serialize();
                Deserializer.Deserialize(buf, out NinoTestData[] arr2);
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
            [TestMethod]
            public override void RunTest()
            {
                MessagePackage package = new MessagePackage
                {
                    agreement = AgreementType.Move,
                    move = new Move(1, 2, 3, 4, 5, 6, 7)
                };


                var a = package.Serialize();
                Console.WriteLine(string.Join(",", a));
                Deserializer.Deserialize(a, out MessagePackage b);
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