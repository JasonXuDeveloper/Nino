package nino

import (
	"bytes"
	"encoding/binary"
	"math/rand"
	"strings"
	"testing"
	"unicode/utf16"
	"unicode/utf8"
)

// The reference: what .NET writes for a string of UTF-16 code units, and the units of a Go string read as WTF-8,
// each written plainly, char by char.

// refUnits returns the UTF-16 code units of s read as WTF-8: an unpaired surrogate's 3-byte form is that unit, any
// other byte that is not UTF-8 is U+FFFD.
func refUnits(s string) []uint16 {
	var units []uint16
	for i := 0; i < len(s); {
		r, size := utf8.DecodeRuneInString(s[i:])
		if r == utf8.RuneError && size == 1 && i+2 < len(s) && s[i] == 0xED && s[i+1]&0xE0 == 0xA0 &&
			s[i+2]&0xC0 == 0x80 {
			r, size = rune(0xD000|uint16(s[i+1]&0x3F)<<6|uint16(s[i+2]&0x3F)), 3
		}
		i += size
		if utf16.IsSurrogate(r) {
			units = append(units, uint16(r))
			continue
		}
		units = utf16.AppendRune(units, r)
	}
	return units
}

// refBytes returns the bytes .NET writes for a string of units: ~n then n bytes when every unit is at most 0xFF,
// else n then the units; 0 for the empty string.
func refBytes(units []uint16) []byte {
	if len(units) == 0 {
		return []byte{0, 0, 0, 0}
	}
	narrow := true
	for _, u := range units {
		narrow = narrow && u <= 0xFF
	}
	if narrow {
		out := binary.LittleEndian.AppendUint32(nil, uint32(^int32(len(units))))
		for _, u := range units {
			out = append(out, byte(u))
		}
		return out
	}
	out := binary.LittleEndian.AppendUint32(nil, uint32(len(units)))
	for _, u := range units {
		out = binary.LittleEndian.AppendUint16(out, u)
	}
	return out
}

// refText returns units as WTF-8: pairs as one char, unpaired surrogates in their 3-byte form.
func refText(units []uint16) string {
	var out []byte
	for i := 0; i < len(units); i++ {
		u := rune(units[i])
		if utf16.IsSurrogate(u) && u < 0xDC00 && i+1 < len(units) && units[i+1] >= 0xDC00 && units[i+1] < 0xE000 {
			out = utf8.AppendRune(out, utf16.DecodeRune(u, rune(units[i+1])))
			i++
			continue
		}
		if utf16.IsSurrogate(u) {
			out = append(out, 0xE0|byte(u>>12), 0x80|byte(u>>6)&0x3F, 0x80|byte(u)&0x3F)
			continue
		}
		out = utf8.AppendRune(out, u)
	}
	return string(out)
}

// Each kind of char a writer or reader tells apart: ASCII, Latin-1, 2- and 3-byte UTF-8 at their bounds, chars
// outside the BMP, unpaired surrogates and a pair written as two (WTF-8), and bytes that are not UTF-8 (a
// continuation byte alone, a cut sequence, overlong forms, past U+10FFFF).
var stringKinds = []string{
	"a", "\x7f", "\u0080", "é", "ÿ", "Ā", "߿", "ࠀ", "中", "￿", "😀", "\U0010FFFF",
	"\xed\xa0\x80", "\xed\xbf\xbf", "\xed\xa0\xbd\xed\xb8\x80", "\xed\x9f\xbf",
	"\xff", "\x80", "\xc3", "\xe4\xb8", "\xf0\x9f\x98", "\xc0\x80", "\xc1\xbf", "\xe0\x80\x80", "\xe0\x9f\xbf",
	"\xf0\x80\x80\x80", "\xf4\x90\x80\x80",
	// The bounds of the writer's four-at-a-time paths: Latin-1 pairs (C2, C3) against other 2-byte chars (C4 on,
	// Cyrillic), two 3-byte chars in a word (U+E000, U+FFFD; a second one cut or with a bad continuation), 4-byte
	// leads past F4.
	"ж", "\xc4\x80", "\xc2\xc2", "\xdf\x80", "\xee\x80\x80", "\xef\xbf\xbd", "\xe4\xb8\xad\xe4\xb8",
	"\xe4\xb8\xad\xe4\x41\xad", "\xe0\xa0", "\xf5\x80\x80\x80", "\xf7\xbf\xbf\xbf", "\xf0\x90\x80",
}

// TestStringEncodings writes every string of up to 40 chars of one filler with one char of each kind at each
// position, and checks the bytes against the reference and what reading them gives back.
func TestStringEncodings(t *testing.T) {
	w := NewWriter(0)
	check := func(s string) {
		t.Helper()
		units := refUnits(s)
		want := refBytes(units)
		w.Reset()
		w.String(s)
		if w.Err() != nil || !bytes.Equal(w.Bytes(), want) {
			t.Fatalf("%q: wrote % X, want % X (%v)", s, w.Bytes(), want, w.Err())
		}
		r := NewReader(want)
		got, ok := r.NullableString()
		if !ok || r.Err() != nil || r.More() || got != refText(units) {
			t.Fatalf("%q: read %q, want %q (%v)", s, got, refText(units), r.Err())
		}
		if utf8.ValidString(s) && !strings.Contains(s, "\xed") && got != s {
			t.Fatalf("%q: read back %q", s, got)
		}
	}
	for _, filler := range []string{"a", "é", "ж", "中", "😀"} {
		for n := 0; n <= 40; n++ {
			check(strings.Repeat(filler, n))
			for _, kind := range stringKinds {
				for at := 0; at <= n; at++ {
					check(strings.Repeat(filler, at) + kind + strings.Repeat(filler, n-at))
				}
			}
		}
	}
	// Every Latin-1 char, alone and among ASCII; random strings of the kinds.
	for c := rune(0); c < 0x100; c++ {
		check(string(c))
		check("abcdefghij" + string(c) + "klmnopqrstuvwxyz")
	}
	rnd := rand.New(rand.NewSource(1))
	for i := 0; i < 20000; i++ {
		var sb strings.Builder
		pieces := rnd.Intn(48)
		if i%8 == 0 {
			// Past the reader's short text (shortText), both ways.
			pieces = rnd.Intn(300)
		}
		for k := pieces; k > 0; k-- {
			sb.WriteString(stringKinds[rnd.Intn(len(stringKinds))])
		}
		check(sb.String())
	}
}

// TestStringReadUnits reads strings of code units of every kind, unpaired surrogates included, against the
// reference: up to 40 units, and longer ones (converted on the heap rather than on the stack).
func TestStringReadUnits(t *testing.T) {
	// Also the bounds of the reader's four-at-a-time paths: units of two UTF-8 bytes (0x80 to 0x7FF) against
	// units that differ from them in one bit (0x8000, 0x80C2, 0xF800, 0x0780).
	kinds := []uint16{'a', 0x7F, 0x80, 0xE9, 0xFF, 0x100, 0x7FF, 0x800, 0x4E2D, 0xFFFF, 0xD83D, 0xDE00, 0xD800,
		0xDBFF, 0xDC00, 0xDFFF, 0x436, 0x780, 0x8000, 0x80C2, 0xF800}
	rnd := rand.New(rand.NewSource(2))
	for i := 0; i < 50000; i++ {
		n := rnd.Intn(41)
		if i%8 == 0 {
			n = rnd.Intn(400)
		}
		units := make([]uint16, n)
		for k := range units {
			units[k] = kinds[rnd.Intn(len(kinds))]
		}
		data := refBytes(units)
		r := NewReader(data)
		got := r.String()
		if r.Err() != nil || r.More() || got != refText(units) {
			t.Fatalf("%X: read %q, want %q (%v)", units, got, refText(units), r.Err())
		}
		// WTF-8 round-trips to the same bytes.
		if back := encode(t, func(w *Writer) { w.String(got) }); !bytes.Equal(back, data) {
			t.Fatalf("%X: wrote back % X", units, back)
		}
	}
}

// FuzzString writes any Go string, valid UTF-8 or not, and reads any bytes as a string's chars: the writer must
// match the reference, and the reader must read the reference's bytes back to the reference's text.
func FuzzString(f *testing.F) {
	for _, k := range stringKinds {
		f.Add("ab" + k + "cdefghijklmnop" + k)
	}
	f.Fuzz(func(t *testing.T, s string) {
		units := refUnits(s)
		want := refBytes(units)
		if got := encode(t, func(w *Writer) { w.String(s) }); !bytes.Equal(got, want) {
			t.Fatalf("%q: wrote % X, want % X", s, got, want)
		}
		if got := NewReader(want).String(); got != refText(units) {
			t.Fatalf("%q: read %q, want %q", s, got, refText(units))
		}
		// The bytes of s as UTF-16 units and as Latin-1.
		b := []byte(s)
		raw := make([]uint16, len(b)/2)
		for i := range raw {
			raw[i] = binary.LittleEndian.Uint16(b[2*i:])
		}
		if got := NewReader(refBytes(raw)).String(); got != refText(raw) {
			t.Fatalf("% X: read %q, want %q", raw, got, refText(raw))
		}
		latin := make([]uint16, len(b))
		for i, c := range b {
			latin[i] = uint16(c)
		}
		if got := NewReader(refBytes(latin)).String(); got != refText(latin) {
			t.Fatalf("% X: read %q as Latin-1", b, got)
		}
	})
}
