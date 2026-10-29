package nino

import (
	"bytes"
	"encoding/binary"
	"math"
	"reflect"
	"testing"
	"unsafe"
)

// TestBoolBlocks checks the word-at-a-time bool paths against a byte at a time: every length to 80, a byte that is
// not 0 or 1 at every position (read), and a bool whose memory is not 0 or 1 at every position (write).
func TestBoolBlocks(t *testing.T) {
	for n := 0; n <= 80; n++ {
		v := make([]bool, n)
		for i := range v {
			v[i] = (i*7+n)%3 == 0
		}
		data := encode(t, func(w *Writer) { WriteBools(w, v) })
		for i, x := range v {
			if want := boolByte(x); data[4+i] != want {
				t.Fatalf("n=%d: byte %d is %d", n, i, data[4+i])
			}
		}
		r := NewReader(data)
		back := ReadBools(r)
		if r.Err() != nil || len(back) != n {
			t.Fatalf("n=%d: %v", n, r.Err())
		}
		for i := range v {
			if back[i] != v[i] {
				t.Fatalf("n=%d: element %d", n, i)
			}
		}
		for i := 0; i < n; i++ {
			for _, bad := range []byte{2, 0x80, 0xFF} {
				d := append([]byte(nil), data...)
				d[4+i] = bad
				expectCode(t, d, CodeInvalidData, func(r *Reader) { ReadBools(r) })
			}
			// A bool set through unsafe to a byte other than 0 or 1 is written as 1.
			raw := make([]bool, n)
			copy(raw, v)
			*(*byte)(unsafe.Pointer(&raw[i])) = 0x42
			d := encode(t, func(w *Writer) { WriteBools(w, raw) })
			for j := range raw {
				if want := boolByte(j == i || v[j]); d[4+j] != want {
					t.Fatalf("n=%d, set %d: byte %d is %d", n, i, j, d[4+j])
				}
			}
		}
	}
}

func boolByte(x bool) byte {
	if x {
		return 1
	}
	return 0
}

// rawPoint is a raw struct whose memory is its wire bytes (no padding): a float32, a bool and an int8 kind, an int16.
type rawPoint struct {
	X    float32
	Flag bool
	Kind int8
	Y    int16
}

const rawPointAsIs = unsafe.Sizeof(rawPoint{}) == 8 && unsafe.Offsetof(rawPoint{}.Flag) == 4 &&
	unsafe.Offsetof(rawPoint{}.Kind) == 5 && unsafe.Offsetof(rawPoint{}.Y) == 6

func decodeRawPoint(r *Reader, b []byte) rawPoint {
	_ = b[7]
	return rawPoint{
		X: math.Float32frombits(binary.LittleEndian.Uint32(b)), Flag: r.BoolAt(b[4]), Kind: int8(b[5]),
		Y: int16(binary.LittleEndian.Uint16(b[6:])),
	}
}

func encodeRawPoint(b []byte, v rawPoint) {
	_ = b[7]
	binary.LittleEndian.PutUint32(b, math.Float32bits(v.X))
	if v.Flag {
		b[4] = 1
	}
	b[5] = byte(v.Kind)
	binary.LittleEndian.PutUint16(b[6:], uint16(v.Y))
}

// TestRawBlocks checks that ReadRaw and WriteRaw, copying blocks, read and write what ReadFixed and WriteFixed do,
// element by element: every length to 40, a bool byte other than 0 or 1 in every element, and asIs false.
func TestRawBlocks(t *testing.T) {
	if !rawPointAsIs {
		t.Skip("rawPoint's memory is not its bytes here")
	}
	bools := []int{4}
	for n := 0; n <= 40; n++ {
		v := make([]rawPoint, n)
		for i := range v {
			v[i] = rawPoint{X: float32(i) * 1.5, Flag: i%3 == 1, Kind: int8(-i), Y: int16(i * 1000)}
		}
		want := encode(t, func(w *Writer) { WriteFixed(w, v, 8, encodeRawPoint) })
		for _, asIs := range []bool{true, false} {
			got := encode(t, func(w *Writer) { WriteRaw(w, v, 8, asIs, bools, nil, encodeRawPoint) })
			if !bytes.Equal(got, want) {
				t.Fatalf("n=%d asIs=%v: wrote %x, want %x", n, asIs, got, want)
			}
			r := NewReader(want)
			back := ReadRaw(r, 8, asIs, bools, decodeRawPoint)
			if r.Err() != nil || !reflect.DeepEqual(back, v) {
				t.Fatalf("n=%d asIs=%v: read %v (%v)", n, asIs, back, r.Err())
			}
			for i := 0; i < n; i++ {
				bad := append([]byte(nil), want...)
				bad[4+8*i+4] = 2
				expectCode(t, bad, CodeInvalidData, func(r *Reader) { ReadRaw(r, 8, asIs, bools, decodeRawPoint) })
			}
			// A bool set through unsafe to another byte is written as 0 or 1, never as that byte (the block path writes
			// 1; encodeRawPoint's test of the bool is the compiler's: arm64 tests its lowest bit).
			if n > 0 {
				odd := append([]rawPoint(nil), v...)
				*(*byte)(unsafe.Pointer(&odd[n-1].Flag)) = 0x42
				got := encode(t, func(w *Writer) { WriteRaw(w, odd, 8, asIs, bools, nil, encodeRawPoint) })
				if got[4+8*(n-1)+4] > 1 || asIs && rawPointAsIs && littleEndian && got[4+8*(n-1)+4] != 1 {
					t.Fatalf("n=%d asIs=%v: bool written as %d", n, asIs, got[4+8*(n-1)+4])
				}
			}
		}
	}
	expectBytes(t, encode(t, func(w *Writer) { WriteRaw[rawPoint](w, nil, 8, true, nil, nil, encodeRawPoint) }), "FFFFFFFF")
	if v := ReadRaw(NewReader(hx("FFFFFFFF")), 8, true, nil, decodeRawPoint); v != nil {
		t.Fatal(v)
	}
	expectCode(t, hx("02000000 0000000000000000"), CodeInvalidLength, func(r *Reader) { ReadRaw(r, 8, true, nil, decodeRawPoint) })
}

// rawMixed has padding: a byte after A and three after D (12 bytes, the wire's layout where int32 aligns to 4).
type rawMixed struct {
	A uint8
	B int16
	C int32
	D uint8
}

const rawMixedAsIs = unsafe.Sizeof(rawMixed{}) == 12 && unsafe.Offsetof(rawMixed{}.B) == 2 &&
	unsafe.Offsetof(rawMixed{}.C) == 4 && unsafe.Offsetof(rawMixed{}.D) == 8

func encodeRawMixed(b []byte, v rawMixed) {
	_ = b[11]
	b[0] = v.A
	binary.LittleEndian.PutUint16(b[2:], uint16(v.B))
	binary.LittleEndian.PutUint32(b[4:], uint32(v.C))
	b[8] = v.D
}

// rawWide has padding and is larger than clearPads' mask (40 bytes): byte by byte.
type rawWide struct {
	A uint8
	B int64
	C uint8
	D int64
	E int32
	F bool
}

const rawWideAsIs = unsafe.Sizeof(rawWide{}) == 40 && unsafe.Offsetof(rawWide{}.B) == 8 &&
	unsafe.Offsetof(rawWide{}.C) == 16 && unsafe.Offsetof(rawWide{}.D) == 24 && unsafe.Offsetof(rawWide{}.E) == 32 &&
	unsafe.Offsetof(rawWide{}.F) == 36

func encodeRawWide(b []byte, v rawWide) {
	_ = b[39]
	b[0] = v.A
	binary.LittleEndian.PutUint64(b[8:], uint64(v.B))
	b[16] = v.C
	binary.LittleEndian.PutUint64(b[24:], uint64(v.D))
	binary.LittleEndian.PutUint32(b[32:], uint32(v.E))
	b[36] = boolByte(v.F)
}

func decodeRawWide(r *Reader, b []byte) rawWide {
	_ = b[39]
	return rawWide{
		A: b[0], B: int64(binary.LittleEndian.Uint64(b[8:])), C: b[16], D: int64(binary.LittleEndian.Uint64(b[24:])),
		E: int32(binary.LittleEndian.Uint32(b[32:])), F: r.BoolAt(b[36]),
	}
}

// checkPadded writes v with its padding bytes set to garbage (through unsafe) with WriteRaw and checks the bytes are
// WriteFixed's, padding written as zeros.
func checkPadded[T any](t *testing.T, v []T, size int, asIs bool, bools, pads []int, enc func([]byte, T)) {
	t.Helper()
	for i := range v {
		p := unsafe.Slice((*byte)(unsafe.Pointer(&v[i])), size)
		for k, o := range pads {
			p[o] = byte(0xA5 + i + k)
		}
	}
	want := encode(t, func(w *Writer) { WriteFixed(w, v, size, enc) })
	got := encode(t, func(w *Writer) { WriteRaw(w, v, size, asIs, bools, pads, enc) })
	if !bytes.Equal(got, want) {
		t.Fatalf("%T, %d elements: wrote %x, want %x", v, len(v), got, want)
	}
}

// TestRawPadded checks WriteRaw on raw structs with padding against WriteFixed: every length to 40 (the mask covers 8
// elements), elements of 12 bytes (the mask) and 40 (byte by byte).
func TestRawPadded(t *testing.T) {
	for n := 0; n <= 40; n++ {
		m := make([]rawMixed, n)
		wide := make([]rawWide, n)
		for i := range m {
			m[i] = rawMixed{A: uint8(i), B: int16(-i), C: int32(i) * 7919, D: uint8(i >> 2)}
			wide[i] = rawWide{A: uint8(i), B: -int64(i) << 40, C: 0xFF, D: int64(i), E: int32(i) * 3, F: i%2 == 0}
		}
		checkPadded(t, m, 12, rawMixedAsIs, nil, []int{1, 9, 10, 11}, encodeRawMixed)
		checkPadded(t, wide, 40, rawWideAsIs, []int{36}, []int{1, 2, 3, 4, 5, 6, 7, 17, 18, 19, 20, 21, 22, 23, 37, 38, 39},
			encodeRawWide)
	}
}

// TestRawBools checks rawBools against a byte at a time: elements of 1 to 40 bytes, one to three bool offsets, every
// count to 13 (the steps of four and the rest), and a byte other than 0 or 1 at each bool of each element.
func TestRawBools(t *testing.T) {
	want := func(b []byte, size int, bools []int) bool {
		for e := 0; e+size <= len(b); e += size {
			for _, o := range bools {
				if b[e+o] > 1 {
					return false
				}
			}
		}
		return true
	}
	for size := 1; size <= 40; size++ {
		for _, bools := range [][]int{{0}, {size - 1}, {0, size / 2, size - 1}} {
			for n := 0; n <= 13; n++ {
				b := make([]byte, n*size)
				for i := range b {
					b[i] = byte(i * 37) // the bytes that are not bools are anything
				}
				for e := 0; e < n; e++ {
					for _, o := range bools {
						b[e*size+o] = byte(e & 1)
					}
				}
				if !rawBools(b, size, bools) {
					t.Fatalf("size %d bools %v n %d: valid rejected", size, bools, n)
				}
				for e := 0; e < n; e++ {
					for _, o := range bools {
						for _, bad := range []byte{2, 0x80, 0xFF} {
							old := b[e*size+o]
							b[e*size+o] = bad
							if got := rawBools(b, size, bools); got != want(b, size, bools) || got {
								t.Fatalf("size %d bools %v n %d: byte %d of element %d is %d, accepted", size, bools, n, o, e, bad)
							}
							b[e*size+o] = old
						}
					}
				}
			}
		}
	}
}
