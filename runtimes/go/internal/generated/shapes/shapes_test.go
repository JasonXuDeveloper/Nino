package shapes

import (
	"encoding/hex"
	"errors"
	"reflect"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The shapes the corpus does not declare (runtimes/csharp/tests/Nino.Codegen.Tests/Shapes.cs), as nino-gen writes them: compiled,
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

// Arrays copied as one block (nino.ReadBlock, nino.ReadRaw): the same bytes as element by element, and a bool of a
// raw struct other than 0 or 1 fails.
func TestBlockShapesRoundTrip(t *testing.T) {
	stamps := &Stamps{
		Times:    []nino.DateTime{nino.NewDateTime(638712864000000000, nino.KindUtc), 0},
		Days:     []nino.DateOnly{nino.MaxDateOnly, 1},
		Clocks:   []nino.TimeOnly{nino.MaxTimeOnly},
		Prices:   []nino.Decimal{{Flags: 2 << 16, Lo: 12345}},
		Ids:      []nino.Guid{{1, 2, 3}, {15: 9}},
		Signed:   []nino.Int128{{Lo: 1, Hi: ^uint64(0)}},
		Unsigned: []nino.UInt128{{Lo: 2, Hi: 3}},
		Marks:    []Marked{{Id: 1, On: true, Kind: 2, Code: -3}, {Id: -4, Kind: 5, Code: 6}},
	}
	data, err := nino.Serialize(stamps, WriteStamps)
	if err != nil {
		t.Fatal(err)
	}
	each, err := nino.Serialize(stamps, func(w *nino.Writer, v *Stamps) {
		w.Present(true)
		writeEach(w, v.Times, (*nino.Writer).DateTime)
		writeEach(w, v.Days, (*nino.Writer).DateOnly)
		writeEach(w, v.Clocks, (*nino.Writer).TimeOnly)
		writeEach(w, v.Prices, (*nino.Writer).Decimal)
		writeEach(w, v.Ids, (*nino.Writer).Guid)
		writeEach(w, v.Signed, (*nino.Writer).I128)
		writeEach(w, v.Unsigned, (*nino.Writer).U128)
		w.Length(len(v.Marks))
		for _, m := range v.Marks {
			encodeMarked(w.Extend(8), m)
		}
	})
	if err != nil || !reflect.DeepEqual(data, each) {
		t.Fatalf("wrote %x, element by element %x (%v)", data, each, err)
	}
	back, err := nino.DeserializeExact(data, ReadStamps)
	if err != nil || !reflect.DeepEqual(back, stamps) {
		t.Fatalf("read back %+v (%v)", back, err)
	}
	// The second Marked's bool is the data's fourth byte from the end.
	bad := append([]byte(nil), data...)
	bad[len(bad)-4] = 2
	if _, err := nino.DeserializeExact(bad, ReadStamps); !errors.Is(err, nino.ErrInvalidData) {
		t.Fatalf("a bool of 2 read: %v", err)
	}
}

func TestFixedCollections(t *testing.T) {
	ids := []nino.Guid{{1}, {2}, {3}}
	want := &FixedCollections{
		Ids: []nino.Entry[nino.Guid, Marked]{
			{Key: ids[0], Value: Marked{Id: 1, On: true, Kind: 2, Code: -3}},
			{Key: ids[1], Value: Marked{Id: -4, Kind: 5, Code: 6}},
		},
		UniqueIds: ids,
		Grid:      &NinoGrid[uint8]{Lengths: []int{1, 2}, Items: []uint8{0x80, 0xFF}},
		Pairs:     []nino.Entry[int32, int64]{{Key: 7, Value: -9}},
		Offsets:   []nino.DateTimeOffset{{UtcTicks: 638712864000000000, OffsetMinutes: -60}, {}},
	}
	got, err := nino.Serialize(want, WriteFixedCollections)
	if err != nil {
		t.Fatal(err)
	}
	scalar, err := nino.Serialize(want, func(w *nino.Writer, v *FixedCollections) {
		w.Present(true)
		w.Length(len(v.Ids))
		for _, e := range v.Ids {
			w.Guid(e.Key)
			w.I32(e.Value.Id)
			w.Bool(e.Value.On)
			w.U8(e.Value.Kind)
			w.I16(e.Value.Code)
		}
		writeEach(w, v.UniqueIds, (*nino.Writer).Guid)
		w.WriteShape(v.Grid.Lengths...)
		for _, e := range v.Grid.Items {
			w.U8(e)
		}
		w.Length(len(v.Pairs))
		for _, e := range v.Pairs {
			w.I32(e.Key)
			w.I64(e.Value)
		}
		writeEach(w, v.Offsets, (*nino.Writer).DateTimeOffset)
	})
	if err != nil || !reflect.DeepEqual(got, scalar) {
		t.Fatalf("fixed bytes %x, scalar bytes %x (%v)", got, scalar, err)
	}
	if v, err := nino.DeserializeExact(got, ReadFixedCollections); err != nil || !reflect.DeepEqual(v, want) {
		t.Fatalf("read back %+v (%v)", v, err)
	}
	const mapStart, pair = 5, 16 + 8
	bad := append([]byte(nil), got...)
	copy(bad[mapStart+pair:mapStart+pair+16], bad[mapStart:mapStart+16])
	if _, err := nino.DeserializeExact(bad, ReadFixedCollections); !errors.Is(err, nino.ErrDuplicateKey) {
		t.Fatalf("read duplicate Guid key: %v", err)
	}
	copy(bad, got)
	bad[mapStart+16+4] = 2
	if _, err := nino.DeserializeExact(bad, ReadFixedCollections); !errors.Is(err, nino.ErrInvalidData) {
		t.Fatalf("read invalid bool in raw map value: %v", err)
	}
	// The second DateTimeOffset has its UTC ticks at zero, so an offset of -1 minute is invalid.
	copy(bad, got)
	bad[len(bad)-2], bad[len(bad)-1] = 0xFF, 0xFF
	if _, err := nino.DeserializeExact(bad, ReadFixedCollections); !errors.Is(err, nino.ErrInvalidData) {
		t.Fatalf("read invalid DateTimeOffset: %v", err)
	}
	want.Offsets[1].OffsetMinutes = -1
	if _, err := nino.Serialize(want, WriteFixedCollections); !errors.Is(err, nino.ErrInvalidValue) {
		t.Fatalf("wrote invalid DateTimeOffset: %v", err)
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

// writeEach writes v one element at a time with write (nil is null): the reference the block writes are compared with.
func writeEach[T any](w *nino.Writer, v []T, write func(*nino.Writer, T)) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		write(w, e)
	}
}

// The bytes .NET writes for Shapes.Strides: a T?[] of a raw struct aligned to 4 by the auto layout of the tuple it
// holds (the flag, three bytes of padding, then the value), as a member and as a type argument.
func TestNullableStridesAsDotNetWritesThem(t *testing.T) {
	const want = "01" + "02000000" + "01000000" + "0100000002000000fcff0300" + "00000000000000000000000000000000" +
		"01" + "02000000" + "00000000000000000000000000000000" + "01000000" + "0100000002000000fcff0300"
	cell := nino.NullableSlotOf(TupleCell{A: 1, Inside: TupleInside{A: 2, T: NinoTuple2[uint8, int16]{Item1: 3, Item2: -4}}})
	strides := &Strides{
		Cells: []nino.NullableSlot[TupleCell]{cell, {}},
		Boxed: &Box[[]nino.NullableSlot[TupleCell]]{Value: []nino.NullableSlot[TupleCell]{{}, cell}},
	}
	data, err := nino.Serialize(strides, WriteStrides)
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(data); got != want {
		t.Fatalf("wrote\n %s\nwant %s", got, want)
	}
	back, err := nino.DeserializeExact(data, ReadStrides)
	if err != nil || !reflect.DeepEqual(back, strides) {
		t.Fatalf("read back: %v, %+v", err, back)
	}
}

// A raw T? is a slot of a fixed size as the element of every sequence (a queue, a stack, a linked list, a sorted set,
// a list interface, nested, a set inside type arguments), and a single value (a presence byte, then the value) in a
// dictionary and a tuple: the bytes .NET writes for the NullableSlots of runtimes/csharp/tests/Nino.Codegen.Tests/ModelTests.cs,
// NullableSlotsHex.
func TestNullableSlotsAsDotNetWritesThem(t *testing.T) {
	const want = "0102000000010000000500000000000000000000000200000000000000000000000000000000000000010000000000000007000000000000000200" +
		"00000000010903000000000000000000000001000000ffffffff010000000300000002000000010000000100000000000000000000000000000000" +
		"000000000000000000000003000000010000000100000001000000ffffffff00000000010200000001000000040000000000000000000000010100" +
		"000002000000000000000000000000000000000000000100000000000000feffffffffffffff020000000100000000020000000107000000000000" +
		"00010100000003000000010800000000000000020000000100000000020000000109000000"
	// Queue<int?> {5, null}: the two slots of an int?[]. Dictionary<int, long?> {1: null, 2: 7}: single values.
	const queue, dictionary = "0200000001000000050000000000000000000000", "02000000010000000002000000010700000000000000"
	// The elements of the sequences are slots; a dictionary's value and a tuple's item are single values, pointers.
	i32, i64 := nino.NullableSlotOf[int32], nino.NullableSlotOf[int64]
	seven, eight, nine := int64(7), int64(8), int32(9)
	value := &NullableSlots{
		Queue:       []nino.NullableSlot[int32]{i32(5), {}},
		Stack:       []nino.NullableSlot[int64]{{}, i64(7)},
		Linked:      []nino.NullableSlot[uint8]{{}, nino.NullableSlotOf[uint8](9)},
		Sorted:      []nino.NullableSlot[int32]{{}, i32(-1), i32(3)},
		ReadOnly:    []nino.NullableSlot[TupleCell]{nino.NullableSlotOf(TupleCell{A: 1}), {}},
		Nested:      [][]nino.NullableSlot[int32]{{i32(1)}, nil, {}},
		BoxedSet:    &Box[[]nino.NullableSlot[int32]]{Value: []nino.NullableSlot[int32]{i32(4), {}}},
		BoxedNested: &Box[[][]nino.NullableSlot[int64]]{Value: [][]nino.NullableSlot[int64]{{{}, i64(-2)}}},
		Map:         []nino.Entry[int32, *int64]{{Key: 1}, {Key: 2, Value: &seven}},
		BoxedMap:    &Box[[]nino.Entry[int32, *int64]]{Value: []nino.Entry[int32, *int64]{{Key: 3, Value: &eight}}},
		Tuples:      []NinoTuple2[int32, *int32]{{Item1: 1}, {Item1: 2, Item2: &nine}},
	}
	data, err := nino.Serialize(value, WriteNullableSlots)
	if err != nil {
		t.Fatal(err)
	}
	got := hex.EncodeToString(data)
	if got != want {
		t.Fatalf("wrote\n %s\nwant %s", got, want)
	}
	if got[2:2+len(queue)] != queue || got[430:430+len(dictionary)] != dictionary {
		t.Fatalf("the queue or the dictionary is not where .NET writes it: %s", got)
	}
	back, err := nino.DeserializeExact(data, ReadNullableSlots)
	if err != nil || !reflect.DeepEqual(back, value) {
		t.Fatalf("read back: %v, %+v", err, back)
	}
	// A presence byte other than 0 or 1 is rejected in every sequence: the queue's first slot (at 5), the sorted
	// set's second (at 77), the set's inside a type argument (at 158).
	for _, at := range []int{5, 77, 158} {
		bad := append([]byte(nil), data...)
		bad[at] = 2
		if _, err := nino.DeserializeExact(bad, ReadNullableSlots); err == nil {
			t.Fatalf("a presence of 2 at %d was read", at)
		}
	}
	// A set of slots rejects an element read twice: the sorted set's third slot made the second (-1), and the set
	// inside a type argument made two nulls (its first slot, 4, cleared).
	twice := append([]byte(nil), data...)
	copy(twice[85:93], data[77:85])
	nulls := append([]byte(nil), data...)
	clear(nulls[158:166])
	for name, bad := range map[string][]byte{"a value": twice, "null": nulls} {
		var e *nino.Error
		if _, err := nino.DeserializeExact(bad, ReadNullableSlots); !errors.As(err, &e) || e.Code != nino.CodeDuplicateKey {
			t.Fatalf("%s twice in a set: %v", name, err)
		}
	}
}

// The elements of arrays of two and three dimensions are slots too: the bytes .NET writes for the NullableGrids of
// ModelTests.cs, NullableGridsHex.
func TestNullableGridsAsDotNetWritesThem(t *testing.T) {
	const want = "0101000000020000000100000005000000000000000000000001000000020000000100000000000000000000000000000000000000010000000000" +
		"0000070000000000000002000000010000000000000000000000000000000000000001000000010000000000000000000000"
	value := &NullableGrids{
		Grid:  &NinoGrid[nino.NullableSlot[int32]]{Lengths: []int{1, 2}, Items: []nino.NullableSlot[int32]{nino.NullableSlotOf[int32](5), {}}},
		Cube:  &NinoGrid[nino.NullableSlot[int64]]{Lengths: []int{1, 2, 1}, Items: []nino.NullableSlot[int64]{{}, nino.NullableSlotOf[int64](7)}},
		Cells: &NinoGrid[nino.NullableSlot[TupleCell]]{Lengths: []int{2, 1}, Items: []nino.NullableSlot[TupleCell]{{}, nino.NullableSlotOf(TupleCell{A: 1})}},
	}
	data, err := nino.Serialize(value, WriteNullableGrids)
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(data); got != want {
		t.Fatalf("wrote\n %s\nwant %s", got, want)
	}
	back, err := nino.DeserializeExact(data, ReadNullableGrids)
	if err != nil || !reflect.DeepEqual(back, value) {
		t.Fatalf("read back: %v, %+v", err, back)
	}
	// The presence byte of the grid's first element (at 9) and of the cube's second (at 53).
	for _, at := range []int{9, 53} {
		bad := append([]byte(nil), data...)
		bad[at] = 0xff
		if _, err := nino.DeserializeExact(bad, ReadNullableGrids); err == nil {
			t.Fatalf("a presence of 0xff at %d was read", at)
		}
	}
}

// NinoNullable<T> is its memory wherever it is (a presence and a value of a T each), inside type arguments too: the
// bytes .NET writes for the NinoNullableArgs of runtimes/csharp/tests/Nino.Codegen.Tests/ModelTests.cs, NinoNullableArgsHex.
func TestNinoNullableAsDotNetWritesIt(t *testing.T) {
	const want = "0101000000000000000000000000010000020000000100000000000000010000000000000000000000000000000000000000000000010000000000" +
		"000000000000010000000000000000000000010100000007000000010100000000000000000000000000000000000100000000000f000000000000" +
		"0001020000000000000000000000000000000000000000000000000000000100000000000000000000000100000000000000000000000102000000" +
		"0100000000000000ffffffffffffffff00000000000000000000000000000000020000000100000004000000000000000000000002000000010000" +
		"000100000000000000000000000000e03f020000000000000000000000000000000000000009010000000000000000000000000000003322110055" +
		"4477668899aabbccddeeff"
	guid := nino.Guid{0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff}
	value := &NinoNullableArgsType{
		Enum:  nino.NinoNullableOf(FlagsB),
		Enums: []nino.NinoNullable[Flags]{nino.NinoNullableOf(FlagsA), {}},
		Cell:  nino.NinoNullableOf(TupleCell{A: 1}),
		One:   &Box[nino.NinoNullable[int32]]{Value: nino.NinoNullableOf[int32](7)},
		Wide:  &Box[nino.NinoNullable[nino.Decimal]]{Value: nino.NinoNullableOf(nino.Decimal{Flags: 1 << 16, Lo: 15})},
		Array: &Box[[]nino.NinoNullable[TupleCell]]{Value: []nino.NinoNullable[TupleCell]{{}, nino.NinoNullableOf(TupleCell{A: 1})}},
		List:  &Box[[]nino.NinoNullable[int64]]{Value: []nino.NinoNullable[int64]{nino.NinoNullableOf[int64](-1), {}}},
		Set:   []nino.NinoNullable[int32]{nino.NinoNullableOf[int32](4), {}},
		Map:   []nino.Entry[int32, nino.NinoNullable[float64]]{{Key: 1, Value: nino.NinoNullableOf(0.5)}, {Key: 2}},
		Tuple: NinoTuple2[uint8, nino.NinoNullable[nino.Guid]]{Item1: 9, Item2: nino.NinoNullableOf(guid)},
	}
	data, err := nino.Serialize(value, WriteNinoNullableArgsType)
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(data); got != want {
		t.Fatalf("wrote\n %s\nwant %s", got, want)
	}
	back, err := nino.DeserializeExact(data, ReadNinoNullableArgsType)
	if err != nil || !reflect.DeepEqual(back, value) {
		t.Fatalf("read back: %v, %+v", err, back)
	}
	// What .NET's untrusted mode rejects: a byte above the presence's first (One, a type argument, at 78), and a
	// byte in a value that is absent (the second of Enums, at 37).
	for _, at := range []int{78 + 3, 37 + 8} {
		bad := append([]byte(nil), data...)
		bad[at] = 1
		if _, err := nino.DeserializeExact(bad, ReadNinoNullableArgsType); err == nil {
			t.Fatalf("a byte set at %d was read", at)
		}
	}
}

// NinoNullable<T> inside a raw struct, its memory there as everywhere: the presence a whole T (1 in its first byte),
// then the value, at 4 for an int and at 16 for a decimal. The bytes .NET writes for the values of
// runtimes/csharp/tests/Nino.Codegen.Tests/ModelTests.cs (NinoNullableRawHex, NinoNullableRawWideHex).
func TestNinoNullableInsideRawStructsAsDotNetWritesIt(t *testing.T) {
	narrow := &NinoNullableRawType{
		One:  HoldsNinoNullable{A: 1, B: nino.NinoNullableOf[int32](7)},
		Many: []HoldsNinoNullable{{A: 2}, {A: 3, B: nino.NinoNullableOf[int32](-1)}},
	}
	data, err := nino.Serialize(narrow, WriteNinoNullableRawType)
	if err != nil {
		t.Fatal(err)
	}
	if got, want := hex.EncodeToString(data), "01010000000100000007000000020000000200000000000000000000000300000001000000ffffffff"; got != want {
		t.Fatalf("wrote\n %s\nwant %s", got, want)
	}
	if back, err := nino.DeserializeExact(data, ReadNinoNullableRawType); err != nil || !reflect.DeepEqual(back, narrow) {
		t.Fatalf("read back: %v, %+v", err, back)
	}

	wide := &NinoNullableRawWideType{
		One:  HoldsWideNinoNullable{A: 1, B: nino.NinoNullableOf(nino.Decimal{Flags: 1 << 16, Lo: 15})},
		Many: []HoldsWideNinoNullable{{A: 2}},
	}
	data, err = nino.Serialize(wide, WriteNinoNullableRawWideType)
	if err != nil {
		t.Fatal(err)
	}
	const want = "0101000000000000000100000000000000000000000000000000000100000000000f000000000000000100" +
		"000002000000000000000000000000000000000000000000000000000000000000000000000000000000"
	if got := hex.EncodeToString(data); got != want {
		t.Fatalf("wrote\n %s\nwant %s", got, want)
	}
	if back, err := nino.DeserializeExact(data, ReadNinoNullableRawWideType); err != nil || !reflect.DeepEqual(back, wide) {
		t.Fatalf("read back: %v, %+v", err, back)
	}
	// A presence byte other than 0 or 1 is rejected there too.
	data[1+8] = 2
	if _, err := nino.DeserializeExact(data, ReadNinoNullableRawWideType); err == nil {
		t.Fatal("a presence byte of 2 was read")
	}
}
