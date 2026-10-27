package nino

import (
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
