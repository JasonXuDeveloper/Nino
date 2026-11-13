using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Configs;
using Google.Protobuf;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Workloads.Proto;

#nullable disable
namespace Nino.Benchmark.Workloads;

/// <summary>
/// Twelve payloads shaped like real workloads (Common.cs, StandardModels.cs, ServiceModels.cs, GameModels.cs), each
/// serialized to a reused buffer and deserialized, by Nino, MemoryPack, MessagePack, protobuf-net, Google.Protobuf
/// (its own messages, workloads.proto, made from protobuf-net's bytes of the same values) and System.Text.Json
/// (source generated, UTF-8). The versioned workloads write the current model and read bytes the previous release
/// wrote; the in-place ones read into the instance a program holds where the library can (Nino, MemoryPack) and into
/// a new one where it cannot.
/// </summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class WorkloadTest : IReadBack
{
    private static readonly MediaContent MediaContentValue = MediaContent.Create(new Random(1));
    private static readonly P.MediaContent MediaContentProto = P.MediaContent.Parser.ParseFrom(ProtobufModel.Serialize(MediaContentValue));
    private static readonly StackOverflowPage StackOverflowPageValue = StackOverflowPage.Create(new Random(2));
    private static readonly P.StackOverflowPage StackOverflowPageProto = P.StackOverflowPage.Parser.ParseFrom(ProtobufModel.Serialize(StackOverflowPageValue));
    private static readonly RpcCall RpcCallValue = RpcCall.Create(new Random(3));
    private static readonly P.RpcCall RpcCallProto = P.RpcCall.Parser.ParseFrom(ProtobufModel.Serialize(RpcCallValue));
    private static readonly ProfileV2 ProfileCacheValue = ProfileV2.From(ProfileV1.Create(new Random(4)));
    private static readonly ProfileV1 ProfileCacheOld = ProfileV1.Create(new Random(4));
    private static readonly P.ProfileV2 ProfileCacheProto = P.ProfileV2.Parser.ParseFrom(ProtobufModel.Serialize(ProfileCacheValue));
    private static readonly EventStream EventStreamValue = EventStream.Create(new Random(5));
    private static readonly P.EventStream EventStreamProto = P.EventStream.Parser.ParseFrom(ProtobufModel.Serialize(EventStreamValue));
    private static readonly LogBatch LogBatchValue = LogBatch.Create(new Random(6));
    private static readonly P.LogBatch LogBatchProto = P.LogBatch.Parser.ParseFrom(ProtobufModel.Serialize(LogBatchValue));
    private static readonly StateSnapshot StateSnapshotValue = StateSnapshot.Create(new Random(7));
    private static readonly P.StateSnapshot StateSnapshotProto = P.StateSnapshot.Parser.ParseFrom(ProtobufModel.Serialize(StateSnapshotValue));
    private static readonly LockstepFrames LockstepFramesValue = LockstepFrames.Create(new Random(8));
    private static readonly P.LockstepFrames LockstepFramesProto = P.LockstepFrames.Parser.ParseFrom(ProtobufModel.Serialize(LockstepFramesValue));
    private static readonly AoiUpdate AoiUpdateValue = AoiUpdate.Create(new Random(9));
    private static readonly P.AoiUpdate AoiUpdateProto = P.AoiUpdate.Parser.ParseFrom(ProtobufModel.Serialize(AoiUpdateValue));
    private static readonly WorldRegion WorldRegionValue = WorldRegion.Create(new Random(10));
    private static readonly P.WorldRegion WorldRegionProto = P.WorldRegion.Parser.ParseFrom(ProtobufModel.Serialize(WorldRegionValue));
    private static readonly CardBattleLog CardBattleLogValue = CardBattleLog.Create(new Random(11));
    private static readonly P.CardBattleLog CardBattleLogProto = P.CardBattleLog.Parser.ParseFrom(ProtobufModel.Serialize(CardBattleLogValue));
    private static readonly IdleSaveV2 IdleSaveValue = IdleSaveV2.From(IdleSaveV1.Create(new Random(12)));
    private static readonly IdleSaveV1 IdleSaveOld = IdleSaveV1.Create(new Random(12));
    private static readonly P.IdleSaveV2 IdleSaveProto = P.IdleSaveV2.Parser.ParseFrom(ProtobufModel.Serialize(IdleSaveValue));

    private static readonly byte[] NinoMediaContentPayload = NinoSerializer.Serialize(MediaContentValue);
    private static readonly byte[] MemoryPackMediaContentPayload = MemoryPackSerializer.Serialize(MediaContentValue);
    private static readonly byte[] MessagePackMediaContentPayload = MessagePackSerializer.Serialize(MediaContentValue);
    private static readonly byte[] ProtobufMediaContentPayload = ProtobufModel.Serialize(MediaContentValue);
    private static readonly byte[] GoogleProtobufMediaContentPayload = MediaContentProto.ToByteArray();
    private static readonly byte[] JsonMediaContentPayload = JsonSerializer.SerializeToUtf8Bytes(MediaContentValue, WorkloadJson.Default.MediaContent);
    private static readonly byte[] NinoStackOverflowPagePayload = NinoSerializer.Serialize(StackOverflowPageValue);
    private static readonly byte[] MemoryPackStackOverflowPagePayload = MemoryPackSerializer.Serialize(StackOverflowPageValue);
    private static readonly byte[] MessagePackStackOverflowPagePayload = MessagePackSerializer.Serialize(StackOverflowPageValue);
    private static readonly byte[] ProtobufStackOverflowPagePayload = ProtobufModel.Serialize(StackOverflowPageValue);
    private static readonly byte[] GoogleProtobufStackOverflowPagePayload = StackOverflowPageProto.ToByteArray();
    private static readonly byte[] JsonStackOverflowPagePayload = JsonSerializer.SerializeToUtf8Bytes(StackOverflowPageValue, WorkloadJson.Default.StackOverflowPage);
    private static readonly byte[] NinoRpcCallPayload = NinoSerializer.Serialize(RpcCallValue);
    private static readonly byte[] MemoryPackRpcCallPayload = MemoryPackSerializer.Serialize(RpcCallValue);
    private static readonly byte[] MessagePackRpcCallPayload = MessagePackSerializer.Serialize(RpcCallValue);
    private static readonly byte[] ProtobufRpcCallPayload = ProtobufModel.Serialize(RpcCallValue);
    private static readonly byte[] GoogleProtobufRpcCallPayload = RpcCallProto.ToByteArray();
    private static readonly byte[] JsonRpcCallPayload = JsonSerializer.SerializeToUtf8Bytes(RpcCallValue, WorkloadJson.Default.RpcCall);
    private static readonly byte[] NinoProfileCachePayload = NinoSerializer.Serialize(ProfileCacheValue);
    private static readonly byte[] MemoryPackProfileCachePayload = MemoryPackSerializer.Serialize(ProfileCacheValue);
    private static readonly byte[] MessagePackProfileCachePayload = MessagePackSerializer.Serialize(ProfileCacheValue);
    private static readonly byte[] ProtobufProfileCachePayload = ProtobufModel.Serialize(ProfileCacheValue);
    private static readonly byte[] GoogleProtobufProfileCachePayload = ProfileCacheProto.ToByteArray();
    private static readonly byte[] JsonProfileCachePayload = JsonSerializer.SerializeToUtf8Bytes(ProfileCacheValue, WorkloadJson.Default.ProfileV2);
    private static readonly byte[] NinoProfileCacheOldBytes = NinoSerializer.Serialize(ProfileCacheOld);
    private static readonly byte[] MemoryPackProfileCacheOldBytes = MemoryPackSerializer.Serialize(ProfileCacheOld);
    private static readonly byte[] MessagePackProfileCacheOldBytes = MessagePackSerializer.Serialize(ProfileCacheOld);
    private static readonly byte[] ProtobufProfileCacheOldBytes = ProtobufModel.Serialize(ProfileCacheOld);
    private static readonly byte[] GoogleProtobufProfileCacheOldBytes = P.ProfileV1.Parser.ParseFrom(ProtobufModel.Serialize(ProfileCacheOld)).ToByteArray();
    private static readonly byte[] JsonProfileCacheOldBytes = JsonSerializer.SerializeToUtf8Bytes(ProfileCacheOld, WorkloadJson.Default.ProfileV1);
    private static readonly byte[] NinoEventStreamPayload = NinoSerializer.Serialize(EventStreamValue);
    private static readonly byte[] MemoryPackEventStreamPayload = MemoryPackSerializer.Serialize(EventStreamValue);
    private static readonly byte[] MessagePackEventStreamPayload = MessagePackSerializer.Serialize(EventStreamValue);
    private static readonly byte[] ProtobufEventStreamPayload = ProtobufModel.Serialize(EventStreamValue);
    private static readonly byte[] GoogleProtobufEventStreamPayload = EventStreamProto.ToByteArray();
    private static readonly byte[] JsonEventStreamPayload = JsonSerializer.SerializeToUtf8Bytes(EventStreamValue, WorkloadJson.Default.EventStream);
    private static readonly byte[] NinoLogBatchPayload = NinoSerializer.Serialize(LogBatchValue);
    private static readonly byte[] MemoryPackLogBatchPayload = MemoryPackSerializer.Serialize(LogBatchValue);
    private static readonly byte[] MessagePackLogBatchPayload = MessagePackSerializer.Serialize(LogBatchValue);
    private static readonly byte[] ProtobufLogBatchPayload = ProtobufModel.Serialize(LogBatchValue);
    private static readonly byte[] GoogleProtobufLogBatchPayload = LogBatchProto.ToByteArray();
    private static readonly byte[] JsonLogBatchPayload = JsonSerializer.SerializeToUtf8Bytes(LogBatchValue, WorkloadJson.Default.LogBatch);
    private static readonly byte[] NinoStateSnapshotPayload = NinoSerializer.Serialize(StateSnapshotValue);
    private static readonly byte[] MemoryPackStateSnapshotPayload = MemoryPackSerializer.Serialize(StateSnapshotValue);
    private static readonly byte[] MessagePackStateSnapshotPayload = MessagePackSerializer.Serialize(StateSnapshotValue);
    private static readonly byte[] ProtobufStateSnapshotPayload = ProtobufModel.Serialize(StateSnapshotValue);
    private static readonly byte[] GoogleProtobufStateSnapshotPayload = StateSnapshotProto.ToByteArray();
    private static readonly byte[] JsonStateSnapshotPayload = JsonSerializer.SerializeToUtf8Bytes(StateSnapshotValue, WorkloadJson.Default.StateSnapshot);
    private static readonly byte[] NinoLockstepFramesPayload = NinoSerializer.Serialize(LockstepFramesValue);
    private static readonly byte[] MemoryPackLockstepFramesPayload = MemoryPackSerializer.Serialize(LockstepFramesValue);
    private static readonly byte[] MessagePackLockstepFramesPayload = MessagePackSerializer.Serialize(LockstepFramesValue);
    private static readonly byte[] ProtobufLockstepFramesPayload = ProtobufModel.Serialize(LockstepFramesValue);
    private static readonly byte[] GoogleProtobufLockstepFramesPayload = LockstepFramesProto.ToByteArray();
    private static readonly byte[] JsonLockstepFramesPayload = JsonSerializer.SerializeToUtf8Bytes(LockstepFramesValue, WorkloadJson.Default.LockstepFrames);
    private static readonly byte[] NinoAoiUpdatePayload = NinoSerializer.Serialize(AoiUpdateValue);
    private static readonly byte[] MemoryPackAoiUpdatePayload = MemoryPackSerializer.Serialize(AoiUpdateValue);
    private static readonly byte[] MessagePackAoiUpdatePayload = MessagePackSerializer.Serialize(AoiUpdateValue);
    private static readonly byte[] ProtobufAoiUpdatePayload = ProtobufModel.Serialize(AoiUpdateValue);
    private static readonly byte[] GoogleProtobufAoiUpdatePayload = AoiUpdateProto.ToByteArray();
    private static readonly byte[] JsonAoiUpdatePayload = JsonSerializer.SerializeToUtf8Bytes(AoiUpdateValue, WorkloadJson.Default.AoiUpdate);
    private static readonly byte[] NinoWorldRegionPayload = NinoSerializer.Serialize(WorldRegionValue);
    private static readonly byte[] MemoryPackWorldRegionPayload = MemoryPackSerializer.Serialize(WorldRegionValue);
    private static readonly byte[] MessagePackWorldRegionPayload = MessagePackSerializer.Serialize(WorldRegionValue);
    private static readonly byte[] ProtobufWorldRegionPayload = ProtobufModel.Serialize(WorldRegionValue);
    private static readonly byte[] GoogleProtobufWorldRegionPayload = WorldRegionProto.ToByteArray();
    private static readonly byte[] JsonWorldRegionPayload = JsonSerializer.SerializeToUtf8Bytes(WorldRegionValue, WorkloadReferenceJson.Default.WorldRegion);
    private static readonly byte[] NinoCardBattleLogPayload = NinoSerializer.Serialize(CardBattleLogValue);
    private static readonly byte[] MemoryPackCardBattleLogPayload = MemoryPackSerializer.Serialize(CardBattleLogValue);
    private static readonly byte[] MessagePackCardBattleLogPayload = MessagePackSerializer.Serialize(CardBattleLogValue);
    private static readonly byte[] ProtobufCardBattleLogPayload = ProtobufModel.Serialize(CardBattleLogValue);
    private static readonly byte[] GoogleProtobufCardBattleLogPayload = CardBattleLogProto.ToByteArray();
    private static readonly byte[] JsonCardBattleLogPayload = JsonSerializer.SerializeToUtf8Bytes(CardBattleLogValue, WorkloadReferenceJson.Default.CardBattleLog);
    private static readonly byte[] NinoIdleSavePayload = NinoSerializer.Serialize(IdleSaveValue);
    private static readonly byte[] MemoryPackIdleSavePayload = MemoryPackSerializer.Serialize(IdleSaveValue);
    private static readonly byte[] MessagePackIdleSavePayload = MessagePackSerializer.Serialize(IdleSaveValue);
    private static readonly byte[] ProtobufIdleSavePayload = ProtobufModel.Serialize(IdleSaveValue);
    private static readonly byte[] GoogleProtobufIdleSavePayload = IdleSaveProto.ToByteArray();
    private static readonly byte[] JsonIdleSavePayload = JsonSerializer.SerializeToUtf8Bytes(IdleSaveValue, WorkloadJson.Default.IdleSaveV2);
    private static readonly byte[] NinoIdleSaveOldBytes = NinoSerializer.Serialize(IdleSaveOld);
    private static readonly byte[] MemoryPackIdleSaveOldBytes = MemoryPackSerializer.Serialize(IdleSaveOld);
    private static readonly byte[] MessagePackIdleSaveOldBytes = MessagePackSerializer.Serialize(IdleSaveOld);
    private static readonly byte[] ProtobufIdleSaveOldBytes = ProtobufModel.Serialize(IdleSaveOld);
    private static readonly byte[] GoogleProtobufIdleSaveOldBytes = P.IdleSaveV1.Parser.ParseFrom(ProtobufModel.Serialize(IdleSaveOld)).ToByteArray();
    private static readonly byte[] JsonIdleSaveOldBytes = JsonSerializer.SerializeToUtf8Bytes(IdleSaveOld, WorkloadJson.Default.IdleSaveV1);

    static WorkloadTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeMediaContent), NinoMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeMediaContent), MemoryPackMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeMediaContent), MessagePackMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeMediaContent), ProtobufMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeMediaContent), GoogleProtobufMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeMediaContent), JsonMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeStackOverflowPage), NinoStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeStackOverflowPage), MemoryPackStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeStackOverflowPage), MessagePackStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeStackOverflowPage), ProtobufStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeStackOverflowPage), GoogleProtobufStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeStackOverflowPage), JsonStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeRpcCall), NinoRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeRpcCall), MemoryPackRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeRpcCall), MessagePackRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeRpcCall), ProtobufRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeRpcCall), GoogleProtobufRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeRpcCall), JsonRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeProfileCache), NinoProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeProfileCache), MemoryPackProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeProfileCache), MessagePackProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeProfileCache), ProtobufProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeProfileCache), GoogleProtobufProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeProfileCache), JsonProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeEventStream), NinoEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeEventStream), MemoryPackEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeEventStream), MessagePackEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeEventStream), ProtobufEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeEventStream), GoogleProtobufEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeEventStream), JsonEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeLogBatch), NinoLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeLogBatch), MemoryPackLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeLogBatch), MessagePackLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeLogBatch), ProtobufLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeLogBatch), GoogleProtobufLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeLogBatch), JsonLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeStateSnapshot), NinoStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeStateSnapshot), MemoryPackStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeStateSnapshot), MessagePackStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeStateSnapshot), ProtobufStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeStateSnapshot), GoogleProtobufStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeStateSnapshot), JsonStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeLockstepFrames), NinoLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeLockstepFrames), MemoryPackLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeLockstepFrames), MessagePackLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeLockstepFrames), ProtobufLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeLockstepFrames), GoogleProtobufLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeLockstepFrames), JsonLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeAoiUpdate), NinoAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeAoiUpdate), MemoryPackAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeAoiUpdate), MessagePackAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeAoiUpdate), ProtobufAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeAoiUpdate), GoogleProtobufAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeAoiUpdate), JsonAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeWorldRegion), NinoWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeWorldRegion), MemoryPackWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeWorldRegion), MessagePackWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeWorldRegion), ProtobufWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeWorldRegion), GoogleProtobufWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeWorldRegion), JsonWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeCardBattleLog), NinoCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeCardBattleLog), MemoryPackCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeCardBattleLog), MessagePackCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeCardBattleLog), ProtobufCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeCardBattleLog), GoogleProtobufCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeCardBattleLog), JsonCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeIdleSave), NinoIdleSavePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeIdleSave), MemoryPackIdleSavePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeIdleSave), MessagePackIdleSavePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeIdleSave), ProtobufIdleSavePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeIdleSave), GoogleProtobufIdleSavePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeIdleSave), JsonIdleSavePayload.Length);
    }

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private MemoryStream _protobufStream;
    private ArrayBufferWriter<byte> _googleProtobufBuffer;
    private ArrayBufferWriter<byte> _jsonBuffer;
    private Utf8JsonWriter _jsonWriter;
    private StateSnapshot _ninoStateSnapshot, _memoryPackStateSnapshot;
    private IdleSaveV2 _ninoIdleSave, _memoryPackIdleSave;

    [GlobalSetup]
    public void Setup()
    {
        ProtoFile.Workloads.Check();
        _ninoBuffer = new NinoArrayBufferWriter(1 << 20);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _messagePackBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _protobufStream = new MemoryStream(1 << 20);
        _googleProtobufBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _jsonBuffer = new ArrayBufferWriter<byte>(1 << 20);
        // What JsonSerializer's own writers use: no validation of the writer's calls.
        _jsonWriter = new Utf8JsonWriter(_jsonBuffer, new JsonWriterOptions { SkipValidation = true });
        _ninoStateSnapshot = NinoDeserializer.Deserialize<StateSnapshot>(NinoStateSnapshotPayload);
        _memoryPackStateSnapshot = MemoryPackSerializer.Deserialize<StateSnapshot>(MemoryPackStateSnapshotPayload);
        _ninoIdleSave = NinoDeserializer.Deserialize<IdleSaveV2>(NinoIdleSaveOldBytes);
        _memoryPackIdleSave = MemoryPackSerializer.Deserialize<IdleSaveV2>(MemoryPackIdleSaveOldBytes);
    }

    /// <summary>Verify.cs: a Google.Protobuf message read back through protobuf-net as the workload's model.</summary>
    public object ReadBack(object result, Type model) => ProtobufModel.ReadBack(result, model);

    [Benchmark(Baseline = true), BenchmarkCategory("MediaContentSerialize")]
    public int NinoSerializeMediaContent()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(MediaContentValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("MediaContentSerialize")]
    public int MemoryPackSerializeMediaContent()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, MediaContentValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("MediaContentSerialize")]
    public int MessagePackSerializeMediaContent()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, MediaContentValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("MediaContentSerialize")]
    public int ProtobufSerializeMediaContent()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, MediaContentValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("MediaContentSerialize")]
    public int GoogleProtobufSerializeMediaContent()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        MediaContentProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("MediaContentSerialize")]
    public int JsonSerializeMediaContent()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, MediaContentValue, WorkloadJson.Default.MediaContent);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("MediaContentDeserialize")]
    public MediaContent NinoDeserializeMediaContent() => NinoDeserializer.Deserialize<MediaContent>(NinoMediaContentPayload);

    [Benchmark, BenchmarkCategory("MediaContentDeserialize")]
    public MediaContent MemoryPackDeserializeMediaContent() => MemoryPackSerializer.Deserialize<MediaContent>(MemoryPackMediaContentPayload);

    [Benchmark, BenchmarkCategory("MediaContentDeserialize")]
    public MediaContent MessagePackDeserializeMediaContent() => MessagePackSerializer.Deserialize<MediaContent>(MessagePackMediaContentPayload);

    [Benchmark, BenchmarkCategory("MediaContentDeserialize")]
    public MediaContent ProtobufDeserializeMediaContent() => Serializer.Deserialize<MediaContent>(new ReadOnlySpan<byte>(ProtobufMediaContentPayload));

    [Benchmark, BenchmarkCategory("MediaContentDeserialize")]
    public P.MediaContent GoogleProtobufDeserializeMediaContent() => P.MediaContent.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufMediaContentPayload));

    [Benchmark, BenchmarkCategory("MediaContentDeserialize")]
    public MediaContent JsonDeserializeMediaContent() => JsonSerializer.Deserialize(JsonMediaContentPayload, WorkloadJson.Default.MediaContent);

    [Benchmark(Baseline = true), BenchmarkCategory("StackOverflowPageSerialize")]
    public int NinoSerializeStackOverflowPage()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(StackOverflowPageValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StackOverflowPageSerialize")]
    public int MemoryPackSerializeStackOverflowPage()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, StackOverflowPageValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StackOverflowPageSerialize")]
    public int MessagePackSerializeStackOverflowPage()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, StackOverflowPageValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StackOverflowPageSerialize")]
    public int ProtobufSerializeStackOverflowPage()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, StackOverflowPageValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("StackOverflowPageSerialize")]
    public int GoogleProtobufSerializeStackOverflowPage()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        StackOverflowPageProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StackOverflowPageSerialize")]
    public int JsonSerializeStackOverflowPage()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, StackOverflowPageValue, WorkloadJson.Default.StackOverflowPage);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("StackOverflowPageDeserialize")]
    public StackOverflowPage NinoDeserializeStackOverflowPage() => NinoDeserializer.Deserialize<StackOverflowPage>(NinoStackOverflowPagePayload);

    [Benchmark, BenchmarkCategory("StackOverflowPageDeserialize")]
    public StackOverflowPage MemoryPackDeserializeStackOverflowPage() => MemoryPackSerializer.Deserialize<StackOverflowPage>(MemoryPackStackOverflowPagePayload);

    [Benchmark, BenchmarkCategory("StackOverflowPageDeserialize")]
    public StackOverflowPage MessagePackDeserializeStackOverflowPage() => MessagePackSerializer.Deserialize<StackOverflowPage>(MessagePackStackOverflowPagePayload);

    [Benchmark, BenchmarkCategory("StackOverflowPageDeserialize")]
    public StackOverflowPage ProtobufDeserializeStackOverflowPage() => Serializer.Deserialize<StackOverflowPage>(new ReadOnlySpan<byte>(ProtobufStackOverflowPagePayload));

    [Benchmark, BenchmarkCategory("StackOverflowPageDeserialize")]
    public P.StackOverflowPage GoogleProtobufDeserializeStackOverflowPage() => P.StackOverflowPage.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufStackOverflowPagePayload));

    [Benchmark, BenchmarkCategory("StackOverflowPageDeserialize")]
    public StackOverflowPage JsonDeserializeStackOverflowPage() => JsonSerializer.Deserialize(JsonStackOverflowPagePayload, WorkloadJson.Default.StackOverflowPage);

    [Benchmark(Baseline = true), BenchmarkCategory("RpcCallSerialize")]
    public int NinoSerializeRpcCall()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(RpcCallValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RpcCallSerialize")]
    public int MemoryPackSerializeRpcCall()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, RpcCallValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RpcCallSerialize")]
    public int MessagePackSerializeRpcCall()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, RpcCallValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RpcCallSerialize")]
    public int ProtobufSerializeRpcCall()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, RpcCallValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("RpcCallSerialize")]
    public int GoogleProtobufSerializeRpcCall()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        RpcCallProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RpcCallSerialize")]
    public int JsonSerializeRpcCall()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, RpcCallValue, WorkloadJson.Default.RpcCall);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("RpcCallDeserialize")]
    public RpcCall NinoDeserializeRpcCall() => NinoDeserializer.Deserialize<RpcCall>(NinoRpcCallPayload);

    [Benchmark, BenchmarkCategory("RpcCallDeserialize")]
    public RpcCall MemoryPackDeserializeRpcCall() => MemoryPackSerializer.Deserialize<RpcCall>(MemoryPackRpcCallPayload);

    [Benchmark, BenchmarkCategory("RpcCallDeserialize")]
    public RpcCall MessagePackDeserializeRpcCall() => MessagePackSerializer.Deserialize<RpcCall>(MessagePackRpcCallPayload);

    [Benchmark, BenchmarkCategory("RpcCallDeserialize")]
    public RpcCall ProtobufDeserializeRpcCall() => Serializer.Deserialize<RpcCall>(new ReadOnlySpan<byte>(ProtobufRpcCallPayload));

    [Benchmark, BenchmarkCategory("RpcCallDeserialize")]
    public P.RpcCall GoogleProtobufDeserializeRpcCall() => P.RpcCall.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufRpcCallPayload));

    [Benchmark, BenchmarkCategory("RpcCallDeserialize")]
    public RpcCall JsonDeserializeRpcCall() => JsonSerializer.Deserialize(JsonRpcCallPayload, WorkloadJson.Default.RpcCall);

    [Benchmark(Baseline = true), BenchmarkCategory("ProfileCacheSerialize")]
    public int NinoSerializeProfileCache()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(ProfileCacheValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ProfileCacheSerialize")]
    public int MemoryPackSerializeProfileCache()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, ProfileCacheValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ProfileCacheSerialize")]
    public int MessagePackSerializeProfileCache()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, ProfileCacheValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ProfileCacheSerialize")]
    public int ProtobufSerializeProfileCache()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, ProfileCacheValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("ProfileCacheSerialize")]
    public int GoogleProtobufSerializeProfileCache()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ProfileCacheProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("ProfileCacheSerialize")]
    public int JsonSerializeProfileCache()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, ProfileCacheValue, WorkloadJson.Default.ProfileV2);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ProfileCacheDeserialize")]
    public ProfileV2 NinoDeserializeProfileCache() => NinoDeserializer.Deserialize<ProfileV2>(NinoProfileCacheOldBytes);

    [Benchmark, BenchmarkCategory("ProfileCacheDeserialize")]
    public ProfileV2 MemoryPackDeserializeProfileCache() => MemoryPackSerializer.Deserialize<ProfileV2>(MemoryPackProfileCacheOldBytes);

    [Benchmark, BenchmarkCategory("ProfileCacheDeserialize")]
    public ProfileV2 MessagePackDeserializeProfileCache() => MessagePackSerializer.Deserialize<ProfileV2>(MessagePackProfileCacheOldBytes);

    [Benchmark, BenchmarkCategory("ProfileCacheDeserialize")]
    public ProfileV2 ProtobufDeserializeProfileCache() => Serializer.Deserialize<ProfileV2>(new ReadOnlySpan<byte>(ProtobufProfileCacheOldBytes));

    [Benchmark, BenchmarkCategory("ProfileCacheDeserialize")]
    public P.ProfileV2 GoogleProtobufDeserializeProfileCache() => P.ProfileV2.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufProfileCacheOldBytes));

    [Benchmark, BenchmarkCategory("ProfileCacheDeserialize")]
    public ProfileV2 JsonDeserializeProfileCache() => JsonSerializer.Deserialize(JsonProfileCacheOldBytes, WorkloadJson.Default.ProfileV2);

    [Benchmark(Baseline = true), BenchmarkCategory("EventStreamSerialize")]
    public int NinoSerializeEventStream()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(EventStreamValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int MemoryPackSerializeEventStream()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, EventStreamValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int MessagePackSerializeEventStream()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, EventStreamValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int ProtobufSerializeEventStream()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, EventStreamValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int GoogleProtobufSerializeEventStream()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        EventStreamProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int JsonSerializeEventStream()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, EventStreamValue, WorkloadJson.Default.EventStream);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("EventStreamDeserialize")]
    public EventStream NinoDeserializeEventStream() => NinoDeserializer.Deserialize<EventStream>(NinoEventStreamPayload);

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventStream MemoryPackDeserializeEventStream() => MemoryPackSerializer.Deserialize<EventStream>(MemoryPackEventStreamPayload);

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventStream MessagePackDeserializeEventStream() => MessagePackSerializer.Deserialize<EventStream>(MessagePackEventStreamPayload);

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventStream ProtobufDeserializeEventStream() => Serializer.Deserialize<EventStream>(new ReadOnlySpan<byte>(ProtobufEventStreamPayload));

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public P.EventStream GoogleProtobufDeserializeEventStream() => P.EventStream.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufEventStreamPayload));

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventStream JsonDeserializeEventStream() => JsonSerializer.Deserialize(JsonEventStreamPayload, WorkloadJson.Default.EventStream);

    [Benchmark(Baseline = true), BenchmarkCategory("LogBatchSerialize")]
    public int NinoSerializeLogBatch()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(LogBatchValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int MemoryPackSerializeLogBatch()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, LogBatchValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int MessagePackSerializeLogBatch()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, LogBatchValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int ProtobufSerializeLogBatch()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, LogBatchValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int GoogleProtobufSerializeLogBatch()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        LogBatchProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int JsonSerializeLogBatch()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, LogBatchValue, WorkloadJson.Default.LogBatch);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch NinoDeserializeLogBatch() => NinoDeserializer.Deserialize<LogBatch>(NinoLogBatchPayload);

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch MemoryPackDeserializeLogBatch() => MemoryPackSerializer.Deserialize<LogBatch>(MemoryPackLogBatchPayload);

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch MessagePackDeserializeLogBatch() => MessagePackSerializer.Deserialize<LogBatch>(MessagePackLogBatchPayload);

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch ProtobufDeserializeLogBatch() => Serializer.Deserialize<LogBatch>(new ReadOnlySpan<byte>(ProtobufLogBatchPayload));

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public P.LogBatch GoogleProtobufDeserializeLogBatch() => P.LogBatch.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufLogBatchPayload));

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch JsonDeserializeLogBatch() => JsonSerializer.Deserialize(JsonLogBatchPayload, WorkloadJson.Default.LogBatch);

    [Benchmark(Baseline = true), BenchmarkCategory("StateSnapshotSerialize")]
    public int NinoSerializeStateSnapshot()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(StateSnapshotValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotSerialize")]
    public int MemoryPackSerializeStateSnapshot()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, StateSnapshotValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotSerialize")]
    public int MessagePackSerializeStateSnapshot()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, StateSnapshotValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotSerialize")]
    public int ProtobufSerializeStateSnapshot()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, StateSnapshotValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotSerialize")]
    public int GoogleProtobufSerializeStateSnapshot()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        StateSnapshotProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotSerialize")]
    public int JsonSerializeStateSnapshot()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, StateSnapshotValue, WorkloadJson.Default.StateSnapshot);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("StateSnapshotDeserialize")]
    public StateSnapshot NinoDeserializeStateSnapshot()
    {
        NinoDeserializer.Deserialize(NinoStateSnapshotPayload, ref _ninoStateSnapshot);
        return _ninoStateSnapshot;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotDeserialize")]
    public StateSnapshot MemoryPackDeserializeStateSnapshot()
    {
        MemoryPackSerializer.Deserialize(MemoryPackStateSnapshotPayload, ref _memoryPackStateSnapshot);
        return _memoryPackStateSnapshot;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotDeserialize")]
    public StateSnapshot MessagePackDeserializeStateSnapshot() => MessagePackSerializer.Deserialize<StateSnapshot>(MessagePackStateSnapshotPayload);

    [Benchmark, BenchmarkCategory("StateSnapshotDeserialize")]
    public StateSnapshot ProtobufDeserializeStateSnapshot() => Serializer.Deserialize<StateSnapshot>(new ReadOnlySpan<byte>(ProtobufStateSnapshotPayload));

    [Benchmark, BenchmarkCategory("StateSnapshotDeserialize")]
    public P.StateSnapshot GoogleProtobufDeserializeStateSnapshot() => P.StateSnapshot.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufStateSnapshotPayload));

    [Benchmark, BenchmarkCategory("StateSnapshotDeserialize")]
    public StateSnapshot JsonDeserializeStateSnapshot() => JsonSerializer.Deserialize(JsonStateSnapshotPayload, WorkloadJson.Default.StateSnapshot);

    [Benchmark(Baseline = true), BenchmarkCategory("LockstepFramesSerialize")]
    public int NinoSerializeLockstepFrames()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(LockstepFramesValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LockstepFramesSerialize")]
    public int MemoryPackSerializeLockstepFrames()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, LockstepFramesValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LockstepFramesSerialize")]
    public int MessagePackSerializeLockstepFrames()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, LockstepFramesValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LockstepFramesSerialize")]
    public int ProtobufSerializeLockstepFrames()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, LockstepFramesValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("LockstepFramesSerialize")]
    public int GoogleProtobufSerializeLockstepFrames()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        LockstepFramesProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LockstepFramesSerialize")]
    public int JsonSerializeLockstepFrames()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, LockstepFramesValue, WorkloadJson.Default.LockstepFrames);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("LockstepFramesDeserialize")]
    public LockstepFrames NinoDeserializeLockstepFrames() => NinoDeserializer.Deserialize<LockstepFrames>(NinoLockstepFramesPayload);

    [Benchmark, BenchmarkCategory("LockstepFramesDeserialize")]
    public LockstepFrames MemoryPackDeserializeLockstepFrames() => MemoryPackSerializer.Deserialize<LockstepFrames>(MemoryPackLockstepFramesPayload);

    [Benchmark, BenchmarkCategory("LockstepFramesDeserialize")]
    public LockstepFrames MessagePackDeserializeLockstepFrames() => MessagePackSerializer.Deserialize<LockstepFrames>(MessagePackLockstepFramesPayload);

    [Benchmark, BenchmarkCategory("LockstepFramesDeserialize")]
    public LockstepFrames ProtobufDeserializeLockstepFrames() => Serializer.Deserialize<LockstepFrames>(new ReadOnlySpan<byte>(ProtobufLockstepFramesPayload));

    [Benchmark, BenchmarkCategory("LockstepFramesDeserialize")]
    public P.LockstepFrames GoogleProtobufDeserializeLockstepFrames() => P.LockstepFrames.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufLockstepFramesPayload));

    [Benchmark, BenchmarkCategory("LockstepFramesDeserialize")]
    public LockstepFrames JsonDeserializeLockstepFrames() => JsonSerializer.Deserialize(JsonLockstepFramesPayload, WorkloadJson.Default.LockstepFrames);

    [Benchmark(Baseline = true), BenchmarkCategory("AoiUpdateSerialize")]
    public int NinoSerializeAoiUpdate()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(AoiUpdateValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("AoiUpdateSerialize")]
    public int MemoryPackSerializeAoiUpdate()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, AoiUpdateValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("AoiUpdateSerialize")]
    public int MessagePackSerializeAoiUpdate()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, AoiUpdateValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("AoiUpdateSerialize")]
    public int ProtobufSerializeAoiUpdate()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, AoiUpdateValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("AoiUpdateSerialize")]
    public int GoogleProtobufSerializeAoiUpdate()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        AoiUpdateProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("AoiUpdateSerialize")]
    public int JsonSerializeAoiUpdate()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, AoiUpdateValue, WorkloadJson.Default.AoiUpdate);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("AoiUpdateDeserialize")]
    public AoiUpdate NinoDeserializeAoiUpdate() => NinoDeserializer.Deserialize<AoiUpdate>(NinoAoiUpdatePayload);

    [Benchmark, BenchmarkCategory("AoiUpdateDeserialize")]
    public AoiUpdate MemoryPackDeserializeAoiUpdate() => MemoryPackSerializer.Deserialize<AoiUpdate>(MemoryPackAoiUpdatePayload);

    [Benchmark, BenchmarkCategory("AoiUpdateDeserialize")]
    public AoiUpdate MessagePackDeserializeAoiUpdate() => MessagePackSerializer.Deserialize<AoiUpdate>(MessagePackAoiUpdatePayload);

    [Benchmark, BenchmarkCategory("AoiUpdateDeserialize")]
    public AoiUpdate ProtobufDeserializeAoiUpdate() => Serializer.Deserialize<AoiUpdate>(new ReadOnlySpan<byte>(ProtobufAoiUpdatePayload));

    [Benchmark, BenchmarkCategory("AoiUpdateDeserialize")]
    public P.AoiUpdate GoogleProtobufDeserializeAoiUpdate() => P.AoiUpdate.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufAoiUpdatePayload));

    [Benchmark, BenchmarkCategory("AoiUpdateDeserialize")]
    public AoiUpdate JsonDeserializeAoiUpdate() => JsonSerializer.Deserialize(JsonAoiUpdatePayload, WorkloadJson.Default.AoiUpdate);

    [Benchmark(Baseline = true), BenchmarkCategory("WorldRegionSerialize")]
    public int NinoSerializeWorldRegion()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(WorldRegionValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("WorldRegionSerialize")]
    public int MemoryPackSerializeWorldRegion()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, WorldRegionValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("WorldRegionSerialize")]
    public int MessagePackSerializeWorldRegion()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, WorldRegionValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("WorldRegionSerialize")]
    public int ProtobufSerializeWorldRegion()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, WorldRegionValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("WorldRegionSerialize")]
    public int GoogleProtobufSerializeWorldRegion()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        WorldRegionProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("WorldRegionSerialize")]
    public int JsonSerializeWorldRegion()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, WorldRegionValue, WorkloadReferenceJson.Default.WorldRegion);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("WorldRegionDeserialize")]
    public WorldRegion NinoDeserializeWorldRegion() => NinoDeserializer.Deserialize<WorldRegion>(NinoWorldRegionPayload);

    [Benchmark, BenchmarkCategory("WorldRegionDeserialize")]
    public WorldRegion MemoryPackDeserializeWorldRegion() => MemoryPackSerializer.Deserialize<WorldRegion>(MemoryPackWorldRegionPayload);

    [Benchmark, BenchmarkCategory("WorldRegionDeserialize")]
    public WorldRegion MessagePackDeserializeWorldRegion() => MessagePackSerializer.Deserialize<WorldRegion>(MessagePackWorldRegionPayload);

    [Benchmark, BenchmarkCategory("WorldRegionDeserialize")]
    public WorldRegion ProtobufDeserializeWorldRegion() => Serializer.Deserialize<WorldRegion>(new ReadOnlySpan<byte>(ProtobufWorldRegionPayload));

    [Benchmark, BenchmarkCategory("WorldRegionDeserialize")]
    public P.WorldRegion GoogleProtobufDeserializeWorldRegion() => P.WorldRegion.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufWorldRegionPayload));

    [Benchmark, BenchmarkCategory("WorldRegionDeserialize")]
    public WorldRegion JsonDeserializeWorldRegion() => JsonSerializer.Deserialize(JsonWorldRegionPayload, WorkloadReferenceJson.Default.WorldRegion);

    [Benchmark(Baseline = true), BenchmarkCategory("CardBattleLogSerialize")]
    public int NinoSerializeCardBattleLog()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(CardBattleLogValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("CardBattleLogSerialize")]
    public int MemoryPackSerializeCardBattleLog()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, CardBattleLogValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("CardBattleLogSerialize")]
    public int MessagePackSerializeCardBattleLog()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, CardBattleLogValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("CardBattleLogSerialize")]
    public int ProtobufSerializeCardBattleLog()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, CardBattleLogValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("CardBattleLogSerialize")]
    public int GoogleProtobufSerializeCardBattleLog()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        CardBattleLogProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("CardBattleLogSerialize")]
    public int JsonSerializeCardBattleLog()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, CardBattleLogValue, WorkloadReferenceJson.Default.CardBattleLog);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("CardBattleLogDeserialize")]
    public CardBattleLog NinoDeserializeCardBattleLog() => NinoDeserializer.Deserialize<CardBattleLog>(NinoCardBattleLogPayload);

    [Benchmark, BenchmarkCategory("CardBattleLogDeserialize")]
    public CardBattleLog MemoryPackDeserializeCardBattleLog() => MemoryPackSerializer.Deserialize<CardBattleLog>(MemoryPackCardBattleLogPayload);

    [Benchmark, BenchmarkCategory("CardBattleLogDeserialize")]
    public CardBattleLog MessagePackDeserializeCardBattleLog() => MessagePackSerializer.Deserialize<CardBattleLog>(MessagePackCardBattleLogPayload);

    [Benchmark, BenchmarkCategory("CardBattleLogDeserialize")]
    public CardBattleLog ProtobufDeserializeCardBattleLog() => Serializer.Deserialize<CardBattleLog>(new ReadOnlySpan<byte>(ProtobufCardBattleLogPayload));

    [Benchmark, BenchmarkCategory("CardBattleLogDeserialize")]
    public P.CardBattleLog GoogleProtobufDeserializeCardBattleLog() => P.CardBattleLog.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufCardBattleLogPayload));

    [Benchmark, BenchmarkCategory("CardBattleLogDeserialize")]
    public CardBattleLog JsonDeserializeCardBattleLog() => JsonSerializer.Deserialize(JsonCardBattleLogPayload, WorkloadReferenceJson.Default.CardBattleLog);

    [Benchmark(Baseline = true), BenchmarkCategory("IdleSaveSerialize")]
    public int NinoSerializeIdleSave()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(IdleSaveValue, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("IdleSaveSerialize")]
    public int MemoryPackSerializeIdleSave()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, IdleSaveValue);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("IdleSaveSerialize")]
    public int MessagePackSerializeIdleSave()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, IdleSaveValue);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("IdleSaveSerialize")]
    public int ProtobufSerializeIdleSave()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, IdleSaveValue);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("IdleSaveSerialize")]
    public int GoogleProtobufSerializeIdleSave()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        IdleSaveProto.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("IdleSaveSerialize")]
    public int JsonSerializeIdleSave()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, IdleSaveValue, WorkloadJson.Default.IdleSaveV2);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("IdleSaveDeserialize")]
    public IdleSaveV2 NinoDeserializeIdleSave()
    {
        NinoDeserializer.Deserialize(NinoIdleSaveOldBytes, ref _ninoIdleSave);
        return _ninoIdleSave;
    }

    [Benchmark, BenchmarkCategory("IdleSaveDeserialize")]
    public IdleSaveV2 MemoryPackDeserializeIdleSave()
    {
        MemoryPackSerializer.Deserialize(MemoryPackIdleSaveOldBytes, ref _memoryPackIdleSave);
        return _memoryPackIdleSave;
    }

    [Benchmark, BenchmarkCategory("IdleSaveDeserialize")]
    public IdleSaveV2 MessagePackDeserializeIdleSave() => MessagePackSerializer.Deserialize<IdleSaveV2>(MessagePackIdleSaveOldBytes);

    [Benchmark, BenchmarkCategory("IdleSaveDeserialize")]
    public IdleSaveV2 ProtobufDeserializeIdleSave() => Serializer.Deserialize<IdleSaveV2>(new ReadOnlySpan<byte>(ProtobufIdleSaveOldBytes));

    [Benchmark, BenchmarkCategory("IdleSaveDeserialize")]
    public P.IdleSaveV2 GoogleProtobufDeserializeIdleSave() => P.IdleSaveV2.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufIdleSaveOldBytes));

    [Benchmark, BenchmarkCategory("IdleSaveDeserialize")]
    public IdleSaveV2 JsonDeserializeIdleSave() => JsonSerializer.Deserialize(JsonIdleSaveOldBytes, WorkloadJson.Default.IdleSaveV2);
}

/// <summary>System.Text.Json's source-generated metadata of the workloads (fields included: the models have no properties).</summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(MediaContent))]
[JsonSerializable(typeof(StackOverflowPage))]
[JsonSerializable(typeof(RpcCall))]
[JsonSerializable(typeof(ProfileV2))]
[JsonSerializable(typeof(ProfileV1))]
[JsonSerializable(typeof(EventStream))]
[JsonSerializable(typeof(LogBatch))]
[JsonSerializable(typeof(StateSnapshot))]
[JsonSerializable(typeof(LockstepFrames))]
[JsonSerializable(typeof(AoiUpdate))]
[JsonSerializable(typeof(IdleSaveV2))]
[JsonSerializable(typeof(IdleSaveV1))]
public partial class WorkloadJson : JsonSerializerContext;

/// <summary>The workloads that share objects, with System.Text.Json's reference handling ($id / $ref).</summary>
[JsonSourceGenerationOptions(IncludeFields = true, ReferenceHandler = JsonKnownReferenceHandler.Preserve)]
[JsonSerializable(typeof(WorldRegion))]
[JsonSerializable(typeof(CardBattleLog))]
public partial class WorkloadReferenceJson : JsonSerializerContext;
