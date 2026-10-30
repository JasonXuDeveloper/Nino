# nino-serialization (Rust)

Reads and writes [Nino](https://github.com/JasonXuDeveloper/Nino)'s binary wire format, the bytes Nino v4 writes in
.NET, as specified in `docs/spec/wire-format.md`. The crate is `nino-serialization` (the same name as the npm package;
`nino` is taken on crates.io) and its library is `nino`. Its version is Nino's: each release publishes it to crates.io
with the NuGet packages (`cargo add nino-serialization@=4.0.0-rc.8`, `features = ["derive"]` for the derive).

- Edition 2021, MSRV 1.88.
- `#![deny(unsafe_code)]`: the only `unsafe` code is `src/simd.rs` and its submodules (`simd/x86.rs`, `simd/neon.rs`),
  checked under Miri: the string writer's transcoding, and the reader's decoding of text into a `String` it builds.
  The reader's bounds checks are safe code.
- No required dependencies. Every integration is an opt-in feature.

```rust
let bytes = nino::serialize(&vec![1i32, 2])?;
let back: Vec<i32> = nino::deserialize_exact(&bytes)?;
```

## API

| Item | What it is |
| --- | --- |
| `serialize`, `serialize_into` | Write a value under its declared type, as `NinoSerializer.Serialize<T>` does. `serialize_into` appends to a `Vec`, and on an error truncates it back to where it was: a `Vec` the caller clears and reuses is .NET's `NinoArrayBufferWriter`, with no allocation once its capacity is large enough. |
| `deserialize`, `deserialize_exact` | Read a value. `deserialize_exact` also rejects trailing bytes (`ErrorKind::TrailingData`). |
| `deserialize_into` | Read a value into an existing one, as `NinoDeserializer.Deserialize(data, ref value)` does: the members of classes and structs are read in place, a `Vec` reuses its buffer and reads its elements into the old ones by index (extra ones dropped), maps, sets and deques are cleared and refilled in their buffers, and a polymorphic slot is read into only when the data holds the same type (else replaced, as are tracked objects and class bodies deeper than 128). On an error the value is still valid: the members read before the error are the new ones, the rest are the old. Every type has it through `NinoRead::read_into`, which the derive and nino-gen write as the one body `read` also uses. |
| `NinoRead`, `NinoWrite` | A type's whole encoding. Implemented here for the types in the table below, and by generated or derived code for each `[NinoType]`. |
| `NinoReadBody`, `NinoWriteBody` | A class's body, behind its presence byte or type id. They carry `TYPE_ID` (0 when it has none) and `DEFERRABLE` (whether .NET may defer it, spec §10.5). |
| `Reader`, `Writer` | What the codecs call: primitives, lengths, the three string encodings, classes (`read_sealed`, `read_sealed_boxed`, `read_class`, `write_sealed`, `write_class`; `sealed_read!` and `sealed_write!` write a sealed class's `NinoRead` and `NinoWrite` methods), structs (`enter_struct`/`exit_struct`), version-tolerant and keyed regions (`begin_region`, `end_region`, `end_member`), and deferral (`mark`, `trail`, `Step`, `field!`). |
| `fixed_size`, `decode_next`, `encode_next` | What a struct of fixed-size members (generated or derived) states its `FIXED_SIZE` with, and reads or writes each member from its bytes with, so that a list of it is one block. |
| `type_id(name)` | The type id of a .NET full name: FNV-1a over its UTF-16 code units, as a `const fn`. |
| `Error`, `ErrorKind` | Every failure. `ErrorKind` is `#[non_exhaustive]`. |

How C# types map to Rust types:

| C# | Rust |
| --- | --- |
| `sbyte` … `ulong`, `Int128`, `UInt128`, `float`, `double`, `bool` | `i8` … `u64`, `i128`, `u128`, `f32`, `f64`, `bool` |
| `char` | `u16`, a UTF-16 code unit. A Rust `char` is a `Rune`. |
| `string` | `Option<String>`, or `Option<WideString>`, or `Option<Utf8>` for `[NinoUtf8]`. See "Strings" below. |
| `T?` (a struct), a class | `Option<T>`; `Option<Box<T>>` for a recursive class |
| `T[]`, `List<T>`, `Queue<T>`, … | `Option<Vec<T>>`, `VecDeque<T>` |
| `HashSet<T>`, `SortedSet<T>` | `HashSet<T>`, `BTreeSet<T>`, `IndexSet<T>` |
| `Dictionary<K, V>`, `SortedDictionary<K, V>` | `HashMap<K, V>`, `BTreeMap<K, V>`, `IndexMap<K, V>` |
| `ValueTuple<…>` (1 to 7 items) | tuples |
| `T[,]` and other multi-dimensional arrays | `Grid<T, R>` |
| `T?[]` of raw structs (written as raw memory) | `RawNullables<T>` |
| `decimal`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, `Half`, `Rune`, `Version`, `BigInteger`, `BitArray` | types of the same names. `DateTimeKind` is the `DateTime` kind. |

A polymorphic declared type (an abstract class, an interface, or a class that is not sealed) becomes a Rust enum with
one variant per concrete type. The reader rejects any type id that is not one of the variants
(`ErrorKind::UnknownTypeId`).

## Guarantees

The reader assumes the input may be hostile. For any input:

- It never panics, never reads out of bounds, and returns either the whole value or an `Error`. A panic is a bug,
  whatever the input.
- It recurses at most 128 class bodies deep. Deeper bodies are deferred onto a heap work list, as .NET defers them.
  It stops at 512 nested bodies (`DEPTH_MAX`, `ErrorKind::TooDeep`).
- Memory is bounded by the input:
  - Up-front allocation for collections comes from one budget per read: 16 bytes per input byte, plus 4 KiB.
  - A collection whose count the remaining input cannot fill fails before it allocates (`InvalidLength`).
  - The heaviest shape found so far is a deferral chain, at about 70 bytes of memory per input byte. The tests'
    ceiling is 256 times the input size plus 64 KiB.
- Writing fails only where a value has no wire form: a length or count over `i32::MAX` (`TooLarge`), or classes
  nested more than 512 deep (`TooDeep`).

### Stricter than .NET

The reader rejects bytes that .NET never writes. It reads everything .NET writes. .NET rejects some of them in
every mode, some only with `NinoSettings.Untrusted` set, and the rest not at all:

- Bools and presence bytes must be 0 or 1 (.NET: in every mode, except a `bool` inside a raw struct or tuple, which
  it checks only with `Untrusted`).
- `DateTime`, `DateOnly`, `TimeOnly` and `DateTimeOffset` must be in .NET's range (.NET: `DateTimeOffset` always,
  the others with `Untrusted`).
- A `decimal` must have zero flag bits and a scale of 28 or less (.NET: with `Untrusted`).
- A `BitArray` must have zeros in its unused bits (.NET: no).
- A `BigInteger` must be in its minimal form. A count of 0 reads as zero (.NET: no).
- The padding of raw structs must be zero (.NET: no).
- Sets and maps must not contain duplicate keys (.NET: in every mode).
- A keyed member must use up its whole length (`end_member`) (.NET: in every mode).
- A deferred-body section must be the outermost region, must be filled exactly by its entries, and every entry's key
  must be at or below its own offset (.NET: in every mode).
- A tracked class in a slot that does not track (`object`, an untracked base) must not have a reference header of 0
  after its type id: null is a type id of 0, and .NET never writes the other form (.NET: reads it as null).

### Reference tracking

A type declared with `trackReferences=true` is held as a [`Shared<T>`](src/shared.rs): one object any number of
places hold (a clone is the same object), which may hold itself; it compares by identity, and
`Shared::empty` then `fill` builds a cycle. The code nino-gen writes uses it (`Option<nino::Shared<Node>>`), through
`Reader::read_shared`, `ref_header`, `back_ref`, `read_shared_new`, `read_shared_after_id` and their writer
counterparts; the derive does not take `trackReferences` yet. As .NET does, the reader records each object as it
creates it, before its body (a deferred body waits in the queue until the bodies inside it are read), and rejects a
header other than -1, 0 or the number of an object read so far, and a back reference to an object of a type the slot
does not allow (`ErrorKind::InvalidReference`). Every object takes at least its 4 header bytes, so the table of
objects grows with the input.

## Strings

The wire has three string forms:

A header's first byte gives its size, as MessagePack's string markers do: the value itself up to 0x7F (strings of up
to 63 chars), else 0x80 and a little-endian `u16`, else 0x81 and a `u32` (1, 3 or 5 bytes), minimal; the reader
rejects one cut short, not minimal or with another first byte, as .NET does.

- **Null:** the header is 0.
- **Latin-1:** the header is `2n + 1` (written when every character is ≤ U+00FF; 1 is the empty string), followed by
  n bytes.
- **UTF-16:** the header is `2n`, followed by n code units.
- **`[NinoUtf8]`:** the char count plus one (0 null, 1 empty), then the byte count, then the bytes.

How each Rust type handles them:

- `String` decodes Latin-1 and UTF-16, and replaces each unpaired surrogate with U+FFFD. Use it when the text is known
  to be valid Unicode.
- `WideString` keeps the UTF-16 code units exactly as they were, so a value with lone surrogates writes back byte for
  byte. Writing a `String` picks Latin-1 or UTF-16 as .NET does.
- `Utf8` (or a field marked `#[nino(utf8)]`) is a `[NinoUtf8]` member. The reader checks that the byte count is
  between c and 3c, where c is the char count, and that the bytes are valid UTF-8.

For text-heavy types, prefer `[NinoUtf8]` (`#[nino(utf8)]` or `Utf8`) where the .NET side can declare it. A `String`
in the default encoding is transcoded to Latin-1 or UTF-16 on every write and back to UTF-8 on every read, so formats
that store UTF-8 (bincode, postcard, MessagePack, protobuf) copy their strings faster than Nino can transcode them.
A `[NinoUtf8]` member is written as a count of its UTF-16 code units and one copy of its bytes, and read with std's
UTF-8 check, that count and one copy.

## Features

| Feature | Adds |
| --- | --- |
| `derive` | `#[derive(NinoRead, NinoWrite)]`, from the `nino-serialization-derive` crate in this workspace (directory `nino-derive`) (proc-macro2, quote, syn) |
| `serde` | `Serialize` and `Deserialize` for the value types above, in readable forms (described below). It also turns on serde for `indexmap` and `uuid` when those features are on. |
| `uuid` | Reads and writes `uuid::Uuid` as `Guid`; `From` conversions between them |
| `rust_decimal` | Reads and writes `rust_decimal::Decimal` as `decimal`; conversions between them |
| `chrono` | `TryFrom` conversions between `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset` and chrono's types |
| `num-bigint` | Reads and writes `num_bigint::BigInt` as `BigInteger`; conversions between them |
| `indexmap` | `IndexMap` and `IndexSet`, which keep the wire order, so a map or set reads back and writes back byte for byte. Code nino-gen generates uses them unless run with `--maps std`, which uses `HashMap` and `HashSet` instead (no dependency, but written in `HashMap`'s iteration order, so the bytes differ from what was read) |

## `#[derive(NinoRead, NinoWrite)]`

This is the Rust counterpart of Go's `nino` struct tags. The derive expands to the same calls the shared templates
generate: there is no reflection, and nothing runs that hand-written code would not run.

```rust
use nino::{NinoRead, NinoWrite};

#[derive(Debug, Default, Clone, PartialEq, NinoRead, NinoWrite)]
#[nino(format = "keyed")]                       // [NinoType(NinoFormat.Keyed)] sealed class
pub struct Player {
    #[nino(key = "Name", utf8)]                 // [NinoKey("Name")] [NinoUtf8]
    pub name: Option<String>,
    #[nino(former = "Health")]                  // keyed "Hp", and read under "Health" too
    pub hp: i32,
    #[nino(skip)]                               // not on the wire, Default on read
    pub cached: u64,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, NinoRead, NinoWrite)]
#[nino(kind = "raw")]                           // an unmanaged struct: raw memory, canonical layout
pub struct Vec2 { pub x: f32, pub y: f32 }

#[derive(Debug, Default, Clone, PartialEq, NinoRead, NinoWrite)]
#[nino(kind = "open", id = "MyGame.Dog")]       // a class that is not sealed: written behind its type id
pub struct Dog { pub name: Option<String>, pub at: Vec2 }

#[derive(Debug, Default, Clone, PartialEq, NinoRead, NinoWrite)]
#[nino(kind = "open", id = "MyGame.Cat", format = "version_tolerant")]
pub struct Cat { pub lives: u8 }

#[derive(Debug, Clone, PartialEq, NinoRead, NinoWrite)]
pub enum Animal { Dog(Box<Dog>), Cat(Box<Cat>) } // the abstract Animal: one variant per concrete type
```

Type attributes:

| Attribute | Meaning |
| --- | --- |
| `format = "positional" \| "version_tolerant" \| "keyed"` | `NinoFormat.Positional`, `VersionTolerant` or `Keyed` (`"plain"` and `"tolerant"` are accepted for the first two). The default is positional. |
| `kind = "class" \| "open" \| "struct" \| "raw"` | The type's header (defaults to `class`): <ul><li>`class`: a sealed class, written behind a presence byte.</li><li>`open`: a class that is not sealed, written behind its type id.</li><li>`struct`: no header.</li><li>`raw`: an unmanaged struct written as its memory. It takes `pack = n` and `size = n`, as `[StructLayout]` does.</li></ul> |
| `id = "Full.Name"` or `id = 0x…` | The type id: a .NET full name, hashed at compile time, or the number itself. 0 is rejected. |
| `deferrable = false` | Marks a type .NET never defers (a record, a type built through a constructor taking its members). Taken from the layout's `deferrable=` when not given. |
| `layout = "…"` | The type's published `[NinoTypeLayout]` string, checked at compile time. Format, kind, type id and deferrability come from it when not given. It checks member names and order, keys and former keys, `[NinoUtf8]`, and raw sizes and offsets (through const asserts). |
| `crate = "path"` | Where the runtime is, when it is not `nino`. |

Field attributes:

| Attribute | Meaning |
| --- | --- |
| `index = n` | The position in .NET's member order. Give it on every field or on none; without it, declaration order is the member order. |
| `name = "CSharpName"` | The .NET member name. The default is the field's name in PascalCase (`max_hp` becomes `MaxHp`). |
| `key = "Name"` | A keyed member's key, which is otherwise its .NET name. `keyed` format only. |
| `former = "OldName"` | A former key that is also accepted on read. It can be repeated. `keyed` format only. |
| `utf8` | `[NinoUtf8]`, on a `String` or `Option<String>`. |
| `skip` | The field is not on the wire. On read it is `Default::default()`. |

An enum is a polymorphic declared type. Each variant holds one concrete type (or a `Box` of it) with a type id. An
`#[nino(id = …)]` on a variant overrides that type's id. Duplicate or zero ids are compile errors.

Anything the wire format cannot express is a compile error, and the message names the field:

- `usize` or `isize`;
- a Rust `char` (use `u16` or `Rune`);
- references, slices, `str`, pointers, `fn` types or trait objects;
- `utf8` on a field that is not a string;
- `key` or `former` outside `keyed`;
- `index` on only some fields, or the same index twice;
- colliding keys;
- tuple structs, unions, or a generic `raw` struct;
- unknown attributes;
- a `layout` that does not match the struct.

`tests/compile_fail/` holds one case for each, with the expected message.

## serde

The `serde` feature implements serde for the runtime's value types, so a struct with Nino codecs can also derive serde
and be written as JSON, YAML, or any other serde format. Generated types (`nino-gen --lang rust --serde`, or
`<NinoCodegen Include="rust" Serde="true" />` with the Nino.Codegen package, and
`tests/conformance/gen.py`) and the derive tests add:

```rust
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize), serde(rename_all = "camelCase"))]
```

camelCase matches the JSON names .NET's `System.Text.Json` gives by default. The naming policy is any serde
`rename_all` rule. `gen.py` takes it as `--serde-rename <rule>`, and `none` keeps the Rust names; nino-gen takes
`--serde-names camel|snake|original` and renames each field from its C# name.

The value types take these forms:

| Type | Form |
| --- | --- |
| `Decimal`, `Guid`, `BigInteger` | strings: `"-1.50"`, `"00112233-4455-6677-8899-aabbccddeeff"`, `"-129"` |
| `DateTime` | `{ "ticks": n, "kind": "Utc" }` |
| `DateTimeOffset` | `{ "utcTicks": n, "offsetMinutes": m }` |
| `TimeSpan`, `TimeOnly` | ticks |
| `DateOnly` | the day number |
| `Half` | the bits |
| `Version` | `{ "major", "minor", "build", "revision" }`, with -1 for an undefined part |
| `BitArray` | an array of booleans |
| `WideString` | a string, or its code units when it holds an unpaired surrogate |
| `Grid` | `{ "lengths": [..], "items": [.. row-major] }` |

Deserializing checks the same ranges the Nino reader checks. Without the feature, `Decimal`, `Guid` and `BigInteger`
still parse from these strings (`FromStr`) and print to them (`Display`).

## Testing

```sh
cargo fmt --all --check
cargo clippy --workspace --all-features --all-targets -- -D warnings
cargo test --workspace --all-features
```

- **Unit tests:**
  - `tests/spec.rs`: the specification's examples, byte for byte, including the deferral examples.
  - `tests/values.rs`: primitives, value types, errors and features.
- **Conformance** (`tests/conformance.rs`):
  - Every valid case of the shared corpus must be read, written back byte for byte, and match its JSON.
  - Every malicious case and every truncated prefix must be rejected, without a panic and within the memory
    ceiling.
  - The corpus comes from `NINO_CORPUS`, or by default `../../conformance/corpus`. Without a corpus the test is
    skipped, unless `NINO_CORPUS_REQUIRED=1` is set, which makes a missing corpus a failure.
  - `tests/conformance/types.rs` holds the codecs, generated from the corpus's layouts:
    `python3 tests/conformance/gen.py <corpus> > tests/conformance/types.rs && cargo fmt`.
- **Fuzzing** (`mutational_fuzz` in `tests/conformance.rs`):
  - It mutates the corpus's cases and reads each result as its type. Each input gets one to four mutations: bit
    flips, interesting bytes and i32s (lengths, headers, type ids), nudged lengths and offsets, cuts, insertions,
    deletions and copied chunks.
  - Every read must return a value or an `Error`, without a panic and within the memory ceiling.
  - `NINO_FUZZ_ITERATIONS` sets the inputs per case (default 300). The random seeds are fixed per case, so a run can
    be repeated.
  - It runs on stable. `cargo-fuzz` needs nightly and is not used.
- **Generated code** (`tests/generated.rs`, feature `indexmap`): `tests/generated/` holds what `nino-gen --lang rust`
  writes for the corpus's types and for `tests/Nino.Codegen.Tests`' shapes (docs/codegen.md; regenerated by
  `NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests`). They run against the corpus like `types.rs`, plus
  a short fuzz.
- **Derive** (`tests/derive.rs`, features `derive` and `indexmap`):
  - The corpus's types are declared through the derive with their published layouts, and run against the corpus.
  - Plain structs of fixed-size members are read and written as blocks: the bytes each element writes on its own,
    every truncation and a bad `bool` or `Rune` in any element rejected.
  - `tests/compile_fail.rs` (trybuild) checks every rejection and its message. After a deliberate change to a message,
    `TRYBUILD=overwrite cargo test --features derive --test compile_fail` rewrites the expected messages.

- **Unsafe code** (`src/simd.rs`, `src/simd/x86.rs`, `src/simd/neon.rs`): writing a `String` transcodes its UTF-8 to
  Latin-1 or UTF-16 straight into the buffer, in one pass that combines each char's bytes in a lane and packs the
  lanes with a shuffle (or a compress), as Latin-1 until a char above U+00FF and then as UTF-16. Each instruction set
  has its own kernel, found once at run time on x86_64 (`is_x86_feature_detected!`, cached):
  - x86_64 with AVX-512 (F, BW, VL, VBMI2; built with Rust 1.89 or later): 64 bytes a step with masked loads and
    stores, packed by `vpcompressb`/`vpcompressw`;
  - x86_64 with AVX2 (and BMI1, BMI2, LZCNT): 32-byte windows, each char's code unit computed in 16-bit lanes;
  - x86_64 with SSE4.1: 16-byte windows;
  - little-endian Arm64 (NEON): text over 64 bytes in 32-byte windows, runs of three-byte chars (CJK) 48 bytes a step
    (`ld3`); shorter text char by char;
  - elsewhere (and x86_64 with SSSE3 or SSE2 only): text up to 64 bytes char by char, longer text a block of 16 bytes
    at a time (SSE2, plus SSSE3 for two-byte chars in text of 48 bytes or more; two `u64` words on other targets).

  Reading a string decodes its Latin-1 or UTF-16 straight into the `String`'s allocation (sized from the code units
  first) and builds it unchecked: what the decoder writes is valid UTF-8 by construction, with U+FFFD for each unpaired
  surrogate as before. Latin-1 of 32 bytes or more and UTF-16 of 24 units or more take a vector decoder (AVX-512; SSSE3
  for Latin-1 and SSE4.1 for UTF-16 on x86_64; NEON on Arm64), shorter text and other targets a plain one eight bytes a
  word. Its tests compare every kernel the CPU has (not only the one it picks), and the decoders, with plain ones for
  every char class at every position of text up to 256 chars, runs and random input (unpaired and reversed surrogates
  included), and `string_encodings` (`tests/values.rs`) compares `serialize` with .NET's bytes the same way. CI runs the
  module's tests under Miri on x86_64 (SSE2 only, SSSE3, SSE4.1, AVX2, AVX-512), Arm64 and big-endian s390x (Miri lacks
  the AVX-512 compress instructions and `pdep`: under Miri those kernels pack with a plain loop, the rest of them as on
  the CPU):
  `cargo +nightly miri test -p nino-serialization --lib simd` (`--target aarch64-unknown-linux-gnu`, or
  `RUSTFLAGS='-C target-feature=+avx2,...'`, ...).

## Benchmarks

```sh
cargo bench -p nino-bench                # every group
cargo bench -p nino-bench -- record      # one group
```

`bench/benches/compare.rs` compares Nino with Protocol Buffers (prost, messages derived in the bench: no protoc),
MessagePack (rmp-serde), bincode (2, its standard configuration: variable-length integers), postcard and serde_json on
the same values, with criterion. Each group covers serialize and deserialize, and prints every format's encoded size:

- a record;
- 1,000 records;
- 10,000 `i32`;
- 10,000 `f64`;
- 1,000 ASCII strings;
- 1,000 strings of ASCII, Latin-1 and other text in turn (the same as the Go and TypeScript benchmarks').

The Nino codecs there are what the templates generate for those types: a class's members read and written one by
one, a raw struct as one block, and a class held as `Option<Box<_>>` (every serializer gets the same values). Compare
changes back to back on one machine; local numbers are not published. Each release's notes get a "🦀 Rust runtime benchmark" section from one run of
this benchmark on a GitHub runner (`report.yml`, `benchmarks/runtimes_report.py`).
