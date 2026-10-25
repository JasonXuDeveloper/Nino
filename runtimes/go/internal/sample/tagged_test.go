package sample

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"errors"
	"math/big"
	"path/filepath"
	"reflect"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The corpus through the struct-tag path: the same Go types, read and written by nino.Marshal and nino.Unmarshal
// from their tags and their NinoLayout instead of the hand-written codecs. Each type's plan is checked against
// the layout .NET published for it when it is built.

// tagRoots are the corpus's root types for the tag path. A nil type is one the tag path rejects (with a TagError).
var tagRoots = map[string]reflect.Type{
	"Conformance.Integers":          reflect.TypeFor[*Integers](),
	"Conformance.Floats":            reflect.TypeFor[*Floats](),
	"Conformance.Scalars":           reflect.TypeFor[*Scalars](),
	"Conformance.Dates":             reflect.TypeFor[*Dates](),
	"Conformance.Enums":             reflect.TypeFor[*Enums](),
	"Conformance.BigValues":         reflect.TypeFor[*BigValues](),
	"Conformance.Strings":           reflect.TypeFor[*Strings](),
	"Conformance.StringCollections": reflect.TypeFor[*StringCollections](),
	"Conformance.Bytes":             reflect.TypeFor[*Bytes](),
	"Conformance.Point":             reflect.TypeFor[*Point](),
	"Conformance.Structs":           reflect.TypeFor[*Structs](),
	"Conformance.Outer":             reflect.TypeFor[*Outer](),
	"Conformance.Collections":       nil, // int[,]: grid encodings are not supported by struct tags
	"Conformance.Nullables":         reflect.TypeFor[*Nullables](),
	"Conformance.Empties":           reflect.TypeFor[*Empties](),
	"Conformance.TolerantV1":        reflect.TypeFor[*TolerantV1](),
	"Conformance.TolerantV2":        reflect.TypeFor[*TolerantV2](),
	"Conformance.TolerantHolder":    reflect.TypeFor[*TolerantHolder](),
	"Conformance.KeyedV1":           reflect.TypeFor[*KeyedV1](),
	"Conformance.KeyedV2":           reflect.TypeFor[*KeyedV2](),
	"Conformance.KeyedHolder":       reflect.TypeFor[*KeyedHolder](),
	"Conformance.Animal":            reflect.TypeFor[Animal](),
	"Conformance.Zoo":               reflect.TypeFor[*Zoo](),
	"Conformance.Chain":             reflect.TypeFor[*Chain](),
	"Conformance.TolerantChain":     reflect.TypeFor[*TolerantChain](),
	"Conformance.KeyedChain":        reflect.TypeFor[*KeyedChain](),
	"Conformance.TreeNode":          reflect.TypeFor[*TreeNode](),
	"Conformance.Link":              reflect.TypeFor[Link](),
	"Conformance.RecordChain":       reflect.TypeFor[*RecordChain](),
	"Conformance.TolerantRoot":      reflect.TypeFor[*TolerantRoot](),
	"Conformance.TreeHolder":        reflect.TypeFor[*TreeHolder](),
	"Conformance.Vec2":              reflect.TypeFor[Vec2](),
	"Conformance.Padded":            reflect.TypeFor[Padded](),
	"Conformance.Nested":            reflect.TypeFor[Nested](),
	"Conformance.Label":             reflect.TypeFor[Label](),
	"Conformance.KeyedStruct":       reflect.TypeFor[KeyedStruct](),
	"Conformance.EmptyStruct":       reflect.TypeFor[EmptyStruct](),

	"Conformance.Targets.AnimalSlot":    reflect.TypeFor[*tagValue[Animal]](),
	"Conformance.Targets.BigSlot":       reflect.TypeFor[*tagValue[*big.Int]](),
	"Conformance.Targets.BoolArray":     reflect.TypeFor[*tagValues[[]bool]](),
	"Conformance.Targets.BoolValue":     reflect.TypeFor[*tagValue[bool]](),
	"Conformance.Targets.ByteArray":     reflect.TypeFor[*tagValues[[]byte]](),
	"Conformance.Targets.DogSlot":       reflect.TypeFor[*tagValue[DogType]](),
	"Conformance.Targets.Grid":          nil,
	"Conformance.Targets.IntArray":      reflect.TypeFor[*tagValues[[]int32]](),
	"Conformance.Targets.IntMap":        reflect.TypeFor[*tagValues[map[int32]int32]](),
	"Conformance.Targets.IntSet":        reflect.TypeFor[*tagValues[map[int32]struct{}]](),
	"Conformance.Targets.LongList":      reflect.TypeFor[*tagValues[[]int64]](),
	"Conformance.Targets.MaybeInt":      reflect.TypeFor[*tagValue[*int32]](),
	"Conformance.Targets.MaybeIntArray": reflect.TypeFor[*tagValues[[]*int32]](),
	"Conformance.Targets.ObjectSlot":    reflect.TypeFor[*tagValue[nino.Typed]](),
	"Conformance.Targets.PaddedArray":   reflect.TypeFor[*tagValues[[]Padded]](),
	"Conformance.Targets.PointList":     reflect.TypeFor[*tagValues[[]*Point]](),
	"Conformance.Targets.ShapeSlot":     reflect.TypeFor[*tagValue[Shape]](),
	"Conformance.Targets.SortedMap":     reflect.TypeFor[*tagValues[map[int32]int32]](),
	"Conformance.Targets.StringList":    reflect.TypeFor[*tagValues[[]*string]](),
	"Conformance.Targets.StringMap":     reflect.TypeFor[*tagValues[map[string]int32]](),
	"Conformance.Targets.StringSet":     reflect.TypeFor[*tagValues[map[string]struct{}]](), // null reads as ""
	"Conformance.Targets.TextValue":     reflect.TypeFor[*tagValue[*string]](),
	"Conformance.Targets.Utf8Value":     reflect.TypeFor[*tagUtf8](),
	"Conformance.Targets.VersionSlot":   reflect.TypeFor[*tagValue[*nino.Version]](),
}

// The Targets: sealed classes with one member, Value or Values.
type tagValue[T any] struct {
	Value T `nino:"0" json:"value" yaml:"value"`
}

type tagValues[T any] struct {
	Values T `nino:"0" json:"values" yaml:"values"`
}

type tagUtf8 struct {
	Value *string `nino:"0,utf8" json:"value" yaml:"value"`
}

// polymorphic are the types the corpus writes behind a type id.
var polymorphic = []any{
	(*Dog)(nil), (*Puppy)(nil), (*Cat)(nil), (*BoxInt32)(nil), (*BoxString)(nil), (*BoxPoints)(nil),
	(*BoxVec2)(nil), (*Square)(nil), (*Circle)(nil), (*Plain)(nil), (*Zoo)(nil), (*Point)(nil), (*Outer)(nil),
	(*Integers)(nil), (*TolerantBase)(nil), (*TolerantLeaf)(nil), (*RedLink)(nil), (*BlueLink)(nil),
}

func registerTagged(t testing.TB) {
	t.Helper()
	if err := nino.Register(polymorphic...); err != nil {
		t.Fatal(err)
	}
}

// TestTaggedPlans builds the plan of every root type: each is checked against its layout.
func TestTaggedPlans(t *testing.T) {
	registerTagged(t)
	for name, typ := range tagRoots {
		if typ == nil {
			continue
		}
		if err := nino.Check(typ); err != nil {
			t.Errorf("%s: %v", name, err)
		}
	}
	var te *nino.TagError
	if err := nino.Check(reflect.TypeFor[*Collections]()); !errors.As(err, &te) || te.Field != "Grid" {
		t.Errorf("Collections: %v, want a TagError for Grid", err)
	}
}

func TestTaggedConformanceValid(t *testing.T) {
	dir := corpusDir(t)
	ix := loadIndex(t, dir)
	registerTagged(t)
	for _, name := range ix.Valid {
		t.Run(name, func(t *testing.T) {
			var c validCase
			readJSON(t, filepath.Join(dir, "valid", name+".json"), &c)
			if reason := unsupported(c.Type); reason != "" {
				t.Skip(reason)
			}
			typ, ok := tagRoots[c.Type]
			if !ok {
				t.Fatalf("no tag type for %s", c.Type)
			}
			if typ == nil {
				t.Skipf("%s is not supported by struct tags", c.Type)
			}
			data := readFile(t, filepath.Join(dir, "valid", name+".bin"))
			v := reflect.New(typ)
			if err := nino.UnmarshalExact(data, v.Interface()); err != nil {
				t.Fatalf("read: %v", err)
			}
			var want any
			d := json.NewDecoder(bytes.NewReader(c.Value))
			d.UseNumber()
			if err := d.Decode(&want); err != nil {
				t.Fatal(err)
			}
			if got := render(v.Elem().Interface()); !reflect.DeepEqual(got, want) {
				g, _ := json.Marshal(got)
				w, _ := json.Marshal(want)
				t.Fatalf("value\n got %.2000s\nwant %.2000s", g, w)
			}
			// The same value as the hand-written codec's.
			if k, ok := codecs[c.Type]; ok {
				hand, err := nino.DeserializeExact(data, k.read)
				if err != nil {
					t.Fatal(err)
				}
				if !reflect.DeepEqual(render(hand), render(v.Elem().Interface())) {
					t.Fatal("the tag path and the hand-written codec read different values")
				}
			}
			out, err := marshalAs(typ, v.Elem())
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

func TestTaggedConformanceMalicious(t *testing.T) {
	dir := corpusDir(t)
	ix := loadIndex(t, dir)
	registerTagged(t)
	for _, name := range ix.Malicious {
		t.Run(name, func(t *testing.T) {
			var c maliciousCase
			readJSON(t, filepath.Join(dir, "malicious", name+".json"), &c)
			if reason := unsupported(c.Type); reason != "" {
				t.Skip(reason)
			}
			typ, ok := tagRoots[c.Type]
			if !ok {
				t.Fatalf("no tag type for %s", c.Type)
			}
			if typ == nil {
				t.Skipf("%s is not supported by struct tags", c.Type)
			}
			data := readFile(t, filepath.Join(dir, "malicious", name+".bin"))
			v := reflect.New(typ)
			var err error
			n := allocated(func() { err = nino.UnmarshalExact(data, v.Interface()) })
			if n > allocationBound(len(data)) {
				t.Errorf("allocated %d bytes for %d bytes of data", n, len(data))
			}
			if c.Expected == "error" && err == nil {
				t.Fatal("read without an error")
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

func TestTaggedConformanceTruncations(t *testing.T) {
	dir := corpusDir(t)
	registerTagged(t)
	var tr truncations
	readJSON(t, filepath.Join(dir, "malicious", "truncations.json"), &tr)
	for _, c := range tr.Cases {
		t.Run(c.Case, func(t *testing.T) {
			typ := tagRoots[c.Type]
			if unsupported(c.Type) != "" || typ == nil {
				t.Skip("not supported by struct tags")
			}
			data := readFile(t, filepath.Join(dir, "valid", c.Case+".bin"))
			for n := 0; n < len(data); n++ {
				prefix := append([]byte(nil), data[:n]...)
				if err := nino.Unmarshal(prefix, reflect.New(typ).Interface()); err == nil {
					t.Fatalf("a prefix of %d bytes (of %d) read without an error", n, len(data))
				}
			}
		})
	}
}

// marshalAs writes v as the type typ (an interface root included), through nino.MarshalAs.
func marshalAs(typ reflect.Type, v reflect.Value) ([]byte, error) {
	switch typ {
	case reflect.TypeFor[Animal]():
		a, _ := v.Interface().(Animal)
		return nino.MarshalAs(a)
	case reflect.TypeFor[Link]():
		l, _ := v.Interface().(Link)
		return nino.MarshalAs(l)
	}
	return nino.Marshal(v.Interface())
}

// TestSampleLayoutsAreTheCorpus: each type's NinoLayout is the layout the corpus publishes for it, unchanged
// (deferrable part included), so the plans are checked against what .NET writes now.
func TestSampleLayoutsAreTheCorpus(t *testing.T) {
	dir := corpusDir(t)
	ix := loadIndex(t, dir)
	published := map[string]string{}
	for group, names := range map[string][]string{"valid": ix.Valid, "malicious": ix.Malicious} {
		for _, name := range names {
			var c struct {
				Layouts map[string]string `json:"layouts"`
			}
			readJSON(t, filepath.Join(dir, group, name+".json"), &c)
			for k, v := range c.Layouts {
				published[k] = v
			}
		}
	}
	types := []reflect.Type{}
	for _, typ := range tagRoots {
		if typ != nil {
			types = append(types, typ)
		}
	}
	for _, p := range polymorphic {
		types = append(types, reflect.TypeOf(p))
	}
	checked := 0
	for _, typ := range types {
		if typ.Kind() != reflect.Pointer {
			continue // an interface: its implementations are checked
		}
		v, ok := reflect.New(typ.Elem()).Interface().(interface{ NinoLayout() string })
		if !ok {
			continue
		}
		want, ok := published["Conformance."+typ.Elem().Name()]
		if !ok {
			continue
		}
		checked++
		if got := v.NinoLayout(); got != want {
			t.Errorf("%s: NinoLayout()\n%s\nthe corpus publishes\n%s", typ.Elem().Name(), got, want)
		}
	}
	if checked == 0 {
		t.Fatal("no layout checked")
	}
	t.Logf("%d layouts checked", checked)
}
