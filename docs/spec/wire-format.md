# Nino v4 wire format

**Specification version 4.0 (draft), for Nino 4.0.0-rc.7.**

This document specifies the bytes Nino v4 writes and reads. A reader and a writer built from it, in any language,
exchange data with the .NET implementation (`Nino.Core` plus the code `Nino.Generator` emits). Every rule here was
taken from the source at the commit this file was added in. The examples in [Appendix A](#appendix-a-worked-examples)
are the output of Nino itself. Statements that could not be checked by running Nino are marked **(unverified)**.

How this specification is versioned, and how it relates to Nino's own version, is described in
[§2.7](#27-versioning) and in [README.md](README.md).

## Contents

1. [Terminology and conformance](#1-terminology-and-conformance)
2. [Conventions](#2-conventions)
3. [Primitive and BCL values](#3-primitive-and-bcl-values)
4. [Strings](#4-strings)
5. [Raw values and the canonical layout](#5-raw-values-and-the-canonical-layout)
6. [Collections, tuples and nullables](#6-collections-tuples-and-nullables)
7. [`[NinoType]` objects](#7-ninotype-objects)
8. [Type layout metadata and its grammar](#8-type-layout-metadata-and-its-grammar)
9. [Validation requirements for readers](#9-validation-requirements-for-readers)
10. [Open points and known irregularities](#10-open-points-and-known-irregularities)
- [Appendix A: worked examples](#appendix-a-worked-examples)
- [Appendix B: reference algorithms](#appendix-b-reference-algorithms)

---

## 1. Terminology and conformance

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHOULD**, **SHOULD NOT**, **RECOMMENDED**, **MAY**
and **OPTIONAL** are to be interpreted as described in RFC 2119.

- **Writer**: code that produces a payload. **Reader**: code that consumes one.
- **Payload**: the bytes of one top-level value, produced by one top-level serialize call.
- **Declared type**: the static type a value is written or read as: the type passed to the top-level call, the
  declared type of a member, or the element type of a collection. Nino payloads are not self-describing. The
  declared type alone decides how the bytes are read.
- **Runtime type**: the actual type of an object, which differs from the declared type under polymorphism.
- **Body**: the encoding of a `[NinoType]` value without its header (§7.1).
- **Region**: a byte range with an `int32` length in front (§7.5, §7.6).
- **.NET reader / .NET writer**: the reference implementation (`Nino.Core` 4.0.0-rc.7 with generated formatters).

A conforming **writer** MUST produce the exact bytes this document specifies for every value it writes. The only
exceptions are where the specification lists a choice (for example, the iteration order of hash-based collections,
§6.3). A conforming **reader** MUST accept every payload a conforming writer produces for the same declared types.
It MUST also apply the checks in §9 to data it does not trust.

## 2. Conventions

### 2.1 Byte order and alignment

All multi-byte integers and floating-point values are **little-endian**. Nothing is aligned: a value starts right
after the previous one, whatever its size. The .NET implementation refuses to run on big-endian hosts
(`PlatformNotSupportedException`). Implementations on big-endian hosts MUST byte-swap.

The only padding on the wire is inside raw structs (§5) and inside nullable elements of raw arrays (§6.2). Writers
MUST write that padding as zero bytes.

### 2.2 No envelope

A payload has no magic number, no version field, no length and no type descriptor. It is the encoding of the
top-level value under its declared type, followed by the deferred bodies the value needs (§7.10). Nothing else
follows.

A reader that has read the top-level value (and its deferred bodies) stops there. The .NET reader **ignores any
bytes that follow** by default (verified: `Deserialize<int>` of `01 00 00 00 FF FF` returns 1), and rejects them with
`NinoSettings.Untrusted` set. See §9.12.

### 2.3 Integer types

| Name | Size | Encoding |
| --- | --- | --- |
| `u8` / `i8` | 1 | unsigned / two's complement |
| `u16` / `i16` | 2 | little-endian |
| `u32` / `i32` | 4 | little-endian |
| `u64` / `i64` | 8 | little-endian |

There are no variable-length integers anywhere in the format.

### 2.4 Lengths, counts and null markers

| Marker | Encoding | Used by |
| --- | --- | --- |
| Length / count | `i32`, `>= 0` | strings, collections, regions, keyed entries |
| Null length | `i32` `-1` (`FF FF FF FF`) | strings, collections, `BigInteger`, `Version`, `BitArray`, ... |
| Presence byte | `u8`: `0` = null / absent, `1` = present | sealed classes, `T?`, `Lazy<T>`, `Tuple<...>` |
| Null type id | `i32` `0` | non-sealed classes, abstract classes, interfaces, `object` |
| Reference header | `i32`: `0` null, `-1` new, `n > 0` back reference | `trackReferences` types (§7.9) |

- A length or count of `-1` means null. The default string encoding (§4.1) also uses other negative values. Apart
  from that, a negative length is invalid and readers MUST reject it.
- Writers MUST write presence bytes and `bool` as exactly `0` or `1`, and readers MUST reject any other byte (§9.3).

### 2.5 Booleans and enums

`bool` is one byte, `0` or `1`. An enum is encoded as its underlying integer type (`enum Color : byte` takes 1 byte,
`enum Big : long` takes 8 bytes). Readers do not check enum values against the defined members.

### 2.6 Errors

The .NET reader reports every invalid input as `Nino.Core.NinoDataException` (derived from
`System.InvalidOperationException`) with a message that begins with `Nino:`, running out of data included. The
exceptions are listed in §9.14.

### 2.7 Versioning

- **The format** is versioned with Nino's major version. Nino v4 is not wire-compatible with v3 or with v4 betas
  before the format froze. Payloads carry no version. A writer and a reader agree on the format by agreeing on the
  Nino major version and on the type definitions.
- **Changes inside v4.** A change to the bytes Nino writes is announced in the release notes under "Wire Format"
  (from the `Wire-Format:` trailer of Nino's commits). Each such change bumps this specification's minor version
  and adds a row to the change log in [README.md](README.md).
- **This specification** is versioned `major.minor`. The major version is Nino's (this is `4.x`). The minor version counts
  wire-format changes and normative clarifications within that major version. Editorial fixes do not change it.
- **Schema evolution** (adding members to types) is not format versioning. It is handled per type by the
  version-tolerant and keyed formats (§7.5, §7.6).

---

## 3. Primitive and BCL values

Unless stated otherwise, these values are written **raw**: their bytes, little-endian, with no header. The following
are all raw: integers, `float` (IEEE 754 binary32), `double` (binary64), `Half` (binary16), `char` (one UTF-16 code
unit), `bool` and enums.

| Type | Size | Encoding | Example |
| --- | --- | --- | --- |
| `bool` | 1 | `0` / `1` | `true` → `01` |
| `sbyte` / `byte` | 1 | raw | `-2` → `FE` |
| `short` / `ushort` / `char` | 2 | raw | `0x1234` → `34 12`, `'A'` → `41 00` |
| `int` / `uint` / `float` | 4 | raw | `1.5f` → `00 00 C0 3F` |
| `long` / `ulong` / `double` | 8 | raw | `1.5` → `00 00 00 00 00 00 F8 3F` |
| `Half` | 2 | raw | `1.5` → `00 3E` |
| `nint` / `nuint` | **8** | `i64` / `u64` on every platform | `(nint)(-1)` → `FF` × 8 |
| `Int128` / `UInt128` | 16 | low 64 bits, then high 64 bits | `-2` → `FE FF ... FF` |
| `decimal` | 16 | see §3.1 | `1.5m` → `00 00 01 00 00 00 00 00 0F 00 00 00 00 00 00 00` |
| `Guid` | 16 | see §3.2 | |
| `DateTime` | 8 | see §3.3 | |
| `TimeSpan` | 8 | `i64` ticks (100 ns) | 1 s → `80 96 98 00 00 00 00 00` |
| `DateOnly` | 4 | `i32` day number (days since 0001-01-01) | 2024-01-02 → `46 46 0B 00` |
| `TimeOnly` | 8 | `i64` ticks since midnight | 01:00 → `00 68 C4 61 08 00 00 00` |
| `DateTimeOffset` | 10 | see §3.4 | |
| `Vector2/3/4`, `Quaternion`, `Matrix*` | raw | floats in field order | `Vector3(1,2,3)` → `00 00 80 3F 00 00 00 40 00 00 40 40` |
| `Complex` | 16 | real `f64`, imaginary `f64` | `(1,2)` → `00..F0 3F 00..00 40` |
| `Rune` | 4 | `i32` scalar value | U+1F600 → `00 F6 01 00` |

A `nint` written on a 32-bit host is sign-extended to 8 bytes, and an `nuint` is zero-extended. A reader on a 32-bit
host MUST reject a value that does not fit. The .NET reader builds it with `new IntPtr(long)`, which throws
`OverflowException` on 32-bit hosts (from the source; **unverified: no 32-bit host was run**). The same holds only for
`nint`/`nuint` written as values or members. Inside a raw struct they take the host's pointer size (§10, item 3).

### 3.1 `decimal`

Sixteen bytes, in this order: `i32 flags`, `u32 hi32`, `u64 lo64`. The value is
`(-1)^sign × (hi32 × 2^64 + lo64) / 10^scale`. `scale` is bits 16–23 of `flags` (0–28) and `sign` is bit 31. All
other bits of `flags` are zero.

```
1.5m     00 00 01 00 | 00 00 00 00 | 0F 00 00 00 00 00 00 00      (scale 1, 15)
-0.01m   00 00 02 80 | 00 00 00 00 | 01 00 00 00 00 00 00 00      (scale 2, sign, 1)
2^64+3   00 00 00 00 | 01 00 00 00 | 03 00 00 00 00 00 00 00
```

### 3.2 `Guid`

Sixteen bytes. This is the .NET in-memory layout (`Guid.ToByteArray()`): the first group as `u32` little-endian,
the second and third groups as `u16` little-endian, then the last eight bytes in textual order.

```
00112233-4455-6677-8899-aabbccddeeff  →  33 22 11 00 55 44 77 66 88 99 AA BB CC DD EE FF
```

### 3.3 `DateTime`

One `u64`. Bits 0–61 are the ticks (100 ns since 0001-01-01T00:00:00). Bits 62–63 are the kind: `0` Unspecified,
`1` Utc, `2` Local.

```
2024-01-02 03:04:05 Unspecified  80 C0 BF 7A 3F 0B DC 08
                    Utc          80 C0 BF 7A 3F 0B DC 48
                    Local        80 C0 BF 7A 3F 0B DC 88
```

A `Local` value is written with the ticks of its local time. Readers MUST NOT convert it. Writers MUST NOT set the
kind to `3`. See §9.10 for what readers check.

### 3.4 `DateTimeOffset`

Ten bytes: `i64` **UTC** ticks, then `i16` offset in minutes. This is not the in-memory layout. Inside a collection
every element takes exactly 10 bytes, with no padding.

```
2024-01-02 03:04:05 +02:00   80 F0 36 B7 2E 0B DC 08 | 78 00      (UTC 01:04:05)
2024-01-02 03:04:05 -05:30   80 FC 77 94 6D 0B DC 08 | B6 FE      (UTC 08:34:05)
```

Readers MUST reject an offset outside ±14 hours (±840 minutes), and MUST reject UTC ticks that put the UTC or the
local time outside `DateTime`'s range. The .NET reader does both (`Nino: invalid DateTimeOffset`).

### 3.5 Variable-size BCL values

| Type | Encoding |
| --- | --- |
| `BigInteger` | `i32 n` (`n >= 1`), then `n` bytes: the value in two's complement, little-endian, minimal length (`ToByteArray()`). |
| `Version` | `i32 major`. If major is `-1`, the value is null and nothing follows. Otherwise `i32 minor, i32 build, i32 revision`. An undefined build or revision is `-1`. |
| `BitArray` | `i32` bit count `n` (`-1` = null), then `ceil(n/8)` bytes. Bit `i` is bit `i % 8` (LSB first) of byte `i / 8`. Writers MUST zero the unused high bits of the last byte. |
| `Uri` | its `OriginalString` as a string (§4). Null is a null string. |
| `CultureInfo` | its `Name` as a string. The invariant culture is `""`. |
| `StringBuilder` | a string, always in the UTF-16 form (§4.1, positive header), even when Latin-1 would fit. Readers MUST accept both forms. |
| `TimeZoneInfo` | the `Id` as a string (null = null zone, and nothing follows), then a second string: null for a zone the system knows (`FindSystemTimeZoneById`), otherwise .NET's `ToSerializedString()` of the custom zone. |

```
BigInteger 0      01 00 00 00 00
BigInteger 255    02 00 00 00 FF 00
BigInteger -1     01 00 00 00 FF
BigInteger -129   02 00 00 00 7F FF
Version 1.2       01 00 00 00 02 00 00 00 FF FF FF FF FF FF FF FF
Version null      FF FF FF FF
BitArray [1,0,1,0,0,0,0,0,1,1]   0A 00 00 00 05 03
TimeZoneInfo UTC  FC FF FF FF 55 54 43 | FF FF FF FF
```

`BigInteger` has no null encoding: a `BigInteger` is a struct. A count of `0` reads as zero. Writers MUST NOT write
it. A count of `-1` is invalid.

The custom-zone string of `TimeZoneInfo` is a .NET-specific text format. Non-.NET implementations MAY treat it as
opaque, but it is then not portable (§10, item 4).

---

## 4. Strings

A string has one of two encodings. Which one is chosen per member, by the type's layout (§8): `string` or `utf8`.
The default is `string`.

### 4.1 Default encoding (`string`)

```
i32 h
  h == -1        null; nothing follows
  h == 0         ""; nothing follows
  h <= -2        n = ~h (= -h - 1) chars, then n bytes: Latin-1 (each byte is the code unit U+0000..U+00FF)
  h >= 1         n = h chars, then n UTF-16LE code units (2n bytes)
```

- A writer MUST use the Latin-1 form when every code unit is `<= U+00FF`, and the UTF-16 form otherwise. The
  one exception is `StringBuilder`, which always uses UTF-16 (§3.5).
- The string is the sequence of UTF-16 code units, not of Unicode scalars. Unpaired surrogates are written as they
  are, in the UTF-16 form. Readers MUST NOT reject or replace them: the .NET reader returns them unchanged.
- The minimum Latin-1 header is `~1 = -2`, so `-1` stays free for null. A header of `int.MinValue` (`00 00 00 80`)
  would mean `2^31 - 1` chars. The .NET reader rejects it as running past the end of the data.

```
null        FF FF FF FF
""          00 00 00 00
"abc"       FC FF FF FF 61 62 63
"é"         FE FF FF FF E9
"ÿ"         FE FF FF FF FF
"a中"       02 00 00 00 61 00 2D 4E
"\uD800"    01 00 00 00 00 D8
"😀"        02 00 00 00 3D D8 00 DE
```

### 4.2 UTF-8 encoding (`utf8`, members marked `[NinoUtf8]`)

```
i32 c            char count: the length in UTF-16 code units
  c == -1        null; nothing follows
  c == 0         ""; nothing follows
  c >= 1         i32 b (byte count), then b bytes of UTF-8
```

- Writers MUST encode each unpaired surrogate as U+FFFD (`EF BF BD`). U+FFFD is one code unit, so `c` is also the
  length of the string read back.
- Every valid payload satisfies `c <= b <= 3c`. Readers MUST check this, MUST check that `b` bytes remain, and MUST
  reject bytes that are not strict UTF-8 (overlong forms, encoded surrogates, values above U+10FFFF, a sequence cut
  at the end) or that decode to a number of UTF-16 code units other than `c`. The .NET reader does all of this.

```
"é😀"         03 00 00 00 | 06 00 00 00 | C3 A9 F0 9F 98 80
"a\uD800"     02 00 00 00 | 04 00 00 00 | 61 EF BF BD
null          FF FF FF FF
""            00 00 00 00
```

---

## 5. Raw values and the canonical layout

### 5.1 Which values are raw

A value is **raw** when it is written as one block of bytes in the **canonical layout** (§5.2). The following are
raw:

1. Every type in the raw rows of §3 and every enum.
2. An unmanaged struct that is not a `[NinoType]` (no reference fields at any depth), whatever its fields. This
   excludes `Nullable<T>`, `ValueTuple<...>`, `KeyValuePair<,>` and `DateTimeOffset`, which have encodings of their
   own (§6.5, §6.6, §3.4).
3. A `[NinoType]` struct that is unmanaged, positional (§7.4), has sequential layout, has no custom formatter on any
   member, serializes every instance field (backing fields included), and whose `[NinoType]` struct fields are all
   raw too. Its layout metadata has `raw=true`.

Every other struct is written member by member (§7.4), with no padding.

For each type, the layout metadata (§8) is the normative statement of whether it is raw: `raw=true`, or a member
encoding `raw(n)` / `raw(n){...}`. The rules above explain where that comes from.

### 5.2 The canonical layout

The canonical layout is the one CoreCLR uses for the struct on a 64-bit host. Implementations MUST compute it as
follows (`NinoLayoutCore.Measure`, also given in [Appendix B.2](#b2-canonical-layout)):

- **Leaves** (size, alignment): 1-byte types (1, 1). 2-byte types (2, 2). 4-byte types (4, 4). 8-byte primitives,
  `DateTime` and `TimeSpan` (8, 8). `decimal` (16, 8). `Guid` (16, 4). `Int128` and `UInt128` (16, 16). Enums are
  their underlying type.
  `nint`/`nuint`/pointers are (P, P), where P is the host's pointer size (§10, item 3).
- **Structs**: fields in declaration order. The effective alignment of a field is `min(field alignment, Pack)`,
  where `Pack` comes from `[StructLayout(Pack = n)]` (0 means no limit).
  - Sequential layout: each field goes at the next multiple of its effective alignment after the end of the
    previous field.
  - Explicit layout: each field goes at its `[FieldOffset]`.
  - The struct's alignment is the largest effective alignment of its fields. Its size is the end of its furthest
    field rounded up to that alignment, then raised to `[StructLayout(Size = n)]` if that is larger.
  - Structs nest: a struct field is laid out the same way, then placed as one field.
- `LayoutKind.Auto` with more than one field has no canonical layout (§10, item 3).
- An empty struct (no instance fields) is raw with a size of 1, as in CoreCLR: it is written as one `00` byte, at
  the top level, as a member and as each element of a collection. Its layout is the bare `raw` encoding
  (`raw=true; ... members=raw` for a `[NinoType]`). Readers MUST read and skip that byte (verified: corpus cases
  `struct-root-empty` and `empties`).
- Every byte not covered by a leaf is padding. Writers MUST write it as `00`.

| Struct | Canonical bytes |
| --- | --- |
| `{ byte A; long B; }` = `{1, 2}` | `01 00 00 00 00 00 00 00 02 00 00 00 00 00 00 00` (16) |
| same, `Pack = 1` | `01 02 00 00 00 00 00 00 00` (9) |
| `Explicit, Size = 12 { [0] int A; [8] short B; }` = `{1, 2}` | `01 00 00 00 00 00 00 00 02 00 00 00` (12) |
| `{ int A; decimal D; }` = `{1, 1m}` | `01 00 00 00 00 00 00 00 \| 00 00 00 00 00 00 00 00 01 00 00 00 00 00 00 00` (24) |
| `{ bool A; int B; }` = `{true, 2}` | `01 00 00 00 02 00 00 00` (8) |
| `{ byte A; Int128 B; }` = `{1, -2}` | `01`, 15 × `00`, `FE`, 15 × `FF` (32) |
| `[NinoType] { int X; byte Y; }` = `{1, 2}` | `01 00 00 00 02 00 00 00` (8) |

The bytes of a raw value are copied as they are. Readers MUST NOT normalize them: a `bool` field holding `2` is
written as `02`.

Runtimes whose memory layout differs (x86 aligns 8-byte primitives to 4; Mono and IL2CPP align `decimal` to 4; .NET 7
aligns `Int128` to 8) convert
each value field by field into the canonical layout. The bytes on the wire are the same on every runtime.

---

## 6. Collections, tuples and nullables

### 6.1 Sequences

```
i32 count        -1 = null
element × count
```

Every one-dimensional collection uses this encoding. Each element is encoded under the element's declared type.
This covers:

- `T[]`, `List<T>`, `HashSet<T>`, `SortedSet<T>`, `LinkedList<T>`, `Queue<T>`, `Stack<T>`;
- the concurrent collections (`ConcurrentQueue`, `ConcurrentStack`, `ConcurrentBag`, `BlockingCollection`);
- `ObservableCollection<T>`, `ReadOnlyCollection<T>`, `Collection<T>`;
- the interfaces `IEnumerable<T>`, `ICollection<T>`, `IList<T>`, `IReadOnlyCollection<T>`, `IReadOnlyList<T>`,
  `ISet<T>`, `IReadOnlySet<T>`;
- `Memory<T>`, `ReadOnlyMemory<T>`, `ReadOnlySequence<T>`, `ArraySegment<T>`;
- the immutable sequences (`ImmutableArray<T>`, `ImmutableList<T>`, `ImmutableQueue<T>`, `ImmutableStack<T>`,
  `ImmutableHashSet<T>`, `ImmutableSortedSet<T>`);
- user classes that derive from or implement these collections.

When the elements are raw (§5), the elements follow as one contiguous block of `count × size` bytes. This produces
the same bytes as writing them one by one.

Element order:

| Collection | Order |
| --- | --- |
| arrays, lists, `LinkedList`, `Queue`, `ConcurrentQueue`, `ImmutableQueue` | index order / head to tail |
| `Stack<T>`, `ConcurrentStack<T>`, `ImmutableStack<T>` | **bottom to top** (the first pushed comes first) |
| `SortedSet`, `ImmutableSortedSet` | sorted order |
| `HashSet`, `ImmutableHashSet`, `ConcurrentBag`, interfaces | enumeration order (unspecified; readers MUST NOT depend on it) |
| `PriorityQueue<TElement, TPriority>` | the heap's array order, as `(element, priority)` pairs (§6.4) |

Null and empty values:

- A `Memory<T>`, `ReadOnlyMemory<T>` or `ReadOnlySequence<T>` cannot be null. Its default value is written as count `0`.
- A default `ArraySegment<T>` (no array) is written as `-1`.
- A default `ImmutableArray<T>` is written as `-1`, and `-1` reads back as a default `ImmutableArray<T>`.
- Jagged arrays nest: `int[][] {{1}, null}` → `02 00 00 00 | 01 00 00 00 01 00 00 00 | FF FF FF FF`.

```
int[] {1, 2}              02 00 00 00 01 00 00 00 02 00 00 00
int[] null                FF FF FF FF
string[] {"a", null, ""}  03 00 00 00 FE FF FF FF 61 FF FF FF FF 00 00 00 00
bool[] {true, false}      02 00 00 00 01 00
Stack<int> push 1, 2      02 00 00 00 01 00 00 00 02 00 00 00
```

### 6.2 Raw nullable elements: the fixed stride

A `T?[]` or `List<T?>` (also `Memory`, `ArraySegment` and the like, when the generator binds the formatter), where
`T` is raw, is written as a count followed by `count` elements of **stride `S = A + size(T)`**. Here `A` is `T`'s
canonical alignment and `size(T)` its canonical size. Each element is laid out like this:

```
offset 0       u8 hasValue (0 / 1)
offset 1..A-1  00 padding
offset A       T in the canonical layout, all zero when hasValue = 0
```

| `T` | `A` | stride |
| --- | --- | --- |
| `byte` | 1 | 2 |
| `int` | 4 | 8 |
| `long`, `double`, `DateTime` | 8 | 16 |
| `decimal` | 8 | 24 |
| `Guid` | 4 | 20 |
| `{byte; long}` | 8 | 24 |
| `[NinoType] {int; byte}` | 4 | 12 |

```
int?[]  {1, null}   02 00 00 00 | 01 00 00 00 01 00 00 00 | 00 00 00 00 00 00 00 00
byte?[] {1, null}   02 00 00 00 | 01 01 | 00 00
```

This applies only to array-like and list types bound to the nullable-element formatter. A member declared as
`IList<T?>`, `IEnumerable<T?>` or another interface writes each element as a single `T?` (§6.5), with no padding:
`IList<int?> {1, null}` → `02 00 00 00 | 01 01 00 00 00 | 00`. The layout metadata tells the two cases apart:
`seq(raw(S){...})` versus `seq(nullable(...))`. But see §10, items 1 and 2.

### 6.3 Maps

```
i32 count        -1 = null
(key, value) × count
```

This covers `Dictionary`, `SortedDictionary`, `SortedList`, `ConcurrentDictionary`, `IDictionary`,
`IReadOnlyDictionary`, `ImmutableDictionary` and `ImmutableSortedDictionary`, and their user subclasses. Sorted maps
are written in key order. Hash maps are written in enumeration order, which is unspecified.

"Key order" is the order of the collection's comparer, and the default comparer of a .NET sorted collection
(`Comparer<T>.Default`) is culture-sensitive for `string` keys (`string.CompareTo`, the current culture's
collation): `"a"` sorts before `"B"`, unlike ordinal order (`"B" < "a"`), and the order can differ between
cultures, ICU versions and invariant-globalization mode. Other keys (integers, enums, `Guid`, ...) sort as their values. Therefore:

- A reader in another language MUST keep the entries in wire order (or accept any order) and MUST NOT reject or
  re-sort a sorted map or set whose string keys are not in ordinal order. The same applies to `SortedSet<string>`,
  `ImmutableSortedSet<string>`, `SortedList<string, ...>` and `ImmutableSortedDictionary<string, ...>`.
- A writer in another language cannot reproduce .NET's string order in general. It SHOULD write the entries in the
  order it was given them (as read), or in ordinal order when it builds the map itself. .NET reads either: it
  checks the order with its own comparer and inserts the entries one at a time when they are not in that order. The bytes then differ from what .NET would write for the same map.

```
Dictionary<int,string> {1:"a"}             01 00 00 00 | 01 00 00 00 FE FF FF FF 61
SortedDictionary<string,int> {b:2, a:1}    02 00 00 00 | FE FF FF FF 61 01 00 00 00 | FE FF FF FF 62 02 00 00 00
```

### 6.4 Groupings and priority queues

- `IGrouping<TKey, TElement>`: `i32 count` (`-1` = null), then the key, then `count` elements.
- `ILookup<TKey, TElement>`: `i32 groupCount` (`-1` = null), then for each group `i32 count`, the key and the
  elements. The count comes before the key.
- `PriorityQueue<TElement, TPriority>`: `i32 count` (`-1` = null), then `count` pairs `(element, priority)` in the
  heap's internal order.

```
ILookup<int,int> {1:[1,3], 2:[2]}   02 00 00 00 | 02 00 00 00 01 00 00 00 01 00 00 00 03 00 00 00 | 01 00 00 00 02 00 00 00 02 00 00 00
PriorityQueue {("a",2), ("b",1)}    02 00 00 00 | FE FF FF FF 62 01 00 00 00 | FE FF FF FF 61 02 00 00 00
```

### 6.5 Nullable values, `Lazy<T>` and `Tuple<...>`

- `T?` (a nullable value type): a presence byte, then `T` when present. This holds at every place a single `T?`
  appears, including members and interface-typed collections (§6.2 is the only exception).
- `Lazy<T>`: the same as `T?`, holding the lazy's `Value`.
- `Tuple<T1, ...>` (a class): a presence byte, then the items in order.

```
int? null         00
int? 5            01 05 00 00 00
Color? Green      01 02
Tuple<int,string> (1,"a")   01 | 01 00 00 00 | FE FF FF FF 61
```

### 6.6 `ValueTuple` and `KeyValuePair`

The items are written in order, each under its own type, with **no padding and no header**. This holds even when
every item is raw: `(byte, int)` takes 5 bytes, alone and inside a `List`. A `ValueTuple` of 8 or more items (a
`ValueTuple` whose eighth argument holds the rest) is written the same way, every item in order, and published as one
`tuple(...)` of all its items.

```
(byte, int) (1, 2)                01 02 00 00 00
List<(byte,int)> {(1,2)}          01 00 00 00 | 01 02 00 00 00
KeyValuePair<int,string> (1,"a")  01 00 00 00 FE FF FF FF 61
```

### 6.7 Multi-dimensional arrays

```
i32 len0         -1 = null; nothing follows
i32 len1 [, len2 [, len3]]
elements × (len0 × len1 × ...), row-major (last index varies fastest)
```

Ranks 2, 3 and 4 are supported. Only `len0` may be `-1`. The other lengths, and `len0` when it is not null, MUST be
`>= 0`.

```
int[2,3] {{1,2,3},{4,5,6}}   02 00 00 00 03 00 00 00 | 01.. 02.. 03.. 04.. 05.. 06..
int[0,3]                     00 00 00 00 03 00 00 00
int[,] null                  FF FF FF FF
```

### 6.8 Collections are values

The same collection instance in two places is written twice. Reference tracking (§7.9) covers `[NinoType]` objects
only.

---

## 7. `[NinoType]` objects

### 7.1 Headers

The header depends on the **declared** type, as published in its layout (`header=`, §8):

| Declared type | Layout `header` | Header bytes |
| --- | --- | --- |
| struct | `None` | none (a struct is never null) |
| sealed class | `NullFlag` | presence byte: `00` null, `01` present |
| non-sealed class, abstract class, interface, `record` class, `object` | `TypeId` | `i32` type id of the runtime type, `0` = null |

A record class that is not sealed is a non-sealed class, so it gets a type-id header. With `trackReferences` the
header changes (§7.9).

The body follows the header unless the value is null or its body was deferred (§7.10).

### 7.2 Type ids

The type id is the 32-bit **FNV-1a** hash of the type's **id name**. The hash runs over each UTF-16 code unit,
feeding first its low byte and then its high byte:

```
h = 2166136261
for each UTF-16 code unit c of name:
    h = (h XOR (c & 0xFF)) × 16777619        mod 2^32
    h = (h XOR (c >> 8))   × 16777619        mod 2^32
if h == 0: h = 1
id = h reinterpreted as i32
```

The **id name** is the `[NinoFormerName("...")]` argument of the type, when it has one. Otherwise it is the
runtime-form name (`NinoTypeId.GetName`):

- `Namespace.Name`, or `Name` alone when the type has no namespace.
- Nested types: `Namespace.Outer+Inner`, with the outer chain joined by `+`.
- Generic types: the name without the arity suffix, then `<`, the argument names joined by `,` **(no space)**, then
  `>`. Each argument is written in this same form, with full namespaces (`System.Int32`, not `int`). For a generic
  type nested in a generic type, all the arguments (the outer type's and its own) come at the end, after the last
  name.
- Arrays: the element's name, then `[]`, or `[` plus `rank - 1` commas plus `]` (`System.Int32[,]`).

| Name | Id (hex, as `u32`) | Bytes on the wire |
| --- | --- | --- |
| `Spec.Small` | `F8B7D639` | `39 D6 B7 F8` |
| `Spec.Outer+Inner` | `0FF257FA` | `FA 57 F2 0F` |
| `Spec.Box<System.Int32>` | `6781EE38` | `38 EE 81 67` |
| `Spec.Box<System.String>` | `F0DD44FF` | |
| `Spec.Box<System.Collections.Generic.List<System.Int32>>` | `CB133F4F` | |
| `Spec.Box<System.Int32[]>` | `140E75B6` | |
| `Spec.Box<System.Int32[,]>` | `803DF882` | |
| `Old.Name` (a `[NinoFormerName("Old.Name")]` type) | `A578B9DB` | |
| `""` | `811C9DC5` | |
| `a` | `2B24D044` | |

The layout metadata publishes the id of every non-generic type (`typeId=`). For a generic type definition it
publishes `0`, and the id of each closed type MUST be computed from its name as above.

### 7.3 Members and their order

Each type's layout (§8) lists its members in the order they are written. **That list is normative.** The rules the
generator follows to build it are given here for reference:

- The body holds the members of the whole hierarchy: the root `[NinoType]` base first, then each derived level down
  to the type itself.
- The members of a `[NinoType]` interface come first in the first class that implements it.
- Within a level, members appear in declaration order. With `autoCollect: false` they are ordered by their
  `[NinoMember(n)]` index instead, then by name (ordinal).
- An `override` is written once, at its base's position. A member hidden with `new` is a separate member, and the
  base's member comes first.
- Private members of a base class are not included. A get-only auto-property is included only when a constructor
  parameter restores it.

### 7.4 Positional format (`format=Positional`)

The body is the members, in order, each in its own encoding, with **no padding, no lengths and no names**. A
struct body is the same: a non-raw struct has no header and no padding.

```
sealed class Mixed { int A; byte B; long C; string D; short E; short F; } = {1, 2, 3, "x", 4, 5}
01 | 01 00 00 00 | 02 | 03 00 00 00 00 00 00 00 | FE FF FF FF 78 | 04 00 | 05 00
```

### 7.5 Version-tolerant format (`format=VersionTolerant`)

```
header | i32 L | members of the whole hierarchy, root first (L bytes)
```

- `L` counts the bytes of the members. It does not count the header or `L` itself. A type with no members still
  writes `L = 0`.
- **Reading.** The reader limits itself to the `L` bytes. Before each member it checks whether the region is
  exhausted.
  - When the region is exhausted, the remaining members keep their current value (when reading into an existing
    object) or their default.
  - Bytes left over after the last known member are skipped.
  - A member that is only partly inside the region is an error.
- When the length has bit 31 set, the region carries a section of deferred bodies (§7.10.3).
- Only members appended to the most derived type are tolerated. An appended base member shifts every derived
  member that follows it.
- A version-tolerant struct has no header: `TolStruct { int X = 1 }` → `04 00 00 00 01 00 00 00`.
- **`WEAK_VERSION_TOLERANCE`.** When the project that defines the types sets this compile-time symbol, every
  positional type of that assembly becomes version tolerant. Its structs are then never raw. The layout metadata
  shows the result (`format=VersionTolerant`).

### 7.6 Keyed format (`format=Keyed`)

```
header | i32 L | entries (L bytes)
entry = i32 key | i32 len | value (len bytes)
```

- **Keys.** The key of a member is the FNV-1a hash (§7.2, including the `0 → 1` rule) of its C# name. A member
  hidden by a `new` member in a derived type, when both are serialized, uses `DeclaringTypeSimpleName.Name`. The
  layout publishes each key (`:key=0x...`) and its former-name keys (`:former=0x...,...`). Implementations MUST use
  the published keys rather than recompute them.
- **Writing.** Writers MUST write one entry for every member, in layout order. A member with a null value still
  gets its entry: for example `len = 4` with the value `FF FF FF FF`.
- **Reading.**
  - Readers MUST accept entries in any order.
  - For each entry, the reader looks the key up among the member keys and the former keys of the type. When it
    finds a match, it reads the value from exactly `len` bytes. Otherwise it skips `len` bytes.
  - Members with no entry keep their current or default value.
  - When a key appears more than once, the .NET reader silently keeps the last one (§9.8).
  - A value that reads past `len` is an error. Writers MUST make `len` the exact size of the value, and readers MUST
    reject a known member whose value leaves bytes unread in its entry (`Nino: bytes left over at the end of a keyed
    member`). Entries of unknown keys are skipped whole.
- Keyed types are never raw. A keyed type that is also a `[NinoType]` collection puts its elements inside the
  region, before the entries (§7.7).

### 7.7 `[NinoType]` collections

A `[NinoType]` class or struct that is also a collection (for example `class Bag : List<int> { string Tag; }`) writes
the collection's elements first, then its members. The layout shows this with a first entry `[elements]::<encoding>`.

| Format | Body |
| --- | --- |
| Positional | `elements | members` |
| VersionTolerant | `elements | i32 L | members`: the elements are **outside** the region |
| Keyed | `i32 L | elements | entries`: the elements are **inside** the region, before the entries |

```
Bag (non-sealed, so a type id header) {1}, Tag = "t"
9A 4F 30 73 | 01 00 00 00 01 00 00 00 | FE FF FF FF 74
```

```
TolBag : List<int>, VersionTolerant, {1}, Tag = "t"   D5 08 56 C7 | 01 00 00 00 01 00 00 00 | 05 00 00 00 FE FF FF FF 74
KeyBag : List<int>, Keyed, {1}, Tag = "t"             A5 B3 61 8A | 15 00 00 00 | 01 00 00 00 01 00 00 00 | 57 4F FF 26 05 00 00 00 FE FF FF FF 74
```

### 7.8 Polymorphism

- A value whose declared type has a `TypeId` header is written as the id of its **runtime** type, then the body of
  the runtime type (in that type's own format). There is no presence byte, even when the runtime type is sealed.
- A `[NinoType]` struct held through a `[NinoType]` interface or `object` is written as its type id followed by its
  struct body.
- **Reading under a declared base or interface** (`T`): readers MUST accept only ids of registered `[NinoType]`
  types that are assignable to `T`. An unknown id, or the id of a type that is not a subtype, is an error.
- **Reading under `object`**: any registered `[NinoType]` id is accepted, including closed generic types.
  - In .NET, an id it has never seen makes the reader scan every loaded assembly once, looking for a type with that
    id (§9.6).
  - Implementations SHOULD restrict `object` to an explicit set of types.

```
Shape s = new Circle { Id = 1, R = 2f }      AC 2B C0 ED | 01 00 00 00 | 00 00 00 40
Circle c (declared as the sealed Circle)     01 | 01 00 00 00 | 00 00 00 40
IAnimal a = new Dog { Name = "d", Age = 3 }  92 46 6C 92 | FE FF FF FF 64 | 03 00 00 00
List<Shape> {Circle(1,1f), Square(2,2f), null}
  03 00 00 00 | AC 2B C0 ED 01 00 00 00 00 00 80 3F | 1F 27 E7 8F 02 00 00 00 00 00 00 40 | 00 00 00 00
```

### 7.9 Reference tracking (`trackReferences=true`)

Subtypes inherit tracking from a tracked type. Every tracked object carries a **reference header**:

```
i32 r     0 = null
          -1 = a new object; its encoding follows
          n > 0 = the n-th new object written in this payload (1-based)
```

New objects are numbered in the order their `-1` headers are written, across the whole payload. This includes
objects inside regions and deferred bodies. One numbering is shared by every slot type. How the header combines with
the slot's header depends on the slot:

| Slot (declared type) | Encoding |
| --- | --- |
| tracked sealed class | `r`, then the body when `r == -1` (no presence byte) |
| tracked non-sealed class or interface | `r`, then when `r == -1`: the type id, then the body |
| untracked declared type (`object`, an untracked base or interface) holding a tracked object | the type id, then `r`, then the body when `r == -1` |

In the third form, a back reference is 8 bytes (id, index). The index MUST name an object whose type matches the id.

```
sealed tracked Node {V, Next}, n.Next = n:      FF FF FF FF | 01 00 00 00 | 01 00 00 00
non-sealed tracked TNode, t.Next = t:           FF FF FF FF | 62 41 7C 78 | 01 00 00 00 | 01 00 00 00
sealed Holder { object Any; Node N; }, Any = N = node{5, null}:
  01 | 06 2E 6C 29 FF FF FF FF 05 00 00 00 00 00 00 00 | 01 00 00 00
```

### 7.10 Deep nesting and deferred bodies

To bound stack use, deeply nested class bodies are moved out of line. Shallow data is unaffected: nothing on the
wire marks deferral when it does not happen. **Every reader MUST implement this section.** Payloads nested more than
128 class bodies deep (a linked list of 130 nodes is enough) cannot be read without it.

#### 7.10.1 Depth

- The depth `D` is the number of `[NinoType]` **class** bodies open at the current position. It is 0 at the top
  level and when a deferred body starts (see below).
- Struct bodies, collections, tuples and other built-in types do not count.
- A class body opens (`D` increases by 1) after its header, and closes when its last member has been written.

When a class body is about to open while `D >= 128` (`NinoDepth.Inline`):

- If the runtime type is **deferrable**, the header (presence byte, type id or reference header) is written as
  usual, **but the body is not**. The object is queued instead.
- Otherwise the body is written inline. Writers and readers MUST fail when a body would open at `D >= 512`
  (`NinoDepth.Max`). The .NET implementation may also fail earlier, when its thread runs low on stack.

**Deferrable**: a class that Nino creates before reading its members. That excludes:

- abstract classes and interfaces;
- types re-created through a constructor or factory that takes member values (records with a primary
  constructor, init-only or read-only members);
- types compared by value (records, types that override `GetHashCode`, types that implement `IComparable`).

The layout metadata publishes it as `deferrable=true|false` (§8): readers and writers in other languages MUST
take it from there, not work it out from the rules above. Struct bodies are never deferred (`deferrable=false`).
Layouts written by an older generator (from before Nino.Core had the property) leave it out; a type is then
deferrable exactly when the rules above say so.

#### 7.10.2 Deferred bodies outside regions

A body deferred while no region (§7.5, §7.6) is open goes into the **top queue**:

- When a class body closes and `D` returns to 0, the writer writes every queued body, in queue order, right there.
- Each queued body is written as just the body (its header is already in place), starting at `D = 1`, as if
  inside one open body.
- Bodies deferred while writing them are appended to the same queue.

The bodies therefore come right after the outermost class value that contains them. For a top-level class, that is
after the whole value. For a top-level `List<LinkedNode>`, it is after each element (verified). The reader mirrors
this: at the point the writer deferred, it creates the object, stores it in the slot and queues it. When its own
depth returns to 0, it reads the queued bodies in order.

```
sealed LinkFirst { LinkFirst Next; int V; }, a chain of 131 nodes, V = 1..131:
01 ×129                 presence bytes of nodes 1..129, each followed by its Next
                        (node 129's body is deferred: D = 128 when it opens)
04-byte V of nodes 128..1   (nodes 1..128 close, innermost first)
01 01 00 83 00 00 00 82 00 00 00 | 81 00 00 00
                        deferred body of node 129 at D = 1: Next present, node 130 (Next present, node 131
                        (Next null, V = 131), V = 130), V = 129
```

#### 7.10.3 Deferred bodies inside regions: sections

A body deferred while a region is open goes into a **section** at the end of the **outermost** open region. Only
the outermost region holds a section.

```
i32 (T | 0x80000000) | members (M bytes) | entry* | i32 M
entry = i32 key | i32 len | body (len bytes)
```

- `T` is the total size of everything after the length field: members, entries and the trailing `i32 M`.
- The trailing `i32 M` is the offset of the section, which is also the length of the members. Both `T` and `M` are
  measured from the first byte after the length field.
- `key` is the position, relative to that same start, where the deferred body would have begun if it had been
  written inline: the byte right after the object's header.
- Entries are written in deferral order. Bodies deferred while writing entries are appended as further entries of
  the same section.
- Each entry body is written starting at depth `min(Dr, 127) + 1`. `Dr` is the depth while the outermost region's
  members are written: it counts the region's own class, if the region belongs to one.
- Inner regions hold no sections. Their lengths (bit 31 clear) cover only their inline members.

Reading:

1. A region length with bit 31 set is valid only when `T >= 4` and `T` fits in the remaining data, and only on the
   outermost region: an inner region with bit 31 set MUST be rejected. `M` is read from the last 4 bytes of the
   region and MUST satisfy `0 <= M < T - 4` (a section holds at least one entry). The members are read from the
   first `M` bytes only.
   The entries MUST fill the section exactly: each has its 8-byte head and a `len` that fits in what is left before
   the trailing `M`, and the keys MUST be strictly increasing, `>= 0`, and no greater than the entry's own offset
   (a body is deferred from the members, at most at their end, or from the body of an earlier entry). Readers check
   this when they open the region, before reading any member; .NET throws `Nino: invalid offset of deferred
   bodies` or `Nino: invalid section of deferred bodies`.
2. After the outermost region's members, the reader goes through its queue in order. For each queued object, it
   scans entries forward until it finds one with `key` equal to the object's position, skipping the others by their
   `len`. A reader that skipped unknown members never queued their objects, so their entries are skipped.
3. An entry's body MUST be read from exactly its `len` bytes.
4. When the reader has queued objects but the region has no section (bit 31 clear), the data is invalid
   (`Nino: missing deferred bodies`).

```
VersionTolerant sealed LinkTol { int V; LinkTol Next; }, 130 nodes, V = 1..130 (1183 bytes):
01 | 9A 04 00 80 | 01 00 00 00 01 | 73 04 00 00 02 00 00 00 01 | 6A 04 00 00 ...
     T = 0x49A, flag set          node 2's region, L = 0x473 (an inner region: no flag)
...  05 00 00 00 80 00 00 00 01    node 128's region: L = 5, V = 128, Next present (body deferred)
     7C 04 00 00 | 12 00 00 00 | 0E 00 00 00 81 00 00 00 01 05 00 00 00 82 00 00 00 00
     entry: key = 0x47C, len = 18, body of node 129 (its region: V = 129, Next = node 130 inline)
     7C 04 00 00                   M = 0x47C: the section starts at member offset 1148
```

The keyed format works the same way, each member as its key, its length and its value, in declaration order. The
corpus case `deep-keyed-chain-129` (`Keyed sealed KeyedChain { int Value; KeyedChain Next; }`, 129 nodes, 3238
bytes) starts with the region of node 1 and its `Value` entry, then the `Next` entry holding node 2:

```
01 | A1 0C 00 80 | 3E 23 73 C3 04 00 00 00 01 00 00 00 | 3A 11 A6 C9 68 0C 00 00 01 | 63 0C 00 00 ...
     T, flag set   key(Value), len 4, Value = 1          key(Next), len, present    node 2's region
```

and ends with the section of the outermost region: `... | 7C 0C 00 00` (`M` = 0xC7C).

#### 7.10.4 Cycles

An untracked cycle deeper than 128 would make the writer defer forever. The .NET writer detects it with Brent's
algorithm over the chain of deferred objects and fails. Other writers MUST detect cycles too: untracked object
graphs MUST be trees.

### 7.11 Pooling, reuse and custom formatters

- **Pooling and reuse.** Object pooling (`NinoPool`, `NinoRecycler`) and reading into existing instances have no
  effect on the bytes.
- **Custom formatters.** A member or type that uses a custom formatter (`[NinoCustomFormatter]`, `[NinoFormatter]`,
  or a formatter from another library) is written however that formatter writes it. The layout shows it as
  `formatter(F)`. Its bytes are outside this specification.
- **Formatter-backed BCL types.** Some BCL types are also published as `formatter(F)`, for example
  `Nino.Immutable.ImmutableArrayFormatter<...>`. Their bytes are the ones given in §6.

---

## 8. Type layout metadata and its grammar

For every `[NinoType]` it generates a formatter for, the generator adds `[assembly: NinoTypeLayout(...)]` to the
assembly:

```
NinoTypeLayout(Type type, NinoFormat format, NinoHeader header, bool trackReferences, bool raw, int typeId,
               string members) { Deferrable = bool }
NinoFormat:  Positional = 0, VersionTolerant = 1, Keyed = 2
NinoHeader:  None = 0, NullFlag = 1, TypeId = 2
```

`Deferrable` is a named argument (a settable property): whether a body of the type nested too deep is deferred
(§7.10.1). The generator always sets it; layouts from an older generator (from before Nino.Core had the property) do not,
and their `ToString()` has no `deferrable` part.

Tools can read it through reflection or straight from metadata (`Nino.TypeScript` uses `MetadataLoadContext`). It is
the intended input for code generators in other languages. Its `ToString()`:

```
format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0xA333A1B0; deferrable=true; members=Id:int:raw(4):key=0x98C41948;Name:string:string:key=0x3085B3E2:former=0x93A29D95;Score:long:raw(8):key=0x044AB14D
```

### 8.1 Grammar (EBNF, ISO 14977 style)

```ebnf
layout        = "format=" , format , "; header=" , header ,
                "; trackReferences=" , bool , "; raw=" , bool ,
                "; typeId=0x" , hex8 , [ "; deferrable=" , bool ] ,
                "; members=" , members ;                (* left out only by older generators *)
format        = "Positional" | "VersionTolerant" | "Keyed" ;
header        = "None" | "NullFlag" | "TypeId" ;
bool          = "true" | "false" ;

(* the members attribute argument alone *)
members       = raw_encoding                      (* when raw = true *)
              | [ member_list ] ;                 (* empty for a type without members *)
member_list   = member , { ";" , member } ;
member        = elements | named ;
elements      = "[elements]::" , encoding ;       (* only first *)
named         = name , ":" , type , ":" , encoding , [ key ] ;
key           = ":key=0x" , hex8 , [ ":former=0x" , hex8 , { ",0x" , hex8 } ] ;   (* Keyed only *)

encoding      = raw_encoding
              | "string" | "utf8" | "object"
              | "type(" , type , ")"
              | "nullable(" , encoding , ")"
              | "seq(" , encoding , ")"
              | "map(" , encoding , "," , encoding , ")"
              | "pair(" , encoding , "," , encoding , ")"
              | "tuple(" , encoding , { "," , encoding } , ")"
              | "group(" , encoding , "," , encoding , ")"
              | "grid(" , digit , "," , encoding , ")"
              | "datetimeoffset" | "bigint" | "version" | "bits" | "timezone"
              | "formatter(" , type , ")" ;
raw_encoding  = "raw" , [ "(" , size , ")" , [ "{" , field , { "," , field } , "}" ] ] ;
field         = name , ":" , type , "@" , size , ":" , encoding ;

name          = identifier ;                        (* the C# member name *)
type          = ? a C# type name, see 8.2 ? ;
size          = digit , { digit } ;                 (* decimal *)
hex8          = 8 * hexdigit ;                      (* upper case; the i32 as u32 *)
hexdigit      = digit | "A" | "B" | "C" | "D" | "E" | "F" ;
```

### 8.2 Type names and splitting

- Type names are written like C# source: fully qualified, without `global::`. Keyword aliases are used for the
  primitive types (`int`, `long`, `string`, `decimal`, `object`).
- Constructed types are spelled out in full: `System.Nullable<int>` (not `int?`), `System.ValueTuple<int, long>`,
  `System.IntPtr`.
- Generic arguments are separated by `", "` (comma and space), unlike type-id names (§7.2). Arrays are `int[]` and
  `int[,]`. Type parameters appear by name (`Value:T:type(T)`). Nested types are written in C# form, with a dot
  between outer and inner type (`Spec.Outer2.In2`), unlike the `+` of type-id names.
- Type names can contain `,`, `:`, `<`, `>`, `[` and `]`. To split a list (members on `;`, fields and arguments on
  `,`, parts on `:`), a parser MUST track nesting depth over `()`, `{}`, `<>` and `[]` and split only at depth 0.
  This is what `src/Nino.TypeScript/WireEncoding.cs` (`SplitMembers`, `Parse`) does.

### 8.3 Meaning of the encodings

| Encoding | Bytes |
| --- | --- |
| `raw(n)` | `n` raw bytes (§3, §5) |
| `raw(n){f:T@o:E,...}` | a raw struct of `n` canonical bytes, field `f` at offset `o` |
| `raw` | a raw struct whose size depends on the host (§10, item 3) |
| `string`, `utf8` | §4.1, §4.2 |
| `object` | `i32` type id (`0` = null), then that type's body (§7.8) |
| `type(T)` | `T` as its own layout says (header and body). `T` may be a type parameter. |
| `nullable(E)` | presence byte, then `E` (also `Lazy<T>`, and `Tuple` as `nullable(tuple(...))`) |
| `seq(E)` | §6.1 (`PriorityQueue` is `seq(pair(E,P))`, `ILookup` is `seq(group(K,E))`) |
| `map(K,V)` | §6.3 |
| `pair(K,V)`, `tuple(E,...)` | the items, no header, no padding (§6.6) |
| `group(K,E)` | `i32 count`, key, elements (§6.4) |
| `grid(r,E)` | §6.7 |
| `datetimeoffset`, `bigint`, `version`, `bits`, `timezone` | §3.4, §3.5 |
| `formatter(F)` | written by `F` (§7.11) |

`Uri`, `CultureInfo` and `StringBuilder` are published as `string`. `nint` and `nuint` are published as `raw(8)`.

The layout does not publish everything a reader needs. The gaps are listed in §10.

---

## 9. Validation requirements for readers

A reader that accepts data from an untrusted source MUST satisfy the requirements below. For each one, the note
**.NET** says what the .NET reader does today. **"Note: the .NET reader currently ..."** marks a place where the
.NET reader is weaker than the requirement. Requirements stated only as SHOULD are recommendations that the .NET
reader does not meet.

### 9.1 Bounds

Every read MUST stay inside the payload, or inside the current region or entry. Data that ends early MUST be
rejected. **.NET:** done. Running out of data throws `InvalidOperationException`, and reads never go past a
region's end.

### 9.2 Lengths and allocation

A length or count MUST be `-1` (where null is allowed) or `>= 0`. Before allocating, a reader MUST check that
`count × minimum element size` does not exceed the remaining bytes. The minimum element size is the stride for raw
elements and at least 1 for everything else.

**.NET:** done (`TryReadLength`, with the product computed in 64 bits). Where an element takes far more memory than
its minimum wire size (more than 16 bytes), .NET grows the collection as it reads instead of pre-allocating
`count` slots. Implementations SHOULD bound memory per input byte the same way. Multi-dimensional shapes are
checked one product at a time against overflow and the remaining bytes.

### 9.3 Presence bytes and `bool`

A presence byte or a `bool` MUST be `0` or `1`, and readers MUST reject other values: a `bool` read as a value or a
member, the elements of a `bool[]` or `List<bool>` (and other raw blocks of `bool`), the presence byte of a sealed
class, `T?`, `Lazy<T>` or `Tuple<...>`, and the has-value bytes of a fixed-stride `T?[]` (§6.2). **.NET:** done
(`Nino: invalid bool byte`, `Nino: invalid presence byte`); so are the Go, Rust and TypeScript runtimes.

Note: the .NET reader copies a `bool` field inside a raw struct (§5) with the struct's bytes, unchecked, unless
`NinoSettings.Untrusted` is set (§9.10).

### 9.4 Strings

Readers MUST apply the checks of §4.2 to UTF-8 strings, and MUST reject string headers whose length does not fit in
the remaining bytes. Unpaired surrogates in the default encoding MUST be accepted (§4.1). **.NET:** done.

### 9.5 Regions and keyed entries

- A region length MUST fit in the remaining bytes. The section rules of §7.10.3 MUST be checked.
- A member that runs past its region's end MUST be rejected.
- Each keyed entry's `len` MUST fit in the remaining region, and a known member's value MUST fill it exactly (§7.6).

**.NET:** done (`ReadRegion`, `SectionedRegion`, `EndMember`).

### 9.6 Type ids

- An id MUST be `0` (where null is allowed) or a known type that is assignable to the declared type.
- Unknown ids MUST be rejected.
- Readers SHOULD restrict `object` slots to an allow-list.

**.NET:** unknown ids and non-subtypes throw.

Note: the .NET reader currently accepts any registered `[NinoType]` under `object`, from any loaded assembly. When
it meets an unknown id, it also scans every loaded assembly once to find the type.

### 9.7 References

A back reference MUST be in `1..k`, where `k` is the number of objects created so far, and MUST name an object of a
compatible type. Negative headers other than `-1` MUST be rejected. **.NET:** done (`Nino: invalid object
reference`).

### 9.8 Duplicate keys

- A keyed body with the same key twice SHOULD be rejected.
- A dictionary or set with a key repeated under the reader's comparer MUST be rejected when it is filled from empty.

**.NET:** dictionaries, `HashSet<T>` and lookups throw on a repeated key when filled from empty. When reading into a
non-empty dictionary, the last value wins.

- A reader that fills a hash table from the data SHOULD bound the work each key costs. Keys built to share a hash
  bucket (hash flooding) make each insert scan all the keys before it, and the read quadratic in the data.

**.NET:** a key that finds more than `NinoSettings.MaxHashCollisions` (default 100) keys in its bucket throws
`NinoDataException`, in `Dictionary<TKey, TValue>`, `HashSet<T>`, lookups, `ImmutableDictionary` and `ImmutableHashSet`
(more than that many keys with one hash code). Nino walks the bucket as it adds a key to a new table, and walks it
after the table adds a key itself; string keys of tables that switch to a randomized comparer on their own (.NET's,
and .NET Framework's `Dictionary`) are left to them. `ConcurrentDictionary` is not covered.

Note: the .NET reader currently accepts a keyed body with a repeated key, and the last entry wins (verified).

### 9.9 Depth

Readers MUST bound nesting and implement deferral (§7.10). Data that nests a deferrable type inline past depth 128
is not what writers produce:

- Inside a region, it MUST be rejected (no matching section entry).
- Outside a region, the .NET reader treats it as deferred bodies, which keeps the stack bounded.

Non-deferrable bodies MUST NOT exceed depth 512. **.NET:** done. It also fails early when its thread is low on
stack.

Struct bodies do not count toward `D` (§7.10.1) and are never deferred, but they do count toward the limit of 512
when reading: a struct that can hold itself (through a collection, a nullable, a `Lazy` or an interface) would
otherwise nest without bound. **.NET** counts, while reading, each open body of such a struct (`Reader.EnterStruct`,
emitted for structs whose nesting no bound covers; collections that hold themselves count their levels the same way)
and fails when that count plus `D` exceeds 512, or when a non-deferrable class body opens with `D` plus that count
at 512 or more. Structs that cannot reach themselves are not counted (their nesting is bounded by their types).
The .NET writer counts only class bodies, so it can write struct nesting its reader then rejects. Readers in other
languages MUST count the same struct bodies the same way (Rust: `enter_struct`), so that they accept exactly what
.NET reads.

### 9.10 Value ranges

`DateTimeOffset` MUST be validated (§3.4). `DateTime` SHOULD be rejected when its ticks exceed
`DateTime.MaxValue.Ticks`. Kind `3` is valid: .NET writes it for a local time in the repeated hour of a daylight saving
change (`DateTime.ToBinary`), and reads it back as `DateTimeKind.Local`. A `decimal` SHOULD be rejected when its flags
have bits set other than the sign and the scale, or its scale exceeds 28; `DateOnly` when its day number exceeds
`DateOnly.MaxValue`'s, and `TimeOnly` when its ticks reach a day.

**.NET:** with `NinoSettings.Untrusted` set, every such value is checked where it is read, as a value, a member, an
element or a field of a raw struct or tuple, and so is every `bool` inside one (§9.3). Without it (the default) these
values are copied as they are, at no cost: a `DateTime` with out-of-range ticks, or a `decimal` with an invalid scale,
then reaches the caller. `TimeSpan`, `Guid` and `char` have no invalid bits. Enum values are never checked against
the enum's defined members (§2.5), in either mode: flags enums combine them, and data written by a newer version may
carry members this one lacks.

`Version` MUST reject a negative major (other than `-1`) and a negative minor. **.NET:** done.

Note: the .NET reader currently treats any negative build as undefined (and any negative revision, when the build is
defined), not only `-1`.

### 9.11 Unused bits

Readers SHOULD reject a `BitArray` whose bits past the count are not zero, padding bytes in raw values that are not
zero, and `BigInteger` byte arrays that are not minimal.

Note: the .NET reader currently ignores all three.

### 9.12 Trailing data

Readers SHOULD offer a mode that rejects bytes after the top-level value (and its deferred bodies).

**.NET:** with `NinoSettings.Untrusted` set, a whole payload read through `NinoDeserializer.Deserialize` (or the
generated overloads) that leaves bytes unread throws `NinoDataException`. Without it, trailing bytes are ignored, so
that a caller may read several values from one buffer through a `Reader`; a `Reader` the caller creates is never
checked, in either mode.

### 9.13 Multi-dimensional arrays

Readers MUST reject:

- any length other than the first below 0, and a first length below `-1`;
- a dimension above the platform's array limit;
- an element count that overflows or exceeds the remaining bytes.

**.NET:** done (`MultiDim.TryReadShape`).

### 9.14 Error types

All validation failures SHOULD surface as one error type.

**.NET:** every check of the data throws `NinoDataException` (`Nino: ...`), derived from `InvalidOperationException`.
Mistakes of the caller (a null argument, a type with no formatter, a read-only collection to read into) keep their own
types, and so does a failure while writing: an object graph nested deeper than `NinoSettings.MaxDepth`, or one that
holds itself (a cycle without reference tracking, a collection that contains itself), throws
`InvalidOperationException` before the stack runs out.

---

## 10. Open points and known irregularities

These were found while writing this specification. Implementers should know about them, and the next format
revision should resolve them. Items marked **(resolved)** have been fixed in the code since; they stay listed for
readers of layouts or data from earlier builds.

1. **Published layouts of some BCL structs were wrong (resolved).** The generator read BCL structs from reference
   assemblies, where their private fields are replaced by a placeholder (`_dummyPrimitive`), so `Complex`,
   `TimeOnly`, `Half`, `Int128`/`UInt128`, `DateOnly`, `Rune` and `Nullable<T>` elements (§6.2) were published with
   wrong fields or sizes. They are now published with their real fields (`raw(16){_lower:ulong@0:raw(8),...}`,
   `raw(8){hasValue:bool@0:raw(1),value:int@4:raw(4)}`). Layouts from earlier builds can still carry
   `_dummyPrimitive`: implementations MUST use §3 and §6.2 for these types.
2. **`T?[]` of a raw `[NinoType]` struct depends on who binds the formatter.** For the same value of
   `RawPoint?[]` / `List<RawPoint?>`, the generated formatter uses the fixed stride (§6.2), while the runtime
   resolver (`NinoFormatterResolver.IsRawUnmanaged` is false for every `[NinoType]`) writes each element as a single
   `T?`. Verified: 28 bytes against 14 bytes for `{p, null}`. A payload is therefore readable only by a reader that
   resolves the formatter the same way. The fixed stride (what the layout publishes) SHOULD be treated as normative.
3. **Pointer-sized fields inside raw structs are not portable.** A raw struct with an `nint` field is laid out with
   the host's pointer size: 16 bytes on 64-bit, 8 bytes on 32-bit. The layout publishes it as the bare `raw`
   encoding. `nint` is fixed at 8 bytes only as a value or a member (§3). Structs with no canonical layout
   (`LayoutKind.Auto` with several fields) fall back to the host's memory layout. Writers in other languages SHOULD
   NOT produce either.
4. **`TimeZoneInfo` custom zones** carry .NET's `ToSerializedString()` text, which no other runtime defines.
5. **Deferrability was not published** (resolved): layouts now carry `deferrable=true|false` (§7.10.1,
   §8). Layouts from an older generator leave it out, and a reader can then only reconstruct it from the rules in
   §7.10.1.
6. **Presence bytes and `bool` other than `0` or `1` are rejected in every runtime (resolved)** (§9.3).
7. **Repeated keyed entries are accepted** in .NET, and the last one wins (§9.8).
8. **Trailing bytes are ignored** unless `NinoSettings.Untrusted` is set (§2.2, §9.12).
9. **`DateTime`, `decimal`, `DateOnly` and `TimeOnly` are validated** only when `NinoSettings.Untrusted` is set
   (§9.10).
10. *(Resolved: `-1` reads as a default `ImmutableArray<T>`, §6.1.)*
11. **Member order across `partial` declarations** follows Roslyn's `GetMembers()` order, which in practice is the
    order of the files in the compilation. Moving a member to another file of a `partial` type can change a
    positional format. **(unverified across compilers.)** The published layout is authoritative for any given
    build.
12. **Raw blocks are not normalized**: padding inside raw structs is copied as it is on read, and so are `bool`
    fields unless `NinoSettings.Untrusted` is set (§9.3, §9.11).
13. **`object` resolves any loaded type** (§9.6).
14. **`WEAK_VERSION_TOLERANCE` changes the format per assembly** (§7.5). The same source compiled in two projects can
    produce two formats. The layout shows the effective format.
15. **`[NinoFormerName]` on a generic type** replaces the whole id name, arguments included, so every closed type of
    that definition gets the same id. **(unverified: not run.)**
16. **Hash collisions between type ids** of different types are not detected across assemblies. Within one
    compilation, keyed member collisions are an error (NINO014).

17. **`ValueTuple`s of 8 or more items** were silently skipped as members (resolved): they are now written as
    their items in order, like any `ValueTuple` (§6.6), and published as `tuple(...)` with every item.

Unverified overall: behavior on 32-bit hosts, big-endian hosts, Mono, IL2CPP and Arm; the exact set of BCL
exceptions that can escape a reader (§9.14). Everything else in
this document was checked against the output of the .NET implementation, built from this commit for `net10.0` on
x64 Linux.

---

## Appendix A: worked examples

All of these dumps were produced by running Nino 4.0.0-rc.7 (`net10.0`, x64). Type declarations are C#. Unless shown,
the namespace is `Spec`, whose names enter the type ids.

### A.1 A small `[NinoType]`

```csharp
[NinoType] public sealed class Small { public int A; public string B; }
new Small { A = 1, B = "a" }
```
```
01                presence byte (sealed class)
01 00 00 00       A = 1
FE FF FF FF 61    B = "a" (Latin-1, ~1)
```
`null` → `00`. Layout: `format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xF8B7D639; members=A:int:raw(4);B:string:string`.

### A.2 Polymorphism

```csharp
[NinoType] public abstract class Shape { public int Id; }
[NinoType] public sealed class Circle : Shape { public float R; }
[NinoType] public sealed class Square : Shape { public float Side; }
new List<Shape> { new Circle { Id = 1, R = 1f }, new Square { Id = 2, Side = 2f }, null }
```
```
03 00 00 00                   count 3
AC 2B C0 ED                   type id of "Spec.Circle" (0xEDC02BAC)
01 00 00 00 00 00 80 3F       Id = 1 (Shape's member first), R = 1f
1F 27 E7 8F                   type id of "Spec.Square" (0x8FE7271F)
02 00 00 00 00 00 00 40       Id = 2, Side = 2f
00 00 00 00                   null
```

A non-sealed base held as itself also writes its id: `[NinoType] class Open { int A; }`, `new Open { A = 1 }` →
`32 CC 13 82 01 00 00 00`. A generic type under `object`: `(object)new Box<int> { Value = 3 }` →
`38 EE 81 67 03 00 00 00` (`Spec.Box<System.Int32>`).

### A.3 Keyed and version-tolerant types

```csharp
[NinoType(format: NinoFormat.Keyed)]
public sealed class Keyed { public int Id; [NinoFormerName("Title")] public string Name; public long Score; }
new Keyed { Id = 1, Name = "n", Score = 2 }
```
```
01                            presence byte
29 00 00 00                   L = 41
48 19 C4 98 04 00 00 00       key FNV("Id") = 0x98C41948, len 4
01 00 00 00                     1
E2 B3 85 30 05 00 00 00       key FNV("Name") = 0x3085B3E2, len 5
FE FF FF FF 6E                  "n"
4D B1 4A 04 08 00 00 00       key FNV("Score") = 0x044AB14D, len 8
02 00 00 00 00 00 00 00         2
```
A reader also accepts key `0x93A29D95` (`FNV("Title")`) for `Name`. With `Name = null`, the `Name` entry is
`E2 B3 85 30 04 00 00 00 FF FF FF FF` and `L = 40`.

```csharp
[NinoType(format: NinoFormat.VersionTolerant)] public class TolBase { public int A; }
[NinoType(format: NinoFormat.VersionTolerant)] public sealed class TolLeaf : TolBase { public int B; }
new TolLeaf { A = 1, B = 7 }
```
```
as TolLeaf:   01 | 08 00 00 00 | 01 00 00 00 07 00 00 00
as TolBase:   77 30 46 2B | 08 00 00 00 | 01 00 00 00 07 00 00 00     (type id of Spec.TolLeaf)
```
A `TolBase`-only reader of the second payload reads `A = 1` and skips the 4 bytes of `B`.

### A.4 Strings in each encoding

```csharp
[NinoType] public sealed class Strings { public string Def; [NinoUtf8] public string U8; }
new Strings { Def = "x", U8 = "é😀" }
```
```
01                                     presence byte
FE FF FF FF 78                         Def: Latin-1 "x"
03 00 00 00 06 00 00 00 C3 A9 F0 9F 98 80    U8: 3 chars, 6 bytes UTF-8
```

| Value | Default (`string`) | `[NinoUtf8]` (`utf8`) |
| --- | --- | --- |
| `null` | `FF FF FF FF` | `FF FF FF FF` |
| `""` | `00 00 00 00` | `00 00 00 00` |
| `"abc"` | `FC FF FF FF 61 62 63` | `03 00 00 00 03 00 00 00 61 62 63` |
| `"é"` | `FE FF FF FF E9` | |
| `"a中"` | `02 00 00 00 61 00 2D 4E` | |
| `"a\uD800"` | `02 00 00 00 61 00 00 D8` | `02 00 00 00 04 00 00 00 61 EF BF BD` |
| `"😀"` | `02 00 00 00 3D D8 00 DE` | |

### A.5 Reference tracking

```csharp
[NinoType(trackReferences: true)] public sealed class Node { public int V; public Node Next; }
[NinoType] public sealed class Holder { public object Any; public Node N; }
var n = new Node { V = 5 }; new Holder { Any = n, N = n }
```
```
01                    Holder presence
06 2E 6C 29           Any: type id of Spec.Node (slot "object" is untracked: id first)
FF FF FF FF           reference header: new object #1
05 00 00 00           V = 5
00 00 00 00           Next: reference header 0 = null
01 00 00 00           N: reference header 1 = object #1
```

### A.6 Raw values and nullable strides

```
[NinoType] struct RawPoint { int X; byte Y; }      {1, 2}       01 00 00 00 02 00 00 00
RawPoint[] {(1,2), (3,4)}                          02 00 00 00 | 01 00 00 00 02 00 00 00 | 03 00 00 00 04 00 00 00
long?[] {1, null}                                  02 00 00 00 | 01 00×7 01 00×7 | 00×16
sealed class Nulls { int?[] Arr; List<int?> L; IList<int?> IL; int? One; long? LongOne; }
  = { {1,null}, {1,null}, {1,null}, 7, null }
01
02 00 00 00 01 00 00 00 01 00 00 00 00 00 00 00 00 00 00 00     Arr (stride 8)
02 00 00 00 01 00 00 00 01 00 00 00 00 00 00 00 00 00 00 00     L   (stride 8)
02 00 00 00 01 01 00 00 00 00                                    IL  (one T? each)
01 07 00 00 00                                                   One
00                                                               LongOne
```

---

## Appendix B: reference algorithms

### B.1 FNV-1a over UTF-16 (type ids and keys)

```text
function ninoHash(s: sequence of UTF-16 code units) -> i32:
    h: u32 = 0x811C9DC5
    for c in s:
        h = (h xor (c and 0xFF)) * 0x01000193   (mod 2^32)
        h = (h xor (c >> 8))     * 0x01000193   (mod 2^32)
    if h == 0: h = 1
    return h as i32
```

### B.2 Canonical layout

```text
function measure(t) -> (size, align, leaves):
    if t is a leaf: return (leafSize(t), leafAlign(t), [(0, leafSize(t))])
        leaf sizes / aligns: 1/1, 2/2, 4/4, 8-byte primitives 8/8, decimal 16/8, Guid 16/4, Int128/UInt128 16/16,
        pointer P/P
    fields = instance fields of t in declaration order
    if layoutKind(t) == Auto and count(fields) > 1: fail
    pack = Pack(t) if Pack(t) > 0 else infinity
    end = 0; align = 1; leaves = []
    for f in fields:
        (fs, fa, fl) = measure(type(f))
        fa = min(fa, pack)
        off = explicitOffset(f) if layoutKind(t) == Explicit else roundUp(end, fa)
        leaves += [(off + o, s) for (o, s) in fl]
        end = max(end, off + fs)
        align = max(align, fa)
    size = max(roundUp(end, align), SizeAttribute(t))
    if size == 0: fail
    return (size, align, leaves)
```

The wire bytes of a raw value are its leaves at their offsets, with every other byte zero.
