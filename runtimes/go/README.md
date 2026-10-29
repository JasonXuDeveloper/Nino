# Nino for Go

`github.com/JasonXuDeveloper/Nino/runtimes/go/v4` (package `nino`) reads and writes Nino's binary format (v4): the bytes
the .NET serializer writes, as specified in `docs/spec/wire-format.md`. It is the runtime that generated Go code calls,
like `nino.ts` for TypeScript. It has no dependencies outside the standard library. Generated code uses no
reflection; Go structs without generated code can be mapped with struct tags instead ([Struct tags](#struct-tags)),
through a reflection-built plan.

Go 1.22 or later. Its versions are Nino's: each release tags the module `runtimes/go/vX.Y.Z`
(`go get github.com/JasonXuDeveloper/Nino/runtimes/go/v4@v4.0.0-rc.8`).

## How code uses it

A type's codec is a read and a write function per type, generated (or written by hand in the same style:
`internal/sample` holds the types of the conformance corpus written that way). For a sealed class:

```go
type Point struct{ X, Y int32 }

var pointClass nino.Class[Point]

func init() { // in init: a recursive type's functions refer to its descriptor
	pointClass = nino.Class[Point]{Read: readPointBody, Write: writePointBody, Deferrable: true}
}

func ReadPoint(r *nino.Reader) *Point     { return nino.ReadSealed(r, &pointClass) } // presence byte, body
func WritePoint(w *nino.Writer, v *Point) { nino.WriteSealed(w, v, &pointClass) }

func readPointBody(r *nino.Reader, v *Point) {
	b := r.Take(8) // a run of fixed-size members: one bounds check
	if len(b) < 8 {
		return // the reader has failed; it returns zeros from now on
	}
	v.X = int32(binary.LittleEndian.Uint32(b))
	v.Y = int32(binary.LittleEndian.Uint32(b[4:]))
}

func writePointBody(w *nino.Writer, v *Point) {
	b := w.Extend(8)
	if len(b) < 8 {
		return
	}
	binary.LittleEndian.PutUint32(b, uint32(v.X))
	binary.LittleEndian.PutUint32(b[4:], uint32(v.Y))
}
```

Then:

```go
data, err := nino.Serialize(p, WritePoint)        // a new slice
buf, err = nino.Append(buf[:0], p, WritePoint)     // no allocation when buf has room
w.Reset(); WritePoint(w, p); out := w.Bytes()      // a reused *nino.Writer: no allocation once it is large enough
p, err := nino.Deserialize(data, ReadPoint)        // bytes after the value are ignored, as in .NET
p, err := nino.DeserializeExact(data, ReadPoint)   // ... or rejected
err = nino.DeserializeInto(data, &p, ReadPointInto) // into p, reusing what it holds
```

`Append` and a reused `Writer` are what `NinoSerializer.Serialize(value, bufferWriter)` and
`NinoArrayBufferWriter` are in .NET: memory the caller owns, reused across values. A `Writer` keeps its buffer, its
error and its reference numbers until `Reset`, so reset it between values; check `w.Err()` before using the bytes.

`DeserializeInto` with a generated `ReadXInto` is `NinoDeserializer.Deserialize(data, ref value)`: the objects the
value holds are read into (their members in place), slices keep their storage and read their elements into the old
ones by index (a map's entries are read anew into its storage; slices of fixed-size values and sets are read anew), a member
declared as an interface or a class with subtypes is read into only when the data holds the type it holds, and
tracked objects are read new. On a failure the value holds what was read before it and zero values from there on.

### The API in brief

| Area | Reader / Writer |
| --- | --- |
| Primitives | `Bool`, `U8`...`I64`, `I128`, `U128`, `F32`, `F64`, `Char`, `Half` |
| BCL values | `Decimal`, `Guid`, `DateTime`, `TimeSpan`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `BigInteger` (`*big.Int`), `Version` |
| Strings | `String`, `NullableString`, `StringPtr` (default encoding); `UTF8`, `NullableUTF8`, `UTF8Ptr` (`[NinoUtf8]`); `UTF16` (code units) |
| Raw runs | `Reader.Take(n)`, `Writer.Extend(n)` / `ExtendZero(n)`, `BoolAt` for a bool inside a raw struct |
| Collections | `Length`, `ByteArray`, `ReadPrimitives` / `WritePrimitives` (one block copy), `WriteStrings` / `WriteStringPtrs`, `ReadBools`, `ReadBlock` (dates, decimals, Guids, 128-bit integers: one block copy, then checked), `ReadSlice`, `ReadMap`, `ReadSet`, `ReadUnique`, `ReadEntries`, `ReadUniqueEntries`, `ReadPrimitiveEntries` (dictionaries of primitive keys and values: one block copy on little-endian hosts where a pair has no padding), `ReadPrimitiveMap` (the same into a Go map: one bounds check for the block, no call per pair), `ReadFixed` (raw structs, the fixed stride of `T?[]`), `ReadRaw` (raw structs whose Go memory is their bytes: one block copy on little-endian hosts), `ReadShape` (multi-dimensional arrays) and their writers |
| Classes | `Class[T]`, `ReadClass`, `WriteClass`, `ReadSealed`, `WriteSealed` (depth and deferral, below) |
| Regions | `BeginRegion` / `EndRegion` (version-tolerant and keyed bodies), `More`, `BeginEntry` / `EndEntry` / `SkipEntry`, `KeyedSeen` |
| Polymorphism | `TypeID(name)`, `WriteTyped`, `UnknownType`; or `TypeSet`, `ClassType`, `StructType`, `ReadAny`, `WriteAny` |
| Self-nesting collections | `EnterStruct` / `ExitStruct` |
| Errors | `*nino.Error` (`Code`, `Offset`, `Detail`), `errors.Is(err, nino.ErrTooDeep)`... |

## Guarantees

- **No panics.** Reading never panics on any input and never reads outside the data. This is by construction, not
  by `recover`: every read checks its bounds, and errors are sticky. After the first failure the reader drops
  the rest of the data, and every read returns zero, an empty slice or null, so every loop ends. `FuzzReader` and
  `FuzzDecode` check it.
- **Bounded memory.** Every length is checked before anything is allocated. A count must fit in the bytes left
  (`count x` the fewest bytes an element takes), with .NET's caps. Collections whose elements take far more memory
  than their bytes start small and grow as they are read: the most a read presizes is 16 times the bytes left
  (at least 16 KB), as in .NET. Multi-dimensional shapes are checked one product at a time.
- **Bounded depth.** Class bodies deeper than 128 are deferred exactly as .NET defers them (spec §7.10), so the
  bytes are the same and the stack stays bounded however deep the data is. Bodies that cannot be deferred
  (`Class.Deferrable` false: records, types built through a constructor) fail past 512. So do collections that
  contain themselves, counted with `EnterStruct`. The writer finds cycles (Brent's algorithm, as .NET does).
- **Restricted polymorphism.** A type id is accepted only if it names a type allowed in that slot: generated
  dispatches switch over the subtypes of the declared type, and `object` slots take an explicit `TypeSet`. An
  unknown id is an error; nothing is ever looked up by name.
- **One error type, no partial results.** Every failure is a `*nino.Error`. `Deserialize` returns the zero value
  with it, never part of a value.
- **Strict bytes.** It rejects what .NET rejects in every mode: presence bytes and `bool`s other than 0 and 1, a
  keyed member that does not fill its entry, a deferred body that does not fill its entry, and a section of deferred
  bodies that is not well-formed or sits on an inner region. It also rejects what .NET rejects only with
  `NinoSettings.Untrusted` set (spec §9 SHOULDs):
  - `bool`s other than 0 and 1 inside raw structs;
  - `DateTime` with ticks past `DateTime.MaxValue`;
  - `decimal` with a scale above 28 or reserved bits set;
  - `DateOnly` and `TimeOnly` out of range;
  - bytes after the value (`DeserializeExact`);

  and some that .NET accepts in every mode: a non-minimal `BigInteger` or one with 0 bytes,
  and a key repeated in a keyed body.
- **Writer.** It appends to one buffer that doubles as it grows. Reused (`Writer.Reset`, or the pool behind
  `Serialize` and `Append`), writing a value allocates nothing. It rejects what a reader would reject (an
  out-of-range date, a length past `int32`), so it never writes bytes that .NET would misread.

The polymorphic dispatch, the depth counting and the region handling are the same code paths as in .NET (spec §7.8
to §7.10). Reference tracking (`trackReferences`, §7.9) is in `refs.go`, for generated code ("Reference tracking in
generated code" below).

## Struct tags

Go code that has no generated codecs can map its own structs to .NET `[NinoType]`s with a `nino` tag, the way
`encoding/json` maps them to JSON:

```go
type Monster struct {
	Health int32   `nino:"Health"`
	Name   *string `nino:"Name,former=Title"`
	Items  []int32 `nino:"Items"`
	Cache  string  `nino:"-"`
}

func (Monster) NinoLayout() string { // what .NET publishes for the type (NinoTypeLayout), or a part of it
	return "format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0x8F865207; " +
		"members=Health:int:raw(4):key=0x99A16761;Name:string:string:key=0x3085B3E2:former=0x93A29D95;" +
		"Items:System.Collections.Generic.List<int>:seq(raw(4)):key=0xDCA8A053"
}

data, err := nino.Marshal(m)          // m is a *Monster: a class (presence byte, body)
err = nino.Unmarshal(data, &m)        // *m is left unchanged on any error
err = nino.UnmarshalExact(data, &m)   // ... and trailing bytes are an error
data, err = nino.MarshalAs[Animal](a) // a polymorphic root: written behind its type id
```

`nino.ReadValue` and `nino.WriteValue` use a tagged type from inside a generated or hand-written codec, and
`nino.Check(reflect.TypeFor[T]())` builds a type's plan to report a bad tag at init or in a test.

### The tag

| Tag | Meaning |
| --- | --- |
| `nino:"0"`, `nino:"1"`, ... | the member's position (positional and version-tolerant types): 0 to n-1, no gaps, no repeats |
| `nino:"Name"` | the member of a keyed type: its key is the published key of the layout member of that name (or, without a layout, FNV-1a of the name over UTF-16 as `TypeID` computes it, 0 mapped to 1); a hidden member is `Base.Name` |
| `nino:"-"` | a Go-only field: not read or written |
| `,utf8` | a `[NinoUtf8]` string (`string` and `*string` only) |
| `,former=Old` | a former name of a keyed member, once per name |

A struct without any `nino` tag takes its exported fields in declaration order (positions), or by their Go names
(keyed). Once one field has a tag, every exported field needs one (or `-`). Unexported fields are ignored; an
untagged embedded struct is flattened, as in `encoding/json`. There is no `omitempty`: positional and
version-tolerant bodies hold every member in order, and a keyed writer must write an entry for every member (spec
§7.6), so the option is an error.

### The type's shape: `NinoLayout`

A type declares its .NET shape with a method `NinoLayout() string` (value or pointer receiver) returning the layout
string .NET publishes for it (spec §8), or any subset of its `key=value` parts:

- `format=Positional|VersionTolerant|Keyed`. Without it: keyed when the tags are names, else positional. Version
  tolerance is never guessed.
- `header=None|NullFlag|TypeId`: `None` makes the type a .NET struct (a `*T` is then `Nullable<T>`); `TypeId` writes
  a `*T` behind its type id. Without it a `*T` is a sealed class (presence byte) and a `T` a struct.
- `typeId=0x...`: the type id, for polymorphic slots (or implement `nino.Typed`'s `NinoTypeID()`; the two must agree).
- `raw=true|false`, with `members=raw(n){f:T@off:E,...}` for a raw struct's offsets (`Pack`, `Size`, explicit
  layouts). Without them a struct whose fields are all fixed-size values is raw in the canonical layout (spec §5.2:
  natural alignment, CoreCLR 64-bit); `raw=false` declares a `ValueTuple`, which is written item by item.
- `members=...`: the published members. With them the plan is checked against the layout: the number of members
  (all for positional, a prefix for version-tolerant, any subset for keyed), each field's encoding against the
  member's, and the keys and former keys (the published ones are used, as the spec requires).
- `trackReferences=true` is an error (reference tracking is for generated code only).
- `deferrable=false` (spec §7.10.1): a type .NET never defers (a record, a type built through its constructor), so it
  nests in place past 128 bodies and fails past 512. Without the part (layouts from an older .NET generator) a type
  is deferrable.

### Go types and their encodings

| Go field | Encoding |
| --- | --- |
| `bool`, `int8`...`int64`, `uint8`...`uint64`, `float32`, `float64`, named types of them (enums) | `raw(n)` |
| `nino.Decimal`, `Guid`, `DateTime`, `TimeSpan`, `DateOnly`, `TimeOnly`, `Half`, `Int128`, `UInt128` | `raw(n)` |
| `nino.DateTimeOffset`, `*big.Int`, `*nino.Version` | `datetimeoffset`, `bigint`, `version` |
| `string`, `*string` | `string` (or `utf8`); a `string` reads null as `""`, a `*string` keeps it |
| `[]byte`, `[]T` | `seq(...)`; nil is null; numbers are one block copy |
| `[]*T` of a fixed-size `T` | `T?[]`: the fixed stride (§6.2); `seq(nullable(...))` in the layout selects one `T?` per element |
| `map[K]V`, `map[K]struct{}` | `map(K,V)`, a set `seq(K)`: written in key order, a repeated key fails on read |
| `[]nino.Entry[K, V]` | `map(K,V)` in the order of the data (no check for repeated keys); a `nino.Entry` alone is a `pair` |
| `*T` (struct) | a class: presence byte, or type id with `header=TypeId`; `Nullable<T>` when `T` is a struct (`header=None`) |
| `*T` (anything else) | `Nullable<T>`: presence byte, then `T` |
| `T` (struct) | a struct: raw, or member by member |
| an interface (`any`, or one the allowed types implement) | `object`, an abstract class, an interface: a type id, then one of the **registered** types that implements the field's interface |

Polymorphic types are registered once, by a value: `nino.Register((*Dog)(nil), (*Cat)(nil), Circle{})` (a nil pointer
for a class, a zero value for a struct; `nino.RegisterID(id, v)` for a closed generic type, whose layout publishes
id 0). Reading an id that is not registered, or whose type does not implement the field's interface, fails with
`CodeUnknownType`, and writing an unregistered type fails too. A `*T` field holds `T` only: data with a subtype
there fails, so a slot with subtypes needs an interface field.

Not supported (a `*TagError` when the plan is built): `int`, `uint` and `uintptr` (no fixed size), arrays, channels,
functions, pointers to pointers, slices, maps or interfaces, multi-dimensional arrays (`grid`), groupings,
`BitArray`, `TimeZoneInfo`, custom formatters, `[NinoType]` collections with members (`[elements]`), reference
tracking, and a set of nullable strings (`map[string]struct{}` reads a null element as `""`).

### Errors, guarantees, cost

A type the tags cannot map returns a `*nino.TagError` (`Type`, `Field`, `Detail`) naming the field, from `Marshal`,
`Unmarshal`, `Register` and `Check`, before a byte is read or written. Bad data returns a `*nino.Error`, as for
generated code: each plan reads through the same `Reader` with the same checks (bounds, lengths before allocation,
at least one byte an element, .NET's growth caps, depth counting, deferral and sections, `EnterStruct` for types
that hold themselves through no class body, strict bools, dates and decimals). The tag path runs the whole
conformance corpus (`internal/sample/tagged_test.go`: the same values and bytes as the hand-written codecs) and is
fuzzed (`FuzzTagged`, `FuzzTaggedDecode`).

A plan is built once per type, under a lock, and cached in a `sync.Map` keyed by `reflect.Type`; after that a call
walks the plan. Fields of numbers are read and written through their address (`unsafe`), which measured 1.2 to
1.8 times faster than reflect's setters; both give the same bytes, and the tests run both. A `float32` always
goes through its bits, as a conversion through `float64` would lose a signaling NaN's payload. Rough local numbers
(`BenchmarkTagged`, `bench`): marshalling takes 3 to 5 times the generated-style codec's time, unmarshalling
2 to 3 times; both are still several times faster than `encoding/json`.

## Tags on generated structs

Generated Go structs carry the same tags as hand-tagged ones, so the two have the same shape and either path reads
either's data. `nino-gen --lang go` (docs/codegen.md; `--tags`, `--tag-names`; in a build, the Nino.Codegen package's
`<NinoCodegen Include="go" Tags="..." TagNames="..." />`) emits, for every `[NinoType]`:

- **the `nino` tag first** on every serialized field: the member's position (`nino:"3"`) for positional and
  version-tolerant types, the C# member name (`nino:"Health"`) for keyed ones, plus `,utf8` for a `[NinoUtf8]`
  string. A hand-tagged struct may add `,former=Old` for each `[NinoFormerName]`; nino-gen leaves it out, since a
  layout carries only the former names' hashes, which `NinoLayout()` keeps. The `nino` name is the C# name as is; the naming policy
  below never applies to it.
- **extra tags**, configurable, by default `json` and `yaml`, with the member name under a naming policy:
  - `camel` (default): the leading run of capitals lower-cased, keeping the last one when a lower-case letter
    follows: `Health` → `health`, `ID` → `id`, `URLPath` → `urlPath`, `I128` → `i128`, `Utf8A` → `utf8A`;
  - `snake`: words joined by `_`, lower-case, digits kept with the word before them: `IntList` → `int_list`,
    `URLPath` → `url_path`, `I128` → `i128`, `Utf8A` → `utf8_a`;
  - `original`: the C# name as is.
  Tag order: `nino`, then the extra tags in the configured order: ``Health int32 `nino:"Health" json:"health" yaml:"health"` ``.
- **`NinoLayout()`** returning the layout string .NET publishes for the type, unchanged (its `deferrable=` part
  included). A closed generic type (layout `typeId=0`) written by hand gets `NinoTypeID()` returning `TypeID` of
  its .NET name. nino-gen generates a generic `[NinoType]` as a generic struct (`Box[T any]`) whose codec is built
  from the codecs of its type arguments (`BoxCodec[T any](p0 NinoArg[T]) NinoArg[*Box[T]]`), and a member
  `Box<int>` as `*Box[int32]`. A generic type written behind a type id is an interface per definition
  (`Shelf[T]`, with marker methods its implementations have) and a member `Shelf<int>` a field `Shelf[int32]`
  (docs/codegen.md, "Generic types behind a type id").
- **an `Elements` field first** on a `[NinoType]` collection (`class Bag : List<int>`), tagged `nino:"-"`
  (`$elements` in the extra tags): the elements are read and written by the generated functions, before the members.
- **`NinoTypeID()`** on the pointer type of every class that can sit behind a type id.
- **`NinoTypeName()`** returning the .NET full name.

Reference tracking (`trackReferences`) and `[NinoType]` collections are read and written by generated code only: their
layouts are an error for struct tags (`*TagError`), so the tagged path reads none of their data.

### Reference tracking in generated code

A tracked class is a pointer, the same pointer for every reference to it in the data, which may hold itself. The
generated functions build it from `refs.go`: `ReadTracked`/`WriteTracked` (a tracked class without subtypes),
`Reader.Ref`, `Back[T]` and `ReadTrackedNew` (a tracked slot: the header, then the type id), and
`ReadTrackedClass`/`WriteTypedTracked`/`TrackedClassType` (a tracked class in a slot that does not track: the type id,
then the header). A reader records each object as it creates it, before its body (deferred bodies too); a writer
numbers objects by pointer. A header other than -1, 0 or the number of an object read so far, and a back reference to
an object the slot does not allow, fail with `CodeInvalidData`; each object takes at least its 4 header bytes, so the
table grows with the data.

`internal/sample` follows these conventions (camel policy, `json` and `yaml`); its `layouts.go` is what the
generator's `NinoLayout` methods look like. `internal/generated` holds nino-gen's output for the corpus's types (and
`internal/generated/shapes` for `tests/Nino.Codegen.Tests`' shapes), run against the corpus like the sample.

## Strings

- **Default encoding.** Latin-1 or UTF-16, as .NET writes it. It is read into a Go string as **WTF-8**: UTF-8,
  except that an unpaired surrogate (which .NET strings may hold, and which the spec says readers MUST keep) is
  stored in the 3-byte form UTF-8 would give it. Writing such a string back gives the same code units. Use
  `Reader.UTF16` for the code units themselves.
- **Reading.** Short strings (up to 128 bytes of UTF-8) are converted into a block the reader keeps (at most 2 KiB,
  and never more than the data left can fill), one after another: one allocation for many strings, as substrings
  share their string's. A string kept keeps its block alive; copy it (`strings.Clone`) to keep it alone. Likewise
  `StringPtr` and `UTF8Ptr` (generated code's `*string`) point into a block of up to 32 strings the reader keeps.
- **Writing.** A string is written as Latin-1 when every char is at most U+00FF, else as UTF-16 (what .NET writes).
  WTF-8 surrogates are written back as the code units they stand for. A byte that is not UTF-8 is written as U+FFFD.
  The text is converted into the writer's buffer eight bytes at a time (Latin-1 beyond ASCII a char at a time), in one
  pass, through `unsafe` pointers within the text and the room reserved for it (`strings_write.go`); the reader, which
  takes untrusted bytes, stays on checked slices. On amd64 and arm64 a whole string is checked and converted a block
  at a time in assembly instead (16 bytes with NEON and SSSE3, 32 with AVX2 and AVX-512), one kernel per instruction
  set, each designed for it: NEON on arm64, SSSE3, AVX2 and AVX-512 (BW, VL and VBMI2) on amd64, the best the
  processor has taken (`strings_wire_amd64.s` and `strings_wire_arm64.s`, written by `go generate`; see below; on
  macOS, AVX-512 is detected through sysctl as golang.org/x/sys/cpu does, from macOS 12.2 on):
  WTF-8 validation, Latin-1 or UTF-16 chosen and written in one pass, with the Go loops taking over for text that is
  not WTF-8. `WriteStrings` and `WriteStringPtrs` (what generated code calls for a `string[]` or `List<string>`) write
  a whole list in one call into it (strings of fewer than 64 bytes, whose header is one byte; a longer one goes
  through the single-string kernel, which reserves the header its byte length could need and moves the text down
  when the header is shorter). Reading Latin-1 and UTF-16 into UTF-8 has kernels too (NEON; SSSE3 and AVX-512
  with VBMI). The kernels read past the text only within its page. The `purego` build tag, x64 without SSSE3 and every
  other platform use the Go loops (whose long runs of ASCII are copied or widened 16 bytes at a time with SSE2 or
  NEON): the same bytes, which the tests compare with every kernel the machine has (lengths 0 to 256, every kind of
  char at every position, invalid WTF-8, surrogates, text ending at a page boundary).
- **Null.** A `string` has no null: `String` (and a `string` field of a tagged struct) reads a null string as `""`,
  and `""` is written back, not null. Read into a `*string` (`StringPtr`, `NullableString`; generated structs use
  `*string` for C# strings) to tell null from empty.
- **`[NinoUtf8]` strings** are read as strict UTF-8 (no overlong forms, no surrogates, nothing above U+10FFFF), with
  the char count checked against the bytes. When written, anything that is not a scalar value becomes U+FFFD, as
  .NET's encoder does.

## Tests

```sh
cd runtimes/go
go vet ./...
go test -race ./...                                    # unit tests, sample codecs, conformance corpus
go test -tags purego ./...                             # the same without the string kernels' assembly
go test -run='^$' -fuzz=FuzzReader -fuzztime=1m .      # any sequence of reads over any bytes
go test -run='^$' -fuzz=FuzzWireString -fuzztime=1m .  # the string writer's kernels against the Go loops
go test -run='^$' -fuzz=FuzzReadVector -fuzztime=1m .  # the string reader's kernels against the Go loops
go test -run='^$' -fuzz=FuzzDecode -fuzztime=1m ./internal/sample   # any bytes as any corpus type
go test -run='^$' -fuzz=FuzzTagged -fuzztime=1m .                   # any bytes into tagged structs
go test -run='^$' -fuzz=FuzzTaggedDecode -fuzztime=1m ./internal/sample   # any bytes as any corpus type, by tags
```

The string kernels' assembly is generated: `go generate` (in `runtimes/go`) runs `internal/asm`, a module of its own
so that this one keeps no dependencies. It writes `strings_wire_amd64.s` with [avo](https://github.com/mmcloughlin/avo),
the tool the Go standard library's and golang.org/x/crypto's assembly is written with: Go code that emits each
instruction, checking its operands against the instruction's forms; one kernel is written once and placed in each
function that takes it (the AVX2 and AVX-512 writers share theirs, the parts the instruction sets do differently
chosen by the tier); the AVX-512 readers take their registers from avo's allocator. avo writes x86 only, so
`strings_wire_arm64.s` is text that `internal/asm` assembles from templates, writing the NEON instructions Go's
assembler lacks as `WORD`s with the instruction in a comment. The kernels read `wireSlack` and `wireMaxText` from
`go_asm.h`. Never edit the `.s` files; after changing the generator, run `go generate` and commit both:

```sh
cd runtimes/go
go generate ./...                       # rewrite strings_wire_amd64.s and strings_wire_arm64.s
(cd internal/asm && go vet . && go test .)   # the committed files are what the generator writes
(cd bench && go test -run TestTiersMatchXSys .)   # the detected tiers match golang.org/x/sys/cpu (amd64)
```

The conformance test reads `conformance/corpus` at the repository root, or the directory `NINO_CORPUS` names (as the
Rust and TypeScript tests do; `NINO_CONFORMANCE_CORPUS`, its former name, is still read). It runs every case:

- each valid case is read, compared with its JSON, and written back to its exact bytes;
- each malicious case must fail, without a panic and within a bounded allocation;
- each strict prefix of a valid case must fail.

Without the corpus the test skips, except when `NINO_CORPUS_REQUIRED=1` or `CI` is set, where it fails. The
hand-written sample skips the cases of reference tracking, `[NinoType]` collections, generic types behind a type id
and Nullable or KeyValuePair type arguments, which `internal/generated` (nino-gen's code) runs, and the one of a
generic type holding itself with other arguments, which Go cannot express. The struct-tag path runs the same cases
(`TestTagged*`), skipping reference tracking and multi-dimensional arrays, which it rejects. `FuzzDecode` and
`FuzzTaggedDecode` are seeded from the corpus when there is one.

Checks of the generated machine code: `go build -gcflags=-d=ssa/check_bce/debug=1 .` lists the bounds checks left
(the primitive reads and writes have none past their one length check; `Writer.Extend` keeps one slice check), and
`go build -gcflags=-m .` lists what escapes (nothing on the primitive paths).

## Benchmarks

`bench` is a separate module, so the runtime's own module has no dependencies. It compares Nino with Protocol Buffers
(google.golang.org/protobuf), `github.com/vmihailenco/msgpack/v5`, `encoding/json` and `encoding/gob` on the data of
the .NET benchmark's `SimpleClass` (one, and a list of 100), on a list of 1,000,000 records with two strings each in
an order unrelated to where they are in memory (`scattered-1m`, larger than the caches) and on 1,000 strings (ASCII;
and ASCII, Latin-1 and other text in turn), with `testing.B`; summarize several counts with benchstat:

```sh
cd runtimes/go/bench
go test -run='^$' -bench=. -benchmem -count=6 . | tee bench.txt
go run golang.org/x/perf/cmd/benchstat@latest bench.txt
```

The benchmarks are `Benchmark{Serialize,Deserialize}/<case>/<serializer>`; see benchmarks/README.md.
`NinoTags` is the struct-tag path on the same values. `go test -run='^$' -bench=Tagged .` in
`runtimes/go` compares the tag path with a hand-written codec of one type, and its address access with reflect's
setters.

Numbers from a laptop or a shared machine are for comparing changes back to back, not for publishing.
