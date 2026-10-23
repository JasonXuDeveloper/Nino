# Nino for Go

`github.com/JasonXuDeveloper/Nino/runtimes/go` (package `nino`) reads and writes Nino's binary format (v4): the bytes
the .NET serializer writes, as specified in `docs/spec/wire-format.md`. It is the runtime that generated Go code calls,
like `nino.ts` for TypeScript. It has no dependencies outside the standard library and uses no reflection.

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
- **Stricter than .NET where the spec allows** (spec §9 SHOULDs; .NET accepts these):
  - presence bytes and `bool`s other than 0 and 1, inside raw structs too;
  - `DateTime` with kind 3 or ticks past `DateTime.MaxValue`;
  - `decimal` with a scale above 28 or reserved bits set;
  - `DateOnly` and `TimeOnly` out of range;
  - a non-minimal `BigInteger`, or one with 0 bytes;
  - a keyed member that does not fill its entry, or a key repeated;
  - a deferred body that does not fill its entry;
  - a section of deferred bodies that is not well-formed, or one on an inner region;
  - bytes after the value (`DeserializeExact`).
- **Writer.** It appends to one buffer that doubles as it grows. Reused (`Writer.Reset`, or the pool behind
  `Serialize` and `Append`), writing a value allocates nothing. It rejects what a reader would reject (an
  out-of-range date, a length past `int32`), so it never writes bytes that .NET would misread.

The polymorphic dispatch, the depth counting and the region handling are the same code paths as in .NET (spec §7.8
to §7.10). Reference tracking (`trackReferences`, §7.9) is not implemented, as in the TypeScript runtime.

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
```

The conformance test reads `conformance/corpus` at the repository root, or the directory `NINO_CONFORMANCE_CORPUS`
names. It runs every case:

- each valid case is read, compared with its JSON, and written back to its exact bytes;
- each malicious case must fail, without a panic and within a bounded allocation;
- each strict prefix of a valid case must fail.

Without the corpus the test skips, except when `CI` is set, where it fails. Cases that need reference tracking are
skipped. `FuzzDecode` is seeded from the corpus when `NINO_CONFORMANCE_CORPUS` is set.

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

Numbers from a laptop or a shared machine are for comparing changes back to back, not for publishing.
