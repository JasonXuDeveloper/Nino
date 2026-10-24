# Nino for Go

`github.com/JasonXuDeveloper/Nino/runtimes/go` (package `nino`) reads and writes Nino's binary format (v4): the bytes
the .NET serializer writes, as specified in `docs/spec/wire-format.md`. It is the runtime that generated Go code calls,
like `nino.ts` for TypeScript. It has no dependencies outside the standard library. Generated code uses no
reflection; Go structs without generated code can be mapped with struct tags instead ([Struct tags](#struct-tags)),
through a reflection-built plan.

Go 1.22 or later.

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
p, err := nino.Deserialize(data, ReadPoint)        // bytes after the value are ignored, as in .NET
p, err := nino.DeserializeExact(data, ReadPoint)   // ... or rejected
```

### The API in brief

| Area | Reader / Writer |
| --- | --- |
| Primitives | `Bool`, `U8`...`I64`, `F32`, `F64`, `Char`, `Half`, `Int128`, `UInt128` |
| BCL values | `Decimal`, `Guid`, `DateTime`, `TimeSpan`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `BigInteger` (`*big.Int`), `Version` |
| Strings | `String`, `NullableString`, `StringPtr` (default encoding); `Utf8`, `NullableUtf8`, `Utf8Ptr` (`[NinoUtf8]`); `UTF16` (code units) |
| Raw runs | `Reader.Take(n)`, `Writer.Extend(n)` / `ExtendZero(n)`, `BoolAt` for a bool inside a raw struct |
| Collections | `Length`, `ByteArray`, `ReadPrimitives` / `WritePrimitives` (one block copy), `ReadBools`, `ReadSlice`, `ReadMap`, `ReadSet`, `ReadUnique`, `ReadEntries`, `ReadUniqueEntries`, `ReadFixed` (raw structs, the fixed stride of `T?[]`), `ReadShape` (multi-dimensional arrays) and their writers |
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
to §7.10). Reference tracking (`trackReferences`, §7.9) is not implemented, as in the TypeScript runtime.

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
- `trackReferences=true` is an error (reference tracking is not implemented).
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
  `Box<int>` as `*Box[int32]`; a generic type written behind a type id is left out (docs/codegen.md, "Generic
  types").
- **`NinoTypeID()`** on the pointer type of every class that can sit behind a type id.
- **`NinoTypeName()`** returning the .NET full name.

`internal/sample` follows these conventions (camel policy, `json` and `yaml`); its `layouts.go` is what the
generator's `NinoLayout` methods look like. `internal/generated` holds nino-gen's output for the corpus's types (and
`internal/generated/shapes` for `tests/Nino.Codegen.Tests`' shapes), run against the corpus like the sample.

## Strings

- **Default encoding.** Latin-1 or UTF-16, as .NET writes it. It is read into a Go string as **WTF-8**: UTF-8,
  except that an unpaired surrogate (which .NET strings may hold, and which the spec says readers MUST keep) is
  stored in the 3-byte form UTF-8 would give it. Writing such a string back gives the same code units. Use
  `Reader.UTF16` for the code units themselves.
- **Writing.** A string is written as Latin-1 when every char is at most U+00FF, else as UTF-16 (what .NET writes).
  WTF-8 surrogates are written back as the code units they stand for. A byte that is not UTF-8 is written as U+FFFD.
- **`[NinoUtf8]` strings** are read as strict UTF-8 (no overlong forms, no surrogates, nothing above U+10FFFF), with
  the char count checked against the bytes. When written, anything that is not a scalar value becomes U+FFFD, as
  .NET's encoder does.

## Tests

```sh
cd runtimes/go
go vet ./...
go test -race ./...                                    # unit tests, sample codecs, conformance corpus
go test -run='^$' -fuzz=FuzzReader -fuzztime=1m .      # any sequence of reads over any bytes
go test -run='^$' -fuzz=FuzzDecode -fuzztime=1m ./internal/sample   # any bytes as any corpus type
go test -run='^$' -fuzz=FuzzTagged -fuzztime=1m .                   # any bytes into tagged structs
go test -run='^$' -fuzz=FuzzTaggedDecode -fuzztime=1m ./internal/sample   # any bytes as any corpus type, by tags
```

The conformance test reads `conformance/corpus` at the repository root, or the directory `NINO_CONFORMANCE_CORPUS`
names. It runs every case:

- each valid case is read, compared with its JSON, and written back to its exact bytes;
- each malicious case must fail, without a panic and within a bounded allocation;
- each strict prefix of a valid case must fail.

Without the corpus the test skips, except when `CI` is set, where it fails. Cases that need reference tracking are
skipped. The struct-tag path runs the same cases (`TestTagged*`), skipping reference tracking and multi-dimensional
arrays, which it rejects. `FuzzDecode` and `FuzzTaggedDecode` are seeded from the corpus when
`NINO_CONFORMANCE_CORPUS` is set.

Checks of the generated machine code: `go build -gcflags=-d=ssa/check_bce/debug=1 .` lists the bounds checks left
(the primitive reads and writes have none past their one length check; `Writer.Extend` keeps one slice check), and
`go build -gcflags=-m .` lists what escapes (nothing on the primitive paths).

## Benchmarks

`bench` is a separate module, so the runtime's own module has no dependencies. It compares Nino with
`encoding/json`, `github.com/vmihailenco/msgpack/v5` and `encoding/gob` on the data of the .NET benchmark's
`SimpleClass`:

```sh
cd runtimes/go/bench
go test -run='^$' -bench=. -benchmem -count=5
```

`NinoTags` and `NinoTagsList` are the struct-tag path on the same struct. `go test -run='^$' -bench=Tagged .` in
`runtimes/go` compares the tag path with a hand-written codec of one type, and its address access with reflect's
setters.

Numbers from a laptop or a shared machine are for comparing changes back to back, not for publishing.
