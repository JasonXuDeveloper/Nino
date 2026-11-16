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
#pragma warning disable PBN0023
namespace Nino.Benchmark.Workloads;

// ---------------------------------------------------------------- State sync: a server's snapshot of one tick, 64
// entities whose components are an interface (full entities carry every component, the others only what changed).
// The client deserializes it into the snapshot it already holds (Nino in place, MemoryPack overwrite; the others
// read a new one).

[NinoType, MemoryPackable, MemoryPackUnion(0, typeof(TransformComponent)), MemoryPackUnion(1, typeof(VelocityComponent)),
 MemoryPackUnion(2, typeof(HealthComponent)), MemoryPackUnion(3, typeof(BuffsComponent)), MemoryPackUnion(4, typeof(AnimationComponent))]
[Union(0, typeof(TransformComponent)), Union(1, typeof(VelocityComponent)), Union(2, typeof(HealthComponent)),
 Union(3, typeof(BuffsComponent)), Union(4, typeof(AnimationComponent))]
[ProtoContract, ProtoInclude(1, typeof(TransformComponent)), ProtoInclude(2, typeof(VelocityComponent)),
 ProtoInclude(3, typeof(HealthComponent)), ProtoInclude(4, typeof(BuffsComponent)), ProtoInclude(5, typeof(AnimationComponent))]
[JsonPolymorphic, JsonDerivedType(typeof(TransformComponent), "transform"), JsonDerivedType(typeof(VelocityComponent), "velocity"),
 JsonDerivedType(typeof(HealthComponent), "health"), JsonDerivedType(typeof(BuffsComponent), "buffs"),
 JsonDerivedType(typeof(AnimationComponent), "animation")]
public partial interface IComponent;

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class TransformComponent : IComponent
{
    [Key(0)] [ProtoMember(1)] public Vec3 Position;
    [Key(1)] [ProtoMember(2)] public Quat Rotation;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class VelocityComponent : IComponent
{
    [Key(0)] [ProtoMember(1)] public Vec3 Linear;
    [Key(1)] [ProtoMember(2)] public float Angular;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class HealthComponent : IComponent
{
    [Key(0)] [ProtoMember(1)] public int Hp;
    [Key(1)] [ProtoMember(2)] public int MaxHp;
    [Key(2)] [ProtoMember(3)] public int Shield;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct Buff
{
    [Key(0)] [ProtoMember(1)] public short Id;
    [Key(1)] [ProtoMember(2)] public byte Stacks;
    [Key(2)] [ProtoMember(3)] public float Remaining;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class BuffsComponent : IComponent
{
    [Key(0)] [ProtoMember(1)] public List<Buff> Buffs;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class AnimationComponent : IComponent
{
    [Key(0)] [ProtoMember(1)] public short Clip;
    [Key(1)] [ProtoMember(2)] public float Time;
    [Key(2)] [ProtoMember(3)] public byte Layer;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class NetEntity
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public short Archetype;
    [Key(2)] [ProtoMember(3)] public bool Full;
    [Key(3)] [ProtoMember(4)] public uint ChangedMask;
    [Key(4)] [ProtoMember(5)] public List<IComponent> Components;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class StateSnapshot
{
    [Key(0)] [ProtoMember(1)] public long Tick;
    [Key(1)] [ProtoMember(2)] public long AckedInputTick;
    [Key(2)] [ProtoMember(3)] public List<NetEntity> Entities;

    public static StateSnapshot Create(Random r) => new()
    {
        Tick = 1_843_200, AckedInputTick = 1_843_197,
        Entities = Enumerable.Range(0, 64).Select(i =>
        {
            bool full = i % 4 == 0;
            var all = new List<IComponent>
            {
                new TransformComponent { Position = Data.V(r, 500), Rotation = Data.Q(r) },
                new VelocityComponent { Linear = Data.V(r, 8), Angular = r.NextSingle() },
                new HealthComponent { Hp = r.Next(1, 1_000), MaxHp = 1_000, Shield = r.Next(200) },
                new BuffsComponent { Buffs = Enumerable.Range(0, r.Next(1, 5))
                    .Select(_ => new Buff { Id = (short)r.Next(300), Stacks = (byte)r.Next(1, 5), Remaining = r.NextSingle() * 10 }).ToList() },
                new AnimationComponent { Clip = (short)r.Next(40), Time = r.NextSingle(), Layer = (byte)r.Next(3) },
            };
            var components = full ? all : all.Where((_, c) => c < 2 || r.Next(4) == 0).Take(r.Next(1, 4)).ToList();
            return new NetEntity
            {
                Id = 10_000 + i, Archetype = (short)r.Next(12), Full = full,
                ChangedMask = full ? 0b11111u : (uint)r.Next(1, 32), Components = components,
            };
        }).ToList(),
    };
}

// ---------------------------------------------------------------- Lockstep: what a frame-sync server broadcasts,
// 10 frames of 8 players' input commands (an interface), the last frames repeated for loss.

[NinoType, MemoryPackable, MemoryPackUnion(0, typeof(MoveCommand)), MemoryPackUnion(1, typeof(CastCommand)),
 MemoryPackUnion(2, typeof(UseItemCommand)), MemoryPackUnion(3, typeof(StopCommand))]
[Union(0, typeof(MoveCommand)), Union(1, typeof(CastCommand)), Union(2, typeof(UseItemCommand)), Union(3, typeof(StopCommand))]
[ProtoContract, ProtoInclude(1, typeof(MoveCommand)), ProtoInclude(2, typeof(CastCommand)),
 ProtoInclude(3, typeof(UseItemCommand)), ProtoInclude(4, typeof(StopCommand))]
[JsonPolymorphic, JsonDerivedType(typeof(MoveCommand), "move"), JsonDerivedType(typeof(CastCommand), "cast"),
 JsonDerivedType(typeof(UseItemCommand), "item"), JsonDerivedType(typeof(StopCommand), "stop")]
public partial interface ICommand;

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class MoveCommand : ICommand
{
    [Key(0)] [ProtoMember(1)] public short DirX;
    [Key(1)] [ProtoMember(2)] public short DirY;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class CastCommand : ICommand
{
    [Key(0)] [ProtoMember(1)] public short SkillId;
    [Key(1)] [ProtoMember(2)] public int TargetId;
    [Key(2)] [ProtoMember(3)] public short X;
    [Key(3)] [ProtoMember(4)] public short Y;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class UseItemCommand : ICommand
{
    [Key(0)] [ProtoMember(1)] public int ItemId;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class StopCommand : ICommand
{
    [Key(0)] [ProtoMember(1)] public byte Reason;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class PlayerInput
{
    [Key(0)] [ProtoMember(1)] public byte Slot;
    [Key(1)] [ProtoMember(2)] public List<ICommand> Commands;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class LockstepFrame
{
    [Key(0)] [ProtoMember(1)] public int Frame;
    [Key(1)] [ProtoMember(2)] public List<PlayerInput> Inputs;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class LockstepFrames
{
    [Key(0)] [ProtoMember(1)] public int FirstFrame;
    [Key(1)] [ProtoMember(2)] public uint Checksum;
    [Key(2)] [ProtoMember(3)] public List<LockstepFrame> Frames;

    public static LockstepFrames Create(Random r) => new()
    {
        FirstFrame = 36_120, Checksum = 0x9e3779b9,
        Frames = Enumerable.Range(0, 10).Select(f => new LockstepFrame
        {
            Frame = 36_120 + f,
            Inputs = Enumerable.Range(0, 8).Select(p => new PlayerInput
            {
                Slot = (byte)p,
                Commands = Enumerable.Range(0, r.Next(1, 3)).Select(_ => (ICommand)(r.Next(10) switch
                {
                    < 6 => new MoveCommand { DirX = (short)r.Next(-1000, 1000), DirY = (short)r.Next(-1000, 1000) },
                    < 8 => new CastCommand { SkillId = (short)r.Next(1, 40), TargetId = r.Next(8), X = (short)r.Next(4096), Y = (short)r.Next(4096) },
                    8 => new UseItemCommand { ItemId = r.Next(100, 200) },
                    _ => new StopCommand { Reason = 1 },
                })).ToList(),
            }).ToList(),
        }).ToList(),
    };
}

// ---------------------------------------------------------------- MMO: one AOI update to one client, about 200
// entity events through an abstract base, and the chat lines of that tick.

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
[MemoryPackUnion(0, typeof(EntityEnter)), MemoryPackUnion(1, typeof(EntityLeave)), MemoryPackUnion(2, typeof(EntityMove)),
 MemoryPackUnion(3, typeof(SkillCast)), MemoryPackUnion(4, typeof(BuffChanged))]
[Union(0, typeof(EntityEnter)), Union(1, typeof(EntityLeave)), Union(2, typeof(EntityMove)), Union(3, typeof(SkillCast)),
 Union(4, typeof(BuffChanged))]
[ProtoInclude(1, typeof(EntityEnter)), ProtoInclude(2, typeof(EntityLeave)), ProtoInclude(3, typeof(EntityMove)),
 ProtoInclude(4, typeof(SkillCast)), ProtoInclude(5, typeof(BuffChanged))]
[JsonPolymorphic, JsonDerivedType(typeof(EntityEnter), "enter"), JsonDerivedType(typeof(EntityLeave), "leave"),
 JsonDerivedType(typeof(EntityMove), "move"), JsonDerivedType(typeof(SkillCast), "cast"), JsonDerivedType(typeof(BuffChanged), "buff")]
public abstract partial class AoiEvent
{
    [Key(0)] [ProtoMember(20)] public int EntityId;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class EntityEnter : AoiEvent
{
    [Key(1)] [ProtoMember(1)] public short Type;
    [Key(2)] [ProtoMember(2)] public string Name;
    [Key(3)] [ProtoMember(3)] public Vec3 Position;
    [Key(4)] [ProtoMember(4)] public int Level;
    [Key(5)] [ProtoMember(5)] public int? GuildId;
    [Key(6)] [ProtoMember(6)] public List<int> Equipment;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class EntityLeave : AoiEvent
{
    [Key(1)] [ProtoMember(1)] public byte Reason;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class EntityMove : AoiEvent
{
    [Key(1)] [ProtoMember(1)] public Vec3 Position;
    [Key(2)] [ProtoMember(2)] public Vec3 Direction;
    [Key(3)] [ProtoMember(3)] public float Speed;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct DamageHit
{
    [Key(0)] [ProtoMember(1)] public int TargetId;
    [Key(1)] [ProtoMember(2)] public int Amount;
    [Key(2)] [ProtoMember(3)] public bool Critical;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class SkillCast : AoiEvent
{
    [Key(1)] [ProtoMember(1)] public short SkillId;
    [Key(2)] [ProtoMember(2)] public Vec3 Target;
    [Key(3)] [ProtoMember(3)] public List<DamageHit> Hits;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class BuffChanged : AoiEvent
{
    [Key(1)] [ProtoMember(1)] public short BuffId;
    [Key(2)] [ProtoMember(2)] public byte Stacks;
    [Key(3)] [ProtoMember(3)] public float Duration;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class ChatMessage
{
    [Key(0)] [ProtoMember(1)] public byte Channel;
    [Key(1)] [ProtoMember(2)] public long SenderId;
    [Key(2)] [ProtoMember(3)] public string SenderName;
    [Key(3)] [ProtoMember(4)] public string Text;
    [Key(4)] [ProtoMember(5)] public DateTime SentAt;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class AoiUpdate
{
    [Key(0)] [ProtoMember(1)] public long ServerTick;
    [Key(1)] [ProtoMember(2)] public int SceneId;
    [Key(2)] [ProtoMember(3)] public List<AoiEvent> Events;
    [Key(3)] [ProtoMember(4)] public List<ChatMessage> Chat;

    private static readonly string[] Lines = ["lfg dungeon, need healer", "wts epic sword 500g", "gg", "anyone for raid tonight?",
        "where is the blacksmith", "guild recruiting, pm me"];

    public static AoiUpdate Create(Random r) => new()
    {
        ServerTick = 9_200_311, SceneId = 1_002,
        Events = Enumerable.Range(0, 200).Select(i => (AoiEvent)(r.Next(10) switch
        {
            0 => new EntityEnter { EntityId = 50_000 + i, Type = (short)r.Next(30), Name = Data.Name(r), Position = Data.V(r, 300),
                Level = r.Next(1, 61), GuildId = r.Next(3) == 0 ? null : r.Next(1, 900),
                Equipment = Enumerable.Range(0, 6).Select(_ => r.Next(10_000, 20_000)).ToList() },
            1 => new EntityLeave { EntityId = 50_000 + i, Reason = (byte)r.Next(3) },
            < 7 => new EntityMove { EntityId = 50_000 + i, Position = Data.V(r, 300), Direction = Data.V(r, 1), Speed = r.NextSingle() * 7 },
            < 9 => new SkillCast { EntityId = 50_000 + i, SkillId = (short)r.Next(1, 200), Target = Data.V(r, 300),
                Hits = Enumerable.Range(0, r.Next(1, 6)).Select(_ => new DamageHit
                    { TargetId = r.Next(50_000, 50_200), Amount = r.Next(1, 5_000), Critical = r.Next(5) == 0 }).ToList() },
            _ => new BuffChanged { EntityId = 50_000 + i, BuffId = (short)r.Next(1, 300), Stacks = (byte)r.Next(1, 6), Duration = r.NextSingle() * 30 },
        })).ToList(),
        Chat = Enumerable.Range(0, 5).Select(_ => new ChatMessage
        {
            Channel = (byte)r.Next(4), SenderId = r.Next(1, 2_000_000), SenderName = Data.Name(r), Text = Lines[r.Next(Lines.Length)],
            SentAt = Data.Epoch.AddSeconds(r.Next(60)),
        }).ToList(),
    };
}

// ---------------------------------------------------------------- SLG: one region of the world map, 64 x 64 tiles,
// its cities, the armies on it and their marches; cities and armies refer to their alliance, one object shared by
// many (Nino tracks references, MemoryPack's CircularReference, System.Text.Json's Preserve; MessagePack and
// protobuf write a copy per reference).

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct Tile
{
    [Key(0)] [ProtoMember(1)] public byte Terrain;
    [Key(1)] [ProtoMember(2)] public byte Level;
    [Key(2)] [ProtoMember(3)] public int OwnerId;
}

#if NINO_BENCH_V3
[NinoType]
#else
[NinoType(trackReferences: true)]
#endif
[MemoryPackable(GenerateType.CircularReference), MessagePackObject, ProtoContract]
public sealed partial class Alliance
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public int Id;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public string Name;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public string Tag;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public int Members;
    [MemoryPackOrder(4)] [Key(4)] [ProtoMember(5)] public long Power;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public partial struct MapPoint
{
    [Key(0)] [ProtoMember(1)] public short X;
    [Key(1)] [ProtoMember(2)] public short Y;
}

public enum MarchKind { Attack, Gather, Reinforce, Scout }

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public sealed partial class March
{
    [Key(0)] [ProtoMember(1)] public MarchKind Kind;
    [Key(1)] [ProtoMember(2)] public DateTime Departed;
    [Key(2)] [ProtoMember(3)] public DateTime Arrives;
    [Key(3)] [ProtoMember(4)] public List<MapPoint> Path;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class City
{
    [Key(0)] [ProtoMember(1)] public int Id;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public int OwnerId;
    [Key(3)] [ProtoMember(4)] public MapPoint Position;
    [Key(4)] [ProtoMember(5)] public byte Level;
    [Key(5)] [ProtoMember(6)] public Dictionary<int, int> Buildings;
    [Key(6)] [ProtoMember(7)] public Alliance Alliance;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class Army
{
    [Key(0)] [ProtoMember(1)] public long Id;
    [Key(1)] [ProtoMember(2)] public int OwnerId;
    [Key(2)] [ProtoMember(3)] public Alliance Alliance;
    [Key(3)] [ProtoMember(4)] public Dictionary<int, int> Troops;
    [Key(4)] [ProtoMember(5)] public March March;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class WorldRegion
{
    [Key(0)] [ProtoMember(1)] public short RegionX;
    [Key(1)] [ProtoMember(2)] public short RegionY;
    [Key(2)] [ProtoMember(3)] public Tile[] Tiles;
    [Key(3)] [ProtoMember(4)] public List<Alliance> Alliances;
    [Key(4)] [ProtoMember(5)] public List<City> Cities;
    [Key(5)] [ProtoMember(6)] public List<Army> Armies;

    public static WorldRegion Create(Random r)
    {
        var alliances = Enumerable.Range(0, 6).Select(i => new Alliance
            { Id = 700 + i, Name = "Alliance " + (char)('A' + i), Tag = "AL" + i, Members = r.Next(20, 100), Power = r.NextInt64(1_000_000, 90_000_000) }).ToList();
        MapPoint P() => new() { X = (short)r.Next(64), Y = (short)r.Next(64) };
        return new WorldRegion
        {
            RegionX = 12, RegionY = 7, Alliances = alliances,
            Tiles = Enumerable.Range(0, 64 * 64).Select(_ => new Tile
                { Terrain = (byte)r.Next(6), Level = (byte)r.Next(1, 9), OwnerId = r.Next(4) == 0 ? r.Next(1, 500_000) : 0 }).ToArray(),
            Cities = Enumerable.Range(0, 40).Select(i => new City
            {
                Id = 30_000 + i, Name = "City " + i, OwnerId = r.Next(1, 500_000), Position = P(), Level = (byte)r.Next(1, 26),
                Buildings = Enumerable.Range(0, 8).ToDictionary(b => 100 + b, _ => r.Next(1, 26)), Alliance = alliances[r.Next(6)],
            }).ToList(),
            Armies = Enumerable.Range(0, 120).Select(i => new Army
            {
                Id = 8_000_000 + i, OwnerId = r.Next(1, 500_000), Alliance = alliances[r.Next(6)],
                Troops = Enumerable.Range(0, 4).ToDictionary(t => t + 1, _ => r.Next(100, 50_000)),
                March = r.Next(3) == 0 ? null : new March
                {
                    Kind = (MarchKind)r.Next(4), Departed = Data.Epoch.AddSeconds(r.Next(3_600)),
                    Arrives = Data.Epoch.AddSeconds(r.Next(3_600, 9_000)),
                    Path = Enumerable.Range(0, r.Next(8, 30)).Select(_ => P()).ToList(),
                },
            }).ToList(),
        };
    }
}

// ---------------------------------------------------------------- Card game: the replay of one battle, 300 actions in
// two levels of abstract classes (spell effects under the action base), referring to the battle's card definitions,
// one object per card shared by decks, hands and actions (references and copies as in WorldRegion).

public enum CardType { Minion, Spell, Weapon }

#if NINO_BENCH_V3
[NinoType]
#else
[NinoType(trackReferences: true)]
#endif
[MemoryPackable(GenerateType.CircularReference), MessagePackObject, ProtoContract]
public sealed partial class CardDef
{
    [MemoryPackOrder(0)] [Key(0)] [ProtoMember(1)] public int Id;
    [MemoryPackOrder(1)] [Key(1)] [ProtoMember(2)] public string Name;
    [MemoryPackOrder(2)] [Key(2)] [ProtoMember(3)] public byte Cost;
    [MemoryPackOrder(3)] [Key(3)] [ProtoMember(4)] public CardType Type;
    [MemoryPackOrder(4)] [Key(4)] [ProtoMember(5)] public short Attack;
    [MemoryPackOrder(5)] [Key(5)] [ProtoMember(6)] public short Health;
    [MemoryPackOrder(6)] [Key(6)] [ProtoMember(7)] public string Text;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
[MemoryPackUnion(0, typeof(PlayCard)), MemoryPackUnion(1, typeof(AttackAction)), MemoryPackUnion(2, typeof(DealDamage)),
 MemoryPackUnion(3, typeof(Heal)), MemoryPackUnion(4, typeof(Summon)), MemoryPackUnion(5, typeof(DrawCard)), MemoryPackUnion(6, typeof(EndTurn))]
[Union(0, typeof(PlayCard)), Union(1, typeof(AttackAction)), Union(2, typeof(DealDamage)), Union(3, typeof(Heal)),
 Union(4, typeof(Summon)), Union(5, typeof(DrawCard)), Union(6, typeof(EndTurn))]
[ProtoInclude(1, typeof(PlayCard)), ProtoInclude(2, typeof(AttackAction)), ProtoInclude(3, typeof(SpellEffect)),
 ProtoInclude(4, typeof(DrawCard)), ProtoInclude(5, typeof(EndTurn))]
[JsonPolymorphic, JsonDerivedType(typeof(PlayCard), "play"), JsonDerivedType(typeof(AttackAction), "attack"),
 JsonDerivedType(typeof(DealDamage), "damage"), JsonDerivedType(typeof(Heal), "heal"), JsonDerivedType(typeof(Summon), "summon"),
 JsonDerivedType(typeof(DrawCard), "draw"), JsonDerivedType(typeof(EndTurn), "end")]
public abstract partial class CardAction
{
    [Key(0)] [ProtoMember(20)] public short Turn;
    [Key(1)] [ProtoMember(21)] public byte Actor;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class PlayCard : CardAction
{
    [Key(2)] [ProtoMember(1)] public CardDef Card;
    [Key(3)] [ProtoMember(2)] public byte Slot;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class AttackAction : CardAction
{
    [Key(2)] [ProtoMember(1)] public int AttackerUid;
    [Key(3)] [ProtoMember(2)] public int TargetUid;
    [Key(4)] [ProtoMember(3)] public short Damage;
}

/// <summary>The second level: what a spell card does, with the card that did it.</summary>
[NinoType, ProtoContract,
 ProtoInclude(1, typeof(DealDamage)), ProtoInclude(2, typeof(Heal)), ProtoInclude(3, typeof(Summon))]
public abstract partial class SpellEffect : CardAction
{
    [Key(2)] [ProtoMember(20)] public CardDef Source;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class DealDamage : SpellEffect
{
    [Key(3)] [ProtoMember(1)] public List<int> Targets;
    [Key(4)] [ProtoMember(2)] public short Amount;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class Heal : SpellEffect
{
    [Key(3)] [ProtoMember(1)] public int Target;
    [Key(4)] [ProtoMember(2)] public short Amount;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class Summon : SpellEffect
{
    [Key(3)] [ProtoMember(1)] public CardDef Minion;
    [Key(4)] [ProtoMember(2)] public byte Slot;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class DrawCard : CardAction
{
    [Key(2)] [ProtoMember(1)] public CardDef Card;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class EndTurn : CardAction
{
    [Key(2)] [ProtoMember(1)] public int Elapsed;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class BattlePlayer
{
    [Key(0)] [ProtoMember(1)] public long PlayerId;
    [Key(1)] [ProtoMember(2)] public string Name;
    [Key(2)] [ProtoMember(3)] public int Hero;
    [Key(3)] [ProtoMember(4)] public List<CardDef> Deck;
    [Key(4)] [ProtoMember(5)] public List<CardDef> Hand;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract]
public sealed partial class CardBattleLog
{
    [Key(0)] [ProtoMember(1)] public long BattleId;
    [Key(1)] [ProtoMember(2)] public List<CardDef> Cards;
    [Key(2)] [ProtoMember(3)] public List<BattlePlayer> Players;
    [Key(3)] [ProtoMember(4)] public List<CardAction> Actions;

    public static CardBattleLog Create(Random r)
    {
        var cards = Enumerable.Range(0, 40).Select(i => new CardDef
        {
            Id = 1_000 + i, Name = "Card " + i, Cost = (byte)r.Next(0, 11), Type = (CardType)(i % 3),
            Attack = (short)r.Next(0, 12), Health = (short)r.Next(1, 12),
            Text = i % 4 == 0 ? "Battlecry: deal 2 damage." : "Taunt. Deathrattle: summon a 1/1.",
        }).ToList();
        CardDef C() => cards[r.Next(cards.Count)];
        return new CardBattleLog
        {
            BattleId = 66_000_123, Cards = cards,
            Players = Enumerable.Range(0, 2).Select(p => new BattlePlayer
            {
                PlayerId = 2_000_000 + p, Name = Data.Name(r), Hero = r.Next(1, 10),
                Deck = Enumerable.Range(0, 30).Select(_ => C()).ToList(), Hand = Enumerable.Range(0, 5).Select(_ => C()).ToList(),
            }).ToList(),
            Actions = Enumerable.Range(0, 300).Select(i =>
            {
                CardAction a = r.Next(10) switch
                {
                    < 3 => new PlayCard { Card = C(), Slot = (byte)r.Next(7) },
                    < 5 => new AttackAction { AttackerUid = r.Next(1, 60), TargetUid = r.Next(1, 60), Damage = (short)r.Next(1, 10) },
                    5 => new DealDamage { Source = C(), Targets = Enumerable.Range(0, r.Next(1, 4)).Select(_ => r.Next(1, 60)).ToList(), Amount = (short)r.Next(1, 8) },
                    6 => new Heal { Source = C(), Target = r.Next(1, 60), Amount = (short)r.Next(1, 8) },
                    7 => new Summon { Source = C(), Minion = C(), Slot = (byte)r.Next(7) },
                    8 => new DrawCard { Card = C() },
                    _ => new EndTurn { Elapsed = r.Next(5_000, 75_000) },
                };
                a.Turn = (short)(i / 12 + 1);
                a.Actor = (byte)(i / 12 % 2);
                return a;
            }).ToList(),
        };
    }
}
