using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using BenchmarkDotNet.Configs;
using Google.Protobuf;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Proto;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Payloads shaped like real workloads (WorkloadModels.cs), each serialized to a reused buffer and deserialized into
/// new instances, by Nino, MemoryPack, MessagePack, protobuf-net, Google.Protobuf and System.Text.Json (source
/// generated, UTF-8): one RPC request, a Web API page of 100 orders, a cached user profile, a game snapshot of 500
/// entities, a batch of 1,000 log events and a stream of 500 polymorphic domain events. Google.Protobuf writes and
/// reads its own messages (workloads.proto) of the same values; the others share one type per workload.
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
    private static readonly RpcRequest Rpc = RpcRequest.Create(new Random(1));
    private static readonly P.RpcRequest ProtoRpc = WorkloadProto.ToProto(Rpc);
    private static readonly OrdersPage Orders = OrdersPage.Create(new Random(2));
    private static readonly P.OrdersPage ProtoOrders = WorkloadProto.ToProto(Orders);
    private static readonly UserProfile Profile = UserProfile.Create(new Random(3));
    private static readonly P.UserProfile ProtoProfile = WorkloadProto.ToProto(Profile);
    private static readonly GameSnapshot Snapshot = GameSnapshot.Create(new Random(4));
    private static readonly P.GameSnapshot ProtoSnapshot = WorkloadProto.ToProto(Snapshot);
    private static readonly LogBatch Logs = LogBatch.Create(new Random(5));
    private static readonly P.LogBatch ProtoLogs = WorkloadProto.ToProto(Logs);
    private static readonly EventBatch Events = EventBatch.Create(new Random(6));
    private static readonly P.EventBatch ProtoEvents = WorkloadProto.ToProto(Events);

    private static readonly byte[] NinoRpc = NinoSerializer.Serialize(Rpc);
    private static readonly byte[] MemoryPackRpc = MemoryPackSerializer.Serialize(Rpc);
    private static readonly byte[] MessagePackRpc = MessagePackSerializer.Serialize(Rpc);
    private static readonly byte[] ProtobufRpc = ProtobufModel.Serialize(Rpc);
    private static readonly byte[] GoogleProtobufRpc = ProtoRpc.ToByteArray();
    private static readonly byte[] JsonRpc = JsonSerializer.SerializeToUtf8Bytes(Rpc, WorkloadJson.Default.RpcRequest);
    private static readonly byte[] NinoOrders = NinoSerializer.Serialize(Orders);
    private static readonly byte[] MemoryPackOrders = MemoryPackSerializer.Serialize(Orders);
    private static readonly byte[] MessagePackOrders = MessagePackSerializer.Serialize(Orders);
    private static readonly byte[] ProtobufOrders = ProtobufModel.Serialize(Orders);
    private static readonly byte[] GoogleProtobufOrders = ProtoOrders.ToByteArray();
    private static readonly byte[] JsonOrders = JsonSerializer.SerializeToUtf8Bytes(Orders, WorkloadJson.Default.OrdersPage);
    private static readonly byte[] NinoProfile = NinoSerializer.Serialize(Profile);
    private static readonly byte[] MemoryPackProfile = MemoryPackSerializer.Serialize(Profile);
    private static readonly byte[] MessagePackProfile = MessagePackSerializer.Serialize(Profile);
    private static readonly byte[] ProtobufProfile = ProtobufModel.Serialize(Profile);
    private static readonly byte[] GoogleProtobufProfile = ProtoProfile.ToByteArray();
    private static readonly byte[] JsonProfile = JsonSerializer.SerializeToUtf8Bytes(Profile, WorkloadJson.Default.UserProfile);
    private static readonly byte[] NinoSnapshot = NinoSerializer.Serialize(Snapshot);
    private static readonly byte[] MemoryPackSnapshot = MemoryPackSerializer.Serialize(Snapshot);
    private static readonly byte[] MessagePackSnapshot = MessagePackSerializer.Serialize(Snapshot);
    private static readonly byte[] ProtobufSnapshot = ProtobufModel.Serialize(Snapshot);
    private static readonly byte[] GoogleProtobufSnapshot = ProtoSnapshot.ToByteArray();
    private static readonly byte[] JsonSnapshot = JsonSerializer.SerializeToUtf8Bytes(Snapshot, WorkloadJson.Default.GameSnapshot);
    private static readonly byte[] NinoLogs = NinoSerializer.Serialize(Logs);
    private static readonly byte[] MemoryPackLogs = MemoryPackSerializer.Serialize(Logs);
    private static readonly byte[] MessagePackLogs = MessagePackSerializer.Serialize(Logs);
    private static readonly byte[] ProtobufLogs = ProtobufModel.Serialize(Logs);
    private static readonly byte[] GoogleProtobufLogs = ProtoLogs.ToByteArray();
    private static readonly byte[] JsonLogs = JsonSerializer.SerializeToUtf8Bytes(Logs, WorkloadJson.Default.LogBatch);
    private static readonly byte[] NinoEvents = NinoSerializer.Serialize(Events);
    private static readonly byte[] MemoryPackEvents = MemoryPackSerializer.Serialize(Events);
    private static readonly byte[] MessagePackEvents = MessagePackSerializer.Serialize(Events);
    private static readonly byte[] ProtobufEvents = ProtobufModel.Serialize(Events);
    private static readonly byte[] GoogleProtobufEvents = ProtoEvents.ToByteArray();
    private static readonly byte[] JsonEvents = JsonSerializer.SerializeToUtf8Bytes(Events, WorkloadJson.Default.EventBatch);

    static WorkloadTest()
    {
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeRpcRequest), NinoRpc.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeRpcRequest), MemoryPackRpc.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeRpcRequest), MessagePackRpc.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeRpcRequest), ProtobufRpc.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeRpcRequest), GoogleProtobufRpc.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeRpcRequest), JsonRpc.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeOrdersPage), NinoOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeOrdersPage), MemoryPackOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeOrdersPage), MessagePackOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeOrdersPage), ProtobufOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeOrdersPage), GoogleProtobufOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeOrdersPage), JsonOrders.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeUserProfile), NinoProfile.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeUserProfile), MemoryPackProfile.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeUserProfile), MessagePackProfile.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeUserProfile), ProtobufProfile.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeUserProfile), GoogleProtobufProfile.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeUserProfile), JsonProfile.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeGameSnapshot), NinoSnapshot.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeGameSnapshot), MemoryPackSnapshot.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeGameSnapshot), MessagePackSnapshot.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeGameSnapshot), ProtobufSnapshot.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeGameSnapshot), GoogleProtobufSnapshot.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeGameSnapshot), JsonSnapshot.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeLogBatch), NinoLogs.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeLogBatch), MemoryPackLogs.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeLogBatch), MessagePackLogs.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeLogBatch), ProtobufLogs.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeLogBatch), GoogleProtobufLogs.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeLogBatch), JsonLogs.Length);
        BenchmarkPayloadRegistry.Register(nameof(NinoSerializeEventStream), NinoEvents.Length);
        BenchmarkPayloadRegistry.Register(nameof(MemoryPackSerializeEventStream), MemoryPackEvents.Length);
        BenchmarkPayloadRegistry.Register(nameof(MessagePackSerializeEventStream), MessagePackEvents.Length);
        BenchmarkPayloadRegistry.Register(nameof(ProtobufSerializeEventStream), ProtobufEvents.Length);
        BenchmarkPayloadRegistry.Register(nameof(GoogleProtobufSerializeEventStream), GoogleProtobufEvents.Length);
        BenchmarkPayloadRegistry.Register(nameof(JsonSerializeEventStream), JsonEvents.Length);
    }

    private NinoArrayBufferWriter _ninoBuffer;
    private ArrayBufferWriter<byte> _memoryPackBuffer;
    private ArrayBufferWriter<byte> _messagePackBuffer;
    private MemoryStream _protobufStream;
    private ArrayBufferWriter<byte> _googleProtobufBuffer;
    private ArrayBufferWriter<byte> _jsonBuffer;
    private Utf8JsonWriter _jsonWriter;

    [GlobalSetup]
    public void Setup()
    {
        _ninoBuffer = new NinoArrayBufferWriter(1 << 20);
        _memoryPackBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _messagePackBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _protobufStream = new MemoryStream(1 << 20);
        _googleProtobufBuffer = new ArrayBufferWriter<byte>(1 << 20);
        _jsonBuffer = new ArrayBufferWriter<byte>(1 << 20);
        // What JsonSerializer's own writers use: no validation of the writer's calls.
        _jsonWriter = new Utf8JsonWriter(_jsonBuffer, new JsonWriterOptions { SkipValidation = true });
    }

    /// <summary>Verify.cs: a Google.Protobuf message read back through protobuf-net as the workload's model.</summary>
    public object ReadBack(object result, Type model) => result is IMessage message
        ? Serializer.NonGeneric.Deserialize(model, new MemoryStream(message.ToByteArray()))
        : result;

    [Benchmark(Baseline = true), BenchmarkCategory("RpcRequestSerialize")]
    public int NinoSerializeRpcRequest()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Rpc, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RpcRequestSerialize")]
    public int MemoryPackSerializeRpcRequest()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Rpc);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RpcRequestSerialize")]
    public int MessagePackSerializeRpcRequest()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Rpc);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RpcRequestSerialize")]
    public int ProtobufSerializeRpcRequest()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Rpc);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("RpcRequestSerialize")]
    public int GoogleProtobufSerializeRpcRequest()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ProtoRpc.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("RpcRequestSerialize")]
    public int JsonSerializeRpcRequest()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Rpc, WorkloadJson.Default.RpcRequest);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("RpcRequestDeserialize")]
    public RpcRequest NinoDeserializeRpcRequest() => NinoDeserializer.Deserialize<RpcRequest>(NinoRpc);

    [Benchmark, BenchmarkCategory("RpcRequestDeserialize")]
    public RpcRequest MemoryPackDeserializeRpcRequest() => MemoryPackSerializer.Deserialize<RpcRequest>(MemoryPackRpc);

    [Benchmark, BenchmarkCategory("RpcRequestDeserialize")]
    public RpcRequest MessagePackDeserializeRpcRequest() => MessagePackSerializer.Deserialize<RpcRequest>(MessagePackRpc);

    [Benchmark, BenchmarkCategory("RpcRequestDeserialize")]
    public RpcRequest ProtobufDeserializeRpcRequest() => Serializer.Deserialize<RpcRequest>(new ReadOnlySpan<byte>(ProtobufRpc));

    [Benchmark, BenchmarkCategory("RpcRequestDeserialize")]
    public P.RpcRequest GoogleProtobufDeserializeRpcRequest() => P.RpcRequest.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufRpc));

    [Benchmark, BenchmarkCategory("RpcRequestDeserialize")]
    public RpcRequest JsonDeserializeRpcRequest() => JsonSerializer.Deserialize(JsonRpc, WorkloadJson.Default.RpcRequest);

    [Benchmark(Baseline = true), BenchmarkCategory("OrdersPageSerialize")]
    public int NinoSerializeOrdersPage()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Orders, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("OrdersPageSerialize")]
    public int MemoryPackSerializeOrdersPage()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Orders);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("OrdersPageSerialize")]
    public int MessagePackSerializeOrdersPage()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Orders);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("OrdersPageSerialize")]
    public int ProtobufSerializeOrdersPage()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Orders);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("OrdersPageSerialize")]
    public int GoogleProtobufSerializeOrdersPage()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ProtoOrders.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("OrdersPageSerialize")]
    public int JsonSerializeOrdersPage()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Orders, WorkloadJson.Default.OrdersPage);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("OrdersPageDeserialize")]
    public OrdersPage NinoDeserializeOrdersPage() => NinoDeserializer.Deserialize<OrdersPage>(NinoOrders);

    [Benchmark, BenchmarkCategory("OrdersPageDeserialize")]
    public OrdersPage MemoryPackDeserializeOrdersPage() => MemoryPackSerializer.Deserialize<OrdersPage>(MemoryPackOrders);

    [Benchmark, BenchmarkCategory("OrdersPageDeserialize")]
    public OrdersPage MessagePackDeserializeOrdersPage() => MessagePackSerializer.Deserialize<OrdersPage>(MessagePackOrders);

    [Benchmark, BenchmarkCategory("OrdersPageDeserialize")]
    public OrdersPage ProtobufDeserializeOrdersPage() => Serializer.Deserialize<OrdersPage>(new ReadOnlySpan<byte>(ProtobufOrders));

    [Benchmark, BenchmarkCategory("OrdersPageDeserialize")]
    public P.OrdersPage GoogleProtobufDeserializeOrdersPage() => P.OrdersPage.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufOrders));

    [Benchmark, BenchmarkCategory("OrdersPageDeserialize")]
    public OrdersPage JsonDeserializeOrdersPage() => JsonSerializer.Deserialize(JsonOrders, WorkloadJson.Default.OrdersPage);

    [Benchmark(Baseline = true), BenchmarkCategory("UserProfileSerialize")]
    public int NinoSerializeUserProfile()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Profile, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("UserProfileSerialize")]
    public int MemoryPackSerializeUserProfile()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Profile);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("UserProfileSerialize")]
    public int MessagePackSerializeUserProfile()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Profile);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("UserProfileSerialize")]
    public int ProtobufSerializeUserProfile()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Profile);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("UserProfileSerialize")]
    public int GoogleProtobufSerializeUserProfile()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ProtoProfile.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("UserProfileSerialize")]
    public int JsonSerializeUserProfile()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Profile, WorkloadJson.Default.UserProfile);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("UserProfileDeserialize")]
    public UserProfile NinoDeserializeUserProfile() => NinoDeserializer.Deserialize<UserProfile>(NinoProfile);

    [Benchmark, BenchmarkCategory("UserProfileDeserialize")]
    public UserProfile MemoryPackDeserializeUserProfile() => MemoryPackSerializer.Deserialize<UserProfile>(MemoryPackProfile);

    [Benchmark, BenchmarkCategory("UserProfileDeserialize")]
    public UserProfile MessagePackDeserializeUserProfile() => MessagePackSerializer.Deserialize<UserProfile>(MessagePackProfile);

    [Benchmark, BenchmarkCategory("UserProfileDeserialize")]
    public UserProfile ProtobufDeserializeUserProfile() => Serializer.Deserialize<UserProfile>(new ReadOnlySpan<byte>(ProtobufProfile));

    [Benchmark, BenchmarkCategory("UserProfileDeserialize")]
    public P.UserProfile GoogleProtobufDeserializeUserProfile() => P.UserProfile.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufProfile));

    [Benchmark, BenchmarkCategory("UserProfileDeserialize")]
    public UserProfile JsonDeserializeUserProfile() => JsonSerializer.Deserialize(JsonProfile, WorkloadJson.Default.UserProfile);

    [Benchmark(Baseline = true), BenchmarkCategory("GameSnapshotSerialize")]
    public int NinoSerializeGameSnapshot()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Snapshot, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GameSnapshotSerialize")]
    public int MemoryPackSerializeGameSnapshot()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Snapshot);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GameSnapshotSerialize")]
    public int MessagePackSerializeGameSnapshot()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Snapshot);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GameSnapshotSerialize")]
    public int ProtobufSerializeGameSnapshot()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Snapshot);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("GameSnapshotSerialize")]
    public int GoogleProtobufSerializeGameSnapshot()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ProtoSnapshot.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("GameSnapshotSerialize")]
    public int JsonSerializeGameSnapshot()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Snapshot, WorkloadJson.Default.GameSnapshot);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("GameSnapshotDeserialize")]
    public GameSnapshot NinoDeserializeGameSnapshot() => NinoDeserializer.Deserialize<GameSnapshot>(NinoSnapshot);

    [Benchmark, BenchmarkCategory("GameSnapshotDeserialize")]
    public GameSnapshot MemoryPackDeserializeGameSnapshot() => MemoryPackSerializer.Deserialize<GameSnapshot>(MemoryPackSnapshot);

    [Benchmark, BenchmarkCategory("GameSnapshotDeserialize")]
    public GameSnapshot MessagePackDeserializeGameSnapshot() => MessagePackSerializer.Deserialize<GameSnapshot>(MessagePackSnapshot);

    [Benchmark, BenchmarkCategory("GameSnapshotDeserialize")]
    public GameSnapshot ProtobufDeserializeGameSnapshot() => Serializer.Deserialize<GameSnapshot>(new ReadOnlySpan<byte>(ProtobufSnapshot));

    [Benchmark, BenchmarkCategory("GameSnapshotDeserialize")]
    public P.GameSnapshot GoogleProtobufDeserializeGameSnapshot() => P.GameSnapshot.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufSnapshot));

    [Benchmark, BenchmarkCategory("GameSnapshotDeserialize")]
    public GameSnapshot JsonDeserializeGameSnapshot() => JsonSerializer.Deserialize(JsonSnapshot, WorkloadJson.Default.GameSnapshot);

    [Benchmark(Baseline = true), BenchmarkCategory("LogBatchSerialize")]
    public int NinoSerializeLogBatch()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Logs, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int MemoryPackSerializeLogBatch()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Logs);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int MessagePackSerializeLogBatch()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Logs);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int ProtobufSerializeLogBatch()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Logs);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int GoogleProtobufSerializeLogBatch()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ProtoLogs.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("LogBatchSerialize")]
    public int JsonSerializeLogBatch()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Logs, WorkloadJson.Default.LogBatch);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch NinoDeserializeLogBatch() => NinoDeserializer.Deserialize<LogBatch>(NinoLogs);

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch MemoryPackDeserializeLogBatch() => MemoryPackSerializer.Deserialize<LogBatch>(MemoryPackLogs);

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch MessagePackDeserializeLogBatch() => MessagePackSerializer.Deserialize<LogBatch>(MessagePackLogs);

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch ProtobufDeserializeLogBatch() => Serializer.Deserialize<LogBatch>(new ReadOnlySpan<byte>(ProtobufLogs));

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public P.LogBatch GoogleProtobufDeserializeLogBatch() => P.LogBatch.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufLogs));

    [Benchmark, BenchmarkCategory("LogBatchDeserialize")]
    public LogBatch JsonDeserializeLogBatch() => JsonSerializer.Deserialize(JsonLogs, WorkloadJson.Default.LogBatch);

    [Benchmark(Baseline = true), BenchmarkCategory("EventStreamSerialize")]
    public int NinoSerializeEventStream()
    {
        _ninoBuffer.ResetWrittenCount();
        NinoSerializer.Serialize(Events, _ninoBuffer);
        return _ninoBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int MemoryPackSerializeEventStream()
    {
        _memoryPackBuffer.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_memoryPackBuffer, Events);
        return _memoryPackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int MessagePackSerializeEventStream()
    {
        _messagePackBuffer.ResetWrittenCount();
        MessagePackSerializer.Serialize(_messagePackBuffer, Events);
        return _messagePackBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int ProtobufSerializeEventStream()
    {
        _protobufStream.SetLength(0);
        Serializer.Serialize(_protobufStream, Events);
        return (int)_protobufStream.Length;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int GoogleProtobufSerializeEventStream()
    {
        _googleProtobufBuffer.ResetWrittenCount();
        ProtoEvents.WriteTo(_googleProtobufBuffer);
        return _googleProtobufBuffer.WrittenCount;
    }

    [Benchmark, BenchmarkCategory("EventStreamSerialize")]
    public int JsonSerializeEventStream()
    {
        _jsonBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_jsonBuffer);
        JsonSerializer.Serialize(_jsonWriter, Events, WorkloadJson.Default.EventBatch);
        return _jsonBuffer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("EventStreamDeserialize")]
    public EventBatch NinoDeserializeEventStream() => NinoDeserializer.Deserialize<EventBatch>(NinoEvents);

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventBatch MemoryPackDeserializeEventStream() => MemoryPackSerializer.Deserialize<EventBatch>(MemoryPackEvents);

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventBatch MessagePackDeserializeEventStream() => MessagePackSerializer.Deserialize<EventBatch>(MessagePackEvents);

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventBatch ProtobufDeserializeEventStream() => Serializer.Deserialize<EventBatch>(new ReadOnlySpan<byte>(ProtobufEvents));

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public P.EventBatch GoogleProtobufDeserializeEventStream() => P.EventBatch.Parser.ParseFrom(new ReadOnlySpan<byte>(GoogleProtobufEvents));

    [Benchmark, BenchmarkCategory("EventStreamDeserialize")]
    public EventBatch JsonDeserializeEventStream() => JsonSerializer.Deserialize(JsonEvents, WorkloadJson.Default.EventBatch);
}
