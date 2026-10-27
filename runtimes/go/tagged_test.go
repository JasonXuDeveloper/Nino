package nino

import (
	"bytes"
	"cmp"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"math"
	"reflect"
	"slices"
	"strings"
	"testing"
)

// Spec appendix A.1.
type tagSmall struct {
	A int32   `nino:"0"`
	B *string `nino:"1"`
}

func (tagSmall) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xF8B7D639; members=A:int:raw(4);B:string:string"
}

// Spec appendix A.3, keyed.
type tagKeyed struct {
	ID    int32   `nino:"Id"`
	Name  *string `nino:"Name,former=Title"`
	Score int64   `nino:"Score"`
}

func (tagKeyed) NinoLayout() string {
	return "format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0xA333A1B0; members=Id:int:raw(4):key=0x98C41948;Name:string:string:key=0x3085B3E2:former=0x93A29D95;Score:long:raw(8):key=0x044AB14D"
}

// The same type with no layout: keys are the hashes of the names.
type tagKeyedBare struct {
	Score int64   `nino:"Score"`
	ID    int32   `nino:"Id"`
	Name  *string `nino:"Name,former=Title"`
}

// Spec appendix A.3, version tolerant.
type tagTolLeaf struct {
	A int32 `nino:"0"`
	B int32 `nino:"1"`
}

func (tagTolLeaf) NinoLayout() string { return "format=VersionTolerant; typeId=0x2B463077" }

type tagTolOnlyA struct {
	A int32 `nino:"0"`
}

func (tagTolOnlyA) NinoLayout() string { return "format=VersionTolerant" }

type tagTolBase struct {
	A int32
}

func (tagTolBase) NinoLayout() string {
	return "format=VersionTolerant; header=TypeId; typeId=0x9A63F3D2"
}

func str(s string) *string { return &s }

func mustHex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(strings.ReplaceAll(s, " ", ""))
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func bothModes(t *testing.T, f func(t *testing.T)) {
	for _, fast := range []bool{true, false} {
		name := "safe"
		if fast {
			name = "unsafe"
		}
		t.Run(name, func(t *testing.T) {
			prev := unsafeAccess.Load()
			unsafeAccess.Store(fast)
			defer unsafeAccess.Store(prev)
			f(t)
		})
	}
}

func TestTagSpecExamples(t *testing.T) {
	bothModes(t, func(t *testing.T) {
		check := func(name string, v any, want string) {
			t.Helper()
			got, err := Marshal(v)
			if err != nil {
				t.Fatalf("%s: %v", name, err)
			}
			if w := mustHex(t, want); !bytes.Equal(got, w) {
				t.Fatalf("%s:\n got %X\nwant %X", name, got, w)
			}
			back := reflect.New(reflect.TypeOf(v))
			if err := UnmarshalExact(got, back.Interface()); err != nil {
				t.Fatalf("%s: read back: %v", name, err)
			}
			if !reflect.DeepEqual(back.Elem().Interface(), v) {
				t.Fatalf("%s: read back %#v, want %#v", name, back.Elem().Interface(), v)
			}
		}
		check("A.1", &tagSmall{A: 1, B: str("a")}, "01 01000000 0361")
		check("A.1 null", (*tagSmall)(nil), "00")
		keyed := "01 26000000 4819C498 04000000 01000000 E2B38530 02000000 036E 4DB14A04 08000000 0200000000000000"
		check("A.3 keyed", &tagKeyed{ID: 1, Name: str("n"), Score: 2}, keyed)
		check("A.3 keyed null name", &tagKeyed{ID: 1, Score: 2},
			"01 25000000 4819C498 04000000 01000000 E2B38530 01000000 00 4DB14A04 08000000 0200000000000000")
		check("A.3 tolerant", &tagTolLeaf{A: 1, B: 7}, "01 08000000 01000000 07000000")
		check("open class", &tagTolBase{A: 1}, "D2F3639A 04000000 01000000")

		// Without a layout the keys are recomputed, and the write order is the fields'.
		var bare *tagKeyedBare
		if err := Unmarshal(mustHex(t, keyed), &bare); err != nil || bare.ID != 1 || *bare.Name != "n" || bare.Score != 2 {
			t.Fatalf("bare keyed: %v %+v", err, bare)
		}
		// A former key reads into the member.
		former := mustHex(t, keyed)
		copy(former[17:], mustHex(t, "959DA293"))
		var k *tagKeyed
		if err := Unmarshal(former, &k); err != nil || *k.Name != "n" {
			t.Fatalf("former key: %v", err)
		}
		// A repeated key fails.
		dup := mustHex(t, "01 18000000 4819C498 04000000 01000000 4819C498 04000000 02000000")
		if err := Unmarshal(dup, &k); !errors.Is(err, ErrDuplicateKey) {
			t.Fatalf("repeated key: %v", err)
		}
		// A TolBase reader skips B.
		var base *tagTolOnlyA
		if err := Unmarshal(mustHex(t, "01 08000000 01000000 07000000"), &base); err != nil || base.A != 1 {
			t.Fatalf("tolerant skip: %v", err)
		}
	})
}

// Every kind of field, round-tripped, and checked against the hand-written calls.
type tagEnum int16

type tagRaw struct {
	A uint8
	B int64
}

// tagRawStruct declares itself a struct: a pointer to it is a Nullable.
type tagRawStruct tagRaw

func (tagRawStruct) NinoLayout() string { return "header=None" }

type tagLabel struct {
	Name   string `nino:"0"`
	Weight int32  `nino:"1"`
}

type tagAll struct {
	Flag    bool                    `nino:"0"`
	I8      int8                    `nino:"1"`
	U16     uint16                  `nino:"2"`
	F32     float32                 `nino:"3"`
	F64     float64                 `nino:"4"`
	Enum    tagEnum                 `nino:"5"`
	Dec     Decimal                 `nino:"6"`
	Guid    Guid                    `nino:"7"`
	When    DateTime                `nino:"8"`
	Off     DateTimeOffset          `nino:"9"`
	Text    string                  `nino:"10"`
	Utf     *string                 `nino:"11,utf8"`
	Bytes   []byte                  `nino:"12"`
	Ints    []int32                 `nino:"13"`
	Bools   []bool                  `nino:"14"`
	Raw     tagRaw                  `nino:"15"`
	Raws    []tagRaw                `nino:"16"`
	Label   tagLabel                `nino:"17"`
	OptInt  *int32                  `nino:"18"`
	OptRaw  *tagRaw                 `nino:"19"`
	Stride  []*int32                `nino:"20"`
	Map     map[string]int32        `nino:"21"`
	Set     map[int32]struct{}      `nino:"22"`
	Pairs   []Entry[int32, *string] `nino:"23"`
	Small   *tagSmall               `nino:"24"`
	OptVec  *tagRawStruct           `nino:"25"`
	Skipped int                     `nino:"-"`
	//lint:ignore U1000 an unexported field the tag path must ignore
	hidden int
}

func writeAllByHand(w *Writer, v *tagAll) {
	w.Present(true)
	w.Bool(v.Flag)
	w.I8(v.I8)
	w.U16(v.U16)
	w.F32(v.F32)
	w.F64(v.F64)
	w.I16(int16(v.Enum))
	w.Decimal(v.Dec)
	w.Guid(v.Guid)
	w.DateTime(v.When)
	w.DateTimeOffset(v.Off)
	w.String(v.Text)
	w.UTF8Ptr(v.Utf)
	w.ByteArray(v.Bytes)
	WritePrimitives(w, v.Ints)
	WriteBools(w, v.Bools)
	rawOf := func(r tagRaw) { w.U8(r.A); w.ExtendZero(7); w.I64(r.B) }
	rawOf(v.Raw)
	w.Length(len(v.Raws))
	for _, r := range v.Raws {
		rawOf(r)
	}
	w.String(v.Label.Name)
	w.I32(v.Label.Weight)
	if w.Present(v.OptInt != nil); v.OptInt != nil {
		w.I32(*v.OptInt)
	}
	// Without a layout saying header=None, a pointer to a struct is a class: its members, no padding.
	if w.Present(v.OptRaw != nil); v.OptRaw != nil {
		w.U8(v.OptRaw.A)
		w.I64(v.OptRaw.B)
	}
	w.Length(len(v.Stride))
	for _, p := range v.Stride {
		if p == nil {
			w.ExtendZero(8)
		} else {
			w.U8(1)
			w.ExtendZero(3)
			w.I32(*p)
		}
	}
	w.Length(len(v.Map)) // one key: no order to choose
	for k, x := range v.Map {
		w.String(k)
		w.I32(x)
	}
	w.Length(len(v.Set))
	for k := range v.Set {
		w.I32(k)
	}
	WriteEntries(w, v.Pairs, (*Writer).I32, (*Writer).StringPtr)
	if w.Present(v.Small != nil); v.Small != nil {
		w.I32(v.Small.A)
		w.StringPtr(v.Small.B)
	}
	if w.Present(v.OptVec != nil); v.OptVec != nil {
		rawOf(tagRaw(*v.OptVec))
	}
}

func sampleAll() *tagAll {
	seven := int32(7)
	return &tagAll{
		Flag: true, I8: -2, U16: 0xBEEF, F32: math.Float32frombits(0x7F800001), F64: -0.5, Enum: -3,
		Dec: Decimal{Flags: 1 << 16, Lo: 15}, Guid: Guid{1, 2, 3}, When: 0x48DC0B3F7ABFC080,
		Off: DateTimeOffset{UtcTicks: 638000000000000000, OffsetMinutes: -60}, Text: "héllo", Utf: str("é😀"),
		Bytes: []byte{1, 2, 3}, Ints: []int32{1, -1, 1 << 30}, Bools: []bool{true, false},
		Raw: tagRaw{1, 2}, Raws: []tagRaw{{3, 4}, {5, 6}}, Label: tagLabel{"x", 9}, OptInt: &seven,
		OptRaw: &tagRaw{7, 8}, OptVec: &tagRawStruct{9, 10}, Stride: []*int32{&seven, nil}, Map: map[string]int32{"k": 1},
		Set: map[int32]struct{}{5: {}}, Pairs: []Entry[int32, *string]{{1, str("a")}, {2, nil}},
		Small: &tagSmall{A: 1, B: str("b")}, Skipped: 99,
	}
}

func TestTagAllKinds(t *testing.T) {
	bothModes(t, func(t *testing.T) {
		v := sampleAll()
		got, err := Marshal(v)
		if err != nil {
			t.Fatal(err)
		}
		want, err := Serialize(v, writeAllByHand)
		if err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(got, want) {
			t.Fatalf("\n got %X\nwant %X", got, want)
		}
		var back *tagAll
		if err := UnmarshalExact(got, &back); err != nil {
			t.Fatal(err)
		}
		v.Skipped = 0
		if math.Float32bits(back.F32) != 0x7F800001 {
			t.Fatalf("signaling NaN: %08X", math.Float32bits(back.F32))
		}
		back.F32, v.F32 = 0, 0
		if !reflect.DeepEqual(back, v) {
			t.Fatalf("read back\n%+v\nwant\n%+v", back, v)
		}
		// Every truncation fails cleanly, never panics, and leaves the target alone.
		for n := 0; n < len(got); n++ {
			keep := back
			if err := Unmarshal(got[:n], &back); err == nil {
				t.Fatalf("truncated to %d: no error", n)
			}
			if back != keep {
				t.Fatal("a failed Unmarshal changed its target")
			}
		}
	})
}

// Self-nesting without a class body counts with EnterStruct, and a chain of classes defers like generated code.
type tagTree struct {
	Kids []tagTree `nino:"0"`
}

type tagChain struct {
	Value int32     `nino:"0"`
	Next  *tagChain `nino:"1"`
}

type tagRecordChain struct {
	Value int32           `nino:"0"`
	Next  *tagRecordChain `nino:"1"`
}

func (tagRecordChain) NinoLayout() string { return "deferrable=false" }

func TestTagDepth(t *testing.T) {
	bothModes(t, func(t *testing.T) {
		// 600 nested struct bodies: past MaxDepth.
		deep := mustHex(t, "")
		for i := 0; i < 600; i++ {
			deep = append(deep, 1, 0, 0, 0)
		}
		deep = append(deep, 0, 0, 0, 0)
		var tree tagTree
		if err := Unmarshal(deep, &tree); !errors.Is(err, ErrTooDeep) {
			t.Fatalf("deep tree: %v", err)
		}
		// A long chain round-trips, with the bytes of the hand-written codec (deferral past 128).
		var head *tagChain
		for i := 1000; i > 0; i-- {
			head = &tagChain{Value: int32(i), Next: head}
		}
		got, err := Marshal(head)
		if err != nil {
			t.Fatal(err)
		}
		var cls Class[tagChain]
		cls = Class[tagChain]{Deferrable: true,
			Read:  func(r *Reader, v *tagChain) { v.Value = r.I32(); v.Next = ReadSealed(r, &cls) },
			Write: func(w *Writer, v *tagChain) { w.I32(v.Value); WriteSealed(w, v.Next, &cls) }}
		want, _ := Serialize(head, func(w *Writer, v *tagChain) { WriteSealed(w, v, &cls) })
		if !bytes.Equal(got, want) {
			t.Fatal("chain bytes differ from the hand-written codec's")
		}
		var back *tagChain
		if err := UnmarshalExact(got, &back); err != nil {
			t.Fatal(err)
		}
		n := 0
		for p := back; p != nil; p = p.Next {
			n++
			if p.Value != int32(n) {
				t.Fatalf("node %d holds %d", n, p.Value)
			}
		}
		if n != 1000 {
			t.Fatalf("%d nodes", n)
		}
		// A record chain is never deferred: 512 bodies nest, 513 fail.
		build := func(n int) *tagRecordChain {
			var h *tagRecordChain
			for i := 0; i < n; i++ {
				h = &tagRecordChain{Value: int32(i), Next: h}
			}
			return h
		}
		if _, err := Marshal(build(512)); err != nil {
			t.Fatalf("512 records: %v", err)
		}
		if _, err := Marshal(build(513)); !errors.Is(err, ErrTooDeep) {
			t.Fatalf("513 records: %v", err)
		}
	})
}

// Polymorphism through registered types.
type tagAnimal interface{ isAnimal() }

type tagDog struct {
	Name  string `nino:"0"`
	Bones int32  `nino:"1"`
}

func (tagDog) NinoLayout() string { return "header=TypeId; typeId=0x11111111" }
func (*tagDog) isAnimal()         {}

type tagCat struct {
	Lives int32 `nino:"0"`
}

func (*tagCat) NinoTypeID() int32 { return 0x22222222 }
func (*tagCat) isAnimal()         {}

type tagVec struct{ X, Y float32 }

func (tagVec) NinoTypeID() int32 { return 0x33333333 }

type tagZoo struct {
	Star    tagAnimal   `nino:"0"`
	Animals []tagAnimal `nino:"1"`
	Any     any         `nino:"2"`
}

type tagNotRegistered struct{}

func (*tagNotRegistered) isAnimal() {}

func TestTagPolymorphism(t *testing.T) {
	if err := Register((*tagDog)(nil), (*tagCat)(nil), tagVec{}); err != nil {
		t.Fatal(err)
	}
	if err := Register((*tagDog)(nil)); err != nil {
		t.Fatalf("registering again: %v", err)
	}
	if err := RegisterID(0x11111111, (*tagCat)(nil)); err == nil {
		t.Fatal("two types with one id")
	}
	bothModes(t, func(t *testing.T) {
		z := &tagZoo{Star: &tagDog{"rex", 3}, Animals: []tagAnimal{&tagCat{9}, nil, &tagDog{"a", 1}}, Any: tagVec{1, 2}}
		got, err := Marshal(z)
		if err != nil {
			t.Fatal(err)
		}
		want := "01 11111111 07726578 03000000 03000000 22222222 09000000 00000000 11111111 0361 01000000 33333333 0000803F 00000040"
		if !bytes.Equal(got, mustHex(t, want)) {
			t.Fatalf("\n got %X\nwant %s", got, want)
		}
		var back *tagZoo
		if err := UnmarshalExact(got, &back); err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(back, z) {
			t.Fatalf("%+v", back)
		}
		// A registered type that is not an animal, in an animal slot: unknown.
		bad := mustHex(t, "01 33333333 0000803F 00000040 00000000 00000000")
		if err := Unmarshal(bad, &back); !errors.Is(err, ErrUnknownType) {
			t.Fatalf("struct in an animal slot: %v", err)
		}
		if err := Unmarshal(mustHex(t, "01 44444444"), &back); !errors.Is(err, ErrUnknownType) {
			t.Fatalf("unregistered id: %v", err)
		}
		if _, err := Marshal(&tagZoo{Star: &tagNotRegistered{}}); !errors.Is(err, ErrUnknownType) {
			t.Fatalf("writing an unregistered type: %v", err)
		}
		// A pointer to a type-id class takes its own id only.
		var dog *tagDog
		if err := Unmarshal(mustHex(t, "22222222 09000000"), &dog); !errors.Is(err, ErrUnknownType) {
			t.Fatalf("cat into a dog: %v", err)
		}
	})
}

// Layout raw structs: explicit offsets, Pack=1, Size.
type tagPacked struct {
	A uint8 `nino:"0"`
	B int64 `nino:"1"`
	C int16 `nino:"2"`
}

func (tagPacked) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=true; typeId=0xEF24AAA6; members=raw(11){A:byte@0:raw(1),B:long@1:raw(8),C:short@9:raw(2)}"
}

type tagSized struct {
	A int32
}

func (tagSized) NinoLayout() string { return "header=None; raw=true; members=raw(16){A:int@0:raw(4)}" }

type tagMixed struct {
	A uint8
	B int16
	C int32
	D uint8
}

type tagTuple struct {
	Item1 uint8
	Item2 int64
}

func (tagTuple) NinoLayout() string { return "raw=false" }

func TestTagRaw(t *testing.T) {
	bothModes(t, func(t *testing.T) {
		for _, c := range []struct {
			v    any
			want string
		}{
			{tagPacked{1, 2, 3}, "01 0200000000000000 0300"},
			{tagSized{5}, "05000000 000000000000000000000000"},
			{tagMixed{1, 2, 3, 4}, "01 00 0200 03000000 04 000000"},
			{tagTuple{1, 2}, "01 0200000000000000"},
			{[]tagMixed{{1, 2, 3, 4}}, "01000000 01 00 0200 03000000 04 000000"},
		} {
			got, err := Marshal(c.v)
			if err != nil {
				t.Fatal(err)
			}
			if !bytes.Equal(got, mustHex(t, c.want)) {
				t.Fatalf("%T:\n got %X\nwant %s", c.v, got, c.want)
			}
			back := reflect.New(reflect.TypeOf(c.v))
			if err := UnmarshalExact(got, back.Interface()); err != nil || !reflect.DeepEqual(back.Elem().Interface(), c.v) {
				t.Fatalf("%T: %v %+v", c.v, err, back.Elem().Interface())
			}
		}
	})
}

// Tag validation: each error names the field.
type (
	badDupPos struct {
		A int32 `nino:"0"`
		B int32 `nino:"0"`
	}
	badGap struct {
		A int32 `nino:"0"`
		B int32 `nino:"2"`
	}
	badUntagged struct {
		A int32 `nino:"0"`
		B int32
	}
	badMixed struct {
		A int32 `nino:"0"`
		B int32 `nino:"B"`
	}
	badInt struct {
		Count int `nino:"0"`
	}
	badOmit struct {
		A int32 `nino:"A,omitempty"`
	}
	badOption struct {
		A int32 `nino:"0,wat"`
	}
	badUTF8 struct {
		A int32 `nino:"0,utf8"`
	}
	badFormer struct {
		A int32 `nino:"0,former=B"`
	}
	badUnexported struct {
		//lint:ignore U1000 an unexported field with a tag: the error under test
		a int32 `nino:"0"`
	}
	badKeyCollision struct {
		A int32 `nino:"A"`
		B int32 `nino:"B,former=A"`
	}
	badArray struct {
		A [4]int32 `nino:"0"`
	}
	badChan struct {
		C chan int `nino:"0"`
	}
	badLayoutCount struct {
		A int32 `nino:"0"`
	}
	badLayoutEnc struct {
		A int64  `nino:"0"`
		B string `nino:"1"`
	}
	badLayoutKey struct {
		A int32 `nino:"Nope"`
	}
	badTracked     struct{ A int32 }
	badClassValue  struct{ S tagSmall }
	badLayoutParse struct{ A int32 }
	badTolerantGap struct {
		A int32 `nino:"0"`
		C int32 `nino:"2"`
	}
	badEmbeddedPtr struct {
		*tagRaw
	}
	badRawInt128 struct {
		A Int128
		B int32
	}
	badElements struct{ A int32 }
	badPtrPtr   struct {
		P **int32 `nino:"0"`
	}
)

func (badLayoutCount) NinoLayout() string { return "members=A:int:raw(4);B:int:raw(4)" }
func (badLayoutEnc) NinoLayout() string   { return "members=A:int:raw(4);B:string:string" }
func (badLayoutKey) NinoLayout() string   { return "format=Keyed; members=A:int:raw(4):key=0x00000041" }
func (badTracked) NinoLayout() string     { return "trackReferences=true" }
func (badLayoutParse) NinoLayout() string { return "format=Sideways" }
func (badTolerantGap) NinoLayout() string {
	return "format=VersionTolerant; members=A:int:raw(4);B:int:raw(4);C:int:raw(4)"
}
func (badElements) NinoLayout() string { return "members=[elements]::seq(raw(4));A:int:raw(4)" }

func TestTagErrors(t *testing.T) {
	for _, c := range []struct {
		v     any
		field string
		want  string
	}{
		{badDupPos{}, "B", "position 0 is also A's"},
		{badGap{}, "", "gap at 1"},
		{badUntagged{}, "B", "no position"},
		{badMixed{}, "", "mixed"},
		{badInt{}, "Count", "no fixed size"},
		{badOmit{}, "A", "omitempty"},
		{badOption{}, "A", "unknown option"},
		{badUTF8{}, "A", "utf8 applies"},
		{badFormer{}, "A", "keyed types only"},
		{badUnexported{}, "a", "unexported"},
		{badKeyCollision{}, "B", "is also A's"},
		{badArray{}, "A", "use a slice"},
		{badChan{}, "C", "no encoding"},
		{badLayoutCount{}, "", "1 fields for the 2 members"},
		{badLayoutEnc{}, "A", "layout member A is raw(4)"},
		{badLayoutKey{}, "A", `no member "Nope"`},
		{badTracked{}, "", "reference tracking"},
		{badClassValue{}, "S", "hold it through a pointer"},
		{badLayoutParse{}, "", "unknown format"},
		{badTolerantGap{}, "", "gap at 1"},
		{badEmbeddedPtr{}, "tagRaw", "embedded pointer"},
		{badRawInt128{}, "A", "needs its offsets"},
		{badElements{}, "", "collections"},
		{badPtrPtr{}, "P", "pointer to a ptr"},
	} {
		err := Check(reflect.TypeOf(c.v))
		var te *TagError
		if !errors.As(err, &te) {
			t.Errorf("%T: %v, want a TagError", c.v, err)
			continue
		}
		if te.Field != c.field || !strings.Contains(te.Detail, c.want) {
			t.Errorf("%T: %v (field %q), want field %q and %q", c.v, err, te.Field, c.field, c.want)
		}
		// The same error from Marshal and Unmarshal, before any byte.
		if _, merr := Marshal(c.v); merr == nil || merr.Error() != err.Error() {
			t.Errorf("%T: Marshal: %v", c.v, merr)
		}
	}
}

// A struct with unexported and skipped fields, embedded flattening, and untagged fields in order.
type tagBase struct {
	A int32
	B string
}

type tagDerived struct {
	tagBase
	C     int32
	cache int
	Skip  string `nino:"-"`
}

func TestTagEmbedded(t *testing.T) {
	got, err := Marshal(&tagDerived{tagBase: tagBase{1, "x"}, C: 2, cache: 5, Skip: "s"})
	if err != nil {
		t.Fatal(err)
	}
	if want := mustHex(t, "01 01000000 0378 02000000"); !bytes.Equal(got, want) {
		t.Fatalf("%X", got)
	}
}

// Plans built from many goroutines at once: one plan per type, the same bytes everywhere.
type tagConcurrent struct {
	A    int32                     `nino:"0"`
	Kids []tagConcurrent           `nino:"1"`
	Map  map[string]*tagConcurrent `nino:"2"`
}

func TestTagConcurrentPlans(t *testing.T) {
	v := &tagConcurrent{A: 1, Kids: []tagConcurrent{{A: 2}}, Map: map[string]*tagConcurrent{"x": {A: 3}}}
	want, err := Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	unsafeAccess.Store(false)
	defer unsafeAccess.Store(true)
	done := make(chan []byte)
	for i := 0; i < 8; i++ {
		go func() {
			b, _ := Marshal(v)
			var back *tagConcurrent
			if Unmarshal(b, &back) != nil {
				b = nil
			}
			done <- b
		}()
	}
	for i := 0; i < 8; i++ {
		if b := <-done; !bytes.Equal(b, want) {
			t.Fatalf("%X, want %X", b, want)
		}
	}
}

func TestParseHex32(t *testing.T) {
	for s, want := range map[string]int32{"0x0": 0, "0x7FFFFFFF": 0x7FFFFFFF, "0x80000000": -0x80000000,
		"0xffffffff": -1, "0xA": 10, "0X1f": 31} {
		if got, err := parseHex32(s); err != nil || got != want {
			t.Errorf("parseHex32(%q) = %d, %v; want %d", s, got, err, want)
		}
	}
	for _, s := range []string{"", "0x", "1F", "0x100000000", "0x1G", "0x-1", "0x+1", "0x 1"} {
		if _, err := parseHex32(s); err == nil {
			t.Errorf("parseHex32(%q) succeeded", s)
		}
	}
}

// Slices of strings, of a named string type too, read and written without a reflect.Value an element: the bytes of
// WriteSlice, nil kept apart from empty, and a slice longer than its first capacity.
type tagWord string

type tagStrings struct {
	Plain []string  `nino:"0"`
	Named []tagWord `nino:"1"`
	Nil   []string  `nino:"2"`
	Empty []string  `nino:"3"`
	Mixed []string  `nino:"4"`
}

func TestTagStrings(t *testing.T) {
	bothModes(t, func(t *testing.T) {
		v := &tagStrings{Named: []tagWord{"a", "", "crème", "строка 🎉"}, Empty: []string{}}
		for i := range 300 {
			v.Plain = append(v.Plain, strings.Repeat("s", i%70))
			v.Mixed = append(v.Mixed, []string{"string", "crème brûlée", "字符串 🎉", "\xff"}[i%4])
		}
		got, err := Marshal(v)
		if err != nil {
			t.Fatal(err)
		}
		want, err := Serialize(v, func(w *Writer, v *tagStrings) {
			w.Present(true)
			WriteSlice(w, v.Plain, (*Writer).String)
			WriteSlice(w, v.Named, func(w *Writer, s tagWord) { w.String(string(s)) })
			WriteSlice(w, v.Nil, (*Writer).String)
			WriteSlice(w, v.Empty, (*Writer).String)
			WriteSlice(w, v.Mixed, (*Writer).String)
		})
		if err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(got, want) {
			t.Fatalf("\n got %X\nwant %X", got, want)
		}
		var back *tagStrings
		if err := UnmarshalExact(got, &back); err != nil {
			t.Fatal(err)
		}
		for i := 3; i < len(v.Mixed); i += 4 {
			v.Mixed[i] = "\uFFFD" // not UTF-8: written as U+FFFD
		}
		if !reflect.DeepEqual(back, v) {
			t.Fatalf("read back\n%+v\nwant\n%+v", back, v)
		}
		if back.Nil != nil || back.Empty == nil {
			t.Fatal("nil and empty slices not kept apart")
		}
		for n := 0; n < len(got); n++ {
			if err := Unmarshal(got[:n], &back); err == nil {
				t.Fatalf("truncated to %d: no error", n)
			}
		}
	})
}

type tagMaps struct {
	IntString map[int32]string  `nino:"0"`
	LongLong  map[int64]int64   `nino:"1"`
	StrInt    map[string]int32  `nino:"2"`
	Named     map[tagWord]int32 `nino:"3"` // not a built-in type: through reflect
	Nil       map[int32]string  `nino:"4"`
	Empty     map[string]string `nino:"5"`
}

// TestTagMaps checks the maps of built-in types (read and written without reflect on the unsafe path) against
// generated code's calls, with keys in order, beside a map of a named type that takes reflect.
func TestTagMaps(t *testing.T) {
	bothModes(t, func(t *testing.T) {
		v := &tagMaps{IntString: map[int32]string{}, LongLong: map[int64]int64{}, StrInt: map[string]int32{},
			Named: map[tagWord]int32{}, Empty: map[string]string{}}
		for i := range 40 {
			v.IntString[int32(i*7919%101-50)] = strings.Repeat("crème ", i%5)
			v.LongLong[int64(i)<<40-int64(i)] = int64(-i)
			v.StrInt[strings.Repeat("k", i)] = int32(i)
			v.Named[tagWord(strings.Repeat("n", 40-i))] = int32(i)
		}
		got, err := Marshal(v)
		if err != nil {
			t.Fatal(err)
		}
		want, err := Serialize(v, func(w *Writer, v *tagMaps) {
			w.Present(true)
			writeSorted(w, v.IntString, (*Writer).I32, (*Writer).String)
			writeSorted(w, v.LongLong, (*Writer).I64, (*Writer).I64)
			writeSorted(w, v.StrInt, (*Writer).String, (*Writer).I32)
			writeSorted(w, v.Named, func(w *Writer, k tagWord) { w.String(string(k)) }, (*Writer).I32)
			w.Null()
			w.Length(0)
		})
		if err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(got, want) {
			t.Fatalf("\n got %X\nwant %X", got, want)
		}
		var back *tagMaps
		if err := UnmarshalExact(got, &back); err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(back, v) || back.Nil != nil || back.Empty == nil {
			t.Fatalf("read back\n%+v\nwant\n%+v", back, v)
		}
		for n := 0; n < len(got); n++ {
			if err := Unmarshal(got[:n], &back); err == nil {
				t.Fatalf("truncated to %d: no error", n)
			}
		}
		// A key twice.
		twice, _ := Serialize(v, func(w *Writer, _ *tagMaps) {
			w.Present(true)
			w.Length(2)
			w.I32(1)
			w.String("a")
			w.I32(1)
			w.String("b")
		})
		var dup *tagMaps
		if err := Unmarshal(twice, &dup); !errors.Is(err, ErrDuplicateKey) {
			t.Fatalf("a key twice: %v", err)
		}
	})
}

func writeSorted[K cmp.Ordered, V any](w *Writer, m map[K]V, wk func(*Writer, K), wv func(*Writer, V)) {
	keys := make([]K, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	slices.Sort(keys)
	w.Length(len(keys))
	for _, k := range keys {
		wk(w, k)
		wv(w, m[k])
	}
}

type tagTimes []DateTime

type tagBlocks struct {
	Bools    []bool     `nino:"0"`
	Times    tagTimes   `nino:"1"`
	Days     []DateOnly `nino:"2"`
	Clocks   []TimeOnly `nino:"3"`
	Prices   []Decimal  `nino:"4"`
	Ids      []Guid     `nino:"5"`
	Signed   []Int128   `nino:"6"`
	Unsigned []UInt128  `nino:"7"`
	Empty    []bool     `nino:"8"`
}

// Slices of bools and BlockValues: the bytes writing each element gives, read back, nil and empty kept, and an
// invalid value rejected, on both paths (the unsafe one reads and writes them as blocks).
func TestTagBlocks(t *testing.T) {
	bothModes(t, func(t *testing.T) {
		v := &tagBlocks{
			Bools: []bool{true, false, true}, Times: tagTimes{NewDateTime(638712864000000000, KindUtc), 0},
			Days: []DateOnly{MaxDateOnly}, Clocks: []TimeOnly{MaxTimeOnly, 0}, Prices: []Decimal{{Flags: 3 << 16, Lo: 7}},
			Ids: []Guid{{1, 2}}, Signed: []Int128{{Lo: 1, Hi: 2}}, Empty: []bool{},
		}
		got, err := Marshal(v)
		if err != nil {
			t.Fatal(err)
		}
		want, err := Serialize(v, func(w *Writer, v *tagBlocks) {
			w.Present(true)
			WriteSlice(w, v.Bools, (*Writer).Bool)
			WriteSlice(w, v.Times, (*Writer).DateTime)
			WriteSlice(w, v.Days, (*Writer).DateOnly)
			WriteSlice(w, v.Clocks, (*Writer).TimeOnly)
			WriteSlice(w, v.Prices, (*Writer).Decimal)
			WriteSlice(w, v.Ids, (*Writer).Guid)
			WriteSlice(w, v.Signed, (*Writer).I128)
			WriteSlice(w, v.Unsigned, (*Writer).U128)
			WriteSlice(w, v.Empty, (*Writer).Bool)
		})
		if err != nil || !bytes.Equal(got, want) {
			t.Fatalf("\n got %X\nwant %X (%v)", got, want, err)
		}
		var back *tagBlocks
		if err := UnmarshalExact(got, &back); err != nil || !reflect.DeepEqual(back, v) {
			t.Fatalf("read back %+v (%v)", back, err)
		}
		// The first DateTime's kind bits kept, its ticks past MaxTicks.
		bad := append([]byte(nil), got...)
		at := 1 + 4 + 3 + 4
		binary.LittleEndian.PutUint64(bad[at:], uint64(MaxTicks+1))
		if err := UnmarshalExact(bad, &back); !errors.Is(err, ErrInvalidData) {
			t.Fatalf("a DateTime past MaxTicks: %v", err)
		}
		bad = append([]byte(nil), got...)
		bad[1+4+1] = 2
		if err := UnmarshalExact(bad, &back); !errors.Is(err, ErrInvalidData) {
			t.Fatalf("a bool of 2: %v", err)
		}
	})
}
