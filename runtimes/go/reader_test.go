package nino

import (
	"bytes"
	"encoding/hex"
	"errors"
	"math"
	"math/big"
	"strings"
	"testing"
	"time"
)

// hx decodes hex with spaces and | separators.
func hx(s string) []byte {
	s = strings.NewReplacer(" ", "", "|", "", "\n", "").Replace(s)
	b, err := hex.DecodeString(s)
	if err != nil {
		panic(err)
	}
	return b
}

// encode writes with f into a fresh writer and returns the bytes, failing the test on a writer error.
func encode(t *testing.T, f func(w *Writer)) []byte {
	t.Helper()
	w := NewWriter(0)
	f(w)
	if err := w.Err(); err != nil {
		t.Fatalf("write: %v", err)
	}
	return w.Bytes()
}

func expectBytes(t *testing.T, got []byte, want string) {
	t.Helper()
	if !bytes.Equal(got, hx(want)) {
		t.Fatalf("bytes\n got %X\nwant %X", got, hx(want))
	}
}

// expectCode reads data with f and checks that it fails with code.
func expectCode(t *testing.T, data []byte, code Code, f func(r *Reader)) {
	t.Helper()
	r := NewReader(data)
	f(r)
	err := r.Err()
	var e *Error
	if !errors.As(err, &e) || e.Code != code {
		t.Fatalf("got error %v, want code %v", err, code)
	}
	if !errors.Is(err, &Error{Code: code}) {
		t.Fatalf("errors.Is does not match the code")
	}
}

func TestPrimitives(t *testing.T) {
	got := encode(t, func(w *Writer) {
		w.Bool(true)
		w.I8(-2)
		w.U16(0x1234)
		w.Char('A')
		w.F32(1.5)
		w.F64(1.5)
		w.Half(HalfOf(1.5))
		w.I64(-1)
		w.U32(math.MaxUint32)
		w.I16(-1)
		w.U64(1)
		w.I32(-5)
		w.U8(7)
	})
	expectBytes(t, got, "01 FE 3412 4100 0000C03F 000000000000F83F 003E FFFFFFFFFFFFFFFF FFFFFFFF FFFF 0100000000000000 FBFFFFFF 07")
	r := NewReader(got)
	if !r.Bool() || r.I8() != -2 || r.U16() != 0x1234 || r.Char() != 'A' || r.F32() != 1.5 || r.F64() != 1.5 ||
		r.Half().Float32() != 1.5 || r.I64() != -1 || r.U32() != math.MaxUint32 || r.I16() != -1 || r.U64() != 1 ||
		r.I32() != -5 || r.U8() != 7 {
		t.Fatal("round trip")
	}
	if r.Err() != nil || r.More() || r.Pos() != len(got) {
		t.Fatalf("state: %v %v %d", r.Err(), r.More(), r.Pos())
	}
}

func TestEndOfDataEveryWidth(t *testing.T) {
	reads := map[string]func(r *Reader){
		"u8":  func(r *Reader) { r.U8() },
		"u16": func(r *Reader) { r.U16() },
		"u32": func(r *Reader) { r.U32() },
		"u64": func(r *Reader) { r.U64() },
		"dec": func(r *Reader) { r.Decimal() },
		"guid": func(r *Reader) {
			r.Guid()
		},
		"take":   func(r *Reader) { r.Take(3) },
		"string": func(r *Reader) { _ = r.String() },
		"utf8":   func(r *Reader) { r.UTF8() },
		"region": func(r *Reader) { r.BeginRegion() },
		"dto":    func(r *Reader) { r.DateTimeOffset() },
	}
	for name, f := range reads {
		t.Run(name, func(t *testing.T) {
			expectCode(t, nil, CodeEndOfData, f)
			expectCode(t, []byte{}, CodeEndOfData, f)
		})
	}
}

func TestStickyError(t *testing.T) {
	r := NewReader(hx("01 02"))
	if r.U32() != 0 || r.Err() == nil {
		t.Fatal("expected failure")
	}
	// Nothing is read after a failure, and lengths read as null.
	if r.U8() != 0 || r.Length(1) != -1 || r.More() || r.ByteArray() != nil {
		t.Fatal("read after failure")
	}
	first := r.Err()
	r.Fail(CodeInvalidData, "later")
	if r.Err() != first {
		t.Fatal("the first error must stay")
	}
}

func TestBoolAndPresence(t *testing.T) {
	expectCode(t, []byte{2}, CodeInvalidData, func(r *Reader) { r.Bool() })
	expectCode(t, []byte{0xFF}, CodeInvalidData, func(r *Reader) { r.Present() })
	r := NewReader([]byte{0, 1})
	if r.Present() || !r.Present() || r.Err() != nil {
		t.Fatal("presence")
	}
}

func TestLength(t *testing.T) {
	r := NewReader(hx("FFFFFFFF 02000000 AABB"))
	if r.Length(1) != -1 || r.Length(1) != 2 {
		t.Fatal("length")
	}
	expectCode(t, hx("FEFFFFFF"), CodeInvalidLength, func(r *Reader) { r.Length(1) })
	expectCode(t, hx("03000000 AABB"), CodeInvalidLength, func(r *Reader) { r.Length(1) })
	// count * minSize computed without overflow.
	expectCode(t, hx("FFFFFF7F 00"), CodeInvalidLength, func(r *Reader) { r.Length(1 << 30) })
	r = NewReader(hx("02000000 AABBCCDD"))
	if r.Length(2) != 2 {
		t.Fatal("length 2x2")
	}
}

func TestByteArray(t *testing.T) {
	data := hx("03000000 010203 FFFFFFFF 00000000")
	r := NewReader(data)
	a, n, e := r.ByteArray(), r.ByteArray(), r.ByteArray()
	if !bytes.Equal(a, []byte{1, 2, 3}) || n != nil || e == nil || len(e) != 0 || r.Err() != nil {
		t.Fatalf("%v %v %v %v", a, n, e, r.Err())
	}
	data[4] = 9
	if a[0] != 1 {
		t.Fatal("ByteArray must copy")
	}
	got := encode(t, func(w *Writer) { w.ByteArray([]byte{1, 2, 3}); w.ByteArray(nil); w.ByteArray([]byte{}) })
	expectBytes(t, got, "03000000 010203 FFFFFFFF 00000000")
}

func TestStringsSpecExamples(t *testing.T) {
	cases := []struct {
		text string
		null bool
		hex  string
	}{
		{"", true, "00"},
		{"", false, "01"},
		{"abc", false, "07 616263"},
		{"é", false, "03 E9"},
		{"ÿ", false, "03 FF"},
		{"a中", false, "04 6100 2D4E"},
		{"😀", false, "04 3DD8 00DE"},
		{"\xED\xA0\x80", false, "02 00D8"},
		{"a\xED\xA0\x80", false, "04 6100 00D8"},
		{strings.Repeat("a", 63), false, "7F" + strings.Repeat("61", 63)},
		{strings.Repeat("a", 64), false, "808100" + strings.Repeat("61", 64)},
		{strings.Repeat("é", 64), false, "808100" + strings.Repeat("E9", 64)},
		{strings.Repeat("中", 64), false, "808000" + strings.Repeat("2D4E", 64)},
		{strings.Repeat("a", 32767), false, "80FFFF" + strings.Repeat("61", 32767)},
		{strings.Repeat("a", 32768), false, "8101000100" + strings.Repeat("61", 32768)},
		{strings.Repeat("中", 63), false, "7E" + strings.Repeat("2D4E", 63)},
	}
	for _, c := range cases {
		got := encode(t, func(w *Writer) { w.NullableString(c.text, !c.null) })
		expectBytes(t, got, c.hex)
		r := NewReader(got)
		s, ok := r.NullableString()
		if s != c.text || ok == c.null || r.Err() != nil || r.More() {
			t.Fatalf("%q: read %q %v %v", c.text, s, ok, r.Err())
		}
	}
}

func TestStringSurrogates(t *testing.T) {
	// A lone low surrogate, a pair, a lone high surrogate at the end: kept as code units and as WTF-8.
	data := hx("08 00DC 3DD8 00DE 00D8")
	r := NewReader(data)
	s := r.String()
	if s != "\xED\xB0\x80😀\xED\xA0\x80" {
		t.Fatalf("%q", s)
	}
	if got := encode(t, func(w *Writer) { w.String(s) }); !bytes.Equal(got, data) {
		t.Fatalf("round trip %X", got)
	}
	r = NewReader(data)
	units := r.UTF16()
	want := []uint16{0xDC00, 0xD83D, 0xDE00, 0xD800}
	if len(units) != 4 || units[0] != want[0] || units[1] != want[1] || units[2] != want[2] || units[3] != want[3] {
		t.Fatalf("%X", units)
	}
	if got := encode(t, func(w *Writer) { w.UTF16(units) }); !bytes.Equal(got, data) {
		t.Fatalf("UTF16 round trip %X", got)
	}
	// Invalid UTF-8 in a Go string is written as U+FFFD.
	expectBytes(t, encode(t, func(w *Writer) { w.String("a\xFF") }), "04 6100 FDFF")
	// UTF16 with Latin-1 units narrows.
	expectBytes(t, encode(t, func(w *Writer) { w.UTF16([]uint16{'a', 0xE9}); w.UTF16(nil); w.UTF16([]uint16{}) }),
		"05 61E9 00 01")
	r = NewReader(hx("05 61E9 01 00"))
	if u := r.UTF16(); len(u) != 2 || u[1] != 0xE9 {
		t.Fatal("UTF16 Latin-1")
	}
	if u := r.UTF16(); u == nil || len(u) != 0 {
		t.Fatal("UTF16 empty")
	}
	if u := r.UTF16(); u != nil {
		t.Fatal("UTF16 null")
	}
}

func TestStringLatin1Long(t *testing.T) {
	var sb strings.Builder
	for i := 0; i < 1000; i++ {
		sb.WriteRune(rune(i % 256))
	}
	s := sb.String()
	got := encode(t, func(w *Writer) { w.String(s) })
	if len(got) != 1003 {
		t.Fatalf("Latin-1 expected, %d bytes", len(got))
	}
	if NewReader(got).String() != s {
		t.Fatal("round trip")
	}
}

func TestStringErrors(t *testing.T) {
	expectCode(t, hx("05 61"), CodeEndOfData, func(r *Reader) { _ = r.String() })
	expectCode(t, hx("81FFFFFFFF"), CodeEndOfData, func(r *Reader) { _ = r.String() })
	expectCode(t, hx("04 6100"), CodeEndOfData, func(r *Reader) { _ = r.String() })
	expectCode(t, hx("81FEFFFFFF"), CodeEndOfData, func(r *Reader) { _ = r.String() })
	expectCode(t, hx("81FEFFFFFF"), CodeEndOfData, func(r *Reader) { r.UTF16() })
	expectCode(t, hx(""), CodeEndOfData, func(r *Reader) { _ = r.String() })
	expectCode(t, hx("80"), CodeEndOfData, func(r *Reader) { _ = r.String() })
	expectCode(t, hx("8080"), CodeEndOfData, func(r *Reader) { _ = r.String() })
	expectCode(t, hx("81FFFFFF"), CodeEndOfData, func(r *Reader) { _ = r.String() })
	expectCode(t, hx("808100 61"), CodeEndOfData, func(r *Reader) { _ = r.String() })
	// Not minimal, or a first byte no header starts with.
	for _, h := range []string{"800000", "800700 616263", "807F00", "8100000000", "81FFFF0000", "82", "8200000000",
		"FFFFFFFF0F"} {
		expectCode(t, hx(h), CodeInvalidData, func(r *Reader) { _ = r.String() })
		expectCode(t, hx(h), CodeInvalidData, func(r *Reader) { r.UTF16() })
	}
}

func TestUtf8Strings(t *testing.T) {
	cases := []struct {
		text string
		null bool
		hex  string
	}{
		{"", true, "00"},
		{"", false, "01"},
		{"abc", false, "04 03 616263"},
		{"é😀", false, "04 06 C3A9F09F9880"},
		{strings.Repeat("a", 126), false, "7F 7E" + strings.Repeat("61", 126)},
		{strings.Repeat("a", 127), false, "808000 7F" + strings.Repeat("61", 127)},
		{strings.Repeat("中", 43), false, "2C 808100" + strings.Repeat("E4B8AD", 43)},
	}
	for _, c := range cases {
		got := encode(t, func(w *Writer) { w.NullableUTF8(c.text, !c.null) })
		expectBytes(t, got, c.hex)
		r := NewReader(got)
		s, ok := r.NullableUTF8()
		if s != c.text || ok == c.null || r.Err() != nil || r.More() {
			t.Fatalf("%q: read %q %v %v", c.text, s, ok, r.Err())
		}
	}
	// An unpaired surrogate (WTF-8) and invalid UTF-8 are written as U+FFFD.
	expectBytes(t, encode(t, func(w *Writer) { w.UTF8("a\xED\xA0\x80") }), "03 04 61EFBFBD")
	expectBytes(t, encode(t, func(w *Writer) { w.UTF8("a\xFF") }), "03 04 61EFBFBD")
}

func TestUtf8Errors(t *testing.T) {
	bad := map[string]string{
		"overlong":           "02 02 C081",
		"encoded surrogate":  "02 03 EDA080",
		"past U+10FFFF":      "03 04 F4908080",
		"cut sequence":       "02 02 E282",
		"too many chars":     "02 02 6162",
		"too few chars":      "04 03 61C3A9",
		"bytes below chars":  "04 02 6162",
		"non-ASCII as ASCII": "03 02 61E9",
		"count not minimal":  "800200 01 61",
		"size not minimal":   "02 800100 61",
		"size not minimal 5": "02 8101000000 61",
		"count past 2^31-1":  "8101000080 01 61",
		"size past 2^31-1":   "02 8100000080 61",
		"size bad marker":    "02 82 61",
		"count bad marker":   "FF 01 61",
	}
	for name, h := range bad {
		t.Run(name, func(t *testing.T) {
			expectCode(t, hx(h), CodeInvalidData, func(r *Reader) { r.UTF8() })
		})
	}
	expectCode(t, hx("02 05 61"), CodeEndOfData, func(r *Reader) { r.UTF8() })
	expectCode(t, hx("02 81FFFFFF0F 61"), CodeEndOfData, func(r *Reader) { r.UTF8() })
	expectCode(t, hx("02"), CodeEndOfData, func(r *Reader) { r.UTF8() })
	expectCode(t, hx("80"), CodeEndOfData, func(r *Reader) { r.UTF8() })
	expectCode(t, hx("02 8105"), CodeEndOfData, func(r *Reader) { r.UTF8() })
}

func TestDecimal(t *testing.T) {
	cases := map[string]string{
		"1.5":                  "00000100 00000000 0F00000000000000",
		"-0.01":                "00000280 00000000 0100000000000000",
		"18446744073709551619": "00000000 01000000 0300000000000000",
	}
	for text, h := range cases {
		d, err := ParseDecimal(text)
		if err != nil {
			t.Fatal(err)
		}
		expectBytes(t, encode(t, func(w *Writer) { w.Decimal(d) }), h)
		if got := NewReader(hx(h)).Decimal().String(); got != text {
			t.Fatalf("%s read as %s", text, got)
		}
	}
	for _, bad := range []string{"", "-", "1.2.3", "1e5", "79228162514264337593543950336", "0.00000000000000000000000000001"} {
		if _, err := ParseDecimal(bad); err == nil {
			t.Fatalf("%q parsed", bad)
		}
	}
	if d, _ := ParseDecimal("79228162514264337593543950335"); d.Coefficient().BitLen() != 96 {
		t.Fatal("max decimal")
	}
	if d, _ := ParseDecimal("-0"); d.Negative() {
		t.Fatal("-0 has no sign")
	}
	if s := (Decimal{Flags: 3 << 16, Lo: 5}).String(); s != "0.005" {
		t.Fatal(s)
	}
}

func TestGuid(t *testing.T) {
	g, err := ParseGuid("00112233-4455-6677-8899-aabbccddeeff")
	if err != nil {
		t.Fatal(err)
	}
	expectBytes(t, encode(t, func(w *Writer) { w.Guid(g) }), "33221100 5544 7766 8899AABBCCDDEEFF")
	if g.String() != "00112233-4455-6677-8899-aabbccddeeff" {
		t.Fatal(g.String())
	}
	for _, s := range []string{"{00112233-4455-6677-8899-AABBCCDDEEFF}", "00112233445566778899aabbccddeeff"} {
		if h, err := ParseGuid(s); err != nil || h != g {
			t.Fatal(s)
		}
	}
	for _, s := range []string{"", "00112233-4455-6677-8899-aabbccddeefg", "0011223344556677-8899-aabbccddeeff0"} {
		if _, err := ParseGuid(s); err == nil {
			t.Fatal(s)
		}
	}
	if NewReader(hx("33221100 5544 7766 8899AABBCCDDEEFF")).Guid() != g {
		t.Fatal("read")
	}
}

func TestDates(t *testing.T) {
	// The specification's §3.3 and §3.4 examples label these bytes 2024-05-06 07:08:09; they are 2024-01-02 03:04:05.
	when := time.Date(2024, 1, 2, 3, 4, 5, 0, time.UTC)
	d, ok := DateTimeOf(when)
	if !ok {
		t.Fatal("DateTimeOf")
	}
	expectBytes(t, encode(t, func(w *Writer) { w.DateTime(d) }), "80C0BF7A3F0BDC48")
	unspecified := NewDateTime(d.Ticks(), KindUnspecified)
	local := NewDateTime(d.Ticks(), KindLocal)
	expectBytes(t, encode(t, func(w *Writer) { w.DateTime(unspecified); w.DateTime(local) }), "80C0BF7A3F0BDC08 80C0BF7A3F0BDC88")
	r := NewReader(hx("80C0BF7A3F0BDC48"))
	if got := r.DateTime(); got.Kind() != KindUtc || !got.Time().Equal(when) {
		t.Fatal(got.Time())
	}
	// Kind 3: .NET's local time in the repeated hour of a daylight saving change (2024-11-03 01:30 in New York, from
	// ToLocalTime), read and written back as it is.
	ambiguous := encode(t, func(w *Writer) { w.DateTime(NewReader(hx("80C0BF7A3F0BDCC8")).DateTime()) })
	expectBytes(t, ambiguous, "80C0BF7A3F0BDCC8")
	if got := NewReader(ambiguous).DateTime(); got.Kind() != 3 || got.Ticks() != d.Ticks() {
		t.Fatal(got.Kind(), got.Ticks())
	}
	expectCode(t, hx("FFFFFFFFFFFFFF3F"), CodeInvalidData, func(r *Reader) { r.DateTime() })

	// DateTimeOffset: UTC ticks, then minutes.
	plus2 := time.Date(2024, 1, 2, 3, 4, 5, 0, time.FixedZone("", 2*3600))
	o, ok := DateTimeOffsetOf(plus2)
	if !ok {
		t.Fatal("DateTimeOffsetOf")
	}
	expectBytes(t, encode(t, func(w *Writer) { w.DateTimeOffset(o) }), "80F036B72E0BDC08 7800")
	minus := time.Date(2024, 1, 2, 3, 4, 5, 0, time.FixedZone("", -(5*3600+1800)))
	o2, _ := DateTimeOffsetOf(minus)
	expectBytes(t, encode(t, func(w *Writer) { w.DateTimeOffset(o2) }), "80FC77946D0BDC08 B6FE")
	if got := NewReader(hx("80F036B72E0BDC08 7800")).DateTimeOffset(); !got.Time().Equal(plus2) {
		t.Fatal("DateTimeOffset read")
	}
	expectCode(t, hx("0000000000000000 4903"), CodeInvalidData, func(r *Reader) { r.DateTimeOffset() })
	expectCode(t, hx("0000000000000000 C4FF"), CodeInvalidData, func(r *Reader) { r.DateTimeOffset() })
	expectCode(t, hx("FFFFFFFFFFFFFFFF 0000"), CodeInvalidData, func(r *Reader) { r.DateTimeOffset() })
	w := NewWriter(0)
	w.DateTimeOffset(DateTimeOffset{OffsetMinutes: 900})
	if w.Err() == nil {
		t.Fatal("invalid DateTimeOffset written")
	}

	expectBytes(t, encode(t, func(w *Writer) { w.TimeSpan(TimeSpanOf(time.Second)) }), "8096980000000000")
	expectBytes(t, encode(t, func(w *Writer) { w.DateOnly(DateOnly(0x0B4646)) }), "46460B00")
	if DateOnly(0x0B4646).Time() != time.Date(2024, 1, 2, 0, 0, 0, 0, time.UTC) {
		t.Fatal(DateOnly(0x0B4646).Time())
	}
	expectBytes(t, encode(t, func(w *Writer) { w.TimeOnly(TimeOnly(36_000_000_000)) }), "0068C46108000000")
	if dur, ok := TimeSpan(math.MaxInt64).Duration(); ok || dur != 0 {
		t.Fatal("TimeSpan overflow")
	}
	if dur, ok := TimeSpan(-10).Duration(); !ok || dur != -1000 {
		t.Fatal("TimeSpan negative")
	}
}

func TestInt128AndHalf(t *testing.T) {
	v, ok := Int128Of(big.NewInt(-2))
	if !ok {
		t.Fatal("Int128Of")
	}
	expectBytes(t, encode(t, func(w *Writer) { w.I128(v) }), "FEFFFFFFFFFFFFFF FFFFFFFFFFFFFFFF")
	if NewReader(hx("FEFFFFFFFFFFFFFF FFFFFFFFFFFFFFFF")).I128().Big().Int64() != -2 {
		t.Fatal("Int128 read")
	}
	limit := new(big.Int).Lsh(big.NewInt(1), 127)
	if _, ok := Int128Of(limit); ok {
		t.Fatal("2^127 fits no Int128")
	}
	if m, ok := Int128Of(new(big.Int).Neg(limit)); !ok || m.Hi != 1<<63 || m.Lo != 0 {
		t.Fatal("-2^127")
	}
	u, ok := UInt128Of(new(big.Int).Lsh(big.NewInt(1), 100))
	if !ok || u.Big().BitLen() != 101 {
		t.Fatal("UInt128")
	}
	if _, ok := UInt128Of(big.NewInt(-1)); ok {
		t.Fatal("negative UInt128")
	}
	// Every Half round-trips through float32.
	for i := 0; i < 1<<16; i++ {
		h := Half(i)
		f := h.Float32()
		if f != f {
			continue
		}
		if HalfOf(f) != h {
			t.Fatalf("%04X -> %v -> %04X", i, f, HalfOf(f))
		}
	}
	if HalfOf(65520) != 0x7C00 || HalfOf(1e-8) != 0 || HalfOf(float32(math.Inf(-1))) != 0xFC00 {
		t.Fatal("Half limits")
	}
}

func TestBigInteger(t *testing.T) {
	cases := map[int64]string{0: "01000000 00", 255: "02000000 FF00", -1: "01000000 FF", -129: "02000000 7FFF", 127: "01000000 7F", -128: "01000000 80", 128: "02000000 8000"}
	for v, h := range cases {
		expectBytes(t, encode(t, func(w *Writer) { w.BigInteger(big.NewInt(v)) }), h)
		if got := NewReader(hx(h)).BigInteger(); got.Int64() != v {
			t.Fatalf("%d read as %v", v, got)
		}
	}
	huge := new(big.Int).Lsh(big.NewInt(-3), 200)
	got := encode(t, func(w *Writer) { w.BigInteger(huge) })
	if NewReader(got).BigInteger().Cmp(huge) != 0 {
		t.Fatal("huge")
	}
	expectCode(t, hx("FFFFFFFF"), CodeInvalidLength, func(r *Reader) { r.BigInteger() })
	expectCode(t, hx("00000000"), CodeInvalidData, func(r *Reader) { r.BigInteger() })
	expectCode(t, hx("02000000 0100"), CodeInvalidData, func(r *Reader) { r.BigInteger() })
	expectCode(t, hx("02000000 FFFF"), CodeInvalidData, func(r *Reader) { r.BigInteger() })
	expectCode(t, hx("05000000 FF"), CodeInvalidLength, func(r *Reader) { r.BigInteger() })
}

func TestVersion(t *testing.T) {
	expectBytes(t, encode(t, func(w *Writer) { w.Version(&Version{1, 2, -1, -1}); w.Version(nil) }),
		"01000000 02000000 FFFFFFFF FFFFFFFF FFFFFFFF")
	r := NewReader(hx("01000000 02000000 03000000 FBFFFFFF FFFFFFFF"))
	if v := r.Version(); v == nil || *v != (Version{1, 2, 3, -1}) || v.String() != "1.2.3" {
		t.Fatal(v)
	}
	if r.Version() != nil || r.Err() != nil {
		t.Fatal("null")
	}
	if v := NewReader(hx("01000000 02000000 FBFFFFFF 07000000")).Version(); *v != (Version{1, 2, -1, -1}) {
		t.Fatal(v)
	}
	expectCode(t, hx("FEFFFFFF 02000000 00000000 00000000"), CodeInvalidData, func(r *Reader) { r.Version() })
	expectCode(t, hx("01000000 FEFFFFFF 00000000 00000000"), CodeInvalidData, func(r *Reader) { r.Version() })
	expectCode(t, hx("01000000 02000000"), CodeEndOfData, func(r *Reader) { r.Version() })
}

func TestPrimitiveBlocks(t *testing.T) {
	ints := []int32{1, -2, 3}
	got := encode(t, func(w *Writer) { WritePrimitives(w, ints); WritePrimitives[float64](w, nil) })
	expectBytes(t, got, "03000000 01000000 FEFFFFFF 03000000 FFFFFFFF")
	r := NewReader(got)
	back := ReadPrimitives[int32](r)
	if len(back) != 3 || back[1] != -2 || ReadPrimitives[float64](r) != nil || r.Err() != nil {
		t.Fatal(back)
	}
	// The element-by-element path of big-endian hosts gives the same values and bytes.
	type E16 int16
	vals := []E16{1, -1, 0x1234}
	b := make([]byte, 6)
	encodeEach(b, vals)
	expectBytes(t, b, "0100 FFFF 3412")
	out := make([]E16, 3)
	decodeEach(out, b)
	if out[2] != 0x1234 || out[1] != -1 {
		t.Fatal(out)
	}
	f := []float64{1.5, math.Inf(1)}
	b = make([]byte, 16)
	encodeEach(b, f)
	g := make([]float64, 2)
	decodeEach(g, b)
	if g[0] != 1.5 || !math.IsInf(g[1], 1) {
		t.Fatal(g)
	}
	u8 := []uint8{1, 2}
	b = make([]byte, 2)
	encodeEach(b, u8)
	decodeEach(u8, []byte{3, 4})
	if b[1] != 2 || u8[1] != 4 {
		t.Fatal("bytes")
	}
	expectCode(t, hx("02000000 01000000"), CodeInvalidLength, func(r *Reader) { ReadPrimitives[int32](r) })
	expectBytes(t, encode(t, func(w *Writer) { WriteBools(w, []bool{true, false}) }), "02000000 0100")
	if v := ReadBools(NewReader(hx("02000000 0100"))); len(v) != 2 || !v[0] || v[1] {
		t.Fatal(v)
	}
	expectCode(t, hx("01000000 02"), CodeInvalidData, func(r *Reader) { ReadBools(r) })
}

func TestCollections(t *testing.T) {
	strs := []string{"a", "", "中"}
	got := encode(t, func(w *Writer) { WriteSlice(w, strs, (*Writer).String) })
	r := NewReader(got)
	back := ReadSlice(r, 1, (*Reader).String)
	if len(back) != 3 || back[2] != "中" || r.Err() != nil {
		t.Fatal(back)
	}
	expectBytes(t, encode(t, func(w *Writer) {
		WriteEntries(w, []Entry[int32, string]{{1, "a"}}, (*Writer).I32, (*Writer).String)
	}), "01000000 01000000 0361")
	m := ReadMap(NewReader(hx("02000000 01000000 0361 02000000 01")), 5, (*Reader).I32, (*Reader).String)
	if len(m) != 2 || m[1] != "a" || m[2] != "" {
		t.Fatal(m)
	}
	expectCode(t, hx("02000000 01000000 0361 01000000 01"), CodeDuplicateKey, func(r *Reader) {
		ReadMap(r, 5, (*Reader).I32, (*Reader).String)
	})
	expectCode(t, hx("02000000 01000000 01000000"), CodeDuplicateKey, func(r *Reader) { ReadSet(r, 4, (*Reader).I32) })
	set := ReadSet(NewReader(hx("02000000 01000000 02000000")), 4, (*Reader).I32)
	if len(set) != 2 {
		t.Fatal(set)
	}
	m2 := map[int32]int32{1: 2}
	if got := encode(t, func(w *Writer) {
		WriteMap(w, m2, (*Writer).I32, (*Writer).I32)
		WriteMap[int32, int32](w, nil, nil, nil)
	}); !bytes.Equal(got, hx("01000000 01000000 02000000 FFFFFFFF")) {
		t.Fatalf("%X", got)
	}
	if got := encode(t, func(w *Writer) { WriteSet(w, map[int32]struct{}{7: {}}, (*Writer).I32) }); !bytes.Equal(got, hx("01000000 07000000")) {
		t.Fatalf("%X", got)
	}
	// An element that fails ends the read with no result.
	r = NewReader(hx("02000000 0361 03"))
	if ReadSlice(r, 1, (*Reader).String) != nil || r.Err() == nil {
		t.Fatal("partial result")
	}
}

func TestSliceCapBoundsMemory(t *testing.T) {
	// 1 MB of data claiming 2^20 elements of 1 KB each: the slice starts at what 16 bytes a byte allows.
	r := NewReader(make([]byte, 1<<20))
	if c := r.SliceCap(1<<20, 1024); c != 1<<14 {
		t.Fatal(c)
	}
	if c := r.SliceCap(100, 8); c != 100 {
		t.Fatal(c)
	}
	if c := NewReader(nil).SliceCap(10, 1024); c != 10 {
		t.Fatal("presize floor", c)
	}
	if c := r.SliceCap(-1, 4); c != 0 {
		t.Fatal(c)
	}
	// Big elements that the data claims but does not hold: the read grows the slice and fails at the end of data
	// without allocating for every claimed element.
	type big [64]int64
	data := encode(t, func(w *Writer) { w.I32(1 << 20) })
	data = append(data, make([]byte, 1<<20-1)...)
	r = NewReader(data)
	allocs := testing.AllocsPerRun(1, func() {
		r.Reset(data)
		ReadSlice(r, 1, func(r *Reader) big { r.U8(); return big{} })
	})
	if r.Err() == nil || allocs > 64 {
		t.Fatalf("err %v allocs %v", r.Err(), allocs)
	}
}

func TestFixedAndShape(t *testing.T) {
	type pair struct {
		a byte
		b int32
	}
	enc := func(b []byte, p pair) {
		b[0] = p.a
		b[1], b[2], b[3], b[4] = byte(p.b), byte(p.b>>8), byte(p.b>>16), byte(p.b>>24)
	}
	dec := func(_ *Reader, b []byte) pair {
		return pair{b[0], int32(b[1]) | int32(b[2])<<8 | int32(b[3])<<16 | int32(b[4])<<24}
	}
	got := encode(t, func(w *Writer) { WriteFixed(w, []pair{{1, 2}}, 5, enc) })
	expectBytes(t, got, "01000000 0102000000")
	if v := ReadFixed(NewReader(got), 5, dec); len(v) != 1 || v[0] != (pair{1, 2}) {
		t.Fatal(v)
	}
	r := NewReader(hx("02000000 03000000"))
	s, ok := r.ReadShape(2, 4)
	if ok || r.Err() == nil {
		t.Fatal("6 ints in 0 bytes")
	}
	r = NewReader(hx("00000000 03000000"))
	if s, ok = r.ReadShape(2, 4); !ok || s.Total != 0 || s.Dims[1] != 3 {
		t.Fatal(s)
	}
	r = NewReader(hx("FFFFFFFF"))
	if _, ok = r.ReadShape(2, 4); ok || r.Err() != nil {
		t.Fatal("null shape")
	}
	expectCode(t, hx("01000000 FFFFFFFF"), CodeInvalidLength, func(r *Reader) { r.ReadShape(2, 1) })
	expectCode(t, hx("00000000 C8FFFF7F"), CodeInvalidLength, func(r *Reader) { r.ReadShape(2, 1) })
	expectCode(t, hx("00000100 00000100 00000100"), CodeInvalidLength, func(r *Reader) { r.ReadShape(3, 0) })
	expectBytes(t, encode(t, func(w *Writer) { w.WriteShape(2, 3) }), "02000000 03000000")
}

func TestTypeID(t *testing.T) {
	cases := map[string]uint32{
		"Spec.Small":              0xF8B7D639,
		"Spec.Outer+Inner":        0x0FF257FA,
		"Spec.Box<System.Int32>":  0x6781EE38,
		"Spec.Box<System.String>": 0xF0DD44FF,
		"Spec.Box<System.Collections.Generic.List<System.Int32>>": 0xCB133F4F,
		"Spec.Box<System.Int32[]>":                                0x140E75B6,
		"Spec.Box<System.Int32[,]>":                               0x803DF882,
		"Old.Name":                                                0xA578B9DB,
		"":                                                        0x811C9DC5,
		"a":                                                       0x2B24D044,
		"Spec.Circle":                                             0xEDC02BAC,
		"Spec.Square":                                             0x8FE7271F,
		"Id":                                                      0x98C41948,
		"Name":                                                    0x3085B3E2,
		"Score":                                                   0x044AB14D,
		"Title":                                                   0x93A29D95,
	}
	for name, id := range cases {
		if got := uint32(TypeID(name)); got != id {
			t.Errorf("%q: %08X, want %08X", name, got, id)
		}
	}
}

func TestErrorFormat(t *testing.T) {
	r := NewReader(hx("01"))
	r.U32()
	if msg := r.Err().Error(); msg != "nino: unexpected end of data (at byte 0)" {
		t.Fatal(msg)
	}
	e := &Error{Code: CodeTooDeep, Offset: 3, Detail: "x"}
	if e.Error() != "nino: nested too deep: x (at byte 3)" || Code(99).String() != "error 99" {
		t.Fatal(e.Error())
	}
	if !errors.Is(e, ErrTooDeep) || errors.Is(e, ErrCycle) {
		t.Fatal("Is")
	}
}

func TestWriterBasics(t *testing.T) {
	w := NewWriter(0)
	w.Raw([]byte{1})
	p := w.ReserveInt32()
	w.U8(2)
	w.PatchInt32(p, 7)
	w.PatchInt32(100, 1)
	if !bytes.Equal(w.Bytes(), hx("01 07000000 02")) || w.Len() != 6 || w.Err() == nil {
		t.Fatalf("%X %v", w.Bytes(), w.Err())
	}
	w.Reset()
	if w.Len() != 0 || w.Err() != nil {
		t.Fatal("reset")
	}
	if w.Extend(-1) != nil || w.Err() == nil {
		t.Fatal("negative extend")
	}
	w.Reset()
	w.Length(-2)
	if w.Err() == nil {
		t.Fatal("negative length")
	}
	w.Reset()
	z := w.ExtendZero(3)
	if len(z) != 3 || z[0]|z[1]|z[2] != 0 {
		t.Fatal(z)
	}
	if b := w.Extend(1000); len(b) != 1000 || w.Len() != 1003 {
		t.Fatal("grow")
	}
	w.Reset()
	w.Version(&Version{1, 0, -1, 2})
	if w.Err() == nil {
		t.Fatal("invalid Version")
	}
}

func TestSerializeAPI(t *testing.T) {
	data, err := Serialize(int32(1), (*Writer).I32)
	if err != nil || !bytes.Equal(data, hx("01000000")) {
		t.Fatal(data, err)
	}
	v, err := Deserialize(hx("01000000 FFFF"), (*Reader).I32)
	if err != nil || v != 1 {
		t.Fatal(v, err)
	}
	if _, err := DeserializeExact(hx("01000000 FFFF"), (*Reader).I32); !errors.Is(err, ErrInvalidData) {
		t.Fatal(err)
	}
	if v, err := DeserializeExact(hx("01000000"), (*Reader).I32); err != nil || v != 1 {
		t.Fatal(err)
	}
	if v, err := Deserialize(hx("0100"), (*Reader).I32); err == nil || v != 0 {
		t.Fatal("partial")
	}
	dst := make([]byte, 0, 16)
	out, err := Append(dst, "ab", (*Writer).String)
	if err != nil || !bytes.Equal(out, hx("05 6162")) || &out[0] != &dst[:1][0] {
		t.Fatal(out, err)
	}
	if out, err := Append(dst, DateTimeOffset{OffsetMinutes: 2000}, (*Writer).DateTimeOffset); err == nil || len(out) != 0 {
		t.Fatal("failed append")
	}
	if _, err := Serialize(DateTimeOffset{OffsetMinutes: 2000}, (*Writer).DateTimeOffset); err == nil {
		t.Fatal("failed serialize")
	}
	allocs := testing.AllocsPerRun(100, func() {
		dst, _ = Append(dst[:0], "hello", (*Writer).String)
		_, _ = Deserialize(dst, (*Reader).I32)
	})
	if allocs != 0 {
		t.Fatalf("%v allocations", allocs)
	}
}

func TestValueRanges(t *testing.T) {
	// decimal: scale above 28, or reserved flag bits.
	expectCode(t, hx("00001D00 00000000 0100000000000000"), CodeInvalidData, func(r *Reader) { r.Decimal() })
	expectCode(t, hx("01000000 00000000 0100000000000000"), CodeInvalidData, func(r *Reader) { r.Decimal() })
	expectOK(t, hx("00001C80 00000000 0100000000000000"), func(r *Reader) { r.Decimal() })
	// DateOnly: 0..3652058.
	expectCode(t, hx("DBB93700"), CodeInvalidData, func(r *Reader) { r.DateOnly() })
	expectOK(t, hx("DAB93700"), func(r *Reader) { r.DateOnly() })
	expectCode(t, hx("FFFFFFFF"), CodeInvalidData, func(r *Reader) { r.DateOnly() })
	// TimeOnly: 0..863999999999.
	expectCode(t, hx("00C0692A C9000000"), CodeInvalidData, func(r *Reader) { r.TimeOnly() })
	expectOK(t, hx("FFBF692A C9000000"), func(r *Reader) { r.TimeOnly() })
	for _, f := range []func(w *Writer){
		func(w *Writer) { w.Decimal(Decimal{Flags: 29 << 16}) },
		func(w *Writer) { w.DateOnly(MaxDateOnly + 1) },
		func(w *Writer) { w.TimeOnly(-1) },
	} {
		w := NewWriter(0)
		if f(w); !errors.Is(w.Err(), ErrInvalidValue) {
			t.Fatal(w.Err())
		}
	}
}

func expectOK(t *testing.T, data []byte, f func(r *Reader)) {
	t.Helper()
	r := NewReader(data)
	if f(r); r.Err() != nil {
		t.Fatal(r.Err())
	}
}
