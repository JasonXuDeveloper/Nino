package generated

import (
	"reflect"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

func str(s string) *string { return &s }

func outerValue(name string) *Outer {
	return &Outer{
		Name:   str(name),
		Point:  &Point{X: 1, Y: 2},
		Middle: &Middle{First: &Point{X: 3, Y: 4}, Inner: &Inner{Value: 7, Text: str("t")}},
	}
}

func mustSerialize[T any](t *testing.T, v T, write func(*nino.Writer, T)) []byte {
	t.Helper()
	b, err := nino.Serialize(v, write)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

// TestDeserializeIntoNested reads into the objects the value holds.
func TestDeserializeIntoNested(t *testing.T) {
	target := &Outer{Name: str("old"), Point: &Point{X: 9, Y: 9}, Middle: &Middle{Second: &Point{X: 5, Y: 5}}}
	root, point, middle := target, target.Point, target.Middle
	want := outerValue("new")
	if err := nino.DeserializeInto(mustSerialize(t, want, WriteOuter), &target, ReadOuterInto); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(target, want) {
		t.Fatalf("got %+v, want %+v", target, want)
	}
	if target != root || target.Point != point || target.Middle != middle {
		t.Fatal("objects not reused")
	}
}

// TestDeserializeIntoSlice keeps a slice's storage and reads its elements into the old ones.
func TestDeserializeIntoSlice(t *testing.T) {
	list := func(n int) *PointList {
		v := &PointList{Values: make([]*Point, n)}
		for i := range v.Values {
			v.Values[i] = &Point{X: int32(i), Y: int32(10 - i)}
		}
		return v
	}
	target := list(8)
	storage, first := &target.Values[:1][0], target.Values[0]
	for _, n := range []int{3, 8} {
		if err := nino.DeserializeInto(mustSerialize(t, list(n), WritePointList), &target, ReadPointListInto); err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(target, list(n)) {
			t.Fatalf("%d elements: got %+v", n, target.Values)
		}
		if n != 0 && (&target.Values[:1][0] != storage || target.Values[0] != first) {
			t.Fatalf("%d elements: storage or elements not reused", n)
		}
	}
	// More elements than the storage holds: new storage, the old elements still reused.
	if err := nino.DeserializeInto(mustSerialize(t, list(20), WritePointList), &target, ReadPointListInto); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(target, list(20)) || target.Values[0] != first {
		t.Fatal("grown slice")
	}
	// None: an empty slice, not nil (nil is null).
	if err := nino.DeserializeInto(mustSerialize(t, list(0), WritePointList), &target, ReadPointListInto); err != nil {
		t.Fatal(err)
	}
	if target.Values == nil || len(target.Values) != 0 {
		t.Fatalf("empty: %#v", target.Values)
	}
}

// TestDeserializeIntoPolymorphic reuses a slot's object only for the type the data holds.
func TestDeserializeIntoPolymorphic(t *testing.T) {
	target := &RedLink{Next: &RedLink{Value: 1}}
	inner := target.Next
	same := &RedLink{Value: 2, Next: &RedLink{Value: 3}}
	if err := nino.DeserializeInto(mustSerialize(t, same, WriteRedLink), &target, ReadRedLinkInto); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(target, same) || target.Next != inner {
		t.Fatal("same type not read into")
	}
	other := &RedLink{Value: 4, Next: &BlueLink{Value: 5, Note: str("b")}}
	if err := nino.DeserializeInto(mustSerialize(t, other, WriteRedLink), &target, ReadRedLinkInto); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(target, other) {
		t.Fatalf("got %+v", target)
	}
}

// TestDeserializeIntoFailure leaves a value that can be written and read into again.
func TestDeserializeIntoFailure(t *testing.T) {
	good := mustSerialize(t, outerValue("name"), WriteOuter)
	for n := range len(good) {
		target := outerValue("x")
		if err := nino.DeserializeInto(good[:n], &target, ReadOuterInto); err == nil {
			t.Fatalf("prefix %d read", n)
		}
		mustSerialize(t, target, WriteOuter)
		if err := nino.DeserializeInto(good, &target, ReadOuterInto); err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(target, outerValue("name")) {
			t.Fatalf("prefix %d: then %+v", n, target)
		}
	}
}

// TestDeserializeIntoDeep reads bodies deeper than 128 later into the same objects.
func TestDeserializeIntoDeep(t *testing.T) {
	chain := func(n int, base int32) *TolerantChain {
		var c *TolerantChain
		for v := n; v >= 1; v-- {
			c = &TolerantChain{Value: int32(v) + base, Next: c}
		}
		return c
	}
	data := mustSerialize(t, chain(400, 0), WriteTolerantChain)
	target := chain(300, 1000)
	if err := nino.DeserializeInto(data, &target, ReadTolerantChainInto); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(target, chain(400, 0)) {
		t.Fatal("deep chain")
	}
}
