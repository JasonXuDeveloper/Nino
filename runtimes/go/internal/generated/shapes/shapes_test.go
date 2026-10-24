package shapes

import (
	"reflect"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go"
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
