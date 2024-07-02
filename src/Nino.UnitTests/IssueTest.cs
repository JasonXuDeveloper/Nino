using System;
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
        public class IssueIgnore
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
            public void RunTest()
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
        public class Issue52
        {
            [NinoType]
            public class NinoTestData
            {
                [NinoMember(1)] public int X;
                [NinoMember(2)] public long Y;
            }

            [TestMethod]
            public void RunTest()
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
        public class Issue41
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
            public void RunTest()
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
        public class Issue32
        {
            [TestMethod]
            public void RunTest()
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