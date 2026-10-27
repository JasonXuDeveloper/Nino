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
			got := encode(t, func(w *Writer) { WriteRaw(w, v, 8, asIs, bools, encodeRawPoint) })
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
			// A bool set through unsafe to another byte is written as 1.
			if n > 0 {
				odd := append([]rawPoint(nil), v...)
				*(*byte)(unsafe.Pointer(&odd[n-1].Flag)) = 0x42
				got := encode(t, func(w *Writer) { WriteRaw(w, odd, 8, asIs, bools, encodeRawPoint) })
				if got[4+8*(n-1)+4] != 1 {
					t.Fatalf("n=%d asIs=%v: bool written as %d", n, asIs, got[4+8*(n-1)+4])
				}
			}
		}
	}
	expectBytes(t, encode(t, func(w *Writer) { WriteRaw[rawPoint](w, nil, 8, true, nil, encodeRawPoint) }), "FFFFFFFF")
	if v := ReadRaw(NewReader(hx("FFFFFFFF")), 8, true, nil, decodeRawPoint); v != nil {
		t.Fatal(v)
	}
	expectCode(t, hx("02000000 0000000000000000"), CodeInvalidLength, func(r *Reader) { ReadRaw(r, 8, true, nil, decodeRawPoint) })
}
