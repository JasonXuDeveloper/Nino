# Types Nino writes, raw structs and layouts

Read this when a member's type is not a plain class, string, primitive, list or dictionary; when a struct is
involved (raw copying, padding, `StructLayout`, BCL structs inside it); when NINO007 or NINO029 appears; or when
data moves between .NET, Mono, IL2CPP, 32-bit hosts and other languages. Full pages:
https://nino.xgamedev.net/en/docs/v4/types.md and https://nino.xgamedev.net/en/docs/v4/struct-layouts.md

## Supported

- Primitives, enums, `decimal`, `DateTime`, `TimeSpan`, `Guid`, unmanaged structs (Unity and `System.Numerics`
  vectors, `Vector64<T>` to `Vector512<T>`), `string`, `T?`, `ValueTuple`/`Tuple` (1-7), `KeyValuePair`,
  `DateTimeOffset`, `nint`, `BigInteger`, `Version`, `Uri`, `StringBuilder`, `BitArray`, `CultureInfo`,
  `TimeZoneInfo`, `Lazy<T>`.
- `T[]`, `T[,]` to `T[,,,]`, `ArraySegment<T>`, `Memory<T>`, `ReadOnlyMemory<T>`, `ReadOnlySequence<T>`, `List<T>`,
  `HashSet<T>`, `Queue<T>`, `Stack<T>`, `LinkedList<T>`, `SortedSet<T>`, `Dictionary`, `SortedDictionary`,
  `SortedList`, the concurrent collections, `BlockingCollection<T>`, `PriorityQueue` (.NET 8+ builds only, not Unity),
  `ILookup`, `IGrouping`, read-only wrappers, immutable collections (with `Nino.Immutable`), and any class with a
  public parameterless constructor (or `[NinoFactory]`) implementing `ICollection<T>`, `IDictionary<K,V>` or
  `IProducerConsumerCollection<T>`. Members declared as interfaces read back as `List<T>`, `HashSet<T>`,
  `Dictionary<K,V>`.
- `object` members hold any `[NinoType]` (by type id), nothing else.

## Not supported (NINO007: the member is skipped, silently at run time)

Delegates, `Task`, pointers and function pointers (`delegate*`) as members, `Span<T>`, `System.Type` (on purpose:
store your own id), non-generic collections, abstract or struct collections, Unity/Godot engine classes except
Unity's `AnimationCurve`, `Gradient`, `RectOffset`. Fix with `[NinoIgnore]` (say it is meant) or a custom formatter.
A generic `[NinoType]` closed over such a type (`Box<Action>`) compiles, but writing a value throws.

## Raw structs

- Structs have no header. An unmanaged, sequential, positional struct whose fields are all raw is copied as one block
  of memory (one block per array or list): its field order, `Pack` and `Size` are the format. Reordering fields or
  changing `StructLayout` changes the bytes. Order fields largest first to avoid padding (padding is written as zeros).
- `Nullable<T>`, tuples, `KeyValuePair`, `nint` and `DateTimeOffset` are never raw *on their own* (written item by
  item). A raw `[NinoType]` struct needs every field raw, so one holding them is written member by member. A plain
  unmanaged struct (no `[NinoType]`) that holds them is still one block, in the canonical layout below.
- A `[NinoType]` struct with a `[NinoIgnore]`d field, or with `format: NinoFormat.VersionTolerant` (so fields can be
  added later), is not raw: it is written member by member, and its `bool` members are checked in every mode.
- A struct that serializes no member is NINO023.

### `NinoNullable<T>`: nullable values in hot collections

`NinoNullable<T>` (Nino.Core, `T : unmanaged`) is a drop-in for `T?` that Nino writes with exactly `T?`'s bytes
(either reads the other's data; the published layout says nullable too). Its presence is a whole `T`, so its memory
has no padding and no stale bytes: an array or `List<>` of it is one block copy where `T` is as large as its alignment
(`long`, `int`, `double`, enums, `DateTime`, `TimeSpan`), where `T?[]` clears padding and absent values element by
element. Use it for arrays and lists of nullable values written often; a single `T?` member is as fast already. It
converts implicitly from `T` and `T?` and to `T?`, and has `HasValue`, `Value`, `GetValueOrDefault`, `ToNullable()`.
It takes twice `T`'s size (`T?` takes `T`'s size plus its alignment: the same for 8-byte primitives, larger for
`decimal` or `Guid`). Its presence byte is checked in every mode.

### One canonical layout on every runtime

Runtimes lay structs out differently (Mono keeps tuple items in declaration order, IL2CPP and .NET Framework align
`decimal` to 4, 32-bit hosts have 4-byte `nint`, .NET 8 and .NET 10 hold `DateTimeOffset` differently). Nino writes
every raw struct in **one canonical layout**, 64-bit CoreCLR's: `DateTimeOffset` as .NET 10 holds it, `nint`,
`nuint`, pointers and function pointers as 8 bytes (`nint` sign-extended, the others zero-extended), the SIMD vectors
aligned as on x64, padding zero. 64-bit .NET copies memory; Mono, IL2CPP, .NET Framework and 32-bit hosts convert
field by field (measured once at startup). So raw data written on a .NET 10 server reads on an IL2CPP Arm phone, a
32-bit player, and in Go, Rust and TypeScript. You do not need to do anything for this; do not hand-pack structs
"for portability".

- A `delegate*` field in a plain unmanaged struct is 8 bytes like `T*`, zero-extended on every runtime (32-bit Unity
  players included) when Nino's generator sees the struct: declared in a project with the generator, or in an
  assembly such a project references (named anywhere or not). A struct only an assembly loaded by name declares (a
  plugin, a hot-update DLL) needs the generator in its own project. An address means nothing in another process anyway: keep pointers
  out of data you store or send.

### `bool` and `Nullable` flags inside raw structs are unchecked by default

Reading a raw struct copies its block. The default mode does not check a `bool` field or a `Nullable` flag inside
it, and a byte other than 0 or 1 there is undefined behavior to the .NET JIT, not only a wrong value: a `long?` with
a flag of 2 read as null in one compiled method and as a value in another, so the struct compared unequal to itself
and was lost in a `Dictionary`. `NinoSettings.Untrusted = true` rejects such bytes (and checks `DateTime`, `decimal`,
`DateOnly`, `TimeOnly` copied as bytes). Data anyone could craft (network peers, shared files) must be read with it.
The Go, Rust and TypeScript readers always check.

## `Vector<T>` is never serializable (NINO029)

`System.Numerics.Vector<T>` is 16, 32 or 64 bytes, chosen by the CPU, so no bytes can carry it between hosts (32
bytes written on an AVX2 server do not fit a 16-byte `Vector<T>` on an Arm phone). Alone or inside anything (arrays,
collections and classes deriving from one, tuples, `T?`, plain structs at any depth, a generic `[NinoType]` whose
formatter or a generic subtype writes that argument, as `Box<Vector<int>>` of `Box<T> { T Value; }`) it is NINO029,
an error, and `NotSupportedException` at run time.
`Vector64<T>` to `Vector512<T>` are fixed size and fine.

Store a fixed number of lanes that is a multiple of `Vector<T>.Count` on every host (16 lanes of a 4-byte type, 64
bytes) and view them as vectors in members Nino does not serialize:

```csharp
[NinoType]
public sealed partial class Particle
{
    public float[] Lanes = new float[16]; // serialized: 16 floats (64 bytes of data) on every host

    [NinoIgnore] // 4, 2 or 1 vectors, by the CPU
    public Span<Vector<float>> Vectors => MemoryMarshal.Cast<float, Vector<float>>(Lanes.AsSpan());
}
```

- Never write `new Vector<float>(lanes)` assuming 8 lanes: `Vector<float>.Count` is 4, 8 or 16.
- Data from an untrusted source may hold another number of lanes: check `Lanes.Length` before casting.
- Alternatives for storage: a fixed buffer, or `Vector128<T>`/`Vector256<T>` fields (.NET only; Unity has none).
- Why: https://nino.xgamedev.net/en/docs/v4/struct-layouts#vector-t

## Encodings that differ (changing one changes the bytes)

- `int?[]` / `List<int?>` (fixed stride) and `IList<int?>` / `IEnumerable<int?>` (element by element).
- `[NinoUtf8]` and the default string encoding.
- A raw struct and the same struct made `VersionTolerant`.
