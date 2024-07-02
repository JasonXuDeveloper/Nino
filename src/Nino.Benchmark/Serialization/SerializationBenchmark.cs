using System;
using System.Text;
using Nino.Benchmark.Models;
using Nino.Benchmark.Serializers;
using BenchmarkDotNet.Attributes;
using System.Collections.Generic;

#pragma warning disable 8618

namespace Nino.Benchmark
{
    [Config(typeof(BenchmarkConfig))]
    public class SerializationBenchmark
    {
        [ParamsSource(nameof(Serializers))] public SerializerBase Serializer;

        public IEnumerable<SerializerBase> Serializers => new SerializerBase[]
        {
            new MessagePack_Lz4(),
            new MessagePack_NoCompression(),
            new ProtobufNetSerializer(),
            new JsonNetSerializer(),
            new BinaryFormatterSerializer(),
            new DataContractSerializer(),
            new JilSerializer(),
            new SpanJsonSerializer(),
            new Utf8JsonSerializer(),
            new FsPicklerSerializer(),
            new CerasSerializer(),
            new OdinSerializer_(),
            new NinoSerializer(),
        };

        static SerializationBenchmark()
        {
            //nested data
            Data[] dt = new Data[10000];
            for (int i = 0; i < dt.Length; i++)
            {
                dt[i] = new Data()
                {
                    X = short.MaxValue,
                    Y = byte.MaxValue,
                    Z = short.MaxValue,
                    F = 1234.56789f,
                    D = 66.66666666m,
                    Db = 999.999999999999,
                    Bo = true,
                    En = TestEnum.A,
                };
            }

            NestedDataInput = new NestedData()
            {
                Name = "Test",
                Ps = dt
            };
        }

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

        // models
        protected static readonly AccessToken AccessTokenInput = new AccessToken();

        protected static readonly AccountMerge AccountMergeInput = new AccountMerge();

        protected static readonly Answer AnswerInput = new Answer();

        protected static readonly Badge BadgeInput = new Badge();

        protected static readonly Comment CommentInput = new Comment();

        protected static NestedData NestedDataInput;

        private byte[] _sByteOutput;
        private byte[] _shortOutput;
        private byte[] _intOutput;
        private byte[] _longOutput;
        private byte[] _byteOutput;
        private byte[] _uShortOutput;
        private byte[] _uIntOutput;
        private byte[] _uLongOutput;
        private byte[] _boolOutput;
        private byte[] _stringOutput;
        private byte[] _charOutput;
        private byte[] _dateTimeOutput;

        private byte[] _accessTokenOutput;
        private byte[] _accountMergeOutput;
        private byte[] _answerOutput;
        private byte[] _badgeOutput;
        private byte[] _commentOutput;
        private byte[] _nestedDataOutput;

        [GlobalSetup]
        public void Setup()
        {
            // primitives
            this._sByteOutput = this.Serializer.Serialize(SByteInput);
            this._shortOutput = this.Serializer.Serialize(ShortInput);
            this._intOutput = this.Serializer.Serialize(IntInput);
            this._longOutput = this.Serializer.Serialize(LongInput);
            this._byteOutput = this.Serializer.Serialize(ByteInput);
            this._uShortOutput = this.Serializer.Serialize(UShortInput);
            this._uIntOutput = this.Serializer.Serialize(UIntInput);
            this._uLongOutput = this.Serializer.Serialize(ULongInput);
            this._boolOutput = this.Serializer.Serialize(BoolInput);
            this._stringOutput = this.Serializer.SerializeString(StringInput);
            this._charOutput = this.Serializer.Serialize(CharInput);
            this._dateTimeOutput = this.Serializer.Serialize(DateTimeInput);

            // models
            this._accessTokenOutput = this.Serializer.SerializeAccessToken(AccessTokenInput);
            this._accountMergeOutput = this.Serializer.SerializeAccountMerge(AccountMergeInput);
            this._answerOutput = this.Serializer.SerializeAnswer(AnswerInput);
            this._badgeOutput = this.Serializer.SerializeBadge(BadgeInput);
            this._commentOutput = this.Serializer.SerializeComment(CommentInput);
            this._nestedDataOutput = this.Serializer.SerializeNestedData(NestedDataInput);
        }

        private static string GetString(int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('a', len);
            return sb.ToString();
        }

        // Serialize

        [Benchmark]
        public byte[] _PrimitiveSByteSerialize() => this.Serializer.Serialize(SByteInput);

        [Benchmark]
        public byte[] _PrimitiveShortSerialize() => this.Serializer.Serialize(ShortInput);

        [Benchmark]
        public byte[] _PrimitiveIntSerialize() => this.Serializer.Serialize(IntInput);

        [Benchmark]
        public byte[] _PrimitiveLongSerialize() => this.Serializer.Serialize(LongInput);

        [Benchmark]
        public byte[] _PrimitiveByteSerialize() => this.Serializer.Serialize(ByteInput);

        [Benchmark]
        public byte[] _PrimitiveUShortSerialize() => this.Serializer.Serialize(UShortInput);

        [Benchmark]
        public byte[] _PrimitiveUIntSerialize() => this.Serializer.Serialize(UIntInput);

        [Benchmark]
        public byte[] _PrimitiveULongSerialize() => this.Serializer.Serialize(ULongInput);

        [Benchmark]
        public byte[] _PrimitiveBoolSerialize() => this.Serializer.Serialize(BoolInput);

        [Benchmark]
        public byte[] _PrimitiveStringSerialize() => this.Serializer.SerializeString(StringInput);

        [Benchmark]
        public byte[] _PrimitiveCharSerialize() => this.Serializer.Serialize(CharInput);

        [Benchmark]
        public byte[] _PrimitiveDateTimeSerialize() => this.Serializer.Serialize(DateTimeInput);

        [Benchmark]
        public byte[] AccessTokenSerialize() => this.Serializer.SerializeAccessToken(AccessTokenInput);

        [Benchmark]
        public byte[] AccountMergeSerialize() => this.Serializer.SerializeAccountMerge(AccountMergeInput);

        [Benchmark]
        public byte[] AnswerSerialize() => this.Serializer.SerializeAnswer(AnswerInput);

        [Benchmark]
        public byte[] BadgeSerialize() => this.Serializer.SerializeBadge(BadgeInput);

        [Benchmark]
        public byte[] CommentSerialize() => this.Serializer.SerializeComment(CommentInput);

        [Benchmark]
        public byte[] NestedDataSerialize() => this.Serializer.SerializeNestedData(NestedDataInput);

        // Deserialize

        [Benchmark]
        public SByte _PrimitiveSByteDeserialize() => this.Serializer.Deserialize<SByte>(this._sByteOutput);

        [Benchmark]
        public short _PrimitiveShortDeserialize() => this.Serializer.Deserialize<short>(this._shortOutput);

        [Benchmark]
        public Int32 _PrimitiveIntDeserialize() => this.Serializer.Deserialize<Int32>(this._intOutput);

        [Benchmark]
        public Int64 _PrimitiveLongDeserialize() => this.Serializer.Deserialize<Int64>(this._longOutput);

        [Benchmark]
        public Byte _PrimitiveByteDeserialize() => this.Serializer.Deserialize<Byte>(this._byteOutput);

        [Benchmark]
        public ushort _PrimitiveUShortDeserialize() => this.Serializer.Deserialize<ushort>(this._uShortOutput);

        [Benchmark]
        public uint _PrimitiveUIntDeserialize() => this.Serializer.Deserialize<uint>(this._uIntOutput);

        [Benchmark]
        public ulong _PrimitiveULongDeserialize() => this.Serializer.Deserialize<ulong>(this._uLongOutput);

        [Benchmark]
        public bool _PrimitiveBoolDeserialize() => this.Serializer.Deserialize<bool>(this._boolOutput);

        [Benchmark]
        public String _PrimitiveStringDeserialize() => this.Serializer.DeserializeString(this._stringOutput);

        [Benchmark]
        public Char _PrimitiveCharDeserialize() => this.Serializer.Deserialize<Char>(this._charOutput);

        [Benchmark]
        public DateTime _PrimitiveDateTimeDeserialize() => this.Serializer.Deserialize<DateTime>(this._dateTimeOutput);

        [Benchmark]
        public AccessToken AccessTokenDeserialize() => this.Serializer.DeserializeAccessToken(this._accessTokenOutput);

        [Benchmark]
        public AccountMerge AccountMergeDeserialize() =>
            this.Serializer.DeserializeAccountMerge(this._accountMergeOutput);

        [Benchmark]
        public Answer AnswerDeserialize() => this.Serializer.DeserializeAnswer(this._answerOutput);

        [Benchmark]
        public Badge BadgeDeserialize() => this.Serializer.DeserializeBadge(this._badgeOutput);

        [Benchmark]
        public Comment CommentDeserialize() => this.Serializer.DeserializeComment(this._commentOutput);

        [Benchmark]
        public NestedData NestedDataDeserialize() => this.Serializer.DeserializeNestedData(this._nestedDataOutput);
    }
}