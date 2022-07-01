using System;
using System.Text;
using Nino.Benchmark.Models;
using Nino.Benchmark.Serializers;
using BenchmarkDotNet.Attributes;
using System.Collections.Generic;

namespace Nino.Benchmark
{
    [Config(typeof(BenchmarkConfig))]
    public class SerializationBenchmark
    {
        [ParamsSource(nameof(Serializers))] public SerializerBase Serializer;

        public IEnumerable<SerializerBase> Serializers => new SerializerBase[]
        {
            new MessagePack_v2(),
            new ProtobufNetSerializer(),
            new JsonNetSerializer(),
            new BinaryFormatterSerializer(),
            new DataContractSerializer(),
            new HyperionSerializer(),
            new JilSerializer(),
            new SpanJsonSerializer(),
            new Utf8JsonSerializer(),
            new FsPicklerSerializer(),
            new CerasSerializer(),
            new OdinSerializer_(),
            new NinoSerializer()
        };

        // primitives
        protected static readonly sbyte SByteInput = sbyte.MinValue;
        protected static readonly short ShortInput = short.MaxValue;
        protected static readonly int IntInput = int.MinValue;
        protected static readonly long LongInput = long.MaxValue;
        protected static readonly byte ByteInput = byte.MaxValue;
        protected static readonly ushort UShortInput = ushort.MaxValue;
        protected static readonly uint UIntInput = uint.MinValue;
        protected static readonly ulong ULongInput = ulong.MaxValue;
        protected static readonly bool BoolInput = false;
        protected static readonly string StringInput = GetString(100);
        protected static readonly char CharInput = 'a';
        protected static readonly DateTime DateTimeInput = DateTime.Today;
        protected static readonly byte[] BytesInput = new byte[]{0,1,2,3,4};

        // models
        protected static readonly AccessToken AccessTokenInput = new AccessToken();

        protected static readonly AccountMerge AccountMergeInput = new AccountMerge();

        protected static readonly Answer AnswerInput = new Answer();

        protected static readonly Badge BadgeInput = new Badge();

        protected static readonly Comment CommentInput = new Comment();

        protected static NestedData NestedDataInput = new NestedData() { };

        private object SByteOutput;
        private object ShortOutput;
        private object IntOutput;
        private object LongOutput;
        private object ByteOutput;
        private object UShortOutput;
        private object UIntOutput;
        private object ULongOutput;
        private object BoolOutput;
        private object StringOutput;
        private object CharOutput;
        private object DateTimeOutput;
        private object BytesOutput;

        private object AccessTokenOutput;
        private object AccountMergeOutput;
        private object AnswerOutput;
        private object BadgeOutput;
        private object CommentOutput;
        private object NestedDataOutput;

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
            for (int i = 0; i < dt.Length; i++)
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
                name = "Test",
                ps = dt
            };

            // primitives
            this.SByteOutput = this.Serializer.Serialize(SByteInput);
            this.ShortOutput = this.Serializer.Serialize(ShortInput);
            this.IntOutput = this.Serializer.Serialize(IntInput);
            this.LongOutput = this.Serializer.Serialize(LongInput);
            this.ByteOutput = this.Serializer.Serialize(ByteInput);
            this.UShortOutput = this.Serializer.Serialize(UShortInput);
            this.UIntOutput = this.Serializer.Serialize(UIntInput);
            this.ULongOutput = this.Serializer.Serialize(ULongInput);
            this.BoolOutput = this.Serializer.Serialize(BoolInput);
            this.StringOutput = this.Serializer.Serialize(StringInput);
            this.CharOutput = this.Serializer.Serialize(CharInput);
            this.DateTimeOutput = this.Serializer.Serialize(DateTimeInput);
            this.BytesOutput = this.Serializer.Serialize(BytesInput);
            
            // models
            this.AccessTokenOutput = this.Serializer.Serialize(AccessTokenInput);
            this.AccountMergeOutput = this.Serializer.Serialize(AccountMergeInput);
            this.AnswerOutput = this.Serializer.Serialize(AnswerInput);
            this.BadgeOutput = this.Serializer.Serialize(BadgeInput);
            this.CommentOutput = this.Serializer.Serialize(CommentInput);
            this.CommentOutput = this.Serializer.Serialize(CommentInput);
            this.NestedDataOutput = this.Serializer.Serialize(NestedDataInput);
        }

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

        // Serialize

        [Benchmark]
        public object _PrimitiveSByteSerialize() => this.Serializer.Serialize(SByteInput);

        [Benchmark]
        public object _PrimitiveShortSerialize() => this.Serializer.Serialize(ShortInput);
        
        [Benchmark]
        public object _PrimitiveIntSerialize() => this.Serializer.Serialize(IntInput);
        
        [Benchmark]
        public object _PrimitiveLongSerialize() => this.Serializer.Serialize(LongInput);
        
        [Benchmark]
        public object _PrimitiveByteSerialize() => this.Serializer.Serialize(ByteInput);
        
        [Benchmark]
        public object _PrimitiveUShortSerialize() => this.Serializer.Serialize(UShortInput);
        
        [Benchmark]
        public object _PrimitiveUIntSerialize() => this.Serializer.Serialize(UIntInput);
        
        [Benchmark]
        public object _PrimitiveULongSerialize() => this.Serializer.Serialize(ULongInput);
        
        [Benchmark]
        public object _PrimitiveBoolSerialize() => this.Serializer.Serialize(BoolInput);
        
        [Benchmark]
        public object _PrimitiveStringSerialize() => this.Serializer.Serialize(StringInput);
        
        [Benchmark]
        public object _PrimitiveCharSerialize() => this.Serializer.Serialize(CharInput);
        
        [Benchmark]
        public object _PrimitiveDateTimeSerialize() => this.Serializer.Serialize(DateTimeInput);
        
        [Benchmark]
        public object AccessTokenSerialize() => this.Serializer.Serialize(AccessTokenInput);
        
        [Benchmark]
        public object AccountMergeSerialize() => this.Serializer.Serialize(AccountMergeInput);
        
        [Benchmark]
        public object AnswerSerialize() => this.Serializer.Serialize(AnswerInput);
        
        [Benchmark]
        public object BadgeSerialize() => this.Serializer.Serialize(BadgeInput);
        
        [Benchmark]
        public object CommentSerialize() => this.Serializer.Serialize(CommentInput);

        [Benchmark]
        public object NestedDataSerialize() => this.Serializer.Serialize(NestedDataInput);

        // Deserialize

        //[Benchmark] public AccessToken AccessTokenDeserialize() => this.Serializer.Deserialize<AccessToken>(this.AccessTokenOutput);

        //[Benchmark] public AccountMerge AccountMergeDeserialize() => this.Serializer.Deserialize<AccountMerge>(this.AccountMergeOutput);

        //[Benchmark] public Answer AnswerDeserialize() => this.Serializer.Deserialize<Answer>(this.AnswerOutput);

        //[Benchmark] public Badge BadgeDeserialize() => this.Serializer.Deserialize<Badge>(this.BadgeOutput);

        //[Benchmark] public Comment CommentDeserialize() => this.Serializer.Deserialize<Comment>(this.CommentOutput);
    }
}