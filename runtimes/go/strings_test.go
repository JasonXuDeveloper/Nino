package nino

import (
	"bytes"
	"encoding/binary"
	"math/rand"
	"strconv"
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

// refHeader appends v as a string header: v itself below 0x80, else 0x80 and a uint16 up to 0xFFFF, else 0x81 and
// a uint32 (little-endian).
func refHeader(out []byte, v uint64) []byte {
	switch {
	case v < 0x80:
		return append(out, byte(v))
	case v <= 0xFFFF:
		return append(out, 0x80, byte(v), byte(v>>8))
	}
	return append(out, 0x81, byte(v), byte(v>>8), byte(v>>16), byte(v>>24))
}

// refBytes returns the bytes .NET writes for a string of units: 2n+1 then n bytes when every unit is at most 0xFF
// (1 for the empty string), else 2n then the units.
func refBytes(units []uint16) []byte {
	narrow := true
	for _, u := range units {
		narrow = narrow && u <= 0xFF
	}
	if narrow {
		out := refHeader(nil, 2*uint64(len(units))+1)
		for _, u := range units {
			out = append(out, byte(u))
		}
		return out
	}
	out := refHeader(nil, 2*uint64(len(units)))
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
		checkAppended(t, s, want)
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
	// Long runs of ASCII before, after and between the kinds, of lengths around the word and block sizes (the
	// writer takes long runs 16 bytes at a time where the platform has vector instructions).
	runs := []int{0, 1, 7, 8, 9, 15, 16, 17, 31, 63, 64, 65, 71, 72, 73, 80, 100, 129}
	var ascii strings.Builder
	for c := 0; ascii.Len() < 130; c++ {
		ascii.WriteByte(byte(0x20 + c*7%0x5F))
	}
	run := func(n, from int) string { return ascii.String()[from : from+n] }
	for _, kind := range stringKinds {
		for _, pre := range runs {
			for _, post := range runs {
				check(run(pre, 0) + kind + run(post, 1))
				check(kind + run(pre, 0) + kind + run(post, 1) + kind)
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

// checkAppended writes s after a byte into writers with room for that byte and the string's header, then one byte
// a byte of s, or two (as much as UTF-16 can take): the bytes must be that byte then want, the writer must not have
// grown when it had room for them (the last chars are written one at a time, see utf16Tail), and want follows when
// s is written again.
func checkAppended(t *testing.T, s string, want []byte) {
	t.Helper()
	room := stringRoom(len(s))
	for _, size := range []int{1 + room + len(s), 1 + room + 2*len(s)} {
		w := NewWriter(size)
		w.U8(0xAB)
		first := &w.buf[:1][0]
		w.String(s)
		got := w.Bytes()
		if w.Err() != nil || len(got) != 1+len(want) || got[0] != 0xAB || !bytes.Equal(got[1:], want) {
			t.Fatalf("%q: wrote % X after a byte, want % X (%v)", s, got, want, w.Err())
		}
		if latin1 := want[0]&1 == 1; (latin1 || size > 1+room+len(s)) && &got[0] != first {
			t.Fatalf("%q: grew the buffer with room for the string", s)
		}
		w.String(s)
		if got := w.Bytes(); w.Err() != nil || !bytes.Equal(got[1+len(want):], want) {
			t.Fatalf("%q: wrote % X again, want % X (%v)", s, got[1+len(want):], want, w.Err())
		}
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
		checkAppended(t, s, want)
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

// TestStringBlocks reads strings of every kind and length around the short-string limits into the blocks a reader
// keeps (as strings and as pointers to them), through one reader reset between values, and checks every string read
// is unchanged once all are read.
func TestStringBlocks(t *testing.T) {
	rnd := rand.New(rand.NewSource(7))
	chars := []string{"a", "z", "é", "ÿ", "Ж", "字", "🎉"}
	var texts []string
	for n := 1; n <= 3*textShort; n += 1 + n/16 {
		for range 4 {
			var sb strings.Builder
			for sb.Len() < n {
				sb.WriteString(chars[rnd.Intn(len(chars))])
			}
			texts = append(texts, sb.String())
		}
	}
	r := NewReader(nil)
	var got []*string
	for start := 0; start < len(texts); start += 37 {
		part := texts[start:min(start+37, len(texts))]
		w := NewWriter(0)
		for i, s := range part {
			if i%2 == 0 {
				w.String(s)
			} else {
				w.UTF8(s)
			}
		}
		data := w.Bytes()
		r.Reset(data)
		for i := range part {
			switch i % 4 {
			case 0:
				s := r.String()
				got = append(got, &s)
			case 1:
				s := r.UTF8()
				got = append(got, &s)
			case 2:
				got = append(got, r.StringPtr())
			default:
				got = append(got, r.UTF8Ptr())
			}
		}
		if err := r.Err(); err != nil {
			t.Fatal(err)
		}
		clear(data)
	}
	for i, s := range texts {
		if *got[i] != s {
			t.Fatalf("string %d: read %q, want %q", i, *got[i], s)
		}
	}
}

// BenchmarkWriteString writes 300 short strings of each kind (ASCII, Latin-1, other text; as the bench module's
// mixed case) and one text of about 1 KiB of each kind, ASCII also after one char that makes it Latin-1 or UTF-16
// (the long runs the writer takes 16 bytes at a time where it has vector instructions).
func BenchmarkWriteString(b *testing.B) {
	short := func(f func(i int) string) []string {
		v := make([]string, 300)
		for i := range v {
			v[i] = f(i)
		}
		return v
	}
	ascii := strings.Repeat("The quick brown fox jumps over. ", 32)
	cases := []struct {
		name string
		v    []string
	}{
		{"ascii", short(func(i int) string { return "string " + strconv.Itoa(i) })},
		{"latin1", short(func(i int) string { return "crème brûlée " + strconv.Itoa(i) })},
		{"other", short(func(i int) string { return "строка 字符串 🎉 " + strconv.Itoa(i) })},
		{"long-ascii", []string{ascii}},
		{"long-latin1", []string{strings.Repeat("Ça a été très démodé à côté. ", 35)}},
		{"long-cyrillic", []string{strings.Repeat("Это предложение на русском языке. ", 30)}},
		{"long-cjk", []string{strings.Repeat("这是一个用于测试的中文句子，", 40)}},
		{"long-ascii-latin1", []string{"é" + ascii}},
		{"long-ascii-utf16", []string{"🎉" + ascii}},
	}
	for _, c := range cases {
		b.Run(c.name, func(b *testing.B) {
			w := NewWriter(1 << 16)
			b.ReportAllocs()
			for i := 0; i < b.N; i++ {
				w.Reset()
				for _, s := range c.v {
					w.String(s)
				}
			}
		})
	}
}

// TestReadStrings checks ReadStrings against ReadSlice with Reader.String: the strings WriteStrings wrote (ASCII,
// Latin-1, UTF-16, long), a null element, null, and every truncation.
func TestReadStrings(t *testing.T) {
	strs := []string{"", "a", "héllo", "日本語", strings.Repeat("x", 300), "\U0001F600", "end"}
	w := NewWriter(0)
	WriteStrings(w, strs)
	data := w.Bytes()
	r := NewReader(data)
	if got := ReadStrings(r); r.Err() != nil || r.More() || strings.Join(got, "|") != strings.Join(strs, "|") {
		t.Fatalf("read %q (%v)", got, r.Err())
	}
	w = NewWriter(0)
	w.Length(2)
	w.NullableString("", false)
	w.String("b")
	if got := ReadStrings(NewReader(w.Bytes())); len(got) != 2 || got[0] != "" || got[1] != "b" {
		t.Fatalf("null element read as %q", got)
	}
	if got := ReadStrings(NewReader(hx("FFFFFFFF"))); got != nil {
		t.Fatalf("null read as %q", got)
	}
	// Short text of every length around the in-loop path's bounds (1 to 16 chars of ASCII), Latin-1 and UTF-16 among
	// it, enough to grow the slice and to fill several blocks of r.text, then a byte after: the same strings as
	// ReadSlice reads, and nothing past them read.
	var many []string
	for i := range 5000 {
		s := "abcdefghijklmnopqrstuvwxyz"[:i%20]
		switch i % 7 {
		case 3:
			s += "é"
		case 5:
			s += "日"
		}
		many = append(many, s)
	}
	w = NewWriter(0)
	WriteStrings(w, many)
	w.U8(0x7E)
	for _, cut := range []int{len(w.Bytes()), len(w.Bytes()) - 1} {
		r, ref := NewReader(w.Bytes()[:cut]), NewReader(w.Bytes()[:cut])
		got, want := ReadStrings(r), ReadSlice(ref, 1, (*Reader).String)
		if strings.Join(got, "|") != strings.Join(want, "|") || r.Remaining() != ref.Remaining() ||
			(r.Err() == nil) != (ref.Err() == nil) || r.Err() != nil && r.Err().Error() != ref.Err().Error() {
			t.Fatalf("%d strings, cut at %d: %d read (%v), want %d (%v)", len(many), cut, len(got), r.Err(), len(want), ref.Err())
		}
		if cut == len(w.Bytes()) && (r.Err() != nil || strings.Join(got, "|") != strings.Join(many, "|")) {
			t.Fatalf("%d strings read as %d (%v)", len(many), len(got), r.Err())
		}
	}
	for cut := range len(data) {
		r := NewReader(data[:cut])
		ref := NewReader(data[:cut])
		got, want := ReadStrings(r), ReadSlice(ref, 1, (*Reader).String)
		if got != nil || want != nil || r.Err() == nil || r.Err().Error() != ref.Err().Error() {
			t.Fatalf("cut at %d: %q (%v), want %q (%v)", cut, got, r.Err(), want, ref.Err())
		}
	}
}

// BenchmarkUTF16 reads and writes strings of 1000 UTF-16 code units through Reader.UTF16 and Writer.UTF16: Latin-1
// (written a byte a unit) and wider text (two bytes a unit), 16 variants of each in turn.
func BenchmarkUTF16(b *testing.B) {
	const variants = 16
	for _, c := range []struct {
		name string
		unit func(rnd *rand.Rand) uint16
	}{
		{"latin1", func(rnd *rand.Rand) uint16 { return uint16(0x20 + rnd.Intn(0xE0)) }},
		{"wide", func(rnd *rand.Rand) uint16 { return uint16(0x400 + rnd.Intn(0x5000)) }},
	} {
		rnd := rand.New(rand.NewSource(3))
		texts := make([][]uint16, variants)
		data := make([][]byte, variants)
		for k := range texts {
			texts[k] = make([]uint16, 1000)
			for i := range texts[k] {
				texts[k][i] = c.unit(rnd)
			}
			w := NewWriter(0)
			w.UTF16(texts[k])
			data[k] = w.Bytes()
		}
		b.Run(c.name+"/read", func(b *testing.B) {
			r := NewReader(nil)
			b.SetBytes(int64(len(data[0])))
			b.ReportAllocs()
			for i := 0; i < b.N; i++ {
				r.Reset(data[i%variants])
				if len(r.UTF16()) != 1000 {
					b.Fatal(r.Err())
				}
			}
		})
		b.Run(c.name+"/write", func(b *testing.B) {
			w := NewWriter(len(data[0]))
			b.SetBytes(int64(len(data[0])))
			b.ReportAllocs()
			for i := 0; i < b.N; i++ {
				w.Reset()
				w.UTF16(texts[i%variants])
			}
		})
	}
}
