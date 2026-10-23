package nino

import (
	"bytes"
	"errors"
	"reflect"
	"testing"
)

// fuzzTargets are the tag-mapped types FuzzTagged reads into: every kind of codec, keyed and tolerant bodies,
// polymorphic slots, self-nesting structs and deep class chains.
var fuzzTargets = []reflect.Type{
	reflect.TypeFor[*tagAll](),
	reflect.TypeFor[*tagKeyed](),
	reflect.TypeFor[*tagTolLeaf](),
	reflect.TypeFor[*tagZoo](),
	reflect.TypeFor[*tagChain](),
	reflect.TypeFor[tagTree](),
	reflect.TypeFor[*tagRecordChain](),
	reflect.TypeFor[[]tagPacked](),
	reflect.TypeFor[map[string][]*tagKeyed](),
}

// FuzzTagged reads arbitrary bytes into tag-mapped types (the first byte picks one). Reading must never panic, must
// fail with an *Error, and must leave the target unchanged on failure; a value read successfully must write and
// read back to the same bytes.
func FuzzTagged(f *testing.F) {
	if err := Register((*tagDog)(nil), (*tagCat)(nil), tagVec{}); err != nil {
		f.Fatal(err)
	}
	seed := func(i byte, v any) {
		b, err := Marshal(v)
		if err != nil {
			f.Fatal(err)
		}
		f.Add(append([]byte{i}, b...))
	}
	seed(0, sampleAll())
	seed(1, &tagKeyed{ID: 1, Name: str("n"), Score: 2})
	seed(2, &tagTolLeaf{A: 1, B: 2})
	seed(3, &tagZoo{Star: &tagDog{"rex", 3}, Animals: []tagAnimal{&tagCat{9}, nil}, Any: tagVec{1, 2}})
	var chain *tagChain
	for i := 0; i < 140; i++ {
		chain = &tagChain{Value: int32(i), Next: chain}
	}
	seed(4, chain)
	seed(5, tagTree{Kids: []tagTree{{}, {Kids: []tagTree{{}}}}})
	seed(6, &tagRecordChain{Value: 1, Next: &tagRecordChain{Value: 2}})
	seed(7, []tagPacked{{1, 2, 3}})
	seed(8, map[string][]*tagKeyed{"a": {{ID: 1}, nil}})
	f.Fuzz(func(t *testing.T, data []byte) {
		if len(data) == 0 {
			return
		}
		typ := fuzzTargets[int(data[0])%len(fuzzTargets)]
		data = data[1:]
		target := reflect.New(typ)
		err := Unmarshal(data, target.Interface())
		if err != nil {
			var e *Error
			if !errors.As(err, &e) {
				t.Fatalf("error of type %T: %v", err, err)
			}
			if !target.Elem().IsZero() {
				t.Fatal("a failed Unmarshal changed its target")
			}
			return
		}
		once, err := Marshal(target.Elem().Interface())
		if err != nil {
			t.Fatalf("a value read did not write back: %v", err)
		}
		again := reflect.New(typ)
		if err := UnmarshalExact(once, again.Interface()); err != nil {
			t.Fatalf("written bytes %x did not read back: %v", once, err)
		}
		twice, err := Marshal(again.Elem().Interface())
		if err != nil || !bytes.Equal(once, twice) {
			t.Fatalf("not stable: %x then %x (%v)", once, twice, err)
		}
	})
}
