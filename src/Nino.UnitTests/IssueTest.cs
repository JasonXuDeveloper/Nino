using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Serialization;

namespace Nino.UnitTests
{
    [TestClass]
    public class IssueTest
    {
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
                    public override void Serialize(GamePatcher2 value, Nino.Serialization.Writer writer)
                    {
                        writer.Write(value.StaticValidityDateTime);
                        writer.CompressAndWrite(ref value.CCC);
                        writer.Write(value.Key);
                    }

                    public override GamePatcher2 Deserialize(Nino.Serialization.Reader reader)
                    {
                        GamePatcher2 value = new GamePatcher2();
                        value.StaticValidityDateTime = reader.ReadDateTime();
                        reader.DecompressAndReadNumber<System.Int32>(ref value.CCC);
                        value.Key = reader.ReadString();
                        return value;
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