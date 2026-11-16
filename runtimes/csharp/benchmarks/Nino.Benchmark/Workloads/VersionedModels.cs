using System;
using System.Collections.Generic;
using System.Linq;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark.Workloads;

// The two workloads whose bytes a previous release wrote: ProfileCache and IdleSave. Their own file because v3
// 3.3.13 tolerates versions only in a project that defines WEAK_VERSION_TOLERANCE, which changes the bytes of every
// type in it (all or nothing, per project): its run compiles this file into a project of its own
// (V3/Versioned/Nino.Benchmark.V3Versioned.csproj), so the other workloads keep v3's default format.

// ---------------------------------------------------------------- A cached profile written by the previous release
// (ProfileV1) and read by this one (ProfileV2: AvatarUrl removed, Pronouns and Badges added). Nino's keyed format and
// MemoryPack's version-tolerant one; MessagePack's integer keys, protobuf's field numbers and JSON's names tolerate it
// as they are.
// The two versions are two types here, one release's each, so they are sealed: a type that is not sealed is written
// with its type's id, which differs between the two (one type across releases keeps it).

#if NINO_BENCH_V3
[NinoType]
#else
[NinoType(format: NinoFormat.Keyed)]
#endif
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, ProtoContract,
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

#if NINO_BENCH_V3
// v3 writes every class with its type's id (a hash of its name): the previous release's name keeps that id here,
// where the two releases are two types ([NinoFormerName], v3's way to rename a type).
[NinoType, NinoFormerName("global::Nino.Benchmark.Workloads.ProfileV1")]
#else
[NinoType(format: NinoFormat.Keyed)]
#endif
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, ProtoContract,
 CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class ProfileV2
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public long Id;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public string UserName;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public string Email;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public string DisplayName;
    // 4: AvatarUrl, removed.
#if NINO_BENCH_V3
    // v3 3.3.13 cannot delete a member (its docs, Version Compatibility: "Delete a serialized field or property: No"),
    // since it reads members by position: the removed member's slot stays, read and dropped. The other libraries
    // skip it (no key of theirs).
    [IgnoreMember, MemoryPackIgnore] public string AvatarUrl { get => null; set { } }
#endif
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

// ---------------------------------------------------------------- Idle game: a save written by the previous
// release (IdleSaveV1) and read by this one (IdleSaveV2 appends prestige members) into the save the game already holds.
// Nino's version-tolerant format, MemoryPack's; the others tolerate appended members as they are, and read new saves.

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class Building
{
    [Key(0)] [ProtoMember(1)] public short Id;
    [Key(1)] [ProtoMember(2)] public int Level;
    [Key(2)] [ProtoMember(3)] public double Multiplier;
    [Key(3)] [ProtoMember(4)] public DateTime? UpgradeEndsAt;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class OfflineState
{
    [Key(0)] [ProtoMember(1)] public DateTime LastSeen;
    [Key(1)] [ProtoMember(2)] public double RatePerSecond;
    [Key(2)] [ProtoMember(3)] public double CapHours;
    [Key(3)] [ProtoMember(4)] public double Pending;
}

#if NINO_BENCH_V3
[NinoType]
#else
[NinoType(format: NinoFormat.VersionTolerant)]
#endif
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, ProtoContract,
 CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class IdleSaveV1
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public long PlayerId;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public DateTime SavedAt;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public double Gold;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public double Gems;
    [MemoryPackOrder(4)] [Key(4)] [ProtoMember(5)] public double LifetimeEarnings;
    [MemoryPackOrder(5)] [Key(5)] [ProtoMember(6)] public Dictionary<int, double> Resources;
    [MemoryPackOrder(6)] [Key(6)] [ProtoMember(7)] public List<Building> Buildings;
    [MemoryPackOrder(7)] [Key(7)] [ProtoMember(8)] public Dictionary<int, byte> Upgrades;
    [MemoryPackOrder(8)] [Key(8)] [ProtoMember(9)] public OfflineState Offline;
    [MemoryPackOrder(9)] [Key(9)] [ProtoMember(10)] public Dictionary<int, long> Stats;
    [MemoryPackOrder(10)] [Key(10)] [ProtoMember(11)] public List<int> Achievements;

    public static IdleSaveV1 Create(Random r) => new()
    {
        PlayerId = 4_100_200, SavedAt = Data.Epoch.AddHours(r.Next(1_000)), Gold = 1.234e18 * r.NextDouble(),
        Gems = r.Next(5_000), LifetimeEarnings = 9.87e21 * r.NextDouble(),
        Resources = Enumerable.Range(0, 50).ToDictionary(i => i, _ => Math.Pow(10, r.Next(3, 30)) * r.NextDouble()),
        Buildings = Enumerable.Range(0, 40).Select(i => new Building
        {
            Id = (short)i, Level = r.Next(1, 2_000), Multiplier = 1 + r.NextDouble() * 99,
            UpgradeEndsAt = r.Next(5) == 0 ? Data.Epoch.AddMinutes(r.Next(600)) : null,
        }).ToList(),
        Upgrades = Enumerable.Range(0, 120).ToDictionary(i => 5_000 + i, _ => (byte)r.Next(1, 6)),
        Offline = new OfflineState
            { LastSeen = Data.Epoch.AddHours(r.Next(1_000)), RatePerSecond = 3.3e9 * r.NextDouble(), CapHours = 8, Pending = 1e12 * r.NextDouble() },
        Stats = Enumerable.Range(0, 100).ToDictionary(i => i, _ => r.NextInt64(0, long.MaxValue / 1024)),
        Achievements = Enumerable.Range(0, 60).Select(i => 9_000 + i * 3).ToList(),
    };
}

#if NINO_BENCH_V3
// v3 writes every class with its type's id (a hash of its name): the previous release's name keeps that id here,
// where the two releases are two types ([NinoFormerName], v3's way to rename a type).
[NinoType, NinoFormerName("global::Nino.Benchmark.Workloads.IdleSaveV1")]
#else
[NinoType(format: NinoFormat.VersionTolerant)]
#endif
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, ProtoContract,
 CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class IdleSaveV2
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public long PlayerId;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public DateTime SavedAt;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public double Gold;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public double Gems;
    [MemoryPackOrder(4)] [Key(4)] [ProtoMember(5)] public double LifetimeEarnings;
    [MemoryPackOrder(5)] [Key(5)] [ProtoMember(6)] public Dictionary<int, double> Resources;
    [MemoryPackOrder(6)] [Key(6)] [ProtoMember(7)] public List<Building> Buildings;
    [MemoryPackOrder(7)] [Key(7)] [ProtoMember(8)] public Dictionary<int, byte> Upgrades;
    [MemoryPackOrder(8)] [Key(8)] [ProtoMember(9)] public OfflineState Offline;
    [MemoryPackOrder(9)] [Key(9)] [ProtoMember(10)] public Dictionary<int, long> Stats;
    [MemoryPackOrder(10)] [Key(10)] [ProtoMember(11)] public List<int> Achievements;
    [MemoryPackOrder(11)] [Key(11)] [ProtoMember(12)] public int PrestigeCount;
    [MemoryPackOrder(12)] [Key(12)] [ProtoMember(13)] public double PrestigeMultiplier;

    public static IdleSaveV2 From(IdleSaveV1 v) => new()
    {
        PlayerId = v.PlayerId, SavedAt = v.SavedAt, Gold = v.Gold, Gems = v.Gems, LifetimeEarnings = v.LifetimeEarnings,
        Resources = v.Resources, Buildings = v.Buildings, Upgrades = v.Upgrades, Offline = v.Offline, Stats = v.Stats,
        Achievements = v.Achievements, PrestigeCount = 3, PrestigeMultiplier = 2.75,
    };
}
