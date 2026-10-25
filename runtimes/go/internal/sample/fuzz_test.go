package sample

import (
	"reflect"
	"sort"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
	"github.com/JasonXuDeveloper/Nino/runtimes/go/v4/internal/corpustest"
)

// FuzzDecode reads arbitrary bytes as one of the corpus's root types (chosen by the first byte). Reading must never
// panic; whatever it accepts must write back, and read again to the same value.
//
// Seeded from the conformance corpus when it is present (corpustest.Dir), and from values written here.
func FuzzDecode(f *testing.F) {
	names := make([]string, 0, len(codecs))
	for name := range codecs {
		names = append(names, name)
	}
	sort.Strings(names)
	index := make(map[string]byte, len(names))
	for i, name := range names {
		index[name] = byte(i)
	}

	for _, seed := range seeds() {
		f.Add(append([]byte{index[seed.typ]}, seed.data...))
	}
	corpustest.Seed(f, index)

	f.Fuzz(func(t *testing.T, input []byte) {
		if len(input) == 0 {
			return
		}
		k := codecs[names[int(input[0])%len(names)]]
		data := input[1:]
		v, err := nino.Deserialize(data, k.read)
		if err != nil {
			if _, ok := err.(*nino.Error); !ok {
				t.Fatalf("error of type %T", err)
			}
			return
		}
		out, err := nino.Serialize(v, k.write)
		if err != nil {
			t.Fatalf("a value read did not write back: %v", err)
		}
		again, err := nino.DeserializeExact(out, k.read)
		if err != nil {
			t.Fatalf("written bytes did not read back: %v", err)
		}
		if a, b := render(v), render(again); !reflect.DeepEqual(a, b) {
			t.Fatalf("the value changed through a write and a read")
		}
	})
}

type seed struct {
	typ  string
	data []byte
}

func seeds() []seed {
	var out []seed
	add := func(typ string, data []byte, err error) {
		if err == nil {
			out = append(out, seed{typ, data})
		}
	}
	s := "seed é 中 \U0001F600"
	b, err := nino.Serialize(&Strings{Text: &s, Utf8: &s}, WriteStrings)
	add("Conformance.Strings", b, err)
	b, err = nino.Serialize(chain(140), WriteChain)
	add("Conformance.Chain", b, err)
	b, err = nino.Serialize(tolerantChain(140), WriteTolerantChain)
	add("Conformance.TolerantChain", b, err)
	b, err = nino.Serialize(keyedChain(140), WriteKeyedChain)
	add("Conformance.KeyedChain", b, err)
	b, err = nino.Serialize(&TolerantRoot{Name: &s, Chain: chain(140), After: 1}, WriteTolerantRoot)
	add("Conformance.TolerantRoot", b, err)
	b, err = nino.Serialize(&Zoo{Star: &Cat{Name: &s}, Animals: []Animal{&Dog{}, &BoxString{Value: &s}}, Anything: &Point{1, 2}}, WriteZoo)
	add("Conformance.Zoo", b, err)
	b, err = nino.Serialize(&Collections{Ints: []int32{1}, StringSet: []*string{&s, nil}, Grid: &Grid[int32]{Lengths: [2]int{1, 2}, Items: []int32{1, 2}}}, WriteCollections)
	add("Conformance.Collections", b, err)
	one := int32(1)
	b, err = nino.Serialize(&Nullables{Int: &one, IntArray: []*int32{&one, nil}, PaddedArray: []*Padded{{1, 2}, nil}}, WriteNullables)
	add("Conformance.Nullables", b, err)
	b, err = nino.Serialize(&KeyedHolder{Keyed: &KeyedV1{Name: &s, Items: []int32{1}}}, WriteKeyedHolder)
	add("Conformance.KeyedHolder", b, err)
	b, err = nino.Serialize(&TreeHolder{Tree: Tree{Tree{}, nil}, DictTree: DictTree{{Key: "a", Value: nil}}}, WriteTreeHolder)
	add("Conformance.TreeHolder", b, err)
	return out
}

// FuzzTaggedDecode is FuzzDecode through the struct-tag path (nino.Unmarshal into the tagged Go types): reading
// must never panic and must fail with an *Error; whatever it accepts must write back to bytes that read and write
// again to the same bytes.
func FuzzTaggedDecode(f *testing.F) {
	registerTagged(f)
	var names []string
	for name, typ := range tagRoots {
		if typ != nil {
			names = append(names, name)
		}
	}
	sort.Strings(names)
	index := make(map[string]byte, len(names))
	for i, name := range names {
		index[name] = byte(i)
	}
	for _, seed := range seeds() {
		if i, ok := index[seed.typ]; ok {
			f.Add(append([]byte{i}, seed.data...))
		}
	}
	corpustest.Seed(f, index)
	f.Fuzz(func(t *testing.T, input []byte) {
		if len(input) == 0 {
			return
		}
		typ := tagRoots[names[int(input[0])%len(names)]]
		v := reflect.New(typ)
		if err := nino.Unmarshal(input[1:], v.Interface()); err != nil {
			if _, ok := err.(*nino.Error); !ok {
				t.Fatalf("error of type %T: %v", err, err)
			}
			return
		}
		once, err := marshalAs(typ, v.Elem())
		if err != nil {
			t.Fatalf("a value read did not write back: %v", err)
		}
		again := reflect.New(typ)
		if err := nino.UnmarshalExact(once, again.Interface()); err != nil {
			t.Fatalf("written bytes did not read back: %v", err)
		}
		twice, err := marshalAs(typ, again.Elem())
		if err != nil || string(once) != string(twice) {
			t.Fatalf("not stable through a write and a read (%v)", err)
		}
	})
}
