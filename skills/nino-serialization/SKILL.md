---
name: nino-serialization
description: "Write correct, fast code with Nino v4 (the source-generated binary serializer for C# on NuGet as Nino, in Unity from OpenUPM) - [NinoType] classes, structs, records and interfaces, member order, constructors, positional / version-tolerant / keyed formats, polymorphism, reference tracking, [NinoUtf8], [NinoPooled] and reuse deserialization, custom formatters, NinoSettings for untrusted input, ASP.NET Core bodies, and TypeScript, Go and Rust codecs from nino-gen - and fix what the NINO analyzer reports. Use when writing, reviewing or migrating code that uses Nino, or choosing a binary format for .NET, Unity, Godot or HybridCLR data. Triggers: a project referencing Nino, Nino.Serialization, Nino.Core, Nino.AspNetCore or Nino.Codegen, [NinoType], NinoSerializer, NinoDeserializer, NinoDataException, a NINO0xx or NINOGEN diagnostic, application/x-nino, nino-gen, 用 Nino 序列化, Nino 反序列化, 二进制序列化, 给类型加 NinoType."
---

# Nino rules for AI assistants

Rules for writing code that uses Nino v4 (namespace `Nino.Core`). Docs: https://nino.xgamedev.net/en/docs/v4 (pages
for models: https://nino.xgamedev.net/llms.txt, full text: https://nino.xgamedev.net/llms-full.txt). Every page is
also served as Markdown: add `.md` to its URL (`https://nino.xgamedev.net/en/docs/v4/start.md`). Per-id diagnostics:
[references/diagnostics.md](references/diagnostics.md); TypeScript, Go and Rust: [references/other-languages.md](references/other-languages.md).

## Install

- .NET and Godot 4 (.NET): `dotnet add package Nino --prerelease` in **every project that declares `[NinoType]`s**, so
  the generator runs there. v4 is a prerelease: without `--prerelease` NuGet installs 3.9.x, which cannot read or
  write v4 data. `Nino` brings `Nino.Serialization` (the runtime, `Nino.Core.dll`) and `Nino.Generator` (generator,
  analyzers, code fixes).
- Optional: `Nino.Immutable` (`System.Collections.Immutable` types), `Nino.AspNetCore` (`application/x-nino` bodies,
  `net8.0`/`net10.0`), `Nino.Codegen` (TypeScript, Go or Rust after each build), and the `nino-gen` tool
  (`dotnet tool install -g Nino.Codegen.Tool --prerelease`). All with `--prerelease`.
- Unity 2022.3+: `openupm add com.jasonxudeveloper.nino` (runtime, generator, code fixes and `Nino.UnityTypes.dll`).
- Requirements: Roslyn 4.3+ for the generator and `LangVersion` 9 or later in the declaring project (old .NET
  Framework projects default to 7.3). `Nino.Core` targets `netstandard2.0`, `netstandard2.1`, `net8.0`, `net10.0`; every
  target, runtime and CPU writes identical bytes.
- Nothing to register or initialize: generated registries load on first use. Optional startup calls only:
  `NinoSettings`, `NinoPolymorphic.Register<T>()`, `NinoRegistry.EnsureInitialized(assembly)`.

## Declaring types

- `[NinoType]` on a `class`, `struct`, `record`, `record struct` or `interface`. The type and every type containing it
  are `public` or `internal`, never a `static` class or `ref struct` (NINO001).
- Options: `[NinoType(autoCollect: true, containNonPublicMembers: false, allowInheritance: true, format:
  NinoFormat.Positional, trackReferences: false)]`; use named arguments.
- With `autoCollect: true` every public field and every public property with a getter and a `set`/`init` is written:
  `[NinoType]` base classes first (root first), then the type's own members in declaration order. Get-only
  auto-properties are skipped unless a constructor parameter has the same name (ignoring case); explicit interface
  implementations are skipped; a member of a type Nino cannot write is skipped with NINO007.
- `[NinoIgnore]` drops a member. `[NinoType(false)]` writes only `[NinoMember(index)]` members, by index ascending
  (`[property: NinoMember(n)]` on record parameters). Don't mix them up: NINO010 to NINO013.
- Non-public members need `containNonPublicMembers: true` (a non-public `[NinoMember]` without it: NINO009). Before
  .NET 8 (Unity, .NET Framework, `netstandard`), and for generic types before .NET 9, the type and every containing type
  must be `partial` (NINO002). Write `partial` on such types anyway when the code may build for Unity.
- Subtypes inherit `[NinoType]` and its options from the nearest `[NinoType]` base class or interface. A subtype's own
  `[NinoType]` replaces the inherited options (repeat `format:`); `trackReferences` is inherited regardless.
  `allowInheritance: false` makes subtypes declare their own (NINO003).
- Generic types work (`Box<T>`); type arguments must be serializable. A closed generic type the process only *reads*
  through a base and never names: `NinoPolymorphic.Register<Box<int>>()` at startup.
- Strings: one byte per char when every char is at most U+00FF, UTF-16 otherwise. `[NinoUtf8]` on a member writes
  UTF-8 (its strings, and the strings of a collection or tuple member): for mostly-ASCII text with a few wider chars;
  not for mostly-CJK text. Adding or removing it changes the bytes.
- Structs have no header. An unmanaged, sequential, positional struct whose fields are all raw is copied as raw memory
  (one block per array or list): its field order, `Pack` and `Size` are the format. Order fields to avoid padding. A
  struct that serializes no member is NINO023. `Nullable<T>`, tuples, `KeyValuePair`, `nint`, `DateTimeOffset` are
  never raw.

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

[NinoType(containNonPublicMembers: true)]
public partial class Account { private long _balance; public int Id; }
```

### Constructors

Order of choice: a constructor or static factory marked `[NinoConstructor]`; else, when every member is assignable, a
constructor callable without arguments (or a `[NinoFactory]`), then members are filled; else the accessible
constructor with the most parameters matching serialized members by name (ignoring case; a positional record's
primary constructor); structs fall back to `new T()`; otherwise NINO004.
- `[NinoConstructor(nameof(Id), nameof(Name))]` lists members when parameter names differ. Each must match a member
  (NINO005); the constructor must be `public`/`internal` (NINO021). A read-only member that is not a parameter is read
  and dropped (NINO006).
- `required` members: a `[SetsRequiredMembers]` constructor, or every required member serialized and settable.
- Types re-created through a constructor (records, `init`/get-only/`readonly` members) are re-created even by reuse
  deserialization, and cannot use `trackReferences` (NINO016) or `[NinoPooled]` (NINO018). Hot-path types: a
  parameterless constructor and settable members.

### Formats: choose before data is stored

| | `Positional` (default) | `VersionTolerant` | `Keyed` |
| --- | --- | --- | --- |
| Change members | nothing (renaming a member is free: names are not written) | append to the most derived type only; renaming is free | add, remove, reorder; rename with `[NinoFormerName("Old")]` |
| Cost | none | 4 bytes per object | 4 per object + 8 per member |
| For | same-build messages, rebuildable caches | messages between versions, saves that only grow | config, documents, data edited for years |

- Changing a type's `format` makes its existing data unreadable. Changing a member's type is never tolerated.
- Version-tolerant hierarchy: never append to a base class with subclasses (it shifts their members, silently).
  Use `Keyed` for that. Missing members keep their initializer or existing value.
- Two keyed names hashing alike: NINO014. Member `[NinoFormerName]` on a non-keyed type: NINO024.
- Renaming or moving a type changes its type id (written whenever the slot is not sealed): keep it with
  `[NinoFormerName("Old.Namespace.Type")]` on the type, runtime form (`+` for nested, no `global::`), one per type
  (NINO026), not on generic types written by type id (NINO027). `NinoTypeId.Of(typeof(T))` returns the id.
- `WEAK_VERSION_TOLERANCE` makes the project's positional types version-tolerant; prefer the per-type option.
- Every build records each type's layout as `[assembly: NinoTypeLayout]`; compare `layout.ToString()` between releases
  in CI to catch byte changes.

### Polymorphism and references

- A slot declared as a non-sealed class, abstract class or interface writes a 4-byte type id (FNV-1a of the full
  name), and reads back the runtime type; a sealed class writes a 1-byte null flag. **Seal classes without subtypes.**
- Serialize and deserialize through the common `[NinoType]` base (`Serialize<Message>(msg)`,
  `Deserialize<Message>(bytes)`), never `object`: `Serialize<object>` accepts only `[NinoType]` values and dispatches
  through the global registry. `SerializeBoxed(value, type)` / `DeserializeBoxed(data, type)` only for a type known
  only as a `Type` with no common base, read with the same type.
- Subtypes in other assemblies (plugins, HybridCLR) are found when the assembly is loaded; the generator must run in
  that assembly too.
- Without tracking, a shared object is written twice and a cycle throws `InvalidOperationException` while writing.
  `[NinoType(trackReferences: true)]` on the shared or cyclic class (classes only, NINO015) keeps identity: 4 bytes and
  a lookup per tracked object. Collections are never tracked. Trees need no tracking; depth is unbounded (objects past
  128 levels are deferred).

## The API

```csharp
byte[] bytes = NinoSerializer.Serialize(order);              // a new byte[] of exactly the data's size
NinoSerializer.Serialize(order, bufferWriter);               // any IBufferWriter<byte>; 0 B
Order copy = NinoDeserializer.Deserialize<Order>(bytes);     // from any ReadOnlySpan<byte>
NinoDeserializer.Deserialize(bytes, ref copy);               // reuse: fills copy in place (null: creates it)
```

- `NinoSerializer` / `NinoDeserializer` are static and thread safe. There are no `Stream`, `ReadOnlySequence<byte>` or
  `out` overloads: copy a multi-segment sequence into one block (or pass `First.Span` when `IsSingleSegment`).
- Hot paths: one `NinoArrayBufferWriter` per thread or connection (not thread safe), `ResetWrittenCount()` before each
  message, send `WrittenSpan`, `Dispose()` at the end (returns its array to the pool). `new
  NinoArrayBufferWriter(capacity, pool)` rents from your own `ArrayPool<byte>`. Nino writes into it in place; any other
  `IBufferWriter<byte>` (a `PipeWriter`) costs one copy. Call `Serialize(value)` only when an API needs a `byte[]`.
- Several values in one buffer: `var w = new Writer(buffer);` then `NinoSerializer.Serialize(a, ref w)`..., then
  `w.Flush(); w.ReleaseReferences();`; read with `var r = new Reader(span);`, `Deserialize<T>(ref r)`, then
  `r.ReleaseReferences()`. Release once per logical message.
- `NinoSerializer.GetFixedSize<T>()` gives the size of a fixed-size type; `NinoSerializer.IsSupported(type)` whether
  Nino can write a type.
- Reuse deserialization fills the instance, its nested objects, lists (by index) and dictionaries (merged by key);
  unchanged strings are kept. Dropped instances go to `NinoSettings.SetRecycler<T>(handler)` for the slot's declared
  type, else `ReturnToPool()` of an `INinoPooled`, else the GC. Use `List<T>` for varying counts: an array is reused
  only at the same length.
- `[NinoType, NinoPooled] public sealed partial class X` gets a built-in pool: Nino takes new instances from it and
  returns dropped ones; you call `x.ReturnToPool()` once (twice throws). Implement both hooks (NINO019):

```csharp
[NinoType, NinoPooled]
public sealed partial class Alert
{
    public int DeviceId;
    public float Value;

    private static partial IEnumerable<Alert> OnNinoPoolWarmup() => Enumerable.Range(0, 64).Select(_ => new Alert());
    private partial void OnNinoPoolReturn() { } // release state Nino does not deserialize
}
```

- Pooled classes are sealed, partial and assignable (NINO018). A class holding pooled children should be pooled too.
  `NinoPool<T>.Shared.Clear()` empties a pool (Unity with domain reload off).
- `[NinoFactory] public static T Rent()` (public, static, callable without arguments, returns the type, never null:
  NINO017) is called whenever Nino needs a new instance; also for your own collection types (implement `INinoPooled`
  so dropped ones come back). Not together with `[NinoConstructor]` (NINO025).

### Settings and untrusted input

Nino is built for untrusted input: lengths are checked against the bytes left before allocating, the stack is bounded,
data names only type ids of declared subtypes (never type names), and **every invalid input throws
`NinoDataException`** (derives from `InvalidOperationException`, message starts with `Nino:`). Catch it at the
boundary. Caller mistakes and write failures (untracked cycle, too deep) stay `InvalidOperationException`.

```csharp
// At startup, before the first Nino call on any thread: these freeze on first use; setting one later throws.
NinoSettings.Untrusted = true;              // seeded hashing of int/char/Guid/string keys, raw value checks, no trailing bytes
NinoSettings.MaxCollectionLength = 100_000; // default int.MaxValue
NinoSettings.MaxDepth = 512;                // default 512, at least 128
NinoSettings.MaxHashCollisions = 100;       // default 100, at least 1
```

- Pool settings (`DefaultPoolMaxRetained`, `SetPoolMaxRetained<T>`, `SetRecycler<T>`) are also startup-only.
- Still your job: validate values (a negative health, an unnamed enum value), cap frame or request sizes before
  buffering, and version payloads yourself (the bytes carry no schema: data for another type with a compatible layout
  reads without error; put a magic number and version in front of stored files).

## Types Nino writes

- Primitives, enums, `decimal`, `DateTime`, `TimeSpan`, `Guid`, unmanaged structs (Unity and `System.Numerics`
  vectors), `string`, `T?`, `ValueTuple`/`Tuple` (1-7), `KeyValuePair`, `DateTimeOffset`, `nint`, `BigInteger`,
  `Version`, `Uri`, `StringBuilder`, `BitArray`, `CultureInfo`, `TimeZoneInfo`, `Lazy<T>`.
- `T[]`, `T[,]` to `T[,,,]`, `ArraySegment<T>`, `Memory<T>`, `ReadOnlyMemory<T>`, `ReadOnlySequence<T>`, `List<T>`,
  `HashSet<T>`, `Queue<T>`, `Stack<T>`, `LinkedList<T>`, `SortedSet<T>`, `Dictionary`, `SortedDictionary`,
  `SortedList`, the concurrent collections, `BlockingCollection<T>`, `PriorityQueue` (.NET 8+ builds only, not Unity),
  `ILookup`, `IGrouping`, read-only wrappers, immutable collections (with `Nino.Immutable`), and any class with a
  public parameterless constructor (or `[NinoFactory]`) implementing `ICollection<T>`, `IDictionary<K,V>` or
  `IProducerConsumerCollection<T>`. Interfaces read back as `List<T>`, `HashSet<T>`, `Dictionary<K,V>`.
- `object` members hold any `[NinoType]` (by type id), nothing else.
- Not supported (NINO007, member skipped): delegates, `Task`, pointers, `Span<T>`, `System.Type` (on purpose: store
  your own id), non-generic collections, abstract or struct collections, Unity/Godot classes except Unity's
  `AnimationCurve`, `Gradient`, `RectOffset`. Fix with `[NinoIgnore]` or a custom formatter.

### Custom formatters

A formatter is a **struct** implementing `INinoFormatter<T>` (`Serialize(T, ref Writer)`, `Deserialize(ref T, ref
Reader)`), called as `default(F)`: no instance state. Read exactly what you wrote, in order.
- One member: `[NinoCustomFormatter(typeof(F))]`, `F` for exactly the member type (NINO008).
- A type Nino does not know: `[assembly: NinoFormatter(typeof(F<,>))]`, generic formatters as open definitions; a
  type parameter constrained `struct, INinoFormatter<E>` gets the element formatter; `[NinoRaw]` and `[NinoCreated]`
  narrow what binds. Invalid registrations: NINO020. There is no runtime registration call.
- Use `writer.WriteLength(n)` / `WriteNullLength()` and `reader.ReadLength(minElementSize)` (checked against the bytes
  left), and hand dropped instances to `NinoRecycler.Discard(ref value)` / `NinoRecycler.Replace(ref value, x)`.
  Nested values: `NinoSerializer.Serialize(v, ref writer)`, `NinoDeserializer.Deserialize(ref v, ref reader)`.

```csharp
public struct ZigZagLongFormatter : INinoFormatter<long>
{
    public void Serialize(long value, ref Writer writer)
    {
        ulong v = (ulong)((value << 1) ^ (value >> 63));
        while (v >= 0x80) { writer.Write((byte)(v | 0x80)); v >>= 7; }
        writer.Write((byte)v);
    }

    public void Deserialize(ref long value, ref Reader reader)
    {
        ulong v = 0; int shift = 0; byte b;
        do { reader.Read(out b); v |= (ulong)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0 && shift < 70);
        value = (long)(v >> 1) ^ -(long)(v & 1);
    }
}

[NinoType]
public sealed class BalanceChange
{
    public int AccountId;
    [NinoCustomFormatter(typeof(ZigZagLongFormatter))] public long DeltaCents;
}
```

## Unity, IL2CPP, HybridCLR, AOT

- Unity compiles against .NET Standard: no `[UnsafeAccessor]`, so private members and `[NinoPooled]` need `partial`
  (NINO002); no interceptors (same bytes). Assembly definitions need `Nino.Core.dll` (Auto Referenced, or listed under
  Override References; also `Nino.UnityTypes.dll` for the Unity classes).
- With `com.unity.burst` 1.8+ installed, Burst SIMD kernels register on their own (`Nino.Burst.NinoBurst`); include
  SSE4 and AVX2 in Burst AOT Settings for x64 players.
- IL2CPP and Native AOT: no reflection at serialization time; types must appear in code the generator sees (a member
  type or a `Serialize`/`Deserialize` type argument). Native AOT on x64 takes Nino's SSSE3 paths on CPUs that have
  SSSE3; its AVX2 and AVX-512 paths need `<IlcInstructionSet>` (`x86-64-v3`, `x86-64-v4` or `native`), which makes the
  app require those CPUs. HybridCLR: `Assembly.Load` is enough, no init call;
  `NinoRegistry.EnsureInitialized(asm)` / `EnsureAllInitialized()` only to pay the registry load on a loading screen.
- Godot 4 .NET: a normal NuGet project; engine objects (`Node`, `Resource`) are not serialized, math structs are raw.

## ASP.NET Core (Nino.AspNetCore)

```csharp
builder.Services.AddControllers().AddNinoFormatters(); // MVC: application/x-nino in and out, JSON stays the default
app.MapPost("/orders", (NinoBody<Order> order) => NinoResults.Ok(order.Value));
app.MapGet("/shape", () => NinoResults.Ok<Shape>(new Circle())); // give T to write a subtype as its base
```

Invalid bodies become 400 (`NinoBody<T>`: 415 for another content type). By hand:
`request.ReadFromNinoAsync<T>(ct)`, `response.WriteAsNinoAsync(value, ct)`, `NinoMediaTypes.Application`.

## Other languages

Nino.Codegen writes your `[NinoType]`s as TypeScript, Go or Rust types with codecs, from the built assembly's layouts:
`<NinoCodegen Include="typescript|go|rust" Out="..." />` items in the project (or `nino-gen --assembly x.dll --lang
ts|go|rust --out dir`). Runtimes: `nino.ts` (written next to the code, or npm `nino-serialization`), Go module
`github.com/JasonXuDeveloper/Nino/runtimes/go/v4`, Rust crate `nino-serialization` (library `nino`). Unsupported
types are left out with NINOGEN002. Details: [references/other-languages.md](references/other-languages.md).

## Diagnostics

`NINO0xx` come from the analyzer as you type; many have a code fix. Errors mean the type cannot work, warnings mean
data is silently dropped or an attribute does nothing: never suppress NINO006 or NINO007 blindly.
- **Type shape** (NINO001, 002, 003, 022, 023): make it public/internal, `partial`, add `[NinoType]`, rename a hidden
  member, serialize at least one struct member.
- **Construction** (NINO004, 005, 006, 021, 025): give a usable constructor, match `[NinoConstructor]` names, make
  read-only members parameters or settable, make it public, pick one of `[NinoConstructor]` / `[NinoFactory]`.
- **Members** (NINO007 to 013): an unsupported member type, a formatter for the wrong type, inconsistent
  `[NinoMember]`/`[NinoIgnore]`, duplicate indices.
- **Features** (NINO014 to 017, 024, 026, 027): keyed name collisions, `trackReferences` on structs or re-created types,
  invalid factories, misplaced `[NinoFormerName]`.
- **Pooling and formatters** (NINO018, 019, 020): pooled types must be sealed, partial, assignable and implement both
  hooks; `[assembly: NinoFormatter]` must name a usable struct.
- **nino-gen** (NINOGEN001 error: bad option or unreadable assembly; 002: type left out; 003: old `NinoTypeScript*`
  properties; 004: Nino.Codegen without a `<NinoCodegen>` item).

## Pitfalls

- A bare `[NinoType]` is positional: adding, removing or reordering members breaks stored data (throws, or worse,
  wrong values). Pick `VersionTolerant` or `Keyed` before shipping anything stored or exchanged across versions.
- Raw structs: reordering fields or changing `StructLayout` changes the bytes.
- `Serialize<object>`, `object` members and the boxed API in message code: use a common `[NinoType]` base.
- Write and read with the same declared type: `Serialize<Base>` then `Deserialize<Base>`.
- `int?[]` / `List<int?>` and `IList<int?>` write different bytes; so do `[NinoUtf8]` and the default.
- `NinoSettings` changed after the first Nino call throws; set it at startup.
- Sharing one `NinoArrayBufferWriter` between threads, or using `WrittenSpan`/`WrittenMemory` after the next write or
  `ResetWrittenCount()`.
- A recycler or `ReturnToPool()` on instances still referenced elsewhere: the pool hands them out again.
- If the project sets `InterceptorsNamespaces` itself, append (`$(InterceptorsNamespaces);Mine`), don't replace.
- v3 code: `Serializer.Serialize` / `Deserializer.Deserialize(data, out v)` are now `NinoSerializer.Serialize` /
  `NinoDeserializer.Deserialize<T>(data)`; `[NinoExplicitOrder]` is gone (`[NinoType(false)]` + `[NinoMember]`). v4
  cannot read v3 data. Beta names: `[NinoRefDeserialization]` is `[NinoFactory]`, `DeserializeRef` is
  `Deserialize(data, ref value)`.

## What Nino does not do

- No `Stream`, async or multi-segment APIs (wrap `IBufferWriter<byte>` / `ReadOnlySpan<byte>`), no compression, no
  callbacks, no `System.Type`, no schema in the bytes, no human-readable output, no reading of v3 data.
- No runtime registration of formatters and no reflection fallback: the generator must see every type.
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
