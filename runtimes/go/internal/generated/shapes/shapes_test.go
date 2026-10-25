package shapes

import (
	"reflect"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The shapes the corpus does not declare (tests/Nino.Codegen.Tests/Shapes.cs), as nino-gen writes them: compiled,
// and written and read back.
func TestShapesRoundTrip(t *testing.T) {
	s := func(v string) *string { return &v }
	four := int32(4)
	leaf := Recursive{Value: 2}
	holder := &Holder{
		Thing:     ThingStruct{Id: 1, Extra: -1},
		Things:    []IThing{nil, &ThingClass{Id: 3, Label: s("x")}},
		Tree:      Recursive{Value: 1, Children: []Recursive{leaf, leaf}},
		Inner:     &OuterInner{Value: 5},
		LeftItem:  &ShapesLeftItem{Left: 6},
		RightItem: &ShapesRightItem{Right: 7},
		Flags:     FlagsB,
		Gaps:      []Gappy{{Small: 7, Large: -7}},
		Names:     []nino.Entry[int32, *string]{{Key: 1, Value: s("one")}, {Key: 2}},
		Tags:      []*string{s("a"), s("b")},
		Pair:      NinoTuple2[int32, *string]{Item1: 5},
		Entry:     nino.Entry[*string, int32]{Key: s("k"), Value: 9},
		Maybe:     &four,
		Keywords:  &Keywords{Type: 1, Self: 2, Match: 3},
	}
	data, err := nino.Serialize(holder, WriteHolder)
	if err != nil {
		t.Fatal(err)
	}
	back, err := nino.DeserializeExact(data, ReadHolder)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(back, holder) {
		t.Fatalf("read back\n %+v\nwant %+v", back, holder)
	}

	renamed := &Renamed{NewName: 5, Other: s("o")}
	data, err = nino.Serialize(renamed, WriteRenamed)
	if err != nil {
		t.Fatal(err)
	}
	if r, err := nino.DeserializeExact(data, ReadRenamed); err != nil || !reflect.DeepEqual(r, renamed) {
		t.Fatalf("renamed: %v, %+v", err, r)
	}
}

// Generic types: closed uses read and written with the codecs built from those of their arguments.
func TestGenericShapesRoundTrip(t *testing.T) {
	s := func(v string) *string { return &v }
	seven := int32(7)
	uses := &Uses{
		Number: &Box[int32]{Value: -42},
		Text:   &Box[*string]{Value: s("text")},
		Pair:   Pair[int32, *string]{Key: 1, Value: s("one")},
		Nested: &Box[*Box[int32]]{Value: &Box[int32]{Value: 7}},
		Mixed:  &Box[Pair[*string, Gappy]]{Value: Pair[*string, Gappy]{Key: s("g"), Value: Gappy{Small: 1, Large: 2}}},
		Boxes:  []*Box[int32]{{Value: 1}, nil},
		Bag: &Bag[*Box[int64]]{
			Items:  []*Box[int64]{{Value: 5}, nil},
			ByName: []nino.Entry[string, *Box[int64]]{{Key: "a", Value: &Box[int64]{Value: 6}}},
			Inner:  &Box[*Box[int64]]{Value: &Box[int64]{Value: 8}},
			Pair:   Pair[int32, *Box[int64]]{Key: 3},
			Boxes:  []*Box[*Box[int64]]{nil},
			Tuple:  NinoTuple2[int32, *Box[int64]]{Item1: 4},
		},
		Chain:      &Node[*string]{Value: s("a"), Next: &Node[*string]{Value: s("b")}},
		Thing:      &Box[IThing]{Value: &ThingClass{Id: 3, Label: s("x")}},
		Array:      &Box[[]int32]{Value: []int32{1, 2}},
		Complex:    Pair[*Box[int32], []nino.Entry[int32, *string]]{Value: []nino.Entry[int32, *string]{{Key: 1}}},
		Tuple:      &Box[NinoTuple2[int32, *string]]{Value: NinoTuple2[int32, *string]{Item1: 9, Item2: s("nine")}},
		Maybe:      &Box[*int32]{Value: &seven},
		Entry:      &Box[nino.Entry[int32, *string]]{Value: nino.Entry[int32, *string]{Key: 2, Value: s("two")}},
		Set:        &Box[[]*string]{Value: []*string{s("x")}},
		IntKeys:    &Keys[int32]{Set: []int32{1, 2}, ByKey: []nino.Entry[int32, int32]{{Key: 3, Value: 4}}},
		StringKeys: &Keys[*string]{Set: []*string{s("k")}},
	}
	data, err := nino.Serialize(uses, WriteUses)
	if err != nil {
		t.Fatal(err)
	}
	back, err := nino.DeserializeExact(data, ReadUses)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(back, uses) {
		t.Fatalf("read back\n %+v\nwant %+v", back, uses)
	}
}

// Slots of generic types written behind type ids: a closed one (Open<int>), and an open one in a generic codec
// (OpenHolder<T>'s Open<T>), given the slot of Open<long> with the closed types the assembly never names.
func TestGenericSlotsRoundTrip(t *testing.T) {
	use := &OpenUse{Open: &MoreOpen[int32]{Value: 1, Extra: 2}}
	data, err := nino.Serialize(use, WriteOpenUse)
	if err != nil {
		t.Fatal(err)
	}
	if back, err := nino.DeserializeExact(data, ReadOpenUse); err != nil || !reflect.DeepEqual(back, use) {
		t.Fatalf("open use: %v, %+v", err, back)
	}
	holder := &OpenHolderUse{Holder: &OpenHolder[int64]{
		Inner: &MoreOpen[int64]{Value: 3, Extra: 4},
		All:   []OpenType[int64]{&Open[int64]{Value: 5}, nil, &MoreOpen[int64]{Value: 6}},
	}}
	data, err = nino.Serialize(holder, WriteOpenHolderUse)
	if err != nil {
		t.Fatal(err)
	}
	back, err := nino.DeserializeExact(data, ReadOpenHolderUse)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(back, holder) {
		t.Fatalf("read back\n %+v\nwant %+v", back, holder)
	}
	// The ids are those of the closed .NET names.
	if id := (&MoreOpen[int64]{}).NinoTypeID(); id != nino.TypeID("Shapes.Generic.MoreOpen<System.Int64>") {
		t.Fatalf("id %d", id)
	}
	// The id of a closed type of another argument is not one the slot of Open<long> allows.
	wrong := append([]byte{1, 1}, data[2:]...)
	copy(wrong[2:], le32(nino.TypeID("Shapes.Generic.MoreOpen<System.Int32>")))
	if _, err := nino.DeserializeExact(wrong, ReadOpenHolderUse); err == nil {
		t.Fatal("read a MoreOpen<int> in a slot of Open<long>")
	}
}

func le32(v int32) []byte { return []byte{byte(v), byte(v >> 8), byte(v >> 16), byte(v >> 24)} }
