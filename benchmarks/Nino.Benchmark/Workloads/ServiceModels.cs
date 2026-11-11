using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
// PBN0023: interfaces as polymorphic contracts are what these workloads measure (the members are on the implementations).
// PBN0013: Envelope declares each closed Envelope<TEvent> it carries, not the open generic.
#pragma warning disable PBN0023, PBN0013
namespace Nino.Benchmark.Workloads;

// ---------------------------------------------------------------- RPC: a request and its response, whose result is
// an interface with a generic implementation (Ok<T>) beside two errors.

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class RpcRequest
{
    [Key(0)] [ProtoMember(1)] public long CallId;
    [Key(1)] [ProtoMember(2)] public string Service;
    [Key(2)] [ProtoMember(3)] public string Method;
    [Key(3)] [ProtoMember(4)] public Guid TraceId;
    [Key(4)] [ProtoMember(5)] public DateTime Deadline;
    [Key(5)] [ProtoMember(6)] public Dictionary<string, string> Metadata;
    [Key(6)] [ProtoMember(7)] public List<int> ItemIds;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Item
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public decimal Price;
    [Key(3)] [ProtoMember(4)] public int Stock;
}

/// <summary>A call's result: an interface, each kind its own type (one of them generic).</summary>
[NinoType, MemoryPackable, MemoryPackUnion(0, typeof(Ok<List<Item>>)), MemoryPackUnion(1, typeof(NotFound)),
 MemoryPackUnion(2, typeof(ValidationFailed))]
[Union(0, typeof(Ok<List<Item>>)), Union(1, typeof(NotFound)), Union(2, typeof(ValidationFailed))]
[ProtoContract, ProtoInclude(1, typeof(Ok<List<Item>>)), ProtoInclude(2, typeof(NotFound)), ProtoInclude(3, typeof(ValidationFailed))]
[JsonPolymorphic, JsonDerivedType(typeof(Ok<List<Item>>), "ok"), JsonDerivedType(typeof(NotFound), "not_found"),
 JsonDerivedType(typeof(ValidationFailed), "invalid")]
public partial interface IRpcResult;

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Ok<T> : IRpcResult
{
    [Key(0)] [ProtoMember(1)] public T Value;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class NotFound : IRpcResult
{
    [Key(0)] [ProtoMember(1)] public string Resource;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class FieldError
{
    [Key(0)] [ProtoMember(1)] public string Field;
    [Key(1)] [ProtoMember(2)] public string Message;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class ValidationFailed : IRpcResult
{
    [Key(0)] [ProtoMember(1)] public List<FieldError> Errors;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class RpcResponse
{
    [Key(0)] [ProtoMember(1)] public long CallId;
    [Key(1)] [ProtoMember(2)] public DateTime ServedAt;
    [Key(2)] [ProtoMember(3)] public int ElapsedMicros;
    [Key(3)] [ProtoMember(4)] public IRpcResult Result;
}

/// <summary>One RPC call: the request and the response a server sends back, with 8 items.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class RpcCall
{
    [Key(0)] [ProtoMember(1)] public RpcRequest Request;
    [Key(1)] [ProtoMember(2)] public RpcResponse Response;

    public static RpcCall Create(Random r)
    {
        var ids = Enumerable.Range(0, 8).Select(_ => r.Next(1, 100_000)).ToList();
        return new RpcCall
        {
            Request = new RpcRequest
            {
                CallId = 7_340_032_118, Service = "inventory.v1.Inventory", Method = "GetItems", TraceId = Data.NewGuid(r),
                Deadline = Data.Epoch.AddMilliseconds(r.Next()),
                Metadata = new Dictionary<string, string>
                    { ["authorization"] = "Bearer eyJhbGciOiJIUzI1NiJ9.e30", ["x-client"] = "web/2.31.0", ["accept-language"] = "en-US" },
                ItemIds = ids,
            },
            Response = new RpcResponse
            {
                CallId = 7_340_032_118, ServedAt = Data.Epoch.AddMilliseconds(r.Next()), ElapsedMicros = r.Next(100, 9_000),
                Result = new Ok<List<Item>>
                {
                    Value = ids.Select(id => new Item
                        { Id = id, Name = "Item " + id, Price = r.Next(99, 99_999) / 100m, Stock = r.Next(1, 500) }).ToList(),
                },
            },
        };
    }
}

// ---------------------------------------------------------------- A cached profile written by the previous release
// (ProfileV1) and read by this one (ProfileV2: AvatarUrl removed, Pronouns and Badges added). Nino's keyed format and
// MemoryPack's version-tolerant one; MessagePack's integer keys, protobuf's field numbers and JSON's names tolerate it
// as they are.
// The two versions are two types here, one release's each, so they are sealed: a type that is not sealed is written
// with its type's id, which differs between the two (one type across releases keeps it).

[NinoType(format: NinoFormat.Keyed), MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, ProtoContract,
 CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class ProfileV1
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public long Id;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public string UserName;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public string Email;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public string DisplayName;
    [MemoryPackOrder(4)] [Key(4)] [ProtoMember(5)] public string AvatarUrl;
    [MemoryPackOrder(5)] [Key(5)] [ProtoMember(6)] public string Bio;
    [MemoryPackOrder(6)] [Key(6)] [ProtoMember(7)] public DateTime CreatedAt;
    [MemoryPackOrder(7)] [Key(7)] [ProtoMember(8)] public DateTime? LastLoginAt;
    [MemoryPackOrder(8)] [Key(8)] [ProtoMember(9)] public bool EmailVerified;
    [MemoryPackOrder(9)] [Key(9)] [ProtoMember(10)] public int? BirthYear;
    [MemoryPackOrder(10)] [Key(10)] [ProtoMember(11)] public string Country;
    [MemoryPackOrder(11)] [Key(11)] [ProtoMember(12)] public string TimeZone;
    [MemoryPackOrder(12)] [Key(12)] [ProtoMember(13)] public int Followers;
    [MemoryPackOrder(13)] [Key(13)] [ProtoMember(14)] public List<string> Roles;
    [MemoryPackOrder(14)] [Key(14)] [ProtoMember(15)] public Dictionary<string, string> Preferences;

    public static ProfileV1 Create(Random r) => new()
    {
        Id = 48_213, UserName = "jdoe48213", Email = "jane.doe48213@example.com", DisplayName = "Jane Doe",
        AvatarUrl = "https://cdn.example.com/avatars/48213/256.png", Bio = null,
        CreatedAt = Data.Epoch.AddDays(-r.Next(2_000)), LastLoginAt = Data.Epoch.AddSeconds(r.Next()),
        EmailVerified = true, BirthYear = null, Country = "US", TimeZone = "America/Los_Angeles",
        Followers = r.Next(10_000), Roles = ["member", "beta-tester"],
        Preferences = new Dictionary<string, string>
        {
            ["theme"] = "dark", ["language"] = "en", ["newsletter"] = "weekly", ["notifications.email"] = "true",
            ["notifications.push"] = "false", ["currency"] = "USD", ["units"] = "metric", ["startPage"] = "/dashboard",
            ["density"] = "compact", ["privacy.profile"] = "friends",
        },
    };
}

[NinoType(format: NinoFormat.Keyed), MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, ProtoContract,
 CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class ProfileV2
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public long Id;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public string UserName;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public string Email;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public string DisplayName;
    // 4: AvatarUrl, removed.
    [MemoryPackOrder(5)] [Key(5)] [ProtoMember(6)] public string Bio;
    [MemoryPackOrder(6)] [Key(6)] [ProtoMember(7)] public DateTime CreatedAt;
    [MemoryPackOrder(7)] [Key(7)] [ProtoMember(8)] public DateTime? LastLoginAt;
    [MemoryPackOrder(8)] [Key(8)] [ProtoMember(9)] public bool EmailVerified;
    [MemoryPackOrder(9)] [Key(9)] [ProtoMember(10)] public int? BirthYear;
    [MemoryPackOrder(10)] [Key(10)] [ProtoMember(11)] public string Country;
    [MemoryPackOrder(11)] [Key(11)] [ProtoMember(12)] public string TimeZone;
    [MemoryPackOrder(12)] [Key(12)] [ProtoMember(13)] public int Followers;
    [MemoryPackOrder(13)] [Key(13)] [ProtoMember(14)] public List<string> Roles;
    [MemoryPackOrder(14)] [Key(14)] [ProtoMember(15)] public Dictionary<string, string> Preferences;
    [MemoryPackOrder(15)] [Key(15)] [ProtoMember(16)] public string Pronouns;
    [MemoryPackOrder(16)] [Key(16)] [ProtoMember(17)] public List<string> Badges;

    public static ProfileV2 From(ProfileV1 v) => new()
    {
        Id = v.Id, UserName = v.UserName, Email = v.Email, DisplayName = v.DisplayName, Bio = v.Bio,
        CreatedAt = v.CreatedAt, LastLoginAt = v.LastLoginAt, EmailVerified = v.EmailVerified, BirthYear = v.BirthYear,
        Country = v.Country, TimeZone = v.TimeZone, Followers = v.Followers, Roles = v.Roles, Preferences = v.Preferences,
        Pronouns = "she/her", Badges = ["early-adopter", "top-reviewer"],
    };
}

// ---------------------------------------------------------------- An event store's page: domain events in two levels
// of abstract classes (three families, eight events), each in a generic envelope whose base is not generic. A field
// typed as a family reads its subtypes: MemoryPack, MessagePack and System.Text.Json list them on the family,
// protobuf-net nests the families in the root.

[NinoType, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300),
 ProtoInclude(1, typeof(OrderEvent)), ProtoInclude(2, typeof(PaymentEvent)), ProtoInclude(3, typeof(CustomerEvent))]
public abstract partial class DomainEvent
{
    [Key(0)] [ProtoMember(20)] public Guid EventId;
    [Key(1)] [ProtoMember(21)] public DateTime OccurredAt;
    [Key(2)] [ProtoMember(22)] public int Version;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
[MemoryPackUnion(0, typeof(OrderPlaced)), MemoryPackUnion(1, typeof(OrderShipped)), MemoryPackUnion(2, typeof(OrderCancelled))]
[Union(0, typeof(OrderPlaced)), Union(1, typeof(OrderShipped)), Union(2, typeof(OrderCancelled))]
[ProtoInclude(1, typeof(OrderPlaced)), ProtoInclude(2, typeof(OrderShipped)), ProtoInclude(3, typeof(OrderCancelled))]
[JsonPolymorphic, JsonDerivedType(typeof(OrderPlaced), "placed"), JsonDerivedType(typeof(OrderShipped), "shipped"),
 JsonDerivedType(typeof(OrderCancelled), "cancelled")]
public abstract partial class OrderEvent : DomainEvent
{
    [Key(3)] [ProtoMember(20)] public long OrderId;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class OrderPlaced : OrderEvent
{
    [Key(4)] [ProtoMember(1)] public Guid CustomerId;
    [Key(5)] [ProtoMember(2)] public decimal Amount;
    [Key(6)] [ProtoMember(3)] public List<int> Skus;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class OrderShipped : OrderEvent
{
    [Key(4)] [ProtoMember(1)] public string Carrier;
    [Key(5)] [ProtoMember(2)] public string TrackingNumber;
    [Key(6)] [ProtoMember(3)] public DateTime? EstimatedDelivery;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class OrderCancelled : OrderEvent
{
    [Key(4)] [ProtoMember(1)] public string Reason;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
[MemoryPackUnion(0, typeof(PaymentAuthorized)), MemoryPackUnion(1, typeof(PaymentCaptured)), MemoryPackUnion(2, typeof(PaymentRefunded))]
[Union(0, typeof(PaymentAuthorized)), Union(1, typeof(PaymentCaptured)), Union(2, typeof(PaymentRefunded))]
[ProtoInclude(1, typeof(PaymentAuthorized)), ProtoInclude(2, typeof(PaymentCaptured)), ProtoInclude(3, typeof(PaymentRefunded))]
[JsonPolymorphic, JsonDerivedType(typeof(PaymentAuthorized), "authorized"),
 JsonDerivedType(typeof(PaymentCaptured), "captured"), JsonDerivedType(typeof(PaymentRefunded), "refunded")]
public abstract partial class PaymentEvent : DomainEvent
{
    [Key(3)] [ProtoMember(20)] public string PaymentId;
    [Key(4)] [ProtoMember(21)] public decimal Amount;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class PaymentAuthorized : PaymentEvent
{
    [Key(5)] [ProtoMember(1)] public string Currency;
    [Key(6)] [ProtoMember(2)] public string CardLast4;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class PaymentCaptured : PaymentEvent
{
    [Key(5)] [ProtoMember(1)] public DateTime CapturedAt;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class PaymentRefunded : PaymentEvent
{
    [Key(5)] [ProtoMember(1)] public string Reason;
    [Key(6)] [ProtoMember(2)] public bool Partial;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
[MemoryPackUnion(0, typeof(CustomerRegistered)), MemoryPackUnion(1, typeof(CustomerRenamed))]
[Union(0, typeof(CustomerRegistered)), Union(1, typeof(CustomerRenamed))]
[ProtoInclude(1, typeof(CustomerRegistered)), ProtoInclude(2, typeof(CustomerRenamed))]
[JsonPolymorphic, JsonDerivedType(typeof(CustomerRegistered), "registered"), JsonDerivedType(typeof(CustomerRenamed), "renamed")]
public abstract partial class CustomerEvent : DomainEvent
{
    [Key(3)] [ProtoMember(20)] public Guid CustomerId;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class CustomerRegistered : CustomerEvent
{
    [Key(4)] [ProtoMember(1)] public string Email;
    [Key(5)] [ProtoMember(2)] public string Country;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class CustomerRenamed : CustomerEvent
{
    [Key(4)] [ProtoMember(1)] public string OldName;
    [Key(5)] [ProtoMember(2)] public string NewName;
}

/// <summary>An event as the store keeps it: a non-generic base, one generic envelope per event family.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
[MemoryPackUnion(0, typeof(Envelope<OrderEvent>)), MemoryPackUnion(1, typeof(Envelope<PaymentEvent>)),
 MemoryPackUnion(2, typeof(Envelope<CustomerEvent>))]
[Union(0, typeof(Envelope<OrderEvent>)), Union(1, typeof(Envelope<PaymentEvent>)), Union(2, typeof(Envelope<CustomerEvent>))]
[ProtoInclude(1, typeof(Envelope<OrderEvent>)), ProtoInclude(2, typeof(Envelope<PaymentEvent>)),
 ProtoInclude(3, typeof(Envelope<CustomerEvent>))]
[JsonPolymorphic, JsonDerivedType(typeof(Envelope<OrderEvent>), "order"),
 JsonDerivedType(typeof(Envelope<PaymentEvent>), "payment"), JsonDerivedType(typeof(Envelope<CustomerEvent>), "customer")]
public abstract partial class Envelope
{
    [Key(0)] [ProtoMember(20)] public long Sequence;
    [Key(1)] [ProtoMember(21)] public string Stream;
    [Key(2)] [ProtoMember(22)] public DateTime RecordedAt;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class Envelope<TEvent> : Envelope where TEvent : DomainEvent
{
    [Key(3)] [ProtoMember(1)] public TEvent Event;
    [Key(4)] [ProtoMember(2)] public Dictionary<string, string> Headers;
}

/// <summary>500 events of an event store's stream, in their envelopes.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class EventStream
{
    [Key(0)] [ProtoMember(1)] public long FromSequence;
    [Key(1)] [ProtoMember(2)] public List<Envelope> Events;

    public static EventStream Create(Random r) => new()
    {
        FromSequence = 5_200_000,
        Events = Enumerable.Range(0, 500).Select(i =>
        {
            var at = Data.Epoch.AddMilliseconds(i * 1_250L);
            T Base<T>(T e) where T : DomainEvent
            {
                e.EventId = Data.NewGuid(r);
                e.OccurredAt = at;
                e.Version = 1 + r.Next(3);
                return e;
            }

            Envelope envelope = (r.Next(8)) switch
            {
                0 => new Envelope<OrderEvent> { Event = Base<OrderEvent>(new OrderPlaced
                    { OrderId = 900_000 + i, CustomerId = Data.NewGuid(r), Amount = r.Next(500, 90_000) / 100m,
                      Skus = Enumerable.Range(0, r.Next(1, 6)).Select(_ => r.Next(10_000, 99_999)).ToList() }) },
                1 => new Envelope<OrderEvent> { Event = Base<OrderEvent>(new OrderShipped
                    { OrderId = 900_000 + i, Carrier = r.Next(2) == 0 ? "UPS" : "FedEx",
                      TrackingNumber = "1Z" + r.NextInt64().ToString("X14"),
                      EstimatedDelivery = r.Next(5) == 0 ? null : at.AddDays(r.Next(2, 9)) }) },
                2 => new Envelope<OrderEvent> { Event = Base<OrderEvent>(new OrderCancelled
                    { OrderId = 900_000 + i, Reason = "customer request" }) },
                3 => new Envelope<PaymentEvent> { Event = Base<PaymentEvent>(new PaymentAuthorized
                    { PaymentId = "pi_" + r.NextInt64().ToString("x16"), Amount = r.Next(500, 90_000) / 100m,
                      Currency = "USD", CardLast4 = r.Next(1_000, 9_999).ToString() }) },
                4 => new Envelope<PaymentEvent> { Event = Base<PaymentEvent>(new PaymentCaptured
                    { PaymentId = "pi_" + r.NextInt64().ToString("x16"), Amount = r.Next(500, 90_000) / 100m, CapturedAt = at }) },
                5 => new Envelope<PaymentEvent> { Event = Base<PaymentEvent>(new PaymentRefunded
                    { PaymentId = "pi_" + r.NextInt64().ToString("x16"), Amount = r.Next(500, 9_000) / 100m,
                      Reason = "damaged", Partial = r.Next(2) == 0 }) },
                6 => new Envelope<CustomerEvent> { Event = Base<CustomerEvent>(new CustomerRegistered
                    { CustomerId = Data.NewGuid(r), Email = "user" + r.Next(1_000_000) + "@example.com", Country = "DE" }) },
                _ => new Envelope<CustomerEvent> { Event = Base<CustomerEvent>(new CustomerRenamed
                    { CustomerId = Data.NewGuid(r), OldName = Data.Name(r), NewName = Data.Name(r) }) },
            };
            envelope.Sequence = 5_200_000 + i;
            envelope.Stream = "orders-" + r.Next(64);
            envelope.RecordedAt = at.AddMilliseconds(r.Next(1, 40));
            switch (envelope)
            {
                case Envelope<OrderEvent> o: o.Headers = Headers(r); break;
                case Envelope<PaymentEvent> p: p.Headers = Headers(r); break;
                case Envelope<CustomerEvent> c: c.Headers = Headers(r); break;
            }

            return envelope;
        }).ToList(),
    };

    private static Dictionary<string, string> Headers(Random r) => new()
        { ["correlation-id"] = r.NextInt64().ToString("x16"), ["producer"] = "checkout-api" };
}

// ---------------------------------------------------------------- 1,000 structured log events a service ships to
// its collector in one request.

public enum Severity { Debug, Info, Warning, Error }

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class LogEvent
{
    [Key(0)] [ProtoMember(1)] public DateTime Timestamp;
    [Key(1)] [ProtoMember(2)] public Severity Level;
    [Key(2)] [ProtoMember(3)] public string Template;
    [Key(3)] [ProtoMember(4)] public string TraceId;
    [Key(4)] [ProtoMember(5)] public double ElapsedMs;
    [Key(5)] [ProtoMember(6)] public Dictionary<string, string> Tags;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class LogBatch
{
    [Key(0)] [ProtoMember(1)] public string Service;
    [Key(1)] [ProtoMember(2)] public string Host;
    [Key(2)] [ProtoMember(3)] public List<LogEvent> Events;

    private static readonly string[] Templates =
    [
        "HTTP {Method} {Path} responded {StatusCode} in {Elapsed} ms", "Cache miss for {Key}",
        "User {UserId} signed in", "Retrying {Operation} after {Delay} ms (attempt {Attempt})",
        "Order {OrderId} placed by {CustomerId}", "Payment {PaymentId} captured", "Queue {Queue} depth is {Depth}",
        "Slow query on {Table}: {Elapsed} ms", "Token refreshed for client {ClientId}", "Health check passed",
    ];
    private static readonly string[] Paths = ["/api/orders", "/api/users/me", "/api/cart", "/api/search", "/health"];

    public static LogBatch Create(Random r) => new()
    {
        Service = "checkout-api",
        Host = "checkout-api-7d9f8c6b5-x2kqp",
        Events = Enumerable.Range(0, 1000).Select(i => new LogEvent
        {
            Timestamp = Data.Epoch.AddMilliseconds(i * 37 + r.Next(37)),
            Level = (Severity)(r.Next(20) switch { 0 => 3, < 3 => 2, < 8 => 0, _ => 1 }),
            Template = Templates[r.Next(Templates.Length)],
            TraceId = r.NextInt64().ToString("x16") + r.NextInt64().ToString("x16"),
            ElapsedMs = Math.Round(r.NextDouble() * 250, 3),
            Tags = new Dictionary<string, string>
            {
                ["path"] = Paths[r.Next(Paths.Length)], ["status"] = r.Next(10) == 0 ? "500" : "200",
                ["region"] = r.Next(2) == 0 ? "us-west-2" : "eu-central-1",
            },
        }).ToList(),
    };
}
