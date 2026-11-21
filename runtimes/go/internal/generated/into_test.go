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

// TestUnionClassCasesStayDistinct checks that batching class allocations keeps each pointer fresh, including
// when a Reader is reused and an outer slice is read into.
func TestUnionClassCasesStayDistinct(t *testing.T) {
	const count = 24
	points := func(x int32) *Unions {
		value := &Unions{Many: make([]Choice, count)}
		for i := range value.Many {
			value.Many[i] = &Point{X: x + int32(i)}
		}
		return value
	}
	r := nino.NewReader(mustSerialize(t, points(1), WriteUnions))
	value := ReadUnions(r)
	if err := r.Err(); err != nil {
		t.Fatal(err)
	}
	if value == nil || len(value.Many) != count {
		t.Fatalf("union sequence length changed: %+v", value)
	}
	earlier := make(map[*Point]int32)
	for i, c := range value.Many {
		p, ok := c.(*Point)
		if !ok || p == nil || p.X != 1+int32(i) {
			t.Fatalf("case %d: %T %+v", i, c, c)
		}
		earlier[p] = p.X
	}
	if len(earlier) != len(value.Many) {
		t.Fatal("union cases shared an instance")
	}

	r.Reset(mustSerialize(t, points(100), WriteUnions))
	ReadUnionsInto(r, &value)
	if err := r.Err(); err != nil {
		t.Fatal(err)
	}
	if value == nil || len(value.Many) != count {
		t.Fatalf("replacement union sequence length changed: %+v", value)
	}
	for i, c := range value.Many {
		p, ok := c.(*Point)
		_, reused := earlier[p]
		if !ok || p == nil || p.X != 100+int32(i) || reused {
			t.Fatalf("replacement case %d: %T %+v", i, c, c)
		}
	}
	for p, x := range earlier {
		if p.X != x {
			t.Fatalf("another decode overwrote a class from %d to %d", x, p.X)
		}
	}
}

func TestUnionPointerCasesOwnTheirValues(t *testing.T) {
	const count = 70
	choices := func(start int32) *Unions {
		value := &Unions{Number: &ChoiceInt32{Value: start}, Many: make([]Choice, count)}
		for i := range value.Many {
			value.Many[i] = &ChoiceInt32{Value: start + int32(i)}
		}
		return value
	}
	r := nino.NewReader(mustSerialize(t, choices(1), WriteUnions))
	value := ReadUnions(r)
	if r.Err() != nil || !reflect.DeepEqual(value, choices(1)) {
		t.Fatalf("first read: %v %#v", r.Err(), value)
	}
	earlier := append([]Choice{value.Number}, value.Many...)
	r.Reset(mustSerialize(t, choices(100), WriteUnions))
	ReadUnionsInto(r, &value)
	if r.Err() != nil || !reflect.DeepEqual(value, choices(100)) {
		t.Fatalf("replacement read: %v %#v", r.Err(), value)
	}
	for i, c := range earlier {
		old := c.(*ChoiceInt32)
		if old.Value != int32(max(1, i)) {
			t.Fatalf("case %d changed to %d after another decode", i, old.Value)
		}
		old.Value = -1
	}
	if !reflect.DeepEqual(value, choices(100)) {
		t.Fatal("retained case aliases a case from the replacement sequence")
	}
}

func TestNilUnionPointerWritesTheEmptyUnion(t *testing.T) {
	var integer *ChoiceInt32
	var point *Point
	var list *ChoiceListInt32
	for _, value := range []Choice{integer, point, list, &ChoiceListInt32{}, ChoiceString{}} {
		bytes := mustSerialize(t, value, WriteChoice)
		if !reflect.DeepEqual(bytes, []byte{0, 0, 0, 0}) {
			t.Fatalf("%T: got %x", value, bytes)
		}
	}
}

func TestRecursiveUnionUsesPointerCases(t *testing.T) {
	var root UnionTree = &UnionTreeInt64{Value: 1}
	for i := range 128 {
		root = &UnionTreeUnionTreeArray{Value: []UnionTree{root, nil, &UnionTreeInt64{Value: int64(i)}}}
	}
	data := mustSerialize(t, root, WriteUnionTree)
	got, err := nino.DeserializeExact(data, ReadUnionTree)
	if err != nil || !reflect.DeepEqual(got, root) {
		t.Fatalf("nested union: %v", err)
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
