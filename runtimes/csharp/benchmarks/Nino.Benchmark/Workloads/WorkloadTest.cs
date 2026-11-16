using System;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Configs;
#if !NINO_BENCH_V3
using P = Nino.Benchmark.Workloads.Proto;
#endif

#nullable disable
namespace Nino.Benchmark.Workloads;

/// <summary>
/// Twelve payloads shaped like real workloads (Common.cs, StandardModels.cs, ServiceModels.cs, GameModels.cs), each
/// serialized to a reused buffer and deserialized, by Nino, MemoryPack, MessagePack, protobuf-net, Google.Protobuf
/// (its own messages, workloads.proto, made from protobuf-net's bytes of the same values) and System.Text.Json
/// (source generated, UTF-8). The versioned workloads write the current model and read bytes the previous release
/// wrote; the in-place ones read into the instance a program holds where the library can (Nino, MemoryPack) and into
/// a new one where it cannot. The timed methods are WorkloadTest.Cases.g.cs, generated from the cases below and the
/// adapters (Adapters/, runtimes/csharp/tools/BenchGen); the v3 build times Nino 3.3.13 and MemoryPack on the same
/// values, through its own features: v3's polymorphism and generics, its version tolerance in the project of its own
/// (V3/Versioned, whose serializers the versioned workloads call), copies of shared objects (no reference tracking),
/// and a new object where v4 reads in place (no overload reads into an existing [NinoType] object).
/// </summary>
[BenchClass(Buffer = "1 << 20", Value = "{Case}Value", Bytes = "{Library}{Case}Payload", Order = "case", JsonContext = "WorkloadJson")]
[BenchCase("MediaContent", Quick = true, Proto = "MediaContent", Json = "MediaContent")]
[BenchCase("StackOverflowPage", Quick = true, Broken = "LuminPack compile: its generator declares element twice for a list of objects inside an element of a list of objects (CS0136)", Proto = "StackOverflowPage", Json = "StackOverflowPage")]
[BenchCase("RpcCall", Broken = "LuminPack compile: a generic type as a union member of a non-generic base (LuminPack015)", Proto = "RpcCall", Json = "RpcCall")]
[BenchCase("ProfileCache", Broken = "LuminPack throws: its version-tolerant reader indexes past the members the previous version wrote", Proto = "ProfileV2", Json = "ProfileV2",
    Old = "ProfileCacheOld", OldProto = "ProfileV1", OldJson = "ProfileV1", NinoGen = "V3Versioned.NinoGen")]
[BenchCase("EventStream", Broken = "LuminPack compile: a generic type as a union member of a non-generic base (LuminPack015)", Proto = "EventStream", Json = "EventStream")]
[BenchCase("LogBatch", Quick = true, Broken = "LuminPack throws: reading its own bytes, length larger than buffer", Proto = "LogBatch", Json = "LogBatch")]
[BenchCase("StateSnapshot", Quick = true, Broken = "LuminPack throws: reading its own bytes, tag 10 not found in IComponent", Proto = "StateSnapshot", Json = "StateSnapshot", InPlace = true)]
[BenchCase("LockstepFrames", Broken = "LuminPack compile: its generator declares element twice for a list of objects inside an element of a list of objects (CS0136)", Proto = "LockstepFrames", Json = "LockstepFrames")]
[BenchCase("AoiUpdate", Broken = "LuminPack throws: reading its own bytes, invalid collection length", Proto = "AoiUpdate", Json = "AoiUpdate")]
[BenchCase("WorldRegion", Broken = "LuminPack throws: reading its own bytes, invalid collection length", Proto = "WorldRegion", Json = "WorldRegion", JsonContext = "WorkloadReferenceJson")]
[BenchCase("CardBattleLog", Broken = "LuminPack compile: its generator declares element twice for a list of objects inside an element of a list of objects (CS0136)", Proto = "CardBattleLog", Json = "CardBattleLog", JsonContext = "WorkloadReferenceJson")]
[BenchCase("IdleSave", Broken = "LuminPack throws: its version-tolerant reader indexes past the members the previous version wrote", Proto = "IdleSaveV2", Json = "IdleSaveV2",
    Old = "IdleSaveOld", OldProto = "IdleSaveV1", OldJson = "IdleSaveV1", InPlace = true, NinoGen = "V3Versioned.NinoGen")]
[PayloadColumn]
[HideColumns("StdDev", "RatioSD", "Error")]
[MinColumn, MaxColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[NinoJob]
[GcServer(true)]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public partial class WorkloadTest
#if !NINO_BENCH_V3
    : IReadBack
#endif
{
    // The values and Google.Protobuf's messages of them, made before the generated static constructor makes their
    // bytes, in this order; the previous version's values (Old) of the versioned workloads.
    private static readonly MediaContent MediaContentValue = MediaContent.Create(new Random(1));
#if !NINO_BENCH_V3
    private static readonly P.MediaContent MediaContentProto = P.MediaContent.Parser.ParseFrom(ProtobufModel.Serialize(MediaContentValue));
#endif
    private static readonly StackOverflowPage StackOverflowPageValue = StackOverflowPage.Create(new Random(2));
#if !NINO_BENCH_V3
    private static readonly P.StackOverflowPage StackOverflowPageProto = P.StackOverflowPage.Parser.ParseFrom(ProtobufModel.Serialize(StackOverflowPageValue));
#endif
    private static readonly RpcCall RpcCallValue = RpcCall.Create(new Random(3));
#if !NINO_BENCH_V3
    private static readonly P.RpcCall RpcCallProto = P.RpcCall.Parser.ParseFrom(ProtobufModel.Serialize(RpcCallValue));
#endif
    private static readonly ProfileV2 ProfileCacheValue = ProfileV2.From(ProfileV1.Create(new Random(4)));
    private static readonly ProfileV1 ProfileCacheOld = ProfileV1.Create(new Random(4));
#if !NINO_BENCH_V3
    private static readonly P.ProfileV2 ProfileCacheProto = P.ProfileV2.Parser.ParseFrom(ProtobufModel.Serialize(ProfileCacheValue));
#endif
    private static readonly EventStream EventStreamValue = EventStream.Create(new Random(5));
#if !NINO_BENCH_V3
    private static readonly P.EventStream EventStreamProto = P.EventStream.Parser.ParseFrom(ProtobufModel.Serialize(EventStreamValue));
#endif
    private static readonly LogBatch LogBatchValue = LogBatch.Create(new Random(6));
#if !NINO_BENCH_V3
    private static readonly P.LogBatch LogBatchProto = P.LogBatch.Parser.ParseFrom(ProtobufModel.Serialize(LogBatchValue));
#endif
    private static readonly StateSnapshot StateSnapshotValue = StateSnapshot.Create(new Random(7));
#if !NINO_BENCH_V3
    private static readonly P.StateSnapshot StateSnapshotProto = P.StateSnapshot.Parser.ParseFrom(ProtobufModel.Serialize(StateSnapshotValue));
#endif
    private static readonly LockstepFrames LockstepFramesValue = LockstepFrames.Create(new Random(8));
#if !NINO_BENCH_V3
    private static readonly P.LockstepFrames LockstepFramesProto = P.LockstepFrames.Parser.ParseFrom(ProtobufModel.Serialize(LockstepFramesValue));
#endif
    private static readonly AoiUpdate AoiUpdateValue = AoiUpdate.Create(new Random(9));
#if !NINO_BENCH_V3
    private static readonly P.AoiUpdate AoiUpdateProto = P.AoiUpdate.Parser.ParseFrom(ProtobufModel.Serialize(AoiUpdateValue));
#endif
    private static readonly WorldRegion WorldRegionValue = WorldRegion.Create(new Random(10));
#if !NINO_BENCH_V3
    private static readonly P.WorldRegion WorldRegionProto = P.WorldRegion.Parser.ParseFrom(ProtobufModel.Serialize(WorldRegionValue));
#endif
    private static readonly CardBattleLog CardBattleLogValue = CardBattleLog.Create(new Random(11));
#if !NINO_BENCH_V3
    private static readonly P.CardBattleLog CardBattleLogProto = P.CardBattleLog.Parser.ParseFrom(ProtobufModel.Serialize(CardBattleLogValue));
#endif
    private static readonly IdleSaveV2 IdleSaveValue = IdleSaveV2.From(IdleSaveV1.Create(new Random(12)));
    private static readonly IdleSaveV1 IdleSaveOld = IdleSaveV1.Create(new Random(12));
#if !NINO_BENCH_V3
    private static readonly P.IdleSaveV2 IdleSaveProto = P.IdleSaveV2.Parser.ParseFrom(ProtobufModel.Serialize(IdleSaveValue));
#endif

#if !NINO_BENCH_V3
    partial void OnSetup() => ProtoFile.Workloads.Check();

    /// <summary>Verify.cs: a Google.Protobuf message read back through protobuf-net as the workload's model.</summary>
    public object ReadBack(object result, Type model) => ProtobufModel.ReadBack(result, model);
#endif
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
