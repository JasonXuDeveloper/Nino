using System;
using System.Buffers;
using System.IO;
using System.Linq;
using UnityEngine;
using MessagePack;
using UnityEngine.UI;
using MongoDB.Bson.IO;
using System.Diagnostics;
using MessagePack.Resolvers;
using MongoDB.Bson.Serialization;
using UnityEngine.SceneManagement;
using System.Runtime.Serialization.Formatters.Binary;
using Test.NinoGen;
using Debug = UnityEngine.Debug;


namespace Nino.Test
{
    public class BuildTest2 : MonoBehaviour
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
        private byte[] binaryFormatterBuffer = Array.Empty<byte>();
        private byte[] bsonBuffer = Array.Empty<byte>();
        private byte[] msgPackBuffer = Array.Empty<byte>();
        private NestedData nd;

        private void OnGUI()
        {
            if (GUI.Button(new Rect(Screen.width - 300, Screen.height - 100, 200, 50), "Small Object Test"))
            {
                SceneManager.LoadScene(0);
            }
        }

        /// <summary>
        /// Prepare data
        /// </summary>
        private void Awake()
        {
            Data[] ps = new Data[1000];
            for (int i = 0, cnt = 1000; i < cnt; i++)
            {
                ps[i] = new Data()
                {
                    x = short.MaxValue,
                    y = byte.MaxValue,
                    z = short.MaxValue,
                    f = 1234.56789f,
                    d = 66.66666666m,
                    db = 999.999999999999,
                    bo = true,
                    en = TestEnum.A,
                };
            }

            nd = new NestedData()
            {
                name = "测试",
                ps = ps,
            };

            //reg
            try
            {
                BsonClassMap.RegisterClassMap<Data>();
                BsonClassMap.RegisterClassMap<NestedData>();
                BsonClassMap.RegisterClassMap<NotIncludeAllClass>();
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
                        Deserializer.Deserialize(ninoBuffer, out NestedData dd1);
                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        sw.Reset();
                        ninoBuffer = Array.Empty<byte>();
                        ninoResultText.text =
                            $"Deserialized NestedData in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd1}\n";
                        ninoBtn.GetComponentInChildren<Text>().text = "NinoSerialize";
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();
                        ninoBuffer = nd.Serialize();
                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        ninoResultText.text =
                            $"Serialized NestedData as {ninoBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms,\n{string.Join(",", ninoBuffer.Take(200))}";
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
                        NestedData dd1;
                        sw.Reset();
                        sw.Start();
                        using (MemoryStream ms = new MemoryStream(pbNetBuffer))
                        {
                            dd1 = ProtoBuf.Serializer.Deserialize<NestedData>(ms);
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;

                        pbNetBuffer = Array.Empty<byte>();
                        pbNetResultText.text =
                            $"Deserialized NestedData in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd1}\n";
                        pbNetBtn.GetComponentInChildren<Text>().text = "PbNetSerialize";
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();
                        using (MemoryStream ms = new MemoryStream())
                        {
                            ProtoBuf.Serializer.Serialize<NestedData>(ms, nd);
                            pbNetBuffer = ms.ToArray();
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        pbNetResultText.text =
                            $"Serialized NestedData as {pbNetBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms,\n{string.Join(",", pbNetBuffer.Take(200))}";
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
                    if (binaryFormatterBuffer.Length > 0)
                    {
                        NestedData dd1;
                        sw.Reset();
                        sw.Start();
                        using (var ms = new MemoryStream(binaryFormatterBuffer))
                        {
                            BinaryFormatter bFormatter = new BinaryFormatter();
                            dd1 = (NestedData)bFormatter.Deserialize(ms);
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        binaryFormatterBuffer = Array.Empty<byte>();
                        binaryFormatterResultText.text =
                            $"Deserialized NestedData in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd1}\n";
                        binaryFormatterBtn.GetComponentInChildren<Text>().text = "BinaryFormatterSerialize";
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();
                        using (var ms = new MemoryStream())
                        {
                            BinaryFormatter bFormatter = new BinaryFormatter();
                            bFormatter.Serialize(ms, nd);
                            binaryFormatterBuffer = ms.ToArray();
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        binaryFormatterResultText.text =
                            $"Serialized NestedData as {binaryFormatterBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms,\n" +
                            $"{string.Join(",", binaryFormatterBuffer.Take(200))}";
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
                        NestedData dd1;
                        sw.Reset();
                        sw.Start();
                        dd1 = (NestedData)BsonSerializer.Deserialize(bsonBuffer,
                            typeof(NestedData));

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        bsonBuffer = Array.Empty<byte>();
                        bsonResultText.text =
                            $"Deserialized NestedData in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd1}\n";
                        bsonBtn.GetComponentInChildren<Text>().text = "BsonSerialize";
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
                                serializer.Serialize(context, args, nd);
                                bsonBuffer = ms.ToArray();
                            }
                        }

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        bsonResultText.text =
                            $"Serialized NestedData as {bsonBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms,\n{string.Join(",", bsonBuffer.Take(200))}";
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
                        NestedData dd1;
                        sw.Reset();
                        sw.Start();
                        dd1 = MessagePackSerializer.Deserialize<NestedData>(msgPackBuffer);

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;

                        msgPackBuffer = Array.Empty<byte>();
                        msgPackResultText.text =
                            $"Deserialized NestedData in {((float)m1 / Stopwatch.Frequency) * 1000} ms: \n{dd1}\n";
                        msgPackBtn.GetComponentInChildren<Text>().text = "MsgPackSerialize";
                    }
                    //serialize
                    else
                    {
                        sw.Reset();
                        sw.Start();

                        var lz4Options =
                            MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.None);
                        msgPackBuffer = MessagePackSerializer.Serialize(nd, lz4Options);

                        sw.Stop();
                        var m1 = sw.ElapsedTicks;
                        msgPackResultText.text =
                            $"Serialized NestedData as {msgPackBuffer.Length} bytes in {((float)m1 / Stopwatch.Frequency) * 1000} ms,\n{string.Join(",", msgPackBuffer.Take(200))}";
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