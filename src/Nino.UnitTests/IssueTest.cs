using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Serialization;

namespace Nino.UnitTests
{
    [NinoSerialize]
    public partial class IntRange
    {
        [NinoMember(0)]
        public int _min;
        [NinoMember(1)]
        public int _max;
    }

    [NinoSerialize]
    public partial class Item
    {
        [NinoMember(0)]
        public IntRange Range;
    }
    
    [TestClass]
    public class IssueTest
    {
        [TestClass]
        public class Issue104
        {
            [TestMethod]
            public void RunTest()
            {
                CodeGenerator.GenerateSerializationCode(typeof(IntRange));
                CodeGenerator.GenerateSerializationCode(typeof(Item));
                CodeGenerator.GenerateSerializationCodeForAllTypePossible();
            }
        }
        
        [TestClass]
        public class Issue52
        {
            [NinoSerialize]
            public partial class NinoTestData
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
                
                var buf = Serializer.Serialize(dt);
                var dt2 = Deserializer.Deserialize<NinoTestData>(buf);
                
                Assert.IsTrue(dt.X == dt2.X);
                Assert.IsTrue(dt.Y == dt2.Y);

                dt = new NinoTestData()
                {
                    X = sbyte.MinValue,
                    Y = short.MinValue
                };
                
                buf = Serializer.Serialize(dt);
                dt2 = Deserializer.Deserialize<NinoTestData>(buf);
                
                Assert.IsTrue(dt.X == dt2.X);
                Assert.IsTrue(dt.Y == dt2.Y);
                
                dt = new NinoTestData()
                {
                    X = int.MinValue,
                    Y = long.MinValue
                };
                
                buf = Serializer.Serialize(dt);
                dt2 = Deserializer.Deserialize<NinoTestData>(buf);
                
                Assert.IsTrue(dt.X == dt2.X);
                Assert.IsTrue(dt.Y == dt2.Y);
            }
        }
        
        [TestClass]
        public class Issue41
        {
            [NinoSerialize]
            public partial class NinoTestData
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

                var buf = Serializer.Serialize(list);
                var list2 = Deserializer.Deserialize<List<NinoTestData>>(buf);
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

                buf = Serializer.Serialize(arr);
                var arr2 = Deserializer.Deserialize<NinoTestData[]>(buf);
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
        public class Issue33
        {
            [NinoSerialize]
            [CodeGenIgnore]
            public partial class GamePatcher
            {

                [NinoMember(1)] public DateTime StaticValidityDateTime = DateTime.MinValue;
                [NinoMember(2)] public int CCC= -1;
                [NinoMember(3)] public string Key = "";
            }
            
            [NinoSerialize]
            public partial class GamePatcher2
            {

                [NinoMember(1)] public DateTime StaticValidityDateTime = DateTime.MinValue;
                [NinoMember(2)] public int CCC= -1;
                [NinoMember(3)] public string Key = "";
            }
            
            //GENERATED CODE
            public partial class GamePatcher2
            {
                public static GamePatcher2.SerializationHelper NinoSerializationHelper = new GamePatcher2.SerializationHelper();
                public class SerializationHelper: Nino.Serialization.NinoWrapperBase<GamePatcher2>
                {
                    #region NINO_CODEGEN
                    public override void Serialize(GamePatcher2 value, ref Writer writer)
                    {
                        if(value == null)
                        {
                            writer.Write(false);
                            return;
                        }
                        writer.Write(true);
                        writer.Write(value.StaticValidityDateTime);
                        writer.Write(value.CCC);
                        writer.Write(value.Key);
                    }

                    public override GamePatcher2 Deserialize(Nino.Serialization.Reader reader)
                    {
                        if(!reader.ReadBool())
                            return null;
                        GamePatcher2 value = new GamePatcher2();
                        value.StaticValidityDateTime = reader.ReadDateTime();
                        reader.Read(ref value.CCC, sizeof(int));
                        value.Key = reader.ReadString();
                        return value;
                    }
                    
                    public override unsafe int GetSize(GamePatcher2 value)
                    {
                        if(value == null)
                            return sizeof(bool);
                        int size = sizeof(bool);
                        size += sizeof(DateTime);
                        size += sizeof(int);
                        size += 1 + 4 + value.Key.Length * 2;
                        return size;
                    }
                    #endregion
                }
            }

            [TestMethod]
            public void RunTest()
            {
                //no code gen
                GamePatcher gp = new GamePatcher();
                gp.CCC=10;
                gp.Key="ajoaiewrnvo";

                var a = Nino.Serialization.Serializer.Serialize<GamePatcher>(gp);
                var b = Nino.Serialization.Deserializer.Deserialize<GamePatcher>(a);
                Assert.AreEqual(gp.CCC, b.CCC);
                Assert.AreEqual(gp.Key, b.Key);
                
                //code gen
                GamePatcher2 gp2 = new GamePatcher2();
                gp2.CCC=10;
                gp2.Key="ajoaiewrnvo";

                var aa = Nino.Serialization.Serializer.Serialize<GamePatcher2>(gp2);
                
                Assert.IsTrue(aa.SequenceEqual(a));
                
                var bb = Nino.Serialization.Deserializer.Deserialize<GamePatcher>(aa);
                Assert.AreEqual(gp2.CCC, bb.CCC);
                Assert.AreEqual(gp2.Key, bb.Key);
                Assert.AreEqual(gp2.CCC, b.CCC);
                Assert.AreEqual(gp2.Key, b.Key);
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


                var a = Nino.Serialization.Serializer.Serialize<MessagePackage>(package);
                Console.WriteLine(string.Join(",", a));
                var b = Nino.Serialization.Deserializer.Deserialize<MessagePackage>(a);
                Assert.IsTrue(package.agreement == b.agreement);
                Assert.IsTrue(package.move.id == b.move.id);
                Assert.IsTrue(package.move.x.ToString() == b.move.x.ToString());
                Assert.IsTrue(package.move.y.ToString() == b.move.y.ToString());
                Assert.IsTrue(package.move.z.ToString() == b.move.z.ToString());
                Assert.IsTrue(package.move.eulerX.ToString() == b.move.eulerX.ToString());
                Assert.IsTrue(package.move.eulerY.ToString() == b.move.eulerY.ToString());
                Assert.IsTrue(package.move.eulerZ.ToString() == b.move.eulerZ.ToString());
            }
            
            public enum AgreementType: byte
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

            [NinoSerialize(true)]
            public partial class MessagePackage
            {
                public AgreementType agreement;
                public Move move;
            }

            [NinoSerialize]
            public partial class Move
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