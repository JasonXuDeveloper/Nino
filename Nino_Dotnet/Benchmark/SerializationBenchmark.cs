using System;
using Benchmark;
using Benchmark.Models;
using Benchmark.Serializers;
using BenchmarkDotNet.Attributes;
using System.Collections.Generic;
using MessagePack;
using System.IO;
using System.Text;

namespace Nino.Benchmark
{
    [Config(typeof(BenchmarkConfig))]
    public class SerializationBenchmark
	{
        //[ParamsSource(nameof(Serializers))]
        //public SerializerBase Serializer;
        // Currently BenchmarkDotNet does not detect inherited ParamsSource so use copy and paste:)
        //public IEnumerable<SerializerBase> Serializers => new SerializerBase[]
        //{
        //    new MessagePack_v2(),
        //    new ProtobufNetSerializer(),
        //    new JsonNetSerializer(),
        //    new BsonNetSerializer(),
        //    new BinaryFormatterSerializer(),
        //    new DataContractSerializer(),
        //    new HyperionSerializer(),
        //    new JilSerializer(),
        //    new SpanJsonSerializer(),
        //    new Utf8JsonSerializer(),
        //    new SystemTextJsonSerializer(),
        //    new FsPicklerSerializer(),
        //    new CerasSerializer(),
        //    new OdinSerializer_(),
        //    new NinoSerializer()
        //};

        // models
        protected static AccessToken AccessTokenInput = new AccessToken();

        protected static AccountMerge AccountMergeInput = new AccountMerge();

        protected static Answer AnswerInput = new Answer();

        protected static Badge BadgeInput = new Badge();

        protected static Comment CommentInput = new Comment();

        protected static NestedData NestedDataInput = new NestedData() { };

        [GlobalSetup]
        public void Setup()
        {
            //register importer (custom way to write those objects)
            Nino.Serialization.Serializer.AddCustomImporter<DateTime>((datetime, writer) =>
            {
                //write long
                writer.Write(datetime.ToBinary());
            });
            //nested data
            Data[] dt = new Data[10000];
            for(int i = 0; i < dt.Length; i++)
            {
                dt[i] = new Data()
                {
                    x = short.MaxValue,
                    y = byte.MaxValue,
                    z = short.MaxValue,
                    f = 1234.56789f,
                    d = 66.66666666m,
                    db = 999.999999999999,
                    bo = true,
                    en = TestEnum.A,
                    name = GetString(20)
                };
            }
            NestedDataInput = new NestedData()
            {
                name = "测试",
                ps = dt
            };
        }

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

        // Serialize

        [Benchmark]
        public object NinoSerialize()
        {
            return Nino.Serialization.Serializer.Serialize(NestedDataInput);
        }

        [Benchmark]
        public object MsgPackSerialize()
        {
            var lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
            return MessagePack.MessagePackSerializer.Serialize(NestedDataInput, lz4Options);
        }

        [Benchmark]
        public object ProtobufSerialize()
        {
            using(var ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, NestedDataInput);
                return ms.ToArray();
            }
        }

        //[Benchmark] public object AccessTokenSerialize() => this.Serializer.Serialize(AccessTokenInput);

        //[Benchmark] public object AccountMergeSerialize() => this.Serializer.Serialize(AccountMergeInput);

        //[Benchmark] public object AnswerSerialize() => this.Serializer.Serialize(AnswerInput);

        //[Benchmark] public object BadgeSerialize() => this.Serializer.Serialize(BadgeInput);

        //[Benchmark] public object CommentSerialize() => this.Serializer.Serialize(CommentInput);

        // Deserialize

        //[Benchmark] public AccessToken AccessTokenDeserialize() => this.Serializer.Deserialize<AccessToken>(this.AccessTokenOutput);

        //[Benchmark] public AccountMerge AccountMergeDeserialize() => this.Serializer.Deserialize<AccountMerge>(this.AccountMergeOutput);

        //[Benchmark] public Answer AnswerDeserialize() => this.Serializer.Deserialize<Answer>(this.AnswerOutput);

        //[Benchmark] public Badge BadgeDeserialize() => this.Serializer.Deserialize<Badge>(this.BadgeOutput);

        //[Benchmark] public Comment CommentDeserialize() => this.Serializer.Deserialize<Comment>(this.CommentOutput);
    }
}

