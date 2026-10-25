// Package corpustest runs the conformance corpus (conformance/corpus at the repository root, conformance/README.md)
// over a set of Go codecs: every valid case is read and compared with its JSON, then written back and compared with
// its bytes; every malicious case must fail (or, where the corpus allows, return a value) without panicking and
// within bounded memory; every strict prefix of a valid case must fail.
//
// It is the one runner for internal/sample (hand-written codecs, and the struct-tag path over the same types) and
// internal/generated (the code nino-gen writes), which differ only in how they look up a type's codec.
package corpustest

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"sort"
	"strings"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// Dir returns the corpus directory: $NINO_CORPUS (as the Rust and TypeScript tests read it, or
// $NINO_CONFORMANCE_CORPUS, its former name here), or else conformance/corpus at the repository root, relative to
// the directory go test runs a package's tests in (a package under runtimes/go/internal). It is "" when the
// directory holds no index.json.
func Dir() string {
	dir := os.Getenv("NINO_CORPUS")
	if dir == "" {
		dir = os.Getenv("NINO_CONFORMANCE_CORPUS")
	}
	if dir == "" {
		dir = filepath.Join("..", "..", "..", "..", "conformance", "corpus")
	}
	if _, err := os.Stat(filepath.Join(dir, "index.json")); err != nil {
		return ""
	}
	return dir
}

// Require returns Dir, or skips t when there is no corpus. It fails t instead when the corpus is required:
// NINO_CORPUS_REQUIRED=1, as for Rust and TypeScript, or CI set.
func Require(t testing.TB) string {
	t.Helper()
	dir := Dir()
	if dir == "" {
		if os.Getenv("NINO_CORPUS_REQUIRED") == "1" || os.Getenv("CI") != "" {
			t.Fatalf("conformance corpus not found (required: NINO_CORPUS_REQUIRED or CI is set): set NINO_CORPUS")
		}
		t.Skipf("conformance corpus not found: set NINO_CORPUS")
	}
	return dir
}

// Seed adds every corpus case (valid and malicious, under 64 KiB) to a fuzz test whose first input byte picks the
// type, as index maps the case's .NET type to it; a case whose type index does not hold is left out. Without a
// corpus it adds nothing.
func Seed(f *testing.F, index map[string]byte) {
	dir := Dir()
	if dir == "" {
		return
	}
	for _, sub := range []string{"valid", "malicious"} {
		files, _ := filepath.Glob(filepath.Join(dir, sub, "*.json"))
		for _, file := range files {
			var c struct {
				Type string `json:"type"`
			}
			meta, err := os.ReadFile(file)
			if err != nil || json.Unmarshal(meta, &c) != nil {
				continue
			}
			i, ok := index[c.Type]
			if !ok {
				continue
			}
			data, err := os.ReadFile(strings.TrimSuffix(file, ".json") + ".bin")
			if err == nil && len(data) < 1<<16 {
				f.Add(append([]byte{i}, data...))
			}
		}
	}
}

// ValidCase is the description of a valid case (valid/<case>.json).
type ValidCase struct {
	Case      string          `json:"case"`
	Type      string          `json:"type"`
	Optional  string          `json:"optional"`
	RoundTrip bool            `json:"roundTrip"`
	Reencoded string          `json:"reencoded"`
	Value     json.RawMessage `json:"value"`
}

type maliciousCase struct {
	Case     string `json:"case"`
	Type     string `json:"type"`
	Expected string `json:"expected"`
}

type truncations struct {
	Expected string `json:"expected"`
	Cases    []struct {
		Case string `json:"case"`
		Type string `json:"type"`
	} `json:"cases"`
}

// Index is the corpus's index.json.
type Index struct {
	CorpusVersion int      `json:"corpusVersion"`
	Valid         []string `json:"valid"`
	Malicious     []string `json:"malicious"`
}

// ReadJSON decodes the JSON file at path into v, numbers as json.Number.
func ReadJSON(t testing.TB, path string, v any) {
	t.Helper()
	d := json.NewDecoder(bytes.NewReader(readFile(t, path)))
	d.UseNumber()
	if err := d.Decode(v); err != nil {
		t.Fatalf("%s: %v", path, err)
	}
}

// LoadIndex reads the corpus's index.json, which must be of the version this runner reads.
func LoadIndex(t testing.TB, dir string) Index {
	t.Helper()
	var ix Index
	ReadJSON(t, filepath.Join(dir, "index.json"), &ix)
	if ix.CorpusVersion != 1 {
		t.Fatalf("corpus version %d: this test reads version 1", ix.CorpusVersion)
	}
	return ix
}

func readFile(t testing.TB, path string) []byte {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

// Codec reads and writes one root type of the corpus, its value boxed.
type Codec struct {
	// Read reads data as the type; exact requires it to take all of data.
	Read func(data []byte, exact bool) (any, error)
	// Write writes v, a value Read returned.
	Write func(v any) ([]byte, error)
}

// Funcs is the Codec of a read and a write function, as nino.Deserialize and nino.Serialize take them.
func Funcs(read func(*nino.Reader) any, write func(*nino.Writer, any)) Codec {
	return Codec{
		Read: func(data []byte, exact bool) (any, error) {
			if exact {
				return nino.DeserializeExact(data, read)
			}
			return nino.Deserialize(data, read)
		},
		Write: func(v any) ([]byte, error) { return nino.Serialize(v, write) },
	}
}

// Suite runs the corpus over one way of reading and writing its types.
type Suite struct {
	// Codec returns the codec of a .NET type, and false when there is none.
	Codec func(typeName string) (Codec, bool)
	// Unsupported, if set, returns why a type the corpus names has no codec here (and needs none), or "".
	Unsupported func(typeName string) string
	// Skip, if set, returns why a type that has a codec is not run, or "".
	Skip func(typeName string) string
	// Expected, if set, adjusts a valid case's value as the corpus's JSON gives it before it is compared.
	Expected func(value any) any
	// Check, if set, runs on each valid case after its value matched the corpus's JSON.
	Check func(t *testing.T, typeName string, data []byte, v any)
}

// unsupported is s.Unsupported, or "" without one.
func (s Suite) unsupported(typeName string) string {
	if s.Unsupported == nil {
		return ""
	}
	return s.Unsupported(typeName)
}

// codec returns the codec of a type, skipping t for a type this runtime or the suite does not read and failing it
// for one without a codec.
func (s Suite) codec(t *testing.T, typeName, optional string) Codec {
	t.Helper()
	if reason := s.unsupported(typeName); reason != "" {
		if optional != "" {
			t.Skipf("%s (optional feature %q)", reason, optional)
		}
		t.Skip(reason)
	}
	k, ok := s.Codec(typeName)
	if !ok {
		t.Fatalf("no codec for %s", typeName)
	}
	if s.Skip != nil {
		if reason := s.Skip(typeName); reason != "" {
			t.Skip(reason)
		}
	}
	return k
}

// Valid reads each valid case, compares the value with the case's JSON and the bytes it writes with the case's.
func (s Suite) Valid(t *testing.T) {
	dir := Require(t)
	ix := LoadIndex(t, dir)
	for _, name := range ix.Valid {
		t.Run(name, func(t *testing.T) {
			var c ValidCase
			ReadJSON(t, filepath.Join(dir, "valid", name+".json"), &c)
			k := s.codec(t, c.Type, c.Optional)
			data := readFile(t, filepath.Join(dir, "valid", name+".bin"))
			v, err := k.Read(data, true)
			if err != nil {
				t.Fatalf("read: %v", err)
			}

			var want any
			d := json.NewDecoder(bytes.NewReader(c.Value))
			d.UseNumber()
			if err := d.Decode(&want); err != nil {
				t.Fatal(err)
			}
			if s.Expected != nil {
				want = s.Expected(want)
			}
			if got := Render(v); !reflect.DeepEqual(got, want) {
				g, _ := json.Marshal(got)
				w, _ := json.Marshal(want)
				t.Fatalf("value\n got %.2000s\nwant %.2000s", g, w)
			}
			if s.Check != nil {
				s.Check(t, c.Type, data, v)
			}

			out, err := k.Write(v)
			if err != nil {
				t.Fatalf("write: %v", err)
			}
			expect := data
			if !c.RoundTrip {
				if expect, err = hex.DecodeString(c.Reencoded); err != nil {
					t.Fatal(err)
				}
			}
			if !bytes.Equal(out, expect) {
				at := 0
				for at < len(out) && at < len(expect) && out[at] == expect[at] {
					at++
				}
				t.Fatalf("written bytes differ at %d (%d bytes, want %d)\n got %.400x\nwant %.400x", at, len(out),
					len(expect), out[at:], expect[at:])
			}
		})
	}
}

// allocated returns the bytes f allocates.
func allocated(f func()) uint64 {
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	f()
	runtime.ReadMemStats(&after)
	return after.TotalAlloc - before.TotalAlloc
}

// allocationBound is the memory a read of n bytes may allocate: a multiple of n (each element read takes at most
// a few times its bytes, collections presized within 16 times the bytes left) plus a fixed amount.
func allocationBound(n int) uint64 { return 64*uint64(n) + 1<<20 }

// Malicious reads each malicious case, which must fail with an *nino.Error (or, where the case allows, return a
// value) within allocationBound.
func (s Suite) Malicious(t *testing.T) {
	dir := Require(t)
	ix := LoadIndex(t, dir)
	for _, name := range ix.Malicious {
		t.Run(name, func(t *testing.T) {
			var c maliciousCase
			ReadJSON(t, filepath.Join(dir, "malicious", name+".json"), &c)
			k := s.codec(t, c.Type, "")
			data := readFile(t, filepath.Join(dir, "malicious", name+".bin"))
			var err error
			n := allocated(func() { _, err = k.Read(data, true) })
			if n > allocationBound(len(data)) {
				t.Errorf("allocated %d bytes for %d bytes of data", n, len(data))
			}
			switch c.Expected {
			case "error":
				if err == nil {
					t.Fatal("read without an error")
				}
			case "error-or-value":
			default:
				t.Fatalf("unknown expectation %q", c.Expected)
			}
			if err != nil {
				var e *nino.Error
				if !errors.As(err, &e) {
					t.Fatalf("error of type %T: %v", err, err)
				}
				t.Log(err)
			}
		})
	}
}

// Truncations reads every strict prefix of the valid cases the corpus lists, each of which must fail.
func (s Suite) Truncations(t *testing.T) {
	dir := Require(t)
	var tr truncations
	ReadJSON(t, filepath.Join(dir, "malicious", "truncations.json"), &tr)
	if tr.Expected != "error" {
		t.Fatalf("unknown expectation %q", tr.Expected)
	}
	for _, c := range tr.Cases {
		t.Run(c.Case, func(t *testing.T) {
			k := s.codec(t, c.Type, "")
			data := readFile(t, filepath.Join(dir, "valid", c.Case+".bin"))
			for n := 0; n < len(data); n++ {
				// A copy of exactly n bytes, so reading past them would be caught by the race detector's and
				// the runtime's bounds checks alike.
				prefix := append([]byte(nil), data[:n]...)
				if _, err := k.Read(prefix, false); err == nil {
					t.Fatalf("a prefix of %d bytes (of %d) read without an error", n, len(data))
				}
			}
		})
	}
}

// Covered checks that every type the corpus names has a codec or is unsupported.
func (s Suite) Covered(t *testing.T) {
	dir := Require(t)
	ix := LoadIndex(t, dir)
	missing := map[string]bool{}
	check := func(sub string, names []string) {
		for _, name := range names {
			var c struct {
				Type string `json:"type"`
			}
			ReadJSON(t, filepath.Join(dir, sub, name+".json"), &c)
			if _, ok := s.Codec(c.Type); !ok && s.unsupported(c.Type) == "" {
				missing[c.Type] = true
			}
		}
	}
	check("valid", ix.Valid)
	check("malicious", ix.Malicious)
	if len(missing) > 0 {
		var names []string
		for n := range missing {
			names = append(names, n)
		}
		sort.Strings(names)
		t.Fatalf("no codec for %v", names)
	}
}
