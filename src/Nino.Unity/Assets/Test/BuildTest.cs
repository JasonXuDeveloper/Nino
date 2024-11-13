using System;
using System.IO;
using System.Linq;
using UnityEngine;
using MessagePack;
using UnityEngine.UI;
using MongoDB.Bson.IO;
using System.Diagnostics;
using MessagePack.Resolvers;
using System.Collections.Generic;
using MongoDB.Bson.Serialization;
using UnityEngine.SceneManagement;
using System.Runtime.Serialization.Formatters.Binary;
using Test.NinoGen;
using Debug = UnityEngine.Debug;

namespace Nino.Test
{
    public class BuildTest : MonoBehaviour
    {
        public Text ninoResultText;
        public Text pbNetResultText;
        public Text binaryFormatterResultText;
        public Text bsonResultText;
        public Text msgPackResultText;

        public Button ninoBtn;
        public Button pbNetBtn;
        public Button binaryFormatterBtn;
        public Button bsonBtn;
        public Button msgPackBtn;

        private readonly Stopwatch sw = new Stopwatch();
        private byte[] ninoBuffer = Array.Empty<byte>();
        private byte[] pbNetBuffer = Array.Empty<byte>();
        private byte[] binaryFormatterBuffer1 = Array.Empty<byte>();
        private byte[] bsonBuffer = Array.Empty<byte>();
        private byte[] msgPackBuffer = Array.Empty<byte>();
        private BuildTestDataCodeGen d2;

        private void OnGUI()
        {
            if (GUI.Button(new Rect(Screen.width - 300, Screen.height - 100, 200, 50), "Large Object Test"))
            {
                SceneManager.LoadScene(1);
            }
        }

        /// <summary>
        /// Prepare data
        /// </summary>
        private void Awake()
        {
            d2 = new BuildTestDataCodeGen()
            {
                a = 1,
                b = 2,
                c = 100,
                d = 500,
                e = 666,
                f = 99999999,
                g = 123456788765,
                h = 123456321,
                i = 6.6f,
                j = 9.9,
                k = 1.23456789012345m,
                l = true,
                m = 'a',
                n = "aksjdfhgheuwi",
                o = new List<int>() { 1, 2, 3, 4, 5 },
                p = new List<NotIncludeAllClass>()
                {
                    new NotIncludeAllClass()
                    {
                        a = 30, b = 20, c = 12938, d = 19283
                    }
                },
                q = new byte[] { 1, 2, 3 },
                r = new NotIncludeAllClass[]
                {
                    new NotIncludeAllClass()
                    {
                        a = 30, b = 20, c = 12938, d = 19283
                    }
                },
                s = new Dictionary<string, NotIncludeAllClass>()
                {
                    {
                        "ks", new NotIncludeAllClass()
                        {
                            a = 30, b = 20, c = 12938, d = 19283
                        }
                    }
                },
                t = new Dictionary<NotIncludeAllClass, int>()
                {
                    { new NotIncludeAllClass() { a = 30, b = 20, c = 12938, d = 19283 }, 39 }
                },
                u = new Dictionary<string, int>()
                {
                    { "usd", 7 }
                },
                v = new Dictionary<NotIncludeAllClass, NotIncludeAllClass>()
                {
                    {
                        new NotIncludeAllClass() { a = 30, b = 20, c = 12938, d = 19283 },
                        new NotIncludeAllClass() { a = 30, b = 20, c = 12938, d = 19283 }
                    }
                }
            };
            //reg
            try
            {
                BsonClassMap.RegisterClassMap<NotIncludeAllClass>();
                BsonClassMap.RegisterClassMap<BuildTestDataCodeGen>();
                StaticCompositeResolver.Instance.Register(
                    GeneratedResolver.Instance,
                    BuiltinResolver.Instance,
                    AttributeFormatterResolver.Instance,
                    MessagePack.Unity.UnityResolver.Instance,
                    PrimitiveObjectResolver.Instance,
                    MessagePack.Unity.Extension.UnityBlitWithPrimitiveArrayResolver.Instance,
                    StandardResolver.Instance
                );
                var option = MessagePackSerializerOptions.Standard.WithResolver(StaticCompositeResolver.Instance);
                MessagePackSerializer.DefaultOptions = option;
            }
            catch
            {
                //ignore
            }
        }

        /// <summary>
        /// Btn event
        /// </summary>
        /// <exception cref="NotImplementedException"></exception>
        private void Start()
        {
            ninoBtn.onClick.AddListener(() =>
            {
                try
                {
                    //deserialize
                    if (ninoBuffer.Length > 0)
                    {
                        sw.Reset();
                        sw.Start();
                        Deserializer.Deserialize(ninoBuffer, out BuildTestDataCodeGen dd1);
                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        sw.Reset();
                        ninoResultText.text =
                            $"Deserialized BuildTestDataCodeGen in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd1}";
                        ninoBtn.GetComponentInChildren<Text>().text = "NinoSerialize";
                        ninoBuffer = Array.Empty<byte>();
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();
                        ninoBuffer = d2.Serialize();
                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        ninoResultText.text =
                            $"Serialized BuildTestDataCodeGen as {ninoBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms\n{string.Join(",", ninoBuffer)}";
                        ninoBtn.GetComponentInChildren<Text>().text = "NinoDeserialize";
                    }
                }
                catch (Exception ex)
                {
                    ninoResultText.text = ex.ToString();
                    Debug.LogException(ex);
                }
            });
            pbNetBtn.onClick.AddListener(() =>
            {
                try
                {
                    //deserialize
                    if (pbNetBuffer.Length > 0)
                    {
                        BuildTestDataCodeGen dd2;
                        sw.Reset();
                        sw.Start();
                        using (MemoryStream ms = new MemoryStream(pbNetBuffer))
                        {
                            dd2 = ProtoBuf.Serializer.Deserialize<BuildTestDataCodeGen>(ms);
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;

                        pbNetResultText.text =
                            $"Deserialized BuildTestDataCodeGen (no code gen for this class for pbnet) in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd2}";
                        pbNetBtn.GetComponentInChildren<Text>().text = "PbNetSerialize";
                        pbNetBuffer = Array.Empty<byte>();
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();
                        using (MemoryStream ms = new MemoryStream())
                        {
                            ProtoBuf.Serializer.Serialize(ms, d2);
                            pbNetBuffer = ms.ToArray();
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;

                        pbNetResultText.text =
                            $"Serialized BuildTestDataCodeGen (no code gen for this class for pbnet) as {pbNetBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms:\n{string.Join(",", pbNetBuffer.Take(300))}";
                        pbNetBtn.GetComponentInChildren<Text>().text = "PbNetDeserialize";
                    }
                }
                catch (Exception ex)
                {
                    pbNetResultText.text = ex.ToString();
                    Debug.LogException(ex);
                }
            });
            binaryFormatterBtn.onClick.AddListener(() =>
            {
                try
                {
                    //deserialize
                    if (binaryFormatterBuffer1.Length > 0)
                    {
                        BuildTestDataCodeGen dd2;
                        sw.Reset();
                        sw.Start();
                        using (var ms = new MemoryStream(binaryFormatterBuffer1))
                        {
                            BinaryFormatter bFormatter = new BinaryFormatter();
                            dd2 = (BuildTestDataCodeGen)bFormatter.Deserialize(ms);
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        binaryFormatterResultText.text =
                            $"Deserialized BuildTestDataCodeGen in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd2}";
                        binaryFormatterBtn.GetComponentInChildren<Text>().text = "BinaryFormatterSerialize";
                        binaryFormatterBuffer1 = Array.Empty<byte>();
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();
                        using (var ms = new MemoryStream())
                        {
                            BinaryFormatter bFormatter = new BinaryFormatter();
                            bFormatter.Serialize(ms, d2);
                            binaryFormatterBuffer1 = ms.ToArray();
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        binaryFormatterResultText.text =
                            $"Serialized BuildTestDataCodeGen (no code gen for this class for bf) as {binaryFormatterBuffer1.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms:\n" +
                            $"{string.Join(",", binaryFormatterBuffer1.Length > 300 ? binaryFormatterBuffer1.Take(300) : binaryFormatterBuffer1)}";
                        binaryFormatterBtn.GetComponentInChildren<Text>().text = "BinaryFormatterDeserialize";
                    }
                }
                catch (Exception ex)
                {
                    binaryFormatterResultText.text = ex.ToString();
                    Debug.LogException(ex);
                }
            });
            bsonBtn.onClick.AddListener(() =>
            {
                try
                {
                    //deserialize
                    if (bsonBuffer.Length > 0)
                    {
                        BuildTestDataCodeGen dd2;
                        sw.Reset();
                        sw.Start();
                        dd2 = (BuildTestDataCodeGen)BsonSerializer.Deserialize(bsonBuffer,
                            typeof(BuildTestDataCodeGen));

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        bsonResultText.text =
                            $"Deserialized BuildTestDataCodeGen (no code gen for this class for bson) in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd2}\n";
                        bsonBtn.GetComponentInChildren<Text>().text = "BsonSerialize";
                        bsonBuffer = Array.Empty<byte>();
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();
                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (BsonBinaryWriter bsonWriter =
                                   new BsonBinaryWriter(ms, BsonBinaryWriterSettings.Defaults))
                            {
                                BsonSerializationContext context = BsonSerializationContext.CreateRoot(bsonWriter);
                                BsonSerializationArgs args = default;
                                args.NominalType = typeof(object);
                                IBsonSerializer serializer = BsonSerializer.LookupSerializer(args.NominalType);
                                serializer.Serialize(context, args, d2);
                                bsonBuffer = ms.ToArray();
                            }
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        bsonResultText.text =
                            $"Serialized BuildTestDataCodeGen (no code gen for this class for bson) as {bsonBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms:\n{string.Join(",", bsonBuffer)}";
                        bsonBtn.GetComponentInChildren<Text>().text = "BsonDeserialize";
                    }
                }
                catch (Exception ex)
                {
                    bsonResultText.text = ex.ToString();
                    Debug.LogException(ex);
                }
            });
            msgPackBtn.onClick.AddListener(() =>
            {
                try
                {
                    //deserialize
                    if (msgPackBuffer.Length > 0)
                    {
                        BuildTestDataCodeGen dd2;
                        sw.Reset();
                        sw.Start();
                        dd2 = MessagePackSerializer.Deserialize<BuildTestDataCodeGen>(msgPackBuffer);

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        msgPackResultText.text =
                            $"Deserialized BuildTestDataCodeGen in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd2}\n";
                        msgPackBtn.GetComponentInChildren<Text>().text = "MsgPackSerialize";
                        msgPackBuffer = Array.Empty<byte>();
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();
                        var lz4Options =
                            MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.None);
                        msgPackBuffer = MessagePackSerializer.Serialize(d2, lz4Options);

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        msgPackResultText.text =
                            $"Serialized BuildTestDataCodeGen as {msgPackBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms:\n{string.Join(",", msgPackBuffer)}";
                        msgPackBtn.GetComponentInChildren<Text>().text = "MsgPackDeserialize";
                    }
                }
                catch (Exception ex)
                {
                    msgPackResultText.text = ex.ToString();
                    Debug.LogException(ex);
                }
            });
        }
    }
}