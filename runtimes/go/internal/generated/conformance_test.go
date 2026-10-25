package generated

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
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The conformance corpus over the code nino-gen generated (nino_types.go), as internal/sample runs it over
// hand-written codecs. The corpus (conformance/corpus at the repository root, or NINO_CONFORMANCE_CORPUS): every valid case
// is read and compared with its JSON, then written back and compared with its bytes; every malicious case must fail
// (or, where the corpus allows, return a value) without panicking and within bounded memory; every strict prefix of
// a valid case must fail.

func corpusDir(t testing.TB) string {
	dir := os.Getenv("NINO_CONFORMANCE_CORPUS")
	if dir == "" {
		dir = filepath.Join("..", "..", "..", "..", "conformance", "corpus")
	}
	if _, err := os.Stat(filepath.Join(dir, "index.json")); err != nil {
		if os.Getenv("CI") != "" {
			t.Fatalf("conformance corpus not found at %s (required in CI)", dir)
		}
		t.Skipf("conformance corpus not found at %s: set NINO_CONFORMANCE_CORPUS", dir)
	}
	return dir
}

type validCase struct {
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

type index struct {
	CorpusVersion int      `json:"corpusVersion"`
	Valid         []string `json:"valid"`
	Malicious     []string `json:"malicious"`
}

func readJSON(t testing.TB, path string, v any) {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	d := json.NewDecoder(bytes.NewReader(data))
	d.UseNumber()
	if err := d.Decode(v); err != nil {
		t.Fatalf("%s: %v", path, err)
	}
}

func loadIndex(t testing.TB, dir string) index {
	var ix index
	readJSON(t, filepath.Join(dir, "index.json"), &ix)
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

// unsupported is the reason a type cannot be read here, or "": nino-gen leaves it out of Go (NINOGEN002).
func unsupported(typeName string) string {
	if typeName == "Conformance.NestHolder" {
		return "Nest<T> holds itself with other type arguments, which Go's generics reject"
	}
	return ""
}

// goTypeOf names the closed generic types that are one Go type with another (nino-gen's NINOGEN002 warnings: "left
// out of the polymorphic slots in Go"): Wrap<int?[]>, Wrap<List<int?>> and Wrap<HashSet<int?>> are all
// Wrap[[]*int32], whose NinoTypeName is the first one's. Their bytes are the same; only the name differs.
var goTypeOf = map[string]string{
	"Conformance.Wrap<System.Nullable<System.Int32>[]>":                                "Conformance.Wrap<System.Collections.Generic.HashSet<System.Nullable<System.Int32>>>",
	"Conformance.Wrap<System.Collections.Generic.List<System.Nullable<System.Int32>>>": "Conformance.Wrap<System.Collections.Generic.HashSet<System.Nullable<System.Int32>>>",
}

// sameGoType replaces, in an expected value, the .NET names of goTypeOf with the name Go gives their type.
func sameGoType(v any) any {
	switch v := v.(type) {
	case map[string]any:
		for k, x := range v {
			if name, ok := x.(string); ok && k == "$type" && goTypeOf[name] != "" {
				v[k] = goTypeOf[name]
				continue
			}
			v[k] = sameGoType(x)
		}
	case []any:
		for i, x := range v {
			v[i] = sameGoType(x)
		}
	}
	return v
}

// codecs looks up the generated codec of a .NET type.
var codecs = codecMap{}

type codecMap struct{}

type codec struct {
	read  func(*nino.Reader) any
	write func(*nino.Writer, any)
}

func (codecMap) lookup(name string) (codec, bool) {
	c, ok := NinoCodecOf(name)
	return codec{c.Read, c.Write}, ok
}

func TestConformanceValid(t *testing.T) {
	dir := corpusDir(t)
	ix := loadIndex(t, dir)
	for _, name := range ix.Valid {
		t.Run(name, func(t *testing.T) {
			var c validCase
			readJSON(t, filepath.Join(dir, "valid", name+".json"), &c)
			if reason := unsupported(c.Type); reason != "" {
				t.Skipf("%s (optional feature %q)", reason, c.Optional)
			}
			k, ok := codecs.lookup(c.Type)
			if !ok {
				t.Fatalf("no codec for %s", c.Type)
			}
			data := readFile(t, filepath.Join(dir, "valid", name+".bin"))
			v, err := nino.DeserializeExact(data, k.read)
			if err != nil {
				t.Fatalf("read: %v", err)
			}

			var want any
			d := json.NewDecoder(bytes.NewReader(c.Value))
			d.UseNumber()
			if err := d.Decode(&want); err != nil {
				t.Fatal(err)
			}
			want = sameGoType(want)
			if got := render(v); !reflect.DeepEqual(got, want) {
				g, _ := json.Marshal(got)
				w, _ := json.Marshal(want)
				t.Fatalf("value\n got %.2000s\nwant %.2000s", g, w)
			}

			out, err := nino.Serialize(v, k.write)
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

func TestConformanceMalicious(t *testing.T) {
	dir := corpusDir(t)
	ix := loadIndex(t, dir)
	for _, name := range ix.Malicious {
		t.Run(name, func(t *testing.T) {
			var c maliciousCase
			readJSON(t, filepath.Join(dir, "malicious", name+".json"), &c)
			if reason := unsupported(c.Type); reason != "" {
				t.Skip(reason)
			}
			k, ok := codecs.lookup(c.Type)
			if !ok {
				t.Fatalf("no codec for %s", c.Type)
			}
			data := readFile(t, filepath.Join(dir, "malicious", name+".bin"))
			var err error
			n := allocated(func() { _, err = nino.DeserializeExact(data, k.read) })
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
					t.Fatalf("error of type %T", err)
				}
				t.Log(err)
			}
		})
	}
}

func TestConformanceTruncations(t *testing.T) {
	dir := corpusDir(t)
	var tr truncations
	readJSON(t, filepath.Join(dir, "malicious", "truncations.json"), &tr)
	if tr.Expected != "error" {
		t.Fatalf("unknown expectation %q", tr.Expected)
	}
	for _, c := range tr.Cases {
		t.Run(c.Case, func(t *testing.T) {
			if reason := unsupported(c.Type); reason != "" {
				t.Skip(reason)
			}
			k, ok := codecs.lookup(c.Type)
			if !ok {
				t.Fatalf("no codec for %s", c.Type)
			}
			data := readFile(t, filepath.Join(dir, "valid", c.Case+".bin"))
			for n := 0; n < len(data); n++ {
				// A copy of exactly n bytes, so reading past them would be caught by the race detector's and
				// the runtime's bounds checks alike.
				prefix := append([]byte(nil), data[:n]...)
				if _, err := nino.Deserialize(prefix, k.read); err == nil {
					t.Fatalf("a prefix of %d bytes (of %d) read without an error", n, len(data))
				}
			}
		})
	}
}

// TestCorpusTypesCovered checks that every type the corpus names has a codec here or is listed as unsupported.
func TestCorpusTypesCovered(t *testing.T) {
	dir := corpusDir(t)
	ix := loadIndex(t, dir)
	missing := map[string]bool{}
	check := func(sub string, names []string) {
		for _, name := range names {
			var c struct {
				Type string `json:"type"`
			}
			readJSON(t, filepath.Join(dir, sub, name+".json"), &c)
			if _, ok := codecs.lookup(c.Type); !ok && unsupported(c.Type) == "" {
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
