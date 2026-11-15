package nino

import (
	"bytes"
	"encoding/binary"
	"math/rand"
	"reflect"
	"testing"
)

// The forms of a .NET NinoNullable<int>[] the block is measured and checked against: the element's bytes decoded
// and encoded one by one, as generated code writes the loop for an element that is no primitive, and the *int32 the
// value type replaced (an allocation an element).

func decodeNinoNullableI32(r *Reader, b []byte) NinoNullable[int32] {
	_ = b[7]
	if !r.NinoNullableAt(b[0:4], b[4:8]) {
		return NinoNullable[int32]{}
	}
	return NinoNullable[int32]{HasValue: true, Value: int32(binary.LittleEndian.Uint32(b[4:]))}
}

func encodeNinoNullableI32(b []byte, v NinoNullable[int32]) {
	_ = b[7]
	if v.HasValue {
		b[0] = 1
		binary.LittleEndian.PutUint32(b[4:], uint32(v.Value))
	}
}

func decodeNinoNullablePtrI32(r *Reader, b []byte) *int32 {
	_ = b[7]
	if !r.NinoNullableAt(b[0:4], b[4:8]) {
		return nil
	}
	x := int32(binary.LittleEndian.Uint32(b[4:]))
	return &x
}

func encodeNinoNullablePtrI32(b []byte, v *int32) {
	_ = b[7]
	if v != nil {
		b[0] = 1
		binary.LittleEndian.PutUint32(b[4:], uint32(*v))
	}
}

func readNinoNullableLoop(r *Reader) []NinoNullable[int32] {
	n := r.Length(8)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 8)
	if len(b) != n*8 {
		return nil
	}
	v := make([]NinoNullable[int32], 0, n)
	for len(b) >= 8 {
		e := decodeNinoNullableI32(r, b[:8:8])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[8:]
	}
	return v
}

func writeNinoNullableLoop(w *Writer, v []NinoNullable[int32]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 8)
	for len(b) >= 8 && len(v) > 0 {
		encodeNinoNullableI32(b[:8:8], v[0])
		b, v = b[8:], v[1:]
	}
}

func readNinoNullablePointers(r *Reader) []*int32 {
	n := r.Length(8)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 8)
	if len(b) != n*8 {
		return nil
	}
	v := make([]*int32, 0, n)
	for len(b) >= 8 {
		e := decodeNinoNullablePtrI32(r, b[:8:8])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[8:]
	}
	return v
}

func writeNinoNullablePointers(w *Writer, v []*int32) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 8)
	for len(b) >= 8 && len(v) > 0 {
		encodeNinoNullablePtrI32(b[:8:8], v[0])
		b, v = b[8:], v[1:]
	}
}

// ninoNullableBlock checks ReadNinoNullables and WriteNinoNullables of one primitive against NinoNullableAt on
// each element: the bytes of values (an absent one zeros whatever it holds), the values read back, and every
// single byte of the block changed.
func ninoNullableBlock[T Primitive](t *testing.T, size int, value func(i int) T) {
	t.Helper()
	var zero T
	values := make([]NinoNullable[T], 9)
	want := binary.LittleEndian.AppendUint32(nil, uint32(len(values)))
	for i := range values {
		element := make([]byte, 2*size)
		if i%3 != 2 {
			values[i] = NinoNullableOf(value(i))
			element[0] = 1
			encodePrimitives(element[size:], []T{values[i].Value})
		} else {
			values[i].Value = value(i) // not written: absent
		}
		want = append(want, element...)
	}
	w := NewWriter(0)
	WriteNinoNullables(w, values)
	if w.Err() != nil || !bytes.Equal(w.Bytes(), want) {
		t.Fatalf("size %d: wrote %X (%v), want %X", size, w.Bytes(), w.Err(), want)
	}
	for i := range values {
		if !values[i].HasValue {
			values[i].Value = zero
		}
	}
	r := NewReader(want)
	if got := ReadNinoNullables[T](r); r.Err() != nil || r.Remaining() != 0 || !reflect.DeepEqual(got, values) {
		t.Fatalf("size %d: read %v (%v), want %v", size, got, r.Err(), values)
	}
	for at := 4; at < len(want); at++ {
		for _, bit := range []byte{1, 2, 0x80} {
			bad := bytes.Clone(want)
			bad[at] ^= bit
			e := bad[4+(at-4)/(2*size)*2*size:][:2*size]
			valid := NewReader(nil)
			valid.NinoNullableAt(e[:size], e[size:])
			r := NewReader(bad)
			got := ReadNinoNullables[T](r)
			if (r.Err() == nil) != (valid.Err() == nil) {
				t.Fatalf("size %d, byte %d ^ %02X: block %v, element %v", size, at, bit, r.Err(), valid.Err())
			}
			if r.Err() != nil && (got != nil || r.Err().(*Error).Code != valid.Err().(*Error).Code) {
				t.Fatalf("size %d, byte %d ^ %02X: %v, %v, want %v", size, at, bit, got, r.Err(), valid.Err())
			}
		}
	}
}

func TestNinoNullables(t *testing.T) {
	ninoNullableBlock(t, 1, func(i int) uint8 { return uint8(0xF0 + i) })
	ninoNullableBlock(t, 1, func(i int) int8 { return int8(-1 - i) })
	ninoNullableBlock(t, 2, func(i int) int16 { return int16(-0x1234 - i) })
	ninoNullableBlock(t, 2, func(i int) uint16 { return 0xFEDC + uint16(i) })
	ninoNullableBlock(t, 4, func(i int) int32 { return int32(-0x12345678 - i) })
	ninoNullableBlock(t, 4, func(i int) uint32 { return 0xFEDCBA98 + uint32(i) })
	ninoNullableBlock(t, 4, func(i int) float32 { return -1.5 - float32(i) })
	ninoNullableBlock(t, 8, func(i int) int64 { return -0x123456789ABCDEF0 - int64(i) })
	ninoNullableBlock(t, 8, func(i int) uint64 { return 0xFEDCBA9876543210 + uint64(i) })
	ninoNullableBlock(t, 8, func(i int) float64 { return -2.5 - float64(i) })

	// Null, empty, and a length past the data.
	w := NewWriter(0)
	WriteNinoNullables[int32](w, nil)
	WriteNinoNullables(w, []NinoNullable[int32]{})
	if !bytes.Equal(w.Bytes(), []byte{0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0}) {
		t.Fatalf("null and empty: %X", w.Bytes())
	}
	r := NewReader(w.Bytes())
	if v := ReadNinoNullables[int32](r); v != nil || r.Err() != nil {
		t.Fatalf("null: %v, %v", v, r.Err())
	}
	if v := ReadNinoNullables[int32](r); v == nil || len(v) != 0 || r.Err() != nil {
		t.Fatalf("empty: %v, %v", v, r.Err())
	}
	r = NewReader([]byte{2, 0, 0, 0, 1, 0, 0, 0, 5, 0, 0, 0})
	if v := ReadNinoNullables[int32](r); v != nil || r.Err() == nil {
		t.Fatalf("a length past the data: %v, %v", v, r.Err())
	}

	// The element-by-element loop of generated code gives the same values and bytes.
	values := []NinoNullable[int32]{NinoNullableOf[int32](5), {}, NinoNullableOf[int32](-1)}
	block, loop := NewWriter(0), NewWriter(0)
	WriteNinoNullables(block, values)
	writeNinoNullableLoop(loop, values)
	if !bytes.Equal(block.Bytes(), loop.Bytes()) {
		t.Fatalf("block %X, loop %X", block.Bytes(), loop.Bytes())
	}
	if got := readNinoNullableLoop(NewReader(block.Bytes())); !reflect.DeepEqual(got, values) {
		t.Fatalf("loop read %v", got)
	}
}

// BenchmarkNinoNullables reads and writes 1000 NinoNullable<int>, one in four absent: as one block
// (ReadNinoNullables), element by element into the value type (the loop generated code writes for other elements),
// and as the []*int32 the value type replaced.
func BenchmarkNinoNullables(b *testing.B) {
	value := func(rnd *rand.Rand) NinoNullable[int32] {
		if rnd.Intn(4) == 0 {
			return NinoNullable[int32]{}
		}
		return NinoNullableOf(rnd.Int31())
	}
	blockBench(b, "block", value, WriteNinoNullables[int32], ReadNinoNullables[int32])
	blockBench(b, "loop", value, writeNinoNullableLoop, readNinoNullableLoop)
	blockBench(b, "pointers", func(rnd *rand.Rand) *int32 {
		v := value(rnd)
		if !v.HasValue {
			return nil
		}
		return &v.Value
	}, writeNinoNullablePointers, readNinoNullablePointers)
}

// The forms of a .NET int?[] the slots' block is measured and checked against: the *int32 an element generated code
// held before the slot type (an allocation each), decoded and encoded one by one.

func decodeSlotPtrI32(r *Reader, b []byte) *int32 {
	_ = b[7]
	if !r.BoolAt(b[0]) {
		return nil
	}
	x := int32(binary.LittleEndian.Uint32(b[4:]))
	return &x
}

func encodeSlotPtrI32(b []byte, v *int32) {
	_ = b[7]
	if v != nil {
		b[0] = 1
		binary.LittleEndian.PutUint32(b[4:], uint32(*v))
	}
}

func readSlotPointers(r *Reader) []*int32 {
	n := r.Length(8)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 8)
	if len(b) != n*8 {
		return nil
	}
	v := make([]*int32, 0, n)
	for len(b) >= 8 {
		e := decodeSlotPtrI32(r, b[:8:8])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[8:]
	}
	return v
}

func writeSlotPointers(w *Writer, v []*int32) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 8)
	for len(b) >= 8 && len(v) > 0 {
		encodeSlotPtrI32(b[:8:8], v[0])
		b, v = b[8:], v[1:]
	}
}

// nullableSlotBlock checks ReadNullableSlots and WriteNullableSlots of one primitive: the bytes of values (an absent
// one zeros whatever it holds), the values read back, and every single byte of the block changed: a presence byte
// other than 0 or 1 fails, padding is ignored, and an absent slot reads as the zero value whatever lies under it.
func nullableSlotBlock[T Primitive](t *testing.T, size int, value func(i int) T) {
	t.Helper()
	var zero T
	values := make([]NullableSlot[T], 9)
	want := binary.LittleEndian.AppendUint32(nil, uint32(len(values)))
	for i := range values {
		element := make([]byte, 2*size)
		if i%3 != 2 {
			values[i] = NullableSlotOf(value(i))
			element[0] = 1
			encodePrimitives(element[size:], []T{values[i].Value})
		} else {
			values[i].Value = value(i) // not written: absent
		}
		want = append(want, element...)
	}
	w := NewWriter(0)
	WriteNullableSlots(w, values)
	if w.Err() != nil || !bytes.Equal(w.Bytes(), want) {
		t.Fatalf("size %d: wrote %X (%v), want %X", size, w.Bytes(), w.Err(), want)
	}
	for i := range values {
		if !values[i].HasValue {
			values[i].Value = zero
		}
	}
	r := NewReader(want)
	if got := ReadNullableSlots[T](r); r.Err() != nil || r.Remaining() != 0 || !reflect.DeepEqual(got, values) {
		t.Fatalf("size %d: read %v (%v), want %v", size, got, r.Err(), values)
	}
	for at := 4; at < len(want); at++ {
		for _, bit := range []byte{1, 2, 0x80} {
			bad := bytes.Clone(want)
			bad[at] ^= bit
			index, offset := (at-4)/(2*size), (at-4)%(2*size)
			r := NewReader(bad)
			got := ReadNullableSlots[T](r)
			if offset == 0 && bad[at] > 1 {
				if got != nil || r.Err() == nil || r.Err().(*Error).Code != CodeInvalidData {
					t.Fatalf("size %d, presence %02X at %d: %v, %v", size, bad[at], at, got, r.Err())
				}
				continue
			}
			if r.Err() != nil || len(got) != len(values) {
				t.Fatalf("size %d, byte %d ^ %02X: %v", size, at, bit, r.Err())
			}
			for i, e := range got {
				switch {
				case i != index || offset > 0 && offset < size:
					// Another element, or padding: as written.
					if e != values[i] {
						t.Fatalf("size %d, byte %d ^ %02X: element %d is %v, want %v", size, at, bit, i, e, values[i])
					}
				case offset == 0:
					// The presence flipped: now absent (zero), or present with the bytes under it.
					if e.HasValue == values[i].HasValue || !e.HasValue && e.Value != zero {
						t.Fatalf("size %d, presence at %d flipped: element %d is %v", size, at, i, e)
					}
				case !values[i].HasValue && e != (NullableSlot[T]{}):
					t.Fatalf("size %d, byte %d ^ %02X under an absent slot: element %d is %v", size, at, bit, i, e)
				}
			}
		}
	}
}

func TestNullableSlots(t *testing.T) {
	nullableSlotBlock(t, 1, func(i int) uint8 { return uint8(0xF0 + i) })
	nullableSlotBlock(t, 1, func(i int) int8 { return int8(-1 - i) })
	nullableSlotBlock(t, 2, func(i int) int16 { return int16(-0x1234 - i) })
	nullableSlotBlock(t, 2, func(i int) uint16 { return 0xFEDC + uint16(i) })
	nullableSlotBlock(t, 4, func(i int) int32 { return int32(-0x12345678 - i) })
	nullableSlotBlock(t, 4, func(i int) uint32 { return 0xFEDCBA98 + uint32(i) })
	nullableSlotBlock(t, 4, func(i int) float32 { return -1.5 - float32(i) })
	nullableSlotBlock(t, 8, func(i int) int64 { return -0x123456789ABCDEF0 - int64(i) })
	nullableSlotBlock(t, 8, func(i int) uint64 { return 0xFEDCBA9876543210 + uint64(i) })
	nullableSlotBlock(t, 8, func(i int) float64 { return -2.5 - float64(i) })

	// Null, empty, and a length past the data.
	w := NewWriter(0)
	WriteNullableSlots[int32](w, nil)
	WriteNullableSlots(w, []NullableSlot[int32]{})
	if !bytes.Equal(w.Bytes(), []byte{0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0}) {
		t.Fatalf("null and empty: %X", w.Bytes())
	}
	r := NewReader(w.Bytes())
	if v := ReadNullableSlots[int32](r); v != nil || r.Err() != nil {
		t.Fatalf("null: %v, %v", v, r.Err())
	}
	if v := ReadNullableSlots[int32](r); v == nil || len(v) != 0 || r.Err() != nil {
		t.Fatalf("empty: %v, %v", v, r.Err())
	}
	r = NewReader([]byte{2, 0, 0, 0, 1, 0, 0, 0, 5, 0, 0, 0})
	if v := ReadNullableSlots[int32](r); v != nil || r.Err() == nil {
		t.Fatalf("a length past the data: %v, %v", v, r.Err())
	}

	// The pointers generated code held before give the same bytes and values.
	five := int32(5)
	values := []NullableSlot[int32]{NullableSlotOf(five), {}, NullableSlotOf[int32](-1)}
	block, pointers := NewWriter(0), NewWriter(0)
	WriteNullableSlots(block, values)
	writeSlotPointers(pointers, []*int32{&five, nil, &values[2].Value})
	if !bytes.Equal(block.Bytes(), pointers.Bytes()) {
		t.Fatalf("block %X, pointers %X", block.Bytes(), pointers.Bytes())
	}
	if got := readSlotPointers(NewReader(block.Bytes())); len(got) != 3 || *got[0] != 5 || got[1] != nil || *got[2] != -1 {
		t.Fatalf("pointers read %v", got)
	}
	if v, ok := NullableSlotOf("x").Get(); v != "x" || !ok {
		t.Fatalf("Get of a value: %q, %v", v, ok)
	}
	if v, ok := (NullableSlot[int32]{Value: 4}).Get(); v != 0 || ok {
		t.Fatalf("Get of an absent value: %d, %v", v, ok)
	}
}

// BenchmarkNullableSlots reads and writes 1000 int?, one in four absent, as the elements of a sequence: as one block
// of the slot type (ReadNullableSlots) and as the []*int32 it replaced.
func BenchmarkNullableSlots(b *testing.B) {
	value := func(rnd *rand.Rand) NullableSlot[int32] {
		if rnd.Intn(4) == 0 {
			return NullableSlot[int32]{}
		}
		return NullableSlotOf(rnd.Int31())
	}
	blockBench(b, "block", value, WriteNullableSlots[int32], ReadNullableSlots[int32])
	blockBench(b, "pointers", func(rnd *rand.Rand) *int32 {
		v := value(rnd)
		if !v.HasValue {
			return nil
		}
		return &v.Value
	}, writeSlotPointers, readSlotPointers)
}

// BenchmarkSlotSets reads 1000 distinct elements as a set, the check for an element read twice included: a
// HashSet<int?> (slots, one of them null; and with a zero beside the null, which checks the values present a second
// time) beside a HashSet<int> (the cost every set's check has), and both read as lists (no check).
func BenchmarkSlotSets(b *testing.B) {
	rnd := rand.New(rand.NewSource(7))
	ints := make([]int32, 1000)
	slots := make([]NullableSlot[int32], len(ints))
	for i, k := range rnd.Perm(1 << 20)[:len(ints)] {
		ints[i], slots[i] = int32(k), NullableSlotOf(int32(k))
	}
	slots[500] = NullableSlot[int32]{}
	w := NewWriter(0)
	WritePrimitives(w, ints)
	intData := bytes.Clone(w.Bytes())
	w.Reset()
	WriteNullableSlots(w, slots)
	slotData := bytes.Clone(w.Bytes())
	slots[0] = NullableSlotOf[int32](0)
	w.Reset()
	WriteNullableSlots(w, slots)
	zeroData := bytes.Clone(w.Bytes())
	run := func(name string, data []byte, read func(r *Reader) int) {
		b.Run(name, func(b *testing.B) {
			r := NewReader(nil)
			b.ReportAllocs()
			for i := 0; i < b.N; i++ {
				r.Reset(data)
				if read(r) != len(ints) || r.Err() != nil {
					b.Fatal(r.Err())
				}
			}
		})
	}
	run("int-set", intData, func(r *Reader) int { return len(CheckUniqueValues(r, ReadPrimitives[int32](r))) })
	run("slot-set", slotData, func(r *Reader) int { return len(CheckUniqueValues(r, ReadNullableSlots[int32](r))) })
	run("slot-set-null-and-zero", zeroData, func(r *Reader) int { return len(CheckUniqueValues(r, ReadNullableSlots[int32](r))) })
	run("int-list", intData, func(r *Reader) int { return len(ReadPrimitives[int32](r)) })
	run("slot-list", slotData, func(r *Reader) int { return len(ReadNullableSlots[int32](r)) })
}
