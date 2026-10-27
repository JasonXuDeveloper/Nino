package nino

import (
	"bytes"
	"math/rand"
	"strings"
	"testing"
	"unicode/utf8"
	"unsafe"
)

// The [NinoUtf8] paths against the plain ones they replace: oldUtf8Units validates rune by rune, oldUtf8 writes
// char by char (what Writer.Utf8 wrote before the one-pass scan).

func oldUtf8Units(b []byte, units int) bool {
	n := 0
	for i := 0; i < len(b); {
		r, size := utf8.DecodeRune(b[i:])
		if r == utf8.RuneError && size == 1 {
			return false
		}
		i += size
		n++
		if r >= 0x10000 {
			n++
		}
	}
	return n == units
}

func oldUtf8(s string) []byte {
	if s == "" {
		return []byte{1}
	}
	var body []byte
	units := 0
	for i := 0; i < len(s); {
		c, n := nextUnit(s, i)
		i += n
		units++
		if c >= 0x10000 {
			units++
		}
		if c >= 0xD800 && c < 0xE000 {
			c = utf8.RuneError
		}
		body = utf8.AppendRune(body, c)
	}
	out := refHeader(nil, uint64(units)+1)
	out = refHeader(out, uint64(len(body)))
	return append(out, body...)
}

// scanRef is utf8Scan's answer the plain way.
func scanRef(b []byte) (int, bool) {
	if !utf8.Valid(b) {
		return 0, false
	}
	n := 0
	for _, r := range string(b) {
		n++
		if r >= 0x10000 {
			n++
		}
	}
	return n, true
}

func checkUtf8(t testing.TB, s string) {
	t.Helper()
	b := []byte(s)
	want, wantOK := scanRef(b)
	got, ok := utf8Scan(b)
	if ok != wantOK || (ok && got != want) {
		t.Fatalf("utf8Scan(% x) = %d %v, want %d %v", b, got, ok, want, wantOK)
	}
	for _, units := range []int{want, want + 1, want - 1, len(b)} {
		if utf8Units(b, units) != oldUtf8Units(b, units) {
			t.Fatalf("utf8Units(% x, %d) = %v, want %v", b, units, !oldUtf8Units(b, units), oldUtf8Units(b, units))
		}
	}
	w := NewWriter(0)
	w.Utf8(s)
	if got := w.Bytes(); w.Err() != nil || !bytes.Equal(got, oldUtf8(s)) {
		t.Fatalf("Utf8(% x) wrote % x (%v), want % x", b, got, w.Err(), oldUtf8(s))
	}
}

// Every sequence of up to three bytes, and four-byte sequences around every lead byte.
func TestUtf8ScanExhaustive(t *testing.T) {
	var b [4]byte
	check := func(n int) {
		want, wantOK := scanRef(b[:n])
		got, ok := utf8Scan(b[:n])
		if ok != wantOK || (ok && got != want) {
			t.Fatalf("utf8Scan(% x) = %d %v, want %d %v", b[:n], got, ok, want, wantOK)
		}
	}
	for x := range 1 << 24 {
		b[0], b[1], b[2] = byte(x), byte(x>>8), byte(x>>16)
		check(1)
		check(2)
		check(3)
		if b[0] >= 0xF0 && b[0] <= 0xF5 {
			for _, c := range []byte{0x00, 0x7F, 0x80, 0x8F, 0x90, 0xBF, 0xC0, 0xFF} {
				b[3] = c
				check(4)
			}
		}
	}
}

// utf8Pieces are the parts randomUtf8 strings are made of: ASCII, 2-, 3- and 4-byte chars, and bytes that are not
// UTF-8 (unpaired and paired surrogates in WTF-8, overlong forms, values past U+10FFFF, stray and cut sequences).
var utf8Pieces = []string{
	"a", "string ", "0123456789abcdef", "é", "crème brûlée ", "ÿ", "строка ", "字符串", "€", "�", "￿",
	"🎉", "😀", "\U0010FFFF", "\U00010000",
	"\xED\xA0\x80", "\xED\xBF\xBF", "\xED\xA0\xBD\xED\xB8\x80", "\xC0\x80", "\xC1\xBF", "\xE0\x80\x80",
	"\xF0\x80\x80\x80", "\xF4\x90\x80\x80", "\xF5\x80\x80\x80", "\x80", "\xBF", "\xFF", "\xC3", "\xE5\xAD", "\xF0\x9F\x8E",
}

func randomUtf8(rnd *rand.Rand) string {
	var b []byte
	for n := rnd.Intn(40); n > 0; n-- {
		if rnd.Intn(8) == 0 {
			b = append(b, byte(rnd.Intn(256)))
			continue
		}
		b = append(b, utf8Pieces[rnd.Intn(len(utf8Pieces))]...)
	}
	return string(b)
}

func TestUtf8FastPath(t *testing.T) {
	for _, s := range utf8Pieces {
		checkUtf8(t, s)
		checkUtf8(t, s+s+s+s+s+s+s+s+s)
	}
	rnd := rand.New(rand.NewSource(1))
	for range 200000 {
		checkUtf8(t, randomUtf8(rnd))
	}
	// Valid text only, so the fast path is taken: and every byte of it read back.
	for range 20000 {
		s := randomUtf8(rnd)
		if !utf8.ValidString(s) {
			continue
		}
		w := NewWriter(0)
		w.Utf8(s)
		r := NewReader(w.Bytes())
		if got := r.Utf8(); got != s || r.Err() != nil {
			t.Fatalf("% x read back as % x (%v)", s, got, r.Err())
		}
	}
}

func FuzzUtf8(f *testing.F) {
	for _, s := range utf8Pieces {
		f.Add([]byte(s))
	}
	f.Fuzz(func(t *testing.T, b []byte) { checkUtf8(t, string(b)) })
}

// scanVec is the vector path of utf8Scan on b (any length but 0), as utf8Scan answers.
func scanVec(b []byte) (int, bool) {
	units := scanUTF8(unsafe.Pointer(unsafe.SliceData(b)), len(b))
	return units, units >= 0
}

// TestUtf8ScanVector compares the vector path of utf8Scan with the plain answer: every sequence of up to three bytes
// across the end of the first block and at the end of the text, each piece of utf8Pieces at every place in the
// first blocks, and random text.
func TestUtf8ScanVector(t *testing.T) {
	if !hasRead {
		t.Skip("no vector path here")
	}
	check := func(b []byte) {
		t.Helper()
		want, wantOK := scanRef(b)
		got, ok := scanVec(b)
		if ok != wantOK || (ok && got != want) {
			t.Fatalf("scanUTF8(% x) = %d %v, want %d %v", b, got, ok, want, wantOK)
		}
	}
	b := []byte(strings.Repeat("a", 40))
	steps := 1 << 24
	if testing.Short() {
		steps = 1 << 18
	}
	for _, at := range []int{13, 14, 15, 29} {
		for x := range steps {
			b[at], b[at+1], b[at+2] = byte(x), byte(x>>8), byte(x>>16)
			check(b[:at+3])
			if at == 13 {
				check(b[:at+3+4])
			}
		}
		b[at], b[at+1], b[at+2] = 'a', 'a', 'a'
	}
	for _, p := range utf8Pieces {
		for at := 0; at < 40; at++ {
			for after := 0; after < 20; after++ {
				check([]byte(strings.Repeat("a", at) + p + strings.Repeat("b", after)))
				check([]byte(strings.Repeat("é", at/2) + p + strings.Repeat("字", after/3)))
			}
		}
	}
	rnd := rand.New(rand.NewSource(7))
	for range 200000 {
		if s := randomUtf8(rnd); s != "" {
			check([]byte(s))
		}
	}
}
