---
name: nino-serialization
description: "Write correct, fast C# with Nino v4, the source-generated binary serializer (NuGet Nino, Unity via OpenUPM): [NinoType] types, positional / version-tolerant / keyed formats for saves and messages that outlive a release (guarded by NinoLayouts.cs), polymorphism, callbacks, zero-allocation buffers, streams and pooling, raw structs and Vector-of-T (NINO029), NinoSettings.Untrusted for untrusted bytes, Unity/IL2CPP/Burst/HybridCLR, ASP.NET Core, and TypeScript, Go and Rust codecs from nino-gen. Use this skill whenever code uses or should use Nino - writing, reviewing, debugging or porting to it (from MemoryPack, MessagePack or Nino v3), fixing a NINO0xx/NINOGEN diagnostic or a NinoDataException - even when the user just says 'the serializer' in a project referencing Nino. Triggers: Nino, Nino.Core, [NinoType], [NinoMember], NinoSerializer, NinoDeserializer, NinoArrayBufferWriter, NinoSettings, NinoDataException, NINO0xx, nino-gen, application/x-nino, 用 Nino 序列化, Nino 反序列化, 给类型加 NinoType, Nino 存档, Nino 网络消息."
---

# Writing code with Nino v4

Nino v4 (namespace `Nino.Core`) generates a serializer for every `[NinoType]` at compile time. The bytes carry no
schema and no member names, so most mistakes are not compile errors: they are data that cannot be read after the next
release, objects that come back as the wrong type, or hostile input that reads without the checks it needed. The
decisions below prevent those. Docs: https://nino.xgamedev.net/en/docs/v4 (every page also as Markdown: add `.md`,
e.g. `https://nino.xgamedev.net/en/docs/v4/start.md`; index for models: https://nino.xgamedev.net/llms.txt).

Read a reference when the task reaches it:

| Reference | Read it when |
| --- | --- |
| [references/types.md](references/types.md) | a member type is unusual (BCL types, collections, `object`, pointers, `Vector<T>`), structs are copied raw, NINO007/NINO029, or data crosses runtimes (Mono, IL2CPP, 32-bit, other languages) |
| [references/performance.md](references/performance.md) | zero allocation, per-frame/per-tick messages, buffer reuse, reuse deserialization, `[NinoPooled]`, `[NinoFactory]`, several values in one buffer, build settings |
| [references/platforms.md](references/platforms.md) | Unity, IL2CPP, Burst, HybridCLR, Native AOT, Godot, ASP.NET Core |
| [references/custom-formatters.md](references/custom-formatters.md) | a member needs its own encoding, or a type Nino does not know |
| [references/diagnostics.md](references/diagnostics.md) | any `NINO0xx`/`NINOGEN` diagnostic or a runtime error message from Nino |
| [references/other-languages.md](references/other-languages.md) | TypeScript, Go or Rust must read or write the same bytes (nino-gen), or a message's bytes need explaining (nino-gen inspect) |

## Install

- .NET and Godot 4: `dotnet add package Nino --prerelease` in **every project that declares `[NinoType]`s** (the
  generator runs per project). v4 is a prerelease: without `--prerelease` NuGet installs 3.9.x, which cannot read or
  write v4 data. Optional, also `--prerelease`: `Nino.Immutable`, `Nino.AspNetCore`, `Nino.Codegen`, the `nino-gen`
  tool (`Nino.Codegen.Tool`).
- Unity 2022.3+: `openupm add com.jasonxudeveloper.nino`.
- Requirements: .NET 8+, Unity or Mono (Nino.Core targets netstandard2.1, net8.0 and net10.0; no .NET Framework),
  Roslyn 4.3+ and `LangVersion` 9+ in the declaring project. Every target framework, runtime and CPU writes
  identical bytes.
- Nothing to register or initialize: generated registries load on first use.

## Decide in this order

1. **How long does the data live?** Pick the format before anything is stored, because changing it later makes
   existing data unreadable (table below). Messages between builds you ship together: positional. Saves or
   messages between versions: `VersionTolerant` or `Keyed`.
2. **Who wrote the bytes?** Anything a player, client or other party could craft (network messages, shared saves,
   uploads, request bodies) is untrusted: `NinoSettings.Untrusted = true` at startup and catch `NinoDataException`
   ([Untrusted input](#untrusted-input)).
3. **Is a slot polymorphic?** Seal every class without subtypes; serialize through the common `[NinoType]` base.
4. **Is it a hot path?** Reuse one buffer writer and deserialize into existing instances
   ([references/performance.md](references/performance.md)).
5. **Where does it run?** Unity and other pre-.NET 8 targets need `partial` types for private members; IL2CPP and AOT
   need every type visible to the generator ([references/platforms.md](references/platforms.md)).
6. **Any structs, SIMD vectors or pointers?** Structs of raw fields are copied as memory in one canonical layout;
   `Vector<T>` is refused (NINO029) because its size depends on the CPU
   ([references/types.md](references/types.md)).

## Declaring types

- `[NinoType]` on a `class`, `struct`, `record`, `record struct` or `interface`. The type and every type containing it
  are `public` or `internal`, never a `static` class or `ref struct` (NINO001).
- Options, as named arguments: `[NinoType(autoCollect: true, includeNonPublicMembers: false, inheritedBySubtypes: true,
  format: NinoFormat.Positional, trackReferences: false)]`.
- `autoCollect: true` (default) writes every public field and every public property with a getter and a `set`/`init`:
  `[NinoType]` base classes first (root first), then the type's own members in declaration order. Get-only
  auto-properties are skipped unless a constructor parameter has the same name (ignoring case); an explicit
  implementation of a `[NinoType]` interface's member is written through the interface, with that member's
  attributes; a member of a type Nino cannot write is skipped with NINO007 (delegates, `Task`, pointers, `Type`,
  `Span<T>`: add `[NinoIgnore]`).
- `[NinoIgnore]` drops a member. `[NinoType(false)]` writes only `[NinoMember(index)]` members, by index ascending
  (`[property: NinoMember(n)]` on record parameters). Mixing them up: NINO010 to NINO013.
- Non-public members need `includeNonPublicMembers: true` (else NINO009). Before .NET 8 (Unity,
  `netstandard`), and for generic types before .NET 9, the type and every containing type must be `partial`
  (NINO002): write `partial` anyway when the code may build for Unity.
- Subtypes inherit `[NinoType]` and its options from the nearest `[NinoType]` base or interface. A subtype's own
  `[NinoType]` replaces the inherited options (repeat `format:`); `trackReferences` is inherited regardless.
  `inheritedBySubtypes: false` makes subtypes declare their own (NINO003). On a `[NinoType]` interface's members only
  `[NinoMember(index)]` counts: implementing types write their own members with their own attributes, so put
  `[NinoUtf8]`, `[NinoMemberFormatter]` and `[NinoIgnore]` there (NINO033); only an explicit implementation writes
  the interface member, with its attributes.
- Generic types work (`Box<T>`) when their type arguments are serializable. A closed generic type the process only
  *reads* through a base and never names in code: `NinoPolymorphic.TryRegister<Box<int>>()` at startup (false when
  it cannot be registered).
- Strings: one byte per char when every char is at most U+00FF, UTF-16 otherwise. `[NinoUtf8]` on a member writes
  UTF-8: for mostly-ASCII text with some wider chars, not for mostly-CJK text.
- Structs have no header; an unmanaged struct of raw fields is copied as one block in a layout that is the same on
  every runtime (details, padding, `bool` flags and pointers: [references/types.md](references/types.md)).

```csharp
[NinoType]
public sealed class Order
{
    public long Id;
    public string Customer { get; set; }
    public List<OrderLine> Lines;
    public DateTime? ShippedAt;
    [NinoIgnore] public decimal CachedTotal;
}

[NinoType]
public struct OrderLine { public int ProductId; public int Quantity; } // raw: 8 bytes, one block per list

[NinoType(includeNonPublicMembers: true)]
public partial class Account { private long _balance; public int Id; }
```

### Constructors

Nino uses, in order: a constructor or static factory marked `[NinoConstructor]`; else, when every member is
assignable, a constructor callable without arguments (or a `[NinoFactory]`), then fills the members; else the
accessible constructor with the most parameters matching serialized members by name (ignoring case; a positional
record's primary constructor); structs fall back to `new T()`; otherwise NINO004.
- `[NinoConstructor(nameof(Id), nameof(Name))]` lists members when parameter names differ (NINO005); it must be
  `public`/`internal` (NINO021). A read-only member that is not a parameter is read and dropped (NINO006).
- `required` members: a `[SetsRequiredMembers]` constructor, or every required member serialized and settable.
- Types re-created through a constructor (records, `init`/get-only/`readonly` members) cannot be filled in place,
  tracked (NINO016) or pooled (NINO018). Give hot-path types a parameterless constructor and settable members.

### Formats: choose before data is stored

| | `Positional` (default) | `VersionTolerant` | `Keyed` |
| --- | --- | --- | --- |
| Change members | nothing (renaming is free: names are not written) | append to the most derived type only; renaming is free | add, remove, reorder; rename with `[NinoFormerName("Old")]` |
| Cost | none | 4 bytes per object | 4 per object + 8 per member |
| For | same-build messages, rebuildable caches | messages between versions, saves that only grow | config, documents, saves edited for years |

- Changing a type's `format` makes its existing data unreadable. Changing a member's type is never tolerated.
- Version-tolerant hierarchy: never append to a base class with subclasses (it shifts their members, silently); use
  `Keyed`. Missing members keep their initializer or existing value.
- Keyed: two names hashing alike is NINO014; member `[NinoFormerName]` on a non-keyed type is NINO024.
- Renaming or moving a *type* changes its type id (written whenever the slot is not sealed): keep it with
  `[NinoFormerName("Old.Namespace.Type")]` on the type, runtime form (`+` for nested, no `global::`), one per type
  (NINO026), not on generic types written by type id (NINO027). `NinoTypeId.Of(typeof(T))` returns the id.
- `WEAK_VERSION_TOLERANCE` was removed in 4.0: where it is still defined, error NINO032's code fix marks each type
  `format: NinoFormat.VersionTolerant`, which keeps its bytes.
- Guard stored and sent data with a layout baseline: `NinoLayouts.cs` (`[assembly: NinoLayouts(@"...")]`, created by
  the IDE refactoring "Create the layout baseline" on a `[NinoType]`, or `nino-gen layouts --assembly X.dll --out
  NinoLayouts.cs`) records the layouts of the version-tolerant and keyed types and all they reach; the analyzer then
  reports a change that breaks old data or old builds (NINO034), loses data silently (NINO035: a keyed member renamed
  without `[NinoFormerName]`) or needs care (NINO036), on the line that made it. Fix it as the message says, or accept
  an intended change with the code fix (it rewrites the type's section; commit it with the change).
  `nino-gen layouts ... --check` does the same in CI (exit 1). Never hand-edit the baseline to silence a finding.

### Polymorphism and references

- A slot declared as a non-sealed class, abstract class or interface writes a 4-byte type id (FNV-1a of the full
  name) and reads back the runtime type; a sealed class writes only a presence byte. **Seal classes without
  subtypes.**
- Serialize and deserialize through the common `[NinoType]` base (`Serialize<Message>(msg)`,
  `Deserialize<Message>(bytes)`), never `object`. `SerializeBoxed(value, type)` / `DeserializeBoxed(data, type)` only
  for a type known only as a `Type`, read with the same type.
- Subtypes in other assemblies (plugins, HybridCLR) are found once the assembly is loaded; the generator must run
  there too.
- Without tracking, a shared object is written twice and a cycle throws `InvalidOperationException` while writing.
  `[NinoType(trackReferences: true)]` on the shared or cyclic class (classes only, NINO015) keeps identity: 4 bytes
  and a lookup per tracked object. Collections are never tracked. Trees need no tracking.

### Callbacks

`[NinoOnSerializing]`, `[NinoOnSerialized]`, `[NinoOnDeserializing]` and `[NinoOnDeserialized]` mark methods of a
`[NinoType]` class or struct that the generated formatter calls directly (IL2CPP and AOT safe) for every value of the
type: top level, members, collection elements, subtypes read through a base.
- `void M()`: non-generic, no parameters (NINO038), `public` or `internal` (NINO039), one per callback per type
  (NINO041). Base classes' callbacks run first; an overridden virtual one runs once.
- Instance methods, except `[NinoOnDeserializing]`, which may be `static` (NINO042). An instance
  `[NinoOnDeserializing]` runs on the instance about to be filled: the existing one (`Deserialize(data, ref value)`),
  a pooled one or a new one. Records and types with `init`/`readonly` members are created from what is read: only a
  static one works there (NINO043); use `[NinoOnDeserialized]`.
- Raw structs (positional, unmanaged fields only) are copied as memory and call nothing: NINO044. A struct's
  `[NinoOnSerializing]` changes the copy being written.
- Reset pooled objects in `OnNinoPoolReturn`, not in a callback. An exception from a callback propagates unchanged.
- Callbacks do not change the bytes; TypeScript, Go and Rust code from nino-gen does not run them.

## The API

```csharp
byte[] bytes = NinoSerializer.Serialize(order);              // a new byte[] of exactly the data's size
NinoSerializer.Serialize(order, bufferWriter);               // any IBufferWriter<byte>: 0 B with a reused one
Order copy = NinoDeserializer.Deserialize<Order>(bytes);     // from any ReadOnlySpan<byte>
NinoDeserializer.Deserialize(bytes, ref copy);               // reuse: fills copy in place (null: creates it)
NinoSerializer.Serialize(order, stream);                     // the same bytes to a Stream, no framing
Order read = NinoDeserializer.Deserialize<Order>(stream);    // reads the stream TO ITS END (also ref copy)
await NinoSerializer.SerializeAsync(order, stream, ct);      // ValueTask
Order next = await NinoDeserializer.DeserializeAsync<Order>(stream, ct);
Order piped = NinoDeserializer.Deserialize<Order>(sequence); // a pipe's ReadOnlySequence<byte> (also ref copy)
```

- Static and thread safe. No `out` overloads. The `Stream` overloads write through a pooled per-thread buffer and
  add no length prefix; a read takes the whole rest of the stream as one value, so several messages on one stream
  (a socket, a file of records) need your own frames (a length, then that many bytes into
  `Deserialize(span, ref value)`). The async read has no `ref` form. Bound a stream from a peer yourself (Nino keeps
  what it delivers, growing as bytes arrive). `SerializeBoxed` / `DeserializeBoxed` (and `...Async`) take a `Stream`
  too. A read consumes the rest of the stream before decoding; a `MemoryStream` whose buffer is exposed, an
  `UnmanagedMemoryStream` of exactly that type and a memory-mapped view are read in place, other streams copied
  ([Streams](https://nino.xgamedev.net/en/docs/v4/advanced/streams#streams)). A `ReadOnlySequence<byte>` of one
  segment is read in place, of several copied once (to the stack up to 1,024 bytes, else a pooled array).
- On rc.9 and earlier there are no `Stream`, async or sequence overloads: serialize to a buffer and write it, read
  the bytes into a buffer, and read a sequence's `First.Span` when `IsSingleSegment`, else copy it first.
- Hot paths: one `NinoArrayBufferWriter` per thread or connection, `ResetWrittenCount()` before each message, send
  `WrittenSpan`, `Dispose()` at the end; deserialize into an existing instance. More, with pooling:
  [references/performance.md](references/performance.md).

## Untrusted input

Nino is built for untrusted input: lengths are checked against the bytes left before allocating, the stack is
bounded, data names only type ids of declared subtypes (never type names), and **every invalid input throws
`NinoDataException`** (derives from `InvalidOperationException`, message starts with `Nino:`). Catch it at the
boundary. Caller mistakes (untracked cycle, too deep while writing) stay `InvalidOperationException`.

```csharp
// At startup, before the first Nino call on any thread: settings freeze on first use, setting one later throws.
NinoSettings.Untrusted = true;              // adds only: seeded hashing of int/char/Guid/string keys,
                                            // checks of values copied as raw bytes, no bytes after the value
NinoSettings.MaxCollectionLength = 100_000; // default int.MaxValue; the three limits apply in every mode
NinoSettings.MaxDepth = 512;                // default 512, at least 128
NinoSettings.MaxHashCollisions = 100;       // default 100, at least 1
```

- **Read anything an outsider could craft with `NinoSettings.Untrusted = true`.** The default mode already rejects a
  `bool` or presence byte other than 0 or 1 in members, values and arrays, but it copies a raw struct as one block
  (any plain unmanaged struct, including one with `bool` or `T?` fields), so a `bool` field or a `Nullable` flag
  *inside* it is not checked. A byte other than 0 or 1 there is undefined behavior to the .NET JIT, not only a wrong
  value (the struct can compare unequal to itself and be lost in a `Dictionary`). Untrusted mode rejects it, and also
  bytes after the value and out-of-range `decimal`, `DateTime`, `DateOnly`, `TimeOnly` copied as bytes. It is one
  process-wide setting: set it once at startup, not per call.
- Still your job: validate values (a negative health, an unnamed enum value, an array of the wrong length), cap frame
  or request sizes before buffering, and version payloads yourself: the bytes carry no schema, so data for another
  type with a compatible layout reads without error. Put a magic number and version in front of stored files.
- To see why bytes do not read (or what they hold): `nino-gen inspect --assembly X.dll --type Ns.Message msg.bin`
  prints every member with its offset, length and value, and the offset and check that fail
  ([references/other-languages.md](references/other-languages.md#inspecting-a-message)).

## Pitfalls

- A bare `[NinoType]` is positional: adding, removing or reordering members breaks stored data.
- `System.Numerics.Vector<T>` anywhere in a serialized type: NINO029, an error. It is 16, 32 or 64 bytes by the CPU;
  store 16 lanes of a 4-byte type (`float[16]`) and expose `Span<Vector<float>>` through a `[NinoIgnore]` member
  ([references/types.md](references/types.md#vectort-is-never-serializable-nino029)). `Vector128<T>` and the other
  fixed-size vectors are fine.
- `Serialize<object>`, `object` members and the boxed API in message code: use a common `[NinoType]` base, and write
  and read with the same declared type (`Serialize<Base>` then `Deserialize<Base>`).
- `NinoSettings` changed after the first Nino call throws.
- Sharing one `NinoArrayBufferWriter` between threads, or using `WrittenSpan` after the next write or reset.
- `int?[]` / `List<int?>` and `IList<int?>` write different bytes; so do `[NinoUtf8]` and the default; so does a raw
  struct made `VersionTolerant`.
- v3 code: `Serializer.Serialize` / `Deserializer.Deserialize(data, out v)` are now `NinoSerializer.Serialize` /
  `NinoDeserializer.Deserialize<T>(data)`; `[NinoExplicitOrder]` is gone (`[NinoType(false)]` + `[NinoMember]`). v4
  cannot read v3 data. Beta names: `[NinoRefDeserialization]` is `[NinoFactory]`, `DeserializeRef` is
  `Deserialize(data, ref value)`.
- Porting from MemoryPack: the bytes differ, so stored data needs a one-time conversion (reference both, read with
  MemoryPack, write with Nino, add a file header). `[MemoryPackable] partial` → `[NinoType]`, `[MemoryPackIgnore]` →
  `[NinoIgnore]`, `[MemoryPackOrder(n)]` → `[NinoType(false)]` + `[NinoMember(n)]`, `[MemoryPackConstructor]` →
  `[NinoConstructor]`, `[MemoryPackUnion]` → nothing (subtypes inherit the base's `[NinoType]`),
  `[MemoryPackOnSerializing]` and the other three → `[NinoOnSerializing]` ... (instance methods, except a static
  `[NinoOnDeserializing]`; no `ref reader`/`ref writer` overloads),
  `GenerateType.VersionTolerant` → `NinoFormat.Keyed`, `GenerateType.CircularReference` → `trackReferences: true`,
  `Serialize(bufferWriter, value)` → `Serialize(value, bufferWriter)` (value first), `SafeNullable<T>` → `T?` (Nino's
  is always safe) or `NinoNullable<T>` for hot nullable arrays (references/types.md). Invalid data throws
  `NinoDataException`. Full table: https://nino.xgamedev.net/en/docs/v4/vs-memorypack.md

Diagnostics: errors mean the type cannot work, warnings mean data is silently dropped or an attribute does nothing.
Never suppress NINO006 or NINO007 blindly. Each id and its fix: [references/diagnostics.md](references/diagnostics.md).

## What Nino does not do

- No framing of several messages on one stream (the `Stream` overloads read one value to the end), no
  compression, no `System.Type`, no schema in the bytes, no human-readable output, no reading of v3 data.
- No reflection over members: generated formatters read and write them directly. The one run-time fallback is for a
  closed generic `[NinoType]` that no compilation names (a `Type` made at run time): on a JIT Nino builds its
  formatter on first use; Native AOT cannot and throws an error naming the type; IL2CPP relies on Unity's full
  generic sharing (Unity 2022.1 and later) and runs slower shared code. Name such a type where the generator sees
  it (a member type, a `NinoSerializer` call's type argument, `typeof` in `IsSupported`).
- No languages besides .NET, TypeScript/JavaScript, Go and Rust (use Protocol Buffers or MessagePack there).

## Example

```csharp
using System;
using System.Collections.Generic;
using Nino.Core;

[NinoType(format: NinoFormat.Keyed)]          // stored: members may change later
public abstract class Message { public long Seq; }

public sealed class Ping : Message { public long SentAt; }               // inherits [NinoType]
public sealed class Chat : Message { [NinoUtf8] public string Text; public List<int> Mentions; }

public static class Program
{
    public static void Main()
    {
        NinoSettings.Untrusted = true;                 // startup, before any Nino call

        using var buffer = new NinoArrayBufferWriter(); // one per thread or connection
        NinoSerializer.Serialize<Message>(new Chat { Seq = 1, Text = "hi “there”", Mentions = new() { 7 } }, buffer);

        Message received = null;
        try
        {
            NinoDeserializer.Deserialize(buffer.WrittenSpan, ref received); // a Chat; reused by later calls
        }
        catch (NinoDataException e)                    // the only exception invalid data throws
        {
            Console.WriteLine($"bad message: {e.Message}");
            return;
        }

        var chat = (Chat)received;
        Console.WriteLine($"{chat.Seq} {chat.Text} {chat.Mentions[0]}");

        buffer.ResetWrittenCount();                    // reuse the same array
        NinoSerializer.Serialize<Message>(new Ping { Seq = 2, SentAt = 42 }, buffer);
        NinoDeserializer.Deserialize(buffer.WrittenSpan, ref received); // type changed: a new Ping
        Console.WriteLine(received is Ping { SentAt: 42 });
    }
}
```
