package nino

import (
	"bytes"
	"math/rand"
	"strings"
	"testing"
	"unicode/utf8"
	"unsafe"
)

// The vector path for writing strings (where the platform has one) against the Go loops, byte for byte.

// wireChars are chars of every length and kind the vector path tells apart, and bytes that are not UTF-8.
var wireChars = []string{
	"a", "z", "\x00", "\x7f", // ASCII
	"\u0080", "é", "ÿ", // Latin-1 (C2, C3)
	"Ā", "Ж", "߿", // 2 bytes above U+00FF
	"ࠀ", "字", "퟿", "", "￿", // 3 bytes
	"\xed\xa0\x80", "\xed\xbf\xbf", "\xed\xb0\x80", // surrogates (WTF-8)
	"🎉", "\U00010000", "\U0010ffff", // 4 bytes
	"\x80", "\xbf", "\xc0\x80", "\xc1\xbf", "\xc2", "\xe0\x80\x80", "\xe0\x9f\xbf", "\xe4\xb8", "\xf0\x8f\xbf\xbf",
	"\xf4\x90\x80\x80", "\xf5\x80\x80\x80", "\xff", "\xf0\x9f\x8e", // not UTF-8
}

// wireTiers are the vector paths this processor has: 1 the first (AVX2), 2 the second (AVX-512).
func wireTiers() []int {
	switch {
	case hasWire512:
		return []int{1, 2}
	case hasWire:
		return []int{1}
	}
	return nil
}

// setWire takes the Go loops (tier 0) or a vector path; resetWire takes the one the processor takes.
func setWire(tier int) {
	useWire = tier > 0
	useWire512 = tier > 1
}

func resetWire() {
	useWire = hasWire
	useWire512 = hasWire512
}

// writeBoth writes s after a prefix of pad bytes with each vector path and without, into buffers of cap bytes or
// more, and fails when the bytes differ.
func writeBoth(t *testing.T, s string, pad, size int) {
	t.Helper()
	defer resetWire()
	write := func(tier int) []byte {
		setWire(tier)
		w := NewWriter(size)
		for range pad {
			w.U8(0xAB)
		}
		w.String(s)
		return w.Bytes()
	}
	want := write(0)
	for _, tier := range wireTiers() {
		if got := write(tier); !bytes.Equal(got, want) {
			t.Fatalf("%q (path %d, after %d bytes, buffer of %d): wrote\n% X\nwant\n% X", s, tier, pad, size, got, want)
		}
	}
}

func TestWireStringBlocks(t *testing.T) {
	if !hasWire {
		t.Skip("no vector path here")
	}
	rnd := rand.New(rand.NewSource(1))
	// Every length up to 256 of each pair of kinds, mixed at random, and ASCII with one char at each position.
	for n := 0; n <= 256; n++ {
		for _, a := range wireChars {
			for range 3 {
				var sb strings.Builder
				for sb.Len() < n {
					if rnd.Intn(3) == 0 {
						sb.WriteString(wireChars[rnd.Intn(len(wireChars))])
					} else {
						sb.WriteString(a)
					}
				}
				s := sb.String()
				writeBoth(t, s, rnd.Intn(5), 0)
				writeBoth(t, s, 0, 4+2*len(s)+64)
			}
		}
	}
	ascii := strings.Repeat("The quick brown fox jumps over. ", 9)
	for _, c := range wireChars {
		for at := 0; at <= len(ascii); at++ {
			writeBoth(t, ascii[:at]+c+ascii[at:], 0, 0)
		}
	}
}

func FuzzWireString(f *testing.F) {
	for _, c := range wireChars {
		f.Add([]byte("crème brûlée "+c), 3)
	}
	f.Fuzz(func(t *testing.T, b []byte, pad int) {
		if !hasWire {
			t.Skip("no vector path here")
		}
		writeBoth(t, string(b), pad&7, 0)
		if utf8.Valid(b) {
			writeBoth(t, string(b), 0, 4+2*len(b)+64)
		}
	})
}

// TestWriteStrings writes lists of strings of every kind (empty, nil, not UTF-8, long) with WriteStrings and
// WriteStringPtrs, with the vector path and without, into buffers of several sizes, against WriteSlice of each
// string.
func TestWriteStrings(t *testing.T) {
	rnd := rand.New(rand.NewSource(2))
	for round := 0; round < 300; round++ {
		count := rnd.Intn(80)
		if round == 0 {
			count = 0
		}
		strs := make([]string, count)
		ptrs := make([]*string, count)
		for k := range strs {
			var sb strings.Builder
			n := rnd.Intn(40)
			if rnd.Intn(20) == 0 {
				n = rnd.Intn(600)
			}
			base := wireChars[rnd.Intn(len(wireChars))]
			for sb.Len() < n {
				if rnd.Intn(4) == 0 {
					sb.WriteString(wireChars[rnd.Intn(len(wireChars))])
				} else {
					sb.WriteString(base)
				}
			}
			strs[k] = sb.String()
			if rnd.Intn(6) != 0 {
				ptrs[k] = &strs[k]
			}
		}
		size := []int{0, 16, 300, 1 << 16}[rnd.Intn(4)]
		check := func(name string, write func(w *Writer), want func(w *Writer)) {
			defer resetWire()
			setWire(0)
			ref := NewWriter(0)
			ref.U8(0xAB)
			want(ref)
			for _, tier := range append([]int{0}, wireTiers()...) {
				setWire(tier)
				w := NewWriter(size)
				w.U8(0xAB)
				write(w)
				if !bytes.Equal(w.Bytes(), ref.Bytes()) {
					t.Fatalf("%s (path %d, %d strings, buffer %d): wrote\n% X\nwant\n% X", name, tier, count, size, w.Bytes(), ref.Bytes())
				}
			}
		}
		check("WriteStrings", func(w *Writer) { WriteStrings(w, strs) }, func(w *Writer) { WriteSlice(w, strs, (*Writer).String) })
		check("WriteStringPtrs", func(w *Writer) { WriteStringPtrs(w, ptrs) }, func(w *Writer) { WriteSlice(w, ptrs, (*Writer).StringPtr) })
	}
	check := func(w *Writer) []byte { return w.Bytes() }
	w := NewWriter(0)
	WriteStrings(w, nil)
	WriteStringPtrs(w, nil)
	if got := check(w); !bytes.Equal(got, []byte{0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF}) {
		t.Fatalf("nil lists: % X", got)
	}
}

// wireUnits are UTF-16 code units of every kind the reader's vector path tells apart.
var wireUnits = []uint16{0, 'a', 0x7F, 0x80, 0xE9, 0xFF, 0x100, 0x416, 0x7FF, 0x800, 0x5B57, 0xD7FF, 0xD800, 0xDBFF,
	0xDC00, 0xDFFF, 0xE000, 0xFFFD, 0xFFFF}

// TestReadVector converts UTF-16 and Latin-1 text of every length up to 256 with the vector path and the Go loops.
func TestReadVector(t *testing.T) {
	if !hasRead {
		t.Skip("no vector path here")
	}
	rnd := rand.New(rand.NewSource(3))
	for n := 0; n <= 256; n++ {
		for round := 0; round < 40; round++ {
			src := make([]byte, 2*n)
			base := wireUnits[rnd.Intn(len(wireUnits))]
			for k := 0; k < n; k++ {
				u := base
				if rnd.Intn(3) == 0 {
					u = wireUnits[rnd.Intn(len(wireUnits))]
				}
				if rnd.Intn(8) == 0 {
					u = uint16(rnd.Intn(0x10000))
				}
				src[2*k], src[2*k+1] = byte(u), byte(u>>8)
			}
			want := make([]byte, 3*n)
			want = want[:utf16UTF8(want, src)]
			got := make([]byte, 3*n+64)
			got = got[:readUTF16(unsafe.Pointer(unsafe.SliceData(got)), unsafe.Pointer(unsafe.SliceData(src)), n)]
			if !bytes.Equal(got, want) {
				t.Fatalf("UTF-16 % X: wrote\n% X\nwant\n% X", src, got, want)
			}
			l1 := make([]byte, n)
			for k := range l1 {
				l1[k] = byte(rnd.Intn(256))
				if rnd.Intn(2) == 0 {
					l1[k] &= 0x7F
				}
			}
			want = make([]byte, 2*n)
			want = want[:latin1UTF8(want, l1)]
			got = make([]byte, 2*n+64)
			got = got[:readLatin1(unsafe.Pointer(unsafe.SliceData(got)), unsafe.Pointer(unsafe.SliceData(l1)), n)]
			if !bytes.Equal(got, want) {
				t.Fatalf("Latin-1 % X: wrote\n% X\nwant\n% X", l1, got, want)
			}
		}
	}
}

// FuzzReadVector converts any bytes as UTF-16 and as Latin-1 with the vector path and the Go loops.
func FuzzReadVector(f *testing.F) {
	f.Add([]byte("a\x00\x00\xd8\x00\xdc\xe9\x00"))
	f.Fuzz(func(t *testing.T, src []byte) {
		if !hasRead {
			t.Skip("no vector path here")
		}
		n := len(src) / 2
		want := make([]byte, 3*n)
		want = want[:utf16UTF8(want, src[:2*n])]
		got := make([]byte, 3*n+64)
		got = got[:readUTF16(unsafe.Pointer(unsafe.SliceData(got)), unsafe.Pointer(unsafe.SliceData(src)), n)]
		if !bytes.Equal(got, want) {
			t.Fatalf("UTF-16 % X: wrote\n% X\nwant\n% X", src, got, want)
		}
		want = make([]byte, 2*len(src))
		want = want[:latin1UTF8(want, src)]
		got = make([]byte, 2*len(src)+64)
		got = got[:readLatin1(unsafe.Pointer(unsafe.SliceData(got)), unsafe.Pointer(unsafe.SliceData(src)), len(src))]
		if !bytes.Equal(got, want) {
			t.Fatalf("Latin-1 % X: wrote\n% X\nwant\n% X", src, got, want)
		}
	})
}
