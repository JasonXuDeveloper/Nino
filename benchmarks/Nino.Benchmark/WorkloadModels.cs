using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Google.Protobuf.WellKnownTypes;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;
using P = Nino.Benchmark.Proto;

#nullable disable
namespace Nino.Benchmark;

// The payloads of WorkloadTest: data shaped like what services and games send, each one type that every library
// writes with its own attributes (Google.Protobuf: the messages of workloads.proto, made from these values by
// WorkloadProto). protobuf-net writes them at CompatibilityLevel.Level300, the well-known types Google.Protobuf
// uses: DateTime as google.protobuf.Timestamp, decimal and Guid as strings. Every DateTime is UTC (what a timestamp
// carries), every list has an element and every string that is not null has a character: protobuf writes no empty
// list or string, so they would read back as null.

public enum Priority { Low, Normal, High }

/// <summary>One RPC call: ids, a trace id, short strings, a timestamp, an enum and a short list.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class RpcRequest
{
    [Key(0)] [ProtoMember(1)] public long RequestId;
    [Key(1)] [ProtoMember(2)] public Guid TraceId;
    [Key(2)] [ProtoMember(3)] public string Method;
    [Key(3)] [ProtoMember(4)] public string User;
    [Key(4)] [ProtoMember(5)] public string Locale;
    [Key(5)] [ProtoMember(6)] public DateTime SentAt;
    [Key(6)] [ProtoMember(7)] public Priority Priority;
    [Key(7)] [ProtoMember(8)] public List<int> ItemIds;

    public static RpcRequest Create(Random r) => new()
    {
        RequestId = 7_340_032_118,
        TraceId = Workload.NewGuid(r),
        Method = "inventory.v1.Inventory/GetItems",
        User = "user-48213",
        Locale = "en-US",
        SentAt = Workload.Epoch.AddMilliseconds(r.Next()),
        Priority = Priority.Normal,
        ItemIds = Enumerable.Range(0, 8).Select(_ => r.Next(1, 100_000)).ToList(),
    };
}

public enum OrderStatus { Pending, Paid, Shipped, Delivered, Cancelled }

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Address
{
    [Key(0)] [ProtoMember(1)] public string Line1;
    [Key(1)] [ProtoMember(2)] public string Line2;
    [Key(2)] [ProtoMember(3)] public string City;
    [Key(3)] [ProtoMember(4)] public string Region;
    [Key(4)] [ProtoMember(5)] public string PostalCode;
    [Key(5)] [ProtoMember(6)] public string Country;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class OrderLine
{
    [Key(0)] [ProtoMember(1)] public string Sku;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public int Quantity;
    [Key(3)] [ProtoMember(4)] public decimal UnitPrice;
    [Key(4)] [ProtoMember(5)] public decimal? Discount;
}

/// <summary>An order as a Web API returns it: money in decimal, optional fields, an address and 1 to 8 lines.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Order
{
    [Key(0)] [ProtoMember(1)] public long Id;
    [Key(1)] [ProtoMember(2)] public string Number;
    [Key(2)] [ProtoMember(3)] public Guid CustomerId;
    [Key(3)] [ProtoMember(4)] public string CustomerName;
    [Key(4)] [ProtoMember(5)] public OrderStatus Status;
    [Key(5)] [ProtoMember(6)] public DateTime CreatedAt;
    [Key(6)] [ProtoMember(7)] public DateTime? ShippedAt;
    [Key(7)] [ProtoMember(8)] public string Coupon;
    [Key(8)] [ProtoMember(9)] public decimal Subtotal;
    [Key(9)] [ProtoMember(10)] public decimal Tax;
    [Key(10)] [ProtoMember(11)] public decimal Total;
    [Key(11)] [ProtoMember(12)] public Address ShipTo;
    [Key(12)] [ProtoMember(13)] public List<OrderLine> Lines;
}

/// <summary>A page of 100 orders, a Web API's list response.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class OrdersPage
{
    [Key(0)] [ProtoMember(1)] public int Page;
    [Key(1)] [ProtoMember(2)] public int PageSize;
    [Key(2)] [ProtoMember(3)] public int TotalCount;
    [Key(3)] [ProtoMember(4)] public List<Order> Orders;

    private static readonly string[] Cities = ["Seattle", "Austin", "Berlin", "Shanghai", "São Paulo", "Toronto"];
    private static readonly string[] Products = ["USB-C cable 2 m", "Mechanical keyboard", "27\" monitor",
        "Wireless mouse", "Laptop stand", "Noise-cancelling headphones", "Webcam 1080p", "Desk lamp"];

    public static OrdersPage Create(Random r) => new()
    {
        Page = 3,
        PageSize = 100,
        TotalCount = 12_874,
        Orders = Enumerable.Range(0, 100).Select(i =>
        {
            var lines = Enumerable.Range(0, r.Next(1, 9)).Select(_ =>
            {
                var product = r.Next(Products.Length);
                return new OrderLine
                {
                    Sku = "SKU-" + (10_000 + product * 37),
                    Name = Products[product],
                    Quantity = r.Next(1, 5),
                    UnitPrice = r.Next(199, 49_999) / 100m,
                    Discount = r.Next(4) == 0 ? r.Next(100, 1_000) / 100m : null,
                };
            }).ToList();
            var subtotal = lines.Sum(l => l.UnitPrice * l.Quantity - (l.Discount ?? 0));
            var tax = Math.Round(subtotal * 0.0825m, 2);
            var created = Workload.Epoch.AddMinutes(r.Next(500_000));
            var status = (OrderStatus)r.Next(5);
            return new Order
            {
                Id = 900_000 + i,
                Number = "SO-2026-" + (300_000 + i).ToString(CultureInfo.InvariantCulture),
                CustomerId = Workload.NewGuid(r),
                CustomerName = Workload.Name(r),
                Status = status,
                CreatedAt = created,
                ShippedAt = status is OrderStatus.Shipped or OrderStatus.Delivered ? created.AddHours(r.Next(4, 72)) : null,
                Coupon = r.Next(10) < 3 ? "SPRING" + r.Next(10, 30) : null,
                Subtotal = subtotal,
                Tax = tax,
                Total = subtotal + tax,
                ShipTo = new Address
                {
                    Line1 = r.Next(1, 9_999) + " Market Street",
                    Line2 = r.Next(3) == 0 ? "Apt " + r.Next(1, 400) : null,
                    City = Cities[r.Next(Cities.Length)],
                    Region = "WA",
                    PostalCode = r.Next(10_000, 99_999).ToString(CultureInfo.InvariantCulture),
                    Country = "US",
                },
                Lines = lines,
            };
        }).ToList(),
    };
}

/// <summary>A user profile as a cache keeps it: some 20 fields, several of them null, roles and preferences.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class UserProfile
{
    [Key(0)] [ProtoMember(1)] public long Id;
    [Key(1)] [ProtoMember(2)] public string UserName;
    [Key(2)] [ProtoMember(3)] public string Email;
    [Key(3)] [ProtoMember(4)] public string DisplayName;
    [Key(4)] [ProtoMember(5)] public string AvatarUrl;
    [Key(5)] [ProtoMember(6)] public string Bio;
    [Key(6)] [ProtoMember(7)] public DateTime CreatedAt;
    [Key(7)] [ProtoMember(8)] public DateTime? LastLoginAt;
    [Key(8)] [ProtoMember(9)] public bool EmailVerified;
    [Key(9)] [ProtoMember(10)] public bool TwoFactor;
    [Key(10)] [ProtoMember(11)] public int? BirthYear;
    [Key(11)] [ProtoMember(12)] public string Country;
    [Key(12)] [ProtoMember(13)] public string TimeZone;
    [Key(13)] [ProtoMember(14)] public string Locale;
    [Key(14)] [ProtoMember(15)] public int Followers;
    [Key(15)] [ProtoMember(16)] public int Following;
    [Key(16)] [ProtoMember(17)] public List<string> Roles;
    [Key(17)] [ProtoMember(18)] public Dictionary<string, string> Preferences;

    public static UserProfile Create(Random r) => new()
    {
        Id = 48_213,
        UserName = "jdoe48213",
        Email = "jane.doe48213@example.com",
        DisplayName = "Jane Doe",
        AvatarUrl = "https://cdn.example.com/avatars/48213/256.png",
        Bio = null,
        CreatedAt = Workload.Epoch.AddDays(-r.Next(2_000)),
        LastLoginAt = Workload.Epoch.AddSeconds(r.Next()),
        EmailVerified = true,
        TwoFactor = false,
        BirthYear = null,
        Country = "US",
        TimeZone = "America/Los_Angeles",
        Locale = "en-US",
        Followers = r.Next(10_000),
        Following = r.Next(1_000),
        Roles = ["member", "beta-tester"],
        Preferences = new Dictionary<string, string>
        {
            ["theme"] = "dark", ["language"] = "en", ["newsletter"] = "weekly", ["notifications.email"] = "true",
            ["notifications.push"] = "false", ["currency"] = "USD", ["units"] = "metric", ["startPage"] = "/dashboard",
            ["density"] = "compact", ["privacy.profile"] = "friends",
        },
    };
}

/// <summary>An engine's own vector type (game engines bring their own math types).</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct Vec3
{
    [Key(0)] [ProtoMember(1)] public float X;
    [Key(1)] [ProtoMember(2)] public float Y;
    [Key(2)] [ProtoMember(3)] public float Z;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct Quat
{
    [Key(0)] [ProtoMember(1)] public float X;
    [Key(1)] [ProtoMember(2)] public float Y;
    [Key(2)] [ProtoMember(3)] public float Z;
    [Key(3)] [ProtoMember(4)] public float W;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct EntityState
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public short Kind;
    [Key(2)] [ProtoMember(3)] public Vec3 Position;
    [Key(3)] [ProtoMember(4)] public Vec3 Velocity;
    [Key(4)] [ProtoMember(5)] public Quat Rotation;
    [Key(5)] [ProtoMember(6)] public float Health;
    [Key(6)] [ProtoMember(7)] public byte Flags;
}

/// <summary>A server's state of 500 entities for one tick: what a game sends to its clients many times a second.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class GameSnapshot
{
    [Key(0)] [ProtoMember(1)] public long Tick;
    [Key(1)] [ProtoMember(2)] public int MatchId;
    [Key(2)] [ProtoMember(3)] public DateTime ServerTime;
    [Key(3)] [ProtoMember(4)] public EntityState[] Entities;

    public static GameSnapshot Create(Random r)
    {
        float F(float scale) => (r.NextSingle() * 2 - 1) * scale;
        return new GameSnapshot
        {
            Tick = 1_843_200,
            MatchId = 77_105,
            ServerTime = Workload.Epoch.AddSeconds(30_720),
            Entities = Enumerable.Range(0, 500).Select(i => new EntityState
            {
                Id = 10_000 + i,
                Kind = (short)r.Next(12),
                Position = new Vec3 { X = F(500), Y = F(20), Z = F(500) },
                Velocity = new Vec3 { X = F(8), Y = F(2), Z = F(8) },
                Rotation = new Quat { X = 0, Y = F(1), Z = 0, W = F(1) },
                Health = r.Next(0, 101),
                Flags = (byte)r.Next(256),
            }).ToArray(),
        };
    }
}

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

/// <summary>1,000 structured log events a service ships to its collector in one request.</summary>
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
            Timestamp = Workload.Epoch.AddMilliseconds(i * 37 + r.Next(37)),
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

/// <summary>
/// A domain event of an event-sourced service, read and written through its abstract base. The subtypes take the
/// lowest protobuf field numbers: protobuf-net reads the subtype's field before the base's (it writes it first), and
/// Google.Protobuf writes the fields in the order of their numbers.
/// </summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
[MemoryPackUnion(0, typeof(OrderPlaced)), MemoryPackUnion(1, typeof(PaymentCaptured)),
 MemoryPackUnion(2, typeof(ItemShipped)), MemoryPackUnion(3, typeof(CustomerRenamed))]
[Union(0, typeof(OrderPlaced)), Union(1, typeof(PaymentCaptured)), Union(2, typeof(ItemShipped)),
 Union(3, typeof(CustomerRenamed))]
[ProtoInclude(1, typeof(OrderPlaced)), ProtoInclude(2, typeof(PaymentCaptured)), ProtoInclude(3, typeof(ItemShipped)),
 ProtoInclude(4, typeof(CustomerRenamed))]
[JsonPolymorphic, JsonDerivedType(typeof(OrderPlaced), "order_placed"),
 JsonDerivedType(typeof(PaymentCaptured), "payment_captured"), JsonDerivedType(typeof(ItemShipped), "item_shipped"),
 JsonDerivedType(typeof(CustomerRenamed), "customer_renamed")]
public abstract partial class DomainEvent
{
    [Key(0)] [ProtoMember(10)] public long Sequence;
    [Key(1)] [ProtoMember(11)] public Guid AggregateId;
    [Key(2)] [ProtoMember(12)] public DateTime OccurredAt;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class OrderPlaced : DomainEvent
{
    [Key(3)] [ProtoMember(1)] public string OrderNumber;
    [Key(4)] [ProtoMember(2)] public decimal Amount;
    [Key(5)] [ProtoMember(3)] public int ItemCount;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class PaymentCaptured : DomainEvent
{
    [Key(3)] [ProtoMember(1)] public string PaymentId;
    [Key(4)] [ProtoMember(2)] public decimal Amount;
    [Key(5)] [ProtoMember(3)] public string Currency;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class ItemShipped : DomainEvent
{
    [Key(3)] [ProtoMember(1)] public string Carrier;
    [Key(4)] [ProtoMember(2)] public string TrackingNumber;
    [Key(5)] [ProtoMember(3)] public DateTime? EstimatedDelivery;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class CustomerRenamed : DomainEvent
{
    [Key(3)] [ProtoMember(1)] public string OldName;
    [Key(4)] [ProtoMember(2)] public string NewName;
}

/// <summary>500 domain events of four kinds, a page of an event store's stream.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class EventBatch
{
    [Key(0)] [ProtoMember(1)] public long FirstSequence;
    [Key(1)] [ProtoMember(2)] public List<DomainEvent> Events;

    public static EventBatch Create(Random r) => new()
    {
        FirstSequence = 5_200_000,
        Events = Enumerable.Range(0, 500).Select(i =>
        {
            DomainEvent e = r.Next(4) switch
            {
                0 => new OrderPlaced
                {
                    OrderNumber = "SO-2026-" + (300_000 + i), Amount = r.Next(500, 90_000) / 100m, ItemCount = r.Next(1, 9)
                },
                1 => new PaymentCaptured
                {
                    PaymentId = "pi_" + r.NextInt64().ToString("x16"), Amount = r.Next(500, 90_000) / 100m, Currency = "USD"
                },
                2 => new ItemShipped
                {
                    Carrier = r.Next(2) == 0 ? "UPS" : "FedEx", TrackingNumber = "1Z" + r.NextInt64().ToString("X14"),
                    EstimatedDelivery = r.Next(5) == 0 ? null : Workload.Epoch.AddDays(r.Next(2, 9)),
                },
                _ => new CustomerRenamed { OldName = Workload.Name(r), NewName = Workload.Name(r) },
            };
            e.Sequence = 5_200_000 + i;
            e.AggregateId = Workload.NewGuid(r);
            e.OccurredAt = Workload.Epoch.AddMilliseconds(i * 1_250L);
            return e;
        }).ToList(),
    };
}

/// <summary>System.Text.Json's source-generated metadata of the workloads (fields included: the models have no properties).</summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(RpcRequest)), JsonSerializable(typeof(OrdersPage)), JsonSerializable(typeof(UserProfile)),
 JsonSerializable(typeof(GameSnapshot)), JsonSerializable(typeof(LogBatch)), JsonSerializable(typeof(EventBatch))]
public partial class WorkloadJson : JsonSerializerContext;

public static class Workload
{
    public static readonly DateTime Epoch = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] First = ["Jane", "Wei", "Amara", "Lucas", "Priya", "Kenji", "Sofia", "Omar"];
    private static readonly string[] Last = ["Doe", "Zhang", "Okafor", "Silva", "Sharma", "Sato", "Rossi", "Haddad"];

    public static string Name(Random r) => First[r.Next(First.Length)] + " " + Last[r.Next(Last.Length)];

    public static Guid NewGuid(Random r)
    {
        Span<byte> bytes = stackalloc byte[16];
        r.NextBytes(bytes);
        return new Guid(bytes);
    }
}

/// <summary>The Google.Protobuf messages of the workloads, made from the same values (outside the timed region).</summary>
public static class WorkloadProto
{
    private static string S(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static Timestamp T(DateTime value) => Timestamp.FromDateTime(value);
    private static Timestamp T(DateTime? value) => value is { } v ? Timestamp.FromDateTime(v) : null;
    private static P.Vec3 V(Vec3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    public static P.RpcRequest ToProto(RpcRequest v) => new()
    {
        RequestId = v.RequestId, TraceId = v.TraceId.ToString(), Method = v.Method, User = v.User, Locale = v.Locale,
        SentAt = T(v.SentAt), Priority = (P.Priority)v.Priority, ItemIds = { v.ItemIds },
    };

    public static P.OrdersPage ToProto(OrdersPage v) => new()
    {
        Page = v.Page, PageSize = v.PageSize, TotalCount = v.TotalCount,
        Orders =
        {
            v.Orders.Select(o =>
            {
                var order = new P.Order
                {
                    Id = o.Id, Number = o.Number, CustomerId = o.CustomerId.ToString(), CustomerName = o.CustomerName,
                    Status = (P.OrderStatus)o.Status, CreatedAt = T(o.CreatedAt), ShippedAt = T(o.ShippedAt),
                    Subtotal = S(o.Subtotal), Tax = S(o.Tax), Total = S(o.Total),
                    ShipTo = new P.Address
                    {
                        Line1 = o.ShipTo.Line1, City = o.ShipTo.City, Region = o.ShipTo.Region,
                        PostalCode = o.ShipTo.PostalCode, Country = o.ShipTo.Country,
                    },
                    Lines =
                    {
                        o.Lines.Select(l =>
                        {
                            var line = new P.OrderLine
                                { Sku = l.Sku, Name = l.Name, Quantity = l.Quantity, UnitPrice = S(l.UnitPrice) };
                            if (l.Discount is { } d) line.Discount = S(d);
                            return line;
                        })
                    },
                };
                if (o.Coupon != null) order.Coupon = o.Coupon;
                if (o.ShipTo.Line2 != null) order.ShipTo.Line2 = o.ShipTo.Line2;
                return order;
            })
        },
    };

    public static P.UserProfile ToProto(UserProfile v)
    {
        var p = new P.UserProfile
        {
            Id = v.Id, UserName = v.UserName, Email = v.Email, DisplayName = v.DisplayName, CreatedAt = T(v.CreatedAt),
            LastLoginAt = T(v.LastLoginAt), EmailVerified = v.EmailVerified, TwoFactor = v.TwoFactor,
            Country = v.Country, TimeZone = v.TimeZone, Locale = v.Locale, Followers = v.Followers,
            Following = v.Following, Roles = { v.Roles }, Preferences = { v.Preferences },
        };
        if (v.AvatarUrl != null) p.AvatarUrl = v.AvatarUrl;
        if (v.Bio != null) p.Bio = v.Bio;
        if (v.BirthYear is { } year) p.BirthYear = year;
        return p;
    }

    public static P.GameSnapshot ToProto(GameSnapshot v) => new()
    {
        Tick = v.Tick, MatchId = v.MatchId, ServerTime = T(v.ServerTime),
        Entities =
        {
            v.Entities.Select(e => new P.EntityState
            {
                Id = e.Id, Kind = e.Kind, Position = V(e.Position), Velocity = V(e.Velocity),
                Rotation = new P.Quat { X = e.Rotation.X, Y = e.Rotation.Y, Z = e.Rotation.Z, W = e.Rotation.W },
                Health = e.Health, Flags = e.Flags,
            })
        },
    };

    public static P.LogBatch ToProto(LogBatch v) => new()
    {
        Service = v.Service, Host = v.Host,
        Events =
        {
            v.Events.Select(e => new P.LogEvent
            {
                Timestamp = T(e.Timestamp), Level = (P.Severity)e.Level, Template = e.Template, TraceId = e.TraceId,
                ElapsedMs = e.ElapsedMs, Tags = { e.Tags },
            })
        },
    };

    public static P.EventBatch ToProto(EventBatch v) => new()
    {
        FirstSequence = v.FirstSequence,
        Events =
        {
            v.Events.Select(e =>
            {
                var p = new P.DomainEvent
                    { Sequence = e.Sequence, AggregateId = e.AggregateId.ToString(), OccurredAt = T(e.OccurredAt) };
                switch (e)
                {
                    case OrderPlaced o:
                        p.OrderPlaced = new P.OrderPlaced
                            { OrderNumber = o.OrderNumber, Amount = S(o.Amount), ItemCount = o.ItemCount };
                        break;
                    case PaymentCaptured c:
                        p.PaymentCaptured = new P.PaymentCaptured
                            { PaymentId = c.PaymentId, Amount = S(c.Amount), Currency = c.Currency };
                        break;
                    case ItemShipped s:
                        p.ItemShipped = new P.ItemShipped
                            { Carrier = s.Carrier, TrackingNumber = s.TrackingNumber, EstimatedDelivery = T(s.EstimatedDelivery) };
                        break;
                    case CustomerRenamed n:
                        p.CustomerRenamed = new P.CustomerRenamed { OldName = n.OldName, NewName = n.NewName };
                        break;
                }

                return p;
            })
        },
    };
}
