using System;
using System.Buffers;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;

#nullable disable
namespace Nino.Benchmark.Workloads;

// Workloads/WorkloadTest.cs ported to Nino 3.3.13 (NuGet), for the v3 build of this project (NINO_BENCH_V3=true):
// the same twelve workloads, models (Workloads/*Models.cs, their v4-only attributes behind #if NINO_BENCH_V3), data,
// method names and job. Only the Nino calls differ, as in V3/SimpleTest.cs (NinoGen.Serializer/NinoGen.Deserializer,
// ArrayBufferWriter<byte>), and the cases of protobuf-net, Google.Protobuf and System.Text.Json are left out (the v3
// run times Nino and MemoryPack). Each feature is v3's own:
//  - RpcCall, StateSnapshot, LockstepFrames: interfaces as [NinoType] (v3's polymorphism writes a type id per
//    object); RpcCall's Ok<List<Item>> and EventStream's Envelope<TEvent>: v3's generics.
//  - ProfileCache, IdleSave (bytes the previous release wrote): v3's version tolerance, WEAK_VERSION_TOLERANCE, in
//    the project of their own (V3/Versioned) whose serializers they call (V3Versioned.NinoGen), since the symbol
//    holds for a whole project. v3 can only append members: ProfileV2 keeps the removed AvatarUrl's slot
//    (Workloads/VersionedModels.cs).
//  - WorldRegion, CardBattleLog: v3 has no reference tracking, so a shared object is written once per reference and
//    read back as equal copies, the workaround MessagePack and protobuf use (Workloads/Common.cs).
//  - StateSnapshot, IdleSave in place: v3 has no overload that reads into an existing [NinoType] object (its
//    generator writes Deserialize(out T value, ...) only), so it reads a new one, as MessagePack does.

/// <summary>WorkloadTest's twelve payloads (Workloads/WorkloadTest.cs) serialized and deserialized by Nino v3.</summary>
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class WorkloadTest
{
    private static readonly MediaContent MediaContentValue = MediaContent.Create(new Random(1));
    private static readonly StackOverflowPage StackOverflowPageValue = StackOverflowPage.Create(new Random(2));
    private static readonly RpcCall RpcCallValue = RpcCall.Create(new Random(3));
    private static readonly ProfileV2 ProfileCacheValue = ProfileV2.From(ProfileV1.Create(new Random(4)));
    private static readonly ProfileV1 ProfileCacheOld = ProfileV1.Create(new Random(4));
    private static readonly EventStream EventStreamValue = EventStream.Create(new Random(5));
    private static readonly LogBatch LogBatchValue = LogBatch.Create(new Random(6));
    private static readonly StateSnapshot StateSnapshotValue = StateSnapshot.Create(new Random(7));
    private static readonly LockstepFrames LockstepFramesValue = LockstepFrames.Create(new Random(8));
    private static readonly AoiUpdate AoiUpdateValue = AoiUpdate.Create(new Random(9));
    private static readonly WorldRegion WorldRegionValue = WorldRegion.Create(new Random(10));
    private static readonly CardBattleLog CardBattleLogValue = CardBattleLog.Create(new Random(11));
    private static readonly IdleSaveV2 IdleSaveValue = IdleSaveV2.From(IdleSaveV1.Create(new Random(12)));
    private static readonly IdleSaveV1 IdleSaveOld = IdleSaveV1.Create(new Random(12));

    private static readonly byte[] NinoMediaContentPayload = NinoGen.Serializer.Serialize(MediaContentValue);
    private static readonly byte[] MemoryPackMediaContentPayload = MemoryPackSerializer.Serialize(MediaContentValue);
    private static readonly byte[] MessagePackMediaContentPayload = MessagePackSerializer.Serialize(MediaContentValue);
    private static readonly byte[] NinoStackOverflowPagePayload = NinoGen.Serializer.Serialize(StackOverflowPageValue);
    private static readonly byte[] MemoryPackStackOverflowPagePayload = MemoryPackSerializer.Serialize(StackOverflowPageValue);
    private static readonly byte[] MessagePackStackOverflowPagePayload = MessagePackSerializer.Serialize(StackOverflowPageValue);
    private static readonly byte[] NinoRpcCallPayload = NinoGen.Serializer.Serialize(RpcCallValue);
    private static readonly byte[] MemoryPackRpcCallPayload = MemoryPackSerializer.Serialize(RpcCallValue);
    private static readonly byte[] MessagePackRpcCallPayload = MessagePackSerializer.Serialize(RpcCallValue);
    private static readonly byte[] NinoProfileCachePayload = V3Versioned.NinoGen.Serializer.Serialize(ProfileCacheValue);
    private static readonly byte[] MemoryPackProfileCachePayload = MemoryPackSerializer.Serialize(ProfileCacheValue);
    private static readonly byte[] MessagePackProfileCachePayload = MessagePackSerializer.Serialize(ProfileCacheValue);
    private static readonly byte[] NinoProfileCacheOldBytes = V3Versioned.NinoGen.Serializer.Serialize(ProfileCacheOld);
    private static readonly byte[] MemoryPackProfileCacheOldBytes = MemoryPackSerializer.Serialize(ProfileCacheOld);
    private static readonly byte[] MessagePackProfileCacheOldBytes = MessagePackSerializer.Serialize(ProfileCacheOld);
    private static readonly byte[] NinoEventStreamPayload = NinoGen.Serializer.Serialize(EventStreamValue);
    private static readonly byte[] MemoryPackEventStreamPayload = MemoryPackSerializer.Serialize(EventStreamValue);
    private static readonly byte[] MessagePackEventStreamPayload = MessagePackSerializer.Serialize(EventStreamValue);
    private static readonly byte[] NinoLogBatchPayload = NinoGen.Serializer.Serialize(LogBatchValue);
    private static readonly byte[] MemoryPackLogBatchPayload = MemoryPackSerializer.Serialize(LogBatchValue);
    private static readonly byte[] MessagePackLogBatchPayload = MessagePackSerializer.Serialize(LogBatchValue);
    private static readonly byte[] NinoStateSnapshotPayload = NinoGen.Serializer.Serialize(StateSnapshotValue);
    private static readonly byte[] MemoryPackStateSnapshotPayload = MemoryPackSerializer.Serialize(StateSnapshotValue);
    private static readonly byte[] MessagePackStateSnapshotPayload = MessagePackSerializer.Serialize(StateSnapshotValue);
    private static readonly byte[] NinoLockstepFramesPayload = NinoGen.Serializer.Serialize(LockstepFramesValue);
    private static readonly byte[] MemoryPackLockstepFramesPayload = MemoryPackSerializer.Serialize(LockstepFramesValue);
    private static readonly byte[] MessagePackLockstepFramesPayload = MessagePackSerializer.Serialize(LockstepFramesValue);
    private static readonly byte[] NinoAoiUpdatePayload = NinoGen.Serializer.Serialize(AoiUpdateValue);
    private static readonly byte[] MemoryPackAoiUpdatePayload = MemoryPackSerializer.Serialize(AoiUpdateValue);
    private static readonly byte[] MessagePackAoiUpdatePayload = MessagePackSerializer.Serialize(AoiUpdateValue);
    private static readonly byte[] NinoWorldRegionPayload = NinoGen.Serializer.Serialize(WorldRegionValue);
    private static readonly byte[] MemoryPackWorldRegionPayload = MemoryPackSerializer.Serialize(WorldRegionValue);
    private static readonly byte[] MessagePackWorldRegionPayload = MessagePackSerializer.Serialize(WorldRegionValue);
    private static readonly byte[] NinoCardBattleLogPayload = NinoGen.Serializer.Serialize(CardBattleLogValue);
    private static readonly byte[] MemoryPackCardBattleLogPayload = MemoryPackSerializer.Serialize(CardBattleLogValue);
    private static readonly byte[] MessagePackCardBattleLogPayload = MessagePackSerializer.Serialize(CardBattleLogValue);
    private static readonly byte[] NinoIdleSavePayload = V3Versioned.NinoGen.Serializer.Serialize(IdleSaveValue);
    private static readonly byte[] MemoryPackIdleSavePayload = MemoryPackSerializer.Serialize(IdleSaveValue);
    private static readonly byte[] MessagePackIdleSavePayload = MessagePackSerializer.Serialize(IdleSaveValue);
    private static readonly byte[] NinoIdleSaveOldBytes = V3Versioned.NinoGen.Serializer.Serialize(IdleSaveOld);
    private static readonly byte[] MemoryPackIdleSaveOldBytes = MemoryPackSerializer.Serialize(IdleSaveOld);
    private static readonly byte[] MessagePackIdleSaveOldBytes = MessagePackSerializer.Serialize(IdleSaveOld);

    static WorkloadTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeMediaContent), NinoMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeMediaContent), MemoryPackMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeMediaContent), MessagePackMediaContentPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeStackOverflowPage), NinoStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeStackOverflowPage), MemoryPackStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeStackOverflowPage), MessagePackStackOverflowPagePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeRpcCall), NinoRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeRpcCall), MemoryPackRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeRpcCall), MessagePackRpcCallPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeProfileCache), NinoProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeProfileCache), MemoryPackProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeProfileCache), MessagePackProfileCachePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeEventStream), NinoEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeEventStream), MemoryPackEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeEventStream), MessagePackEventStreamPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeLogBatch), NinoLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeLogBatch), MemoryPackLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeLogBatch), MessagePackLogBatchPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeStateSnapshot), NinoStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeStateSnapshot), MemoryPackStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeStateSnapshot), MessagePackStateSnapshotPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeLockstepFrames), NinoLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeLockstepFrames), MemoryPackLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeLockstepFrames), MessagePackLockstepFramesPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeAoiUpdate), NinoAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeAoiUpdate), MemoryPackAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeAoiUpdate), MessagePackAoiUpdatePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeWorldRegion), NinoWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeWorldRegion), MemoryPackWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeWorldRegion), MessagePackWorldRegionPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeCardBattleLog), NinoCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeCardBattleLog), MemoryPackCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeCardBattleLog), MessagePackCardBattleLogPayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeIdleSave), NinoIdleSavePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeIdleSave), MemoryPackIdleSavePayload.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeIdleSave), MessagePackIdleSavePayload.Length);
    }

    // v3 3.3.13 has no NinoArrayBufferWriter; its IBufferWriter<byte> overload is the reused-buffer path.
    private ArrayBufferWriter<byte> _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private StateSnapshot _memoryPackStateSnapshot;
    private IdleSaveV2 _memoryPackIdleSave;

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _messagePackBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _memoryPackStateSnapshot = MemoryPackSerializer.Deserialize<StateSnapshot>(MemoryPackStateSnapshotPayload);
        _memoryPackIdleSave = MemoryPackSerializer.Deserialize<IdleSaveV2>(MemoryPackIdleSaveOldBytes);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("MediaContentSerialize")]
    public int NinoSerializeMediaContent()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(MediaContentValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("MediaContentDeserialize")]
    public MediaContent NinoDeserializeMediaContent()
    {
        NinoGen.Deserializer.Deserialize(NinoMediaContentPayload, out MediaContent ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("MediaContentDeserialize")]
    public MediaContent MemoryPackDeserializeMediaContent() => MemoryPackSerializer.Deserialize<MediaContent>(MemoryPackMediaContentPayload);

    [Benchmark, BenchmarkCategory("MediaContentDeserialize")]
    public MediaContent MessagePackDeserializeMediaContent() => MessagePackSerializer.Deserialize<MediaContent>(MessagePackMediaContentPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("StackOverflowPageSerialize")]
    public int NinoSerializeStackOverflowPage()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(StackOverflowPageValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("StackOverflowPageDeserialize")]
    public StackOverflowPage NinoDeserializeStackOverflowPage()
    {
        NinoGen.Deserializer.Deserialize(NinoStackOverflowPagePayload, out StackOverflowPage ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("StackOverflowPageDeserialize")]
    public StackOverflowPage MemoryPackDeserializeStackOverflowPage() => MemoryPackSerializer.Deserialize<StackOverflowPage>(MemoryPackStackOverflowPagePayload);

    [Benchmark, BenchmarkCategory("StackOverflowPageDeserialize")]
    public StackOverflowPage MessagePackDeserializeStackOverflowPage() => MessagePackSerializer.Deserialize<StackOverflowPage>(MessagePackStackOverflowPagePayload);

    [Benchmark(Baseline = true), BenchmarkCategory("RpcCallSerialize")]
    public int NinoSerializeRpcCall()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(RpcCallValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("RpcCallDeserialize")]
    public RpcCall NinoDeserializeRpcCall()
    {
        NinoGen.Deserializer.Deserialize(NinoRpcCallPayload, out RpcCall ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("RpcCallDeserialize")]
    public RpcCall MemoryPackDeserializeRpcCall() => MemoryPackSerializer.Deserialize<RpcCall>(MemoryPackRpcCallPayload);

    [Benchmark, BenchmarkCategory("RpcCallDeserialize")]
    public RpcCall MessagePackDeserializeRpcCall() => MessagePackSerializer.Deserialize<RpcCall>(MessagePackRpcCallPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("ProfileCacheSerialize")]
    public int NinoSerializeProfileCache()
    {
        _ninoBuffer.ResetWrittenCount();
        V3Versioned.NinoGen.Serializer.Serialize(ProfileCacheValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("ProfileCacheDeserialize")]
    public ProfileV2 NinoDeserializeProfileCache()
    {
        V3Versioned.NinoGen.Deserializer.Deserialize(NinoProfileCacheOldBytes, out ProfileV2 ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("ProfileCacheDeserialize")]
    public ProfileV2 MemoryPackDeserializeProfileCache() => MemoryPackSerializer.Deserialize<ProfileV2>(MemoryPackProfileCacheOldBytes);

    [Benchmark, BenchmarkCategory("ProfileCacheDeserialize")]
    public ProfileV2 MessagePackDeserializeProfileCache() => MessagePackSerializer.Deserialize<ProfileV2>(MessagePackProfileCacheOldBytes);

    [Benchmark(Baseline = true), BenchmarkCategory("EventStreamSerialize")]
    public int NinoSerializeEventStream()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(EventStreamValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("EventStreamDeserialize")]
    public EventStream NinoDeserializeEventStream()
    {
        NinoGen.Deserializer.Deserialize(NinoEventStreamPayload, out EventStream ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventStream MemoryPackDeserializeEventStream() => MemoryPackSerializer.Deserialize<EventStream>(MemoryPackEventStreamPayload);

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventStream MessagePackDeserializeEventStream() => MessagePackSerializer.Deserialize<EventStream>(MessagePackEventStreamPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("LogBatchSerialize")]
    public int NinoSerializeLogBatch()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(LogBatchValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch NinoDeserializeLogBatch()
    {
        NinoGen.Deserializer.Deserialize(NinoLogBatchPayload, out LogBatch ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch MemoryPackDeserializeLogBatch() => MemoryPackSerializer.Deserialize<LogBatch>(MemoryPackLogBatchPayload);

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch MessagePackDeserializeLogBatch() => MessagePackSerializer.Deserialize<LogBatch>(MessagePackLogBatchPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("StateSnapshotSerialize")]
    public int NinoSerializeStateSnapshot()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(StateSnapshotValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("StateSnapshotDeserialize")]
    public StateSnapshot NinoDeserializeStateSnapshot()
    {
        NinoGen.Deserializer.Deserialize(NinoStateSnapshotPayload, out StateSnapshot ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotDeserialize")]
    public StateSnapshot MemoryPackDeserializeStateSnapshot()
    {
        MemoryPackSerializer.Deserialize(MemoryPackStateSnapshotPayload, ref _memoryPackStateSnapshot);
        return _memoryPackStateSnapshot;
    }

    [Benchmark, BenchmarkCategory("StateSnapshotDeserialize")]
    public StateSnapshot MessagePackDeserializeStateSnapshot() => MessagePackSerializer.Deserialize<StateSnapshot>(MessagePackStateSnapshotPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("LockstepFramesSerialize")]
    public int NinoSerializeLockstepFrames()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(LockstepFramesValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("LockstepFramesDeserialize")]
    public LockstepFrames NinoDeserializeLockstepFrames()
    {
        NinoGen.Deserializer.Deserialize(NinoLockstepFramesPayload, out LockstepFrames ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("LockstepFramesDeserialize")]
    public LockstepFrames MemoryPackDeserializeLockstepFrames() => MemoryPackSerializer.Deserialize<LockstepFrames>(MemoryPackLockstepFramesPayload);

    [Benchmark, BenchmarkCategory("LockstepFramesDeserialize")]
    public LockstepFrames MessagePackDeserializeLockstepFrames() => MessagePackSerializer.Deserialize<LockstepFrames>(MessagePackLockstepFramesPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("AoiUpdateSerialize")]
    public int NinoSerializeAoiUpdate()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(AoiUpdateValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("AoiUpdateDeserialize")]
    public AoiUpdate NinoDeserializeAoiUpdate()
    {
        NinoGen.Deserializer.Deserialize(NinoAoiUpdatePayload, out AoiUpdate ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("AoiUpdateDeserialize")]
    public AoiUpdate MemoryPackDeserializeAoiUpdate() => MemoryPackSerializer.Deserialize<AoiUpdate>(MemoryPackAoiUpdatePayload);

    [Benchmark, BenchmarkCategory("AoiUpdateDeserialize")]
    public AoiUpdate MessagePackDeserializeAoiUpdate() => MessagePackSerializer.Deserialize<AoiUpdate>(MessagePackAoiUpdatePayload);

    [Benchmark(Baseline = true), BenchmarkCategory("WorldRegionSerialize")]
    public int NinoSerializeWorldRegion()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(WorldRegionValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("WorldRegionDeserialize")]
    public WorldRegion NinoDeserializeWorldRegion()
    {
        NinoGen.Deserializer.Deserialize(NinoWorldRegionPayload, out WorldRegion ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("WorldRegionDeserialize")]
    public WorldRegion MemoryPackDeserializeWorldRegion() => MemoryPackSerializer.Deserialize<WorldRegion>(MemoryPackWorldRegionPayload);

    [Benchmark, BenchmarkCategory("WorldRegionDeserialize")]
    public WorldRegion MessagePackDeserializeWorldRegion() => MessagePackSerializer.Deserialize<WorldRegion>(MessagePackWorldRegionPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("CardBattleLogSerialize")]
    public int NinoSerializeCardBattleLog()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoGen.Serializer.Serialize(CardBattleLogValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("CardBattleLogDeserialize")]
    public CardBattleLog NinoDeserializeCardBattleLog()
    {
        NinoGen.Deserializer.Deserialize(NinoCardBattleLogPayload, out CardBattleLog ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("CardBattleLogDeserialize")]
    public CardBattleLog MemoryPackDeserializeCardBattleLog() => MemoryPackSerializer.Deserialize<CardBattleLog>(MemoryPackCardBattleLogPayload);

    [Benchmark, BenchmarkCategory("CardBattleLogDeserialize")]
    public CardBattleLog MessagePackDeserializeCardBattleLog() => MessagePackSerializer.Deserialize<CardBattleLog>(MessagePackCardBattleLogPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("IdleSaveSerialize")]
    public int NinoSerializeIdleSave()
    {
        _ninoBuffer.ResetWrittenCount();
        V3Versioned.NinoGen.Serializer.Serialize(IdleSaveValue, _ninoBuffer);
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

    [Benchmark(Baseline = true), BenchmarkCategory("IdleSaveDeserialize")]
    public IdleSaveV2 NinoDeserializeIdleSave()
    {
        V3Versioned.NinoGen.Deserializer.Deserialize(NinoIdleSaveOldBytes, out IdleSaveV2 ret);
        return ret;
    }

    [Benchmark, BenchmarkCategory("IdleSaveDeserialize")]
    public IdleSaveV2 MemoryPackDeserializeIdleSave()
    {
        MemoryPackSerializer.Deserialize(MemoryPackIdleSaveOldBytes, ref _memoryPackIdleSave);
        return _memoryPackIdleSave;
    }

    [Benchmark, BenchmarkCategory("IdleSaveDeserialize")]
    public IdleSaveV2 MessagePackDeserializeIdleSave() => MessagePackSerializer.Deserialize<IdleSaveV2>(MessagePackIdleSaveOldBytes);
}
