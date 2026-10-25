package sample

import (
	"bytes"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"reflect"
	"strings"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// Depth, deferral, regions and polymorphism, checked against the spec's examples and by round trips, without the
// corpus.

func chain(n int) *Chain {
	var head *Chain
	for i := n; i >= 1; i-- {
		head = &Chain{Value: int32(i), Next: head}
	}
	return head
}

func tolerantChain(n int) *TolerantChain {
	var head *TolerantChain
	for i := n; i >= 1; i-- {
		head = &TolerantChain{Value: int32(i), Next: head}
	}
	return head
}

func keyedChain(n int) *KeyedChain {
	var head *KeyedChain
	for i := n; i >= 1; i-- {
		head = &KeyedChain{Value: int32(i), Next: head}
	}
	return head
}

func recordChain(n int) *RecordChain {
	var head *RecordChain
	for i := n; i >= 1; i-- {
		head = &RecordChain{Value: int32(i), Next: head}
	}
	return head
}

func roundTrip[T any](t *testing.T, v T, write func(*nino.Writer, T), read func(*nino.Reader) T) []byte {
	t.Helper()
	data, err := nino.Serialize(v, write)
	if err != nil {
		t.Fatalf("write: %v", err)
	}
	got, err := nino.DeserializeExact(data, read)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	if !reflect.DeepEqual(got, v) {
		t.Fatal("the value read differs from the value written")
	}
	again, err := nino.Serialize(got, write)
	if err != nil || !bytes.Equal(again, data) {
		t.Fatalf("written again: %v, same bytes %v", err, bytes.Equal(again, data))
	}
	return data
}

// TestChainDeferral checks the bytes of a positional chain past 128 (spec §7.10.2): nodes 1..128 inline, node
// 129's presence byte, then its body (and node 130) after the top-level value.
func TestChainDeferral(t *testing.T) {
	var want []byte
	for k := 1; k <= 128; k++ {
		want = append(want, 1)
		want = binary.LittleEndian.AppendUint32(want, uint32(k))
	}
	want = append(want, 1)                             // node 129: presence byte, body deferred
	want = binary.LittleEndian.AppendUint32(want, 129) // deferred body of node 129, at depth 1
	want = append(want, 1)
	want = binary.LittleEndian.AppendUint32(want, 130)
	want = append(want, 0)
	got := roundTrip(t, chain(130), WriteChain, ReadChain)
	if !bytes.Equal(got, want) {
		t.Fatalf("bytes\n got %x\nwant %x", got, want)
	}
	for _, n := range []int{1, 127, 128, 129, 300, 1000, 20000} {
		roundTrip(t, chain(n), WriteChain, ReadChain)
	}
}

func hx(s string) []byte {
	b, err := hex.DecodeString(strings.NewReplacer(" ", "", "|", "").Replace(s))
	if err != nil {
		panic(err)
	}
	return b
}

// TestTolerantChainSection checks a version-tolerant chain of 130 nodes against the example of spec §7.10.3 (the
// same shape: {int V; Next}): a section at the end of the outermost region.
func TestTolerantChainSection(t *testing.T) {
	got := roundTrip(t, tolerantChain(130), WriteTolerantChain, ReadTolerantChain)
	if len(got) != 1183 {
		t.Fatalf("%d bytes, want 1183", len(got))
	}
	if head := hx("01 | 9A 04 00 80 | 01 00 00 00 01 | 73 04 00 00 02 00 00 00 01 | 6A 04 00 00"); !bytes.HasPrefix(got, head) {
		t.Fatalf("starts %x", got[:len(head)])
	}
	node128 := hx("05 00 00 00 80 00 00 00 01")
	tail := hx("7C 04 00 00 | 12 00 00 00 | 0E 00 00 00 81 00 00 00 01 05 00 00 00 82 00 00 00 00 | 7C 04 00 00")
	if !bytes.HasSuffix(got, append(node128, tail...)) {
		t.Fatalf("ends %x", got[len(got)-len(node128)-len(tail):])
	}
	for _, n := range []int{128, 129, 300, 1000, 5000} {
		roundTrip(t, tolerantChain(n), WriteTolerantChain, ReadTolerantChain)
	}
}

func TestKeyedChainSection(t *testing.T) {
	got := roundTrip(t, keyedChain(130), WriteKeyedChain, ReadKeyedChain)
	// The outermost region's length has the section flag; the section ends with the members' length.
	if got[4]&0x80 == 0 {
		t.Fatalf("no section flag: %x", got[:8])
	}
	for _, n := range []int{129, 300, 2000} {
		roundTrip(t, keyedChain(n), WriteKeyedChain, ReadKeyedChain)
	}
}

// TestDeferralInsideRegion: a positional chain deep inside a version-tolerant root goes to the root's section.
func TestDeferralInsideRegion(t *testing.T) {
	name := "root"
	v := &TolerantRoot{Name: &name, Chain: chain(400), After: 7}
	data := roundTrip(t, v, WriteTolerantRoot, ReadTolerantRoot)
	if data[4]&0x80 == 0 {
		t.Fatal("no section flag")
	}
}

func TestPolymorphicChain(t *testing.T) {
	var head Link
	for i := 300; i >= 1; i-- {
		if i%2 == 0 {
			note := "n"
			head = &BlueLink{Value: int32(i), Next: head, Note: &note}
		} else {
			head = &RedLink{Value: int32(i), Next: head}
		}
	}
	roundTrip(t, head, WriteLink, ReadLink)
}

func TestTreeNodes(t *testing.T) {
	// Deep through lists: each level is a class body.
	node := &TreeNode{Value: 0}
	root := node
	for i := 1; i < 600; i++ {
		child := &TreeNode{Value: int32(i)}
		node.Children = []*TreeNode{child, nil}
		node = child
	}
	roundTrip(t, root, WriteTreeNode, ReadTreeNode)
}

// TestNonDeferrable: a record nests in place, up to 512 open bodies (the 513th would open at depth 512).
func TestNonDeferrable(t *testing.T) {
	roundTrip(t, recordChain(512), WriteRecordChain, ReadRecordChain)
	if _, err := nino.Serialize(recordChain(513), WriteRecordChain); !errors.Is(err, nino.ErrTooDeep) {
		t.Fatalf("513 records: %v", err)
	}
	// The bytes of 513, read back: 01 {i32, 01 ...} then 00.
	var data []byte
	for k := 1; k <= 513; k++ {
		data = append(data, 1)
		data = binary.LittleEndian.AppendUint32(data, uint32(k))
	}
	data = append(data, 0)
	if _, err := nino.Deserialize(data, ReadRecordChain); !errors.Is(err, nino.ErrTooDeep) {
		t.Fatalf("reading 513 records: %v", err)
	}
}

func TestSelfNestingCollection(t *testing.T) {
	var tree Tree = Tree{}
	for i := 0; i < 400; i++ {
		tree = Tree{tree, nil}
	}
	roundTrip(t, &TreeHolder{Tree: tree, DictTree: DictTree{{Key: "a", Value: DictTree{}}}}, WriteTreeHolder, ReadTreeHolder)
	for i := 0; i < 200; i++ {
		tree = Tree{tree}
	}
	if _, err := nino.Serialize(&TreeHolder{Tree: tree}, WriteTreeHolder); !errors.Is(err, nino.ErrTooDeep) {
		t.Fatalf("600 levels: %v", err)
	}
}

// TestCycle: an untracked cycle would be deferred forever; the writer finds it.
func TestCycle(t *testing.T) {
	for _, n := range []int{1, 2, 3, 200, 1000} {
		head := chain(n)
		last := head
		for last.Next != nil {
			last = last.Next
		}
		last.Next = head
		if _, err := nino.Serialize(head, WriteChain); !errors.Is(err, nino.ErrCycle) {
			t.Fatalf("cycle of %d: %v", n, err)
		}
		th := tolerantChain(n)
		tl := th
		for tl.Next != nil {
			tl = tl.Next
		}
		tl.Next = th
		if _, err := nino.Serialize(th, WriteTolerantChain); !errors.Is(err, nino.ErrCycle) {
			t.Fatalf("tolerant cycle of %d: %v", n, err)
		}
	}
	// A record never deferred: the cycle stops at the depth limit.
	r := &RecordChain{Value: 1}
	r.Next = r
	if _, err := nino.Serialize(r, WriteRecordChain); !errors.Is(err, nino.ErrTooDeep) {
		t.Fatalf("record cycle: %v", err)
	}
}

// TestSharedNotCycle: the same object twice (not a cycle) is written twice, deep or not.
func TestSharedNotCycle(t *testing.T) {
	shared := chain(200)
	v := &TolerantRoot{Chain: &Chain{Value: 0, Next: shared}}
	if _, err := nino.Serialize(v, WriteTolerantRoot); err != nil {
		t.Fatal(err)
	}
	z := &Zoo{Animals: []Animal{&Dog{Bones: 1}, &Dog{Bones: 1}}}
	roundTrip(t, z, WriteZoo, ReadZoo)
}

func TestPolymorphism(t *testing.T) {
	name := "x"
	z := &Zoo{
		Star:     &Puppy{Name: &name, Bones: 2, Sleepy: true},
		Dog:      &Puppy{Name: &name},
		Animals:  []Animal{&Dog{Bones: 3}, nil, &Cat{Lives: 9}, &BoxInt32{Value: 42}, &BoxString{Value: &name}, &BoxPoints{Value: []*Point{{1, 2}, nil}}, &BoxVec2{Value: Vec2{1, 2}}},
		Cages:    []Animal{nil},
		Shape:    &Square{Area: 4, Side: 2},
		Shapes:   []Shape{&Circle{Area: 3}, nil},
		Plain:    &Plain{Value: 1},
		Anything: &Point{1, 2},
		ByName:   []nino.Entry[string, Animal]{{Key: "d", Value: &Dog{}}, {Key: "n", Value: nil}},
	}
	roundTrip(t, z, WriteZoo, ReadZoo)

	// A type id the slot does not allow: a Cat in a Dog slot, an unknown id, the abstract base's id.
	for _, id := range []int32{idCat, idAnimal, 12345} {
		data := []byte{1}
		data = binary.LittleEndian.AppendUint32(data, uint32(idDog))
		data = append(data, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0)
		data = binary.LittleEndian.AppendUint32(data, uint32(id)) // the Dog slot
		if _, err := nino.Deserialize(data, ReadZoo); !errors.Is(err, nino.ErrUnknownType) {
			t.Fatalf("id %x in a Dog slot: %v", id, err)
		}
	}
	// object: only the allow-list.
	data := binary.LittleEndian.AppendUint32(nil, uint32(idTolerantLeaf))
	if _, err := nino.Deserialize(data, ReadObject); !errors.Is(err, nino.ErrUnknownType) {
		t.Fatalf("object: %v", err)
	}
	if _, err := nino.Serialize[nino.Typed](&TolerantLeaf{}, WriteObject); !errors.Is(err, nino.ErrUnknownType) {
		t.Fatalf("writing a type outside the allow-list: %v", err)
	}
	// A typed nil is null.
	if b, err := nino.Serialize[Animal]((*Dog)(nil), WriteAnimal); err != nil || !bytes.Equal(b, []byte{0, 0, 0, 0}) {
		t.Fatalf("typed nil: %x %v", b, err)
	}
	if b, err := nino.Serialize[nino.Typed]((*Point)(nil), WriteObject); err != nil || !bytes.Equal(b, []byte{0, 0, 0, 0}) {
		t.Fatalf("typed nil object: %x %v", b, err)
	}
}

func TestTypeIDs(t *testing.T) {
	for id, name := range typeNames {
		if got := nino.TypeID(name); got != id {
			t.Errorf("%s: id %x, computed %x", name, uint32(id), uint32(got))
		}
	}
	for key, name := range map[int32]string{
		keyKeyedV1Health: "Health", keyName: "Name", keyKeyedV1Items: "Items", keyScore: "Score", keyKeyedV2Hp: "Hp",
		keyKeyedV2Level: "Level", keyKeyedStructA: "A", keyKeyedStructB: "B", keyKeyedChainValue: "Value",
		keyKeyedChainNext: "Next",
	} {
		if got := nino.TypeID(name); got != key {
			t.Errorf("key %s: %x, computed %x", name, uint32(key), uint32(got))
		}
	}
}

func TestVersioning(t *testing.T) {
	b := "b"
	v1, err := nino.Serialize(&TolerantV1{A: 1, B: &b}, WriteTolerantV1)
	if err != nil {
		t.Fatal(err)
	}
	v2, err := nino.DeserializeExact(v1, ReadTolerantV2)
	if err != nil || v2.A != 1 || *v2.B != "b" || v2.C != 0 || v2.D != nil {
		t.Fatalf("v1 as v2: %+v %v", v2, err)
	}
	newer, _ := nino.Serialize(&TolerantV2{A: 2, B: &b, C: 3, D: []int32{4}}, WriteTolerantV2)
	old, err := nino.DeserializeExact(newer, ReadTolerantV1)
	if err != nil || old.A != 2 || *old.B != "b" {
		t.Fatalf("v2 as v1: %+v %v", old, err)
	}

	k1, _ := nino.Serialize(&KeyedV1{Health: 42, Name: &b, Score: 7}, WriteKeyedV1)
	k2, err := nino.DeserializeExact(k1, ReadKeyedV2)
	if err != nil || k2.Hp != 42 || k2.Score != 7 || *k2.Name != "b" {
		t.Fatalf("keyed v1 as v2: %+v %v", k2, err)
	}
	// A repeated key (here Health, then Hp, its new name) fails.
	w := nino.NewWriter(0)
	w.U8(1)
	g := w.BeginRegion()
	for _, key := range []int32{keyKeyedV1Health, keyKeyedV2Hp} {
		e := w.BeginEntry(key)
		w.I32(1)
		w.EndEntry(e)
	}
	w.EndRegion(g)
	if _, err := nino.Deserialize(w.Bytes(), ReadKeyedV2); !errors.Is(err, nino.ErrDuplicateKey) {
		t.Fatalf("repeated key: %v", err)
	}
	// An entry longer than its value fails; an unknown key is skipped.
	w.Reset()
	w.U8(1)
	g = w.BeginRegion()
	e := w.BeginEntry(12345)
	w.I64(1)
	w.EndEntry(e)
	e = w.BeginEntry(keyKeyedV2Level)
	w.I32(5)
	w.EndEntry(e)
	w.EndRegion(g)
	if v, err := nino.DeserializeExact(w.Bytes(), ReadKeyedV2); err != nil || v.Level != 5 {
		t.Fatalf("unknown key: %+v %v", v, err)
	}
	data := append([]byte(nil), w.Bytes()...)
	data[5+4+4+8+4] = 8 // Level's entry length: 8 for a 4-byte value (and 4 bytes past the region)
	if _, err := nino.Deserialize(data, ReadKeyedV2); err == nil {
		t.Fatal("an entry past its region read")
	}
}
