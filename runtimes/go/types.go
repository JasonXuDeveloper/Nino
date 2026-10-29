package nino

import (
	"encoding/binary"
	"math"
	"math/big"
	"math/bits"
	"strconv"
	"strings"
	"time"
)

// .NET value types without a Go equivalent. Each is read and written as .NET lays it out in memory (raw, as Nino
// copies it), except DateTimeOffset, BigInteger and Version, which have formats of their own.

// ---------------------------------------------------------------- decimal

// Decimal is System.Decimal as its 16 bytes: Flags (bits 16-23 the scale, bit 31 the sign), then the 96-bit
// coefficient as Hi (bits 64-95) and Lo (bits 0-63). The value is (-1)^sign * coefficient / 10^scale. The reader
// rejects a scale above 28 or other flag bits set (.NET copies them as they are, making a decimal its arithmetic
// misreads); the writer rejects them too.
type Decimal struct {
	Flags uint32
	Hi    uint32
	Lo    uint64
}

// DecimalSize is the size of a Decimal on the wire.
const DecimalSize = 16

// Decimal reads a decimal.
func (r *Reader) Decimal() Decimal {
	d := DecimalAt(r.Take(DecimalSize))
	if !d.Valid() {
		r.Fail(CodeInvalidData, "invalid decimal")
		return Decimal{}
	}
	return d
}

// Decimal writes a decimal.
func (w *Writer) Decimal(d Decimal) {
	if !d.Valid() {
		w.Fail(CodeInvalidValue, "invalid decimal")
		return
	}
	d.Put(w.Extend(DecimalSize))
}

// Valid reports whether the scale is at most 28 and no flag bit other than the scale and the sign is set: without
// the sign, rotated by 16 bits (the scale and the bits above it low, the bits below the scale high), at most 28.
func (d Decimal) Valid() bool { return bits.RotateLeft32(d.Flags&0x7FFFFFFF, 16) <= 28 }

// DecimalAt decodes the decimal at the start of b (zero when b is shorter).
func DecimalAt(b []byte) Decimal {
	if len(b) < DecimalSize {
		return Decimal{}
	}
	return Decimal{
		Flags: binary.LittleEndian.Uint32(b),
		Hi:    binary.LittleEndian.Uint32(b[4:]),
		Lo:    binary.LittleEndian.Uint64(b[8:]),
	}
}

// Put encodes d at the start of b (nothing when b is shorter).
func (d Decimal) Put(b []byte) {
	if len(b) < DecimalSize {
		return
	}
	binary.LittleEndian.PutUint32(b, d.Flags)
	binary.LittleEndian.PutUint32(b[4:], d.Hi)
	binary.LittleEndian.PutUint64(b[8:], d.Lo)
}

// Scale returns the number of digits after the decimal point.
func (d Decimal) Scale() int { return int(d.Flags >> 16 & 0xFF) }

// Negative reports whether the sign bit is set.
func (d Decimal) Negative() bool { return d.Flags>>31 != 0 }

// Coefficient returns the 96-bit integer of d, without its sign and scale.
func (d Decimal) Coefficient() *big.Int {
	c := new(big.Int).SetUint64(uint64(d.Hi))
	c.Lsh(c, 64)
	return c.Or(c, new(big.Int).SetUint64(d.Lo))
}

// String returns the exact decimal text of d, as .NET's ToString with the invariant culture ("-1.50").
func (d Decimal) String() string {
	var digits string
	if d.Hi == 0 {
		digits = strconv.FormatUint(d.Lo, 10)
	} else {
		digits = d.Coefficient().String()
	}
	if scale := d.Scale(); scale > 0 {
		if len(digits) <= scale {
			digits = strings.Repeat("0", scale-len(digits)+1) + digits
		}
		digits = digits[:len(digits)-scale] + "." + digits[len(digits)-scale:]
	}
	if d.Negative() {
		return "-" + digits
	}
	return digits
}

// ParseDecimal parses decimal text ("12", "-0.050", "+.5"): at most 28 digits after the point and a coefficient
// below 2^96, as System.Decimal holds. The scale is the number of digits after the point, trailing zeros kept.
func ParseDecimal(s string) (Decimal, error) {
	text := strings.TrimSpace(s)
	negative := false
	if text != "" && (text[0] == '-' || text[0] == '+') {
		negative = text[0] == '-'
		text = text[1:]
	}
	whole, fraction, _ := strings.Cut(text, ".")
	digits := whole + fraction
	if digits == "" || len(fraction) > 28 || strings.ContainsFunc(digits, func(c rune) bool { return c < '0' || c > '9' }) {
		return Decimal{}, &Error{Code: CodeInvalidValue, Detail: "decimal " + strconv.Quote(s)}
	}
	c, ok := new(big.Int).SetString(digits, 10)
	if !ok || c.BitLen() > 96 {
		return Decimal{}, &Error{Code: CodeInvalidValue, Detail: "decimal " + strconv.Quote(s)}
	}
	lo := new(big.Int).And(c, new(big.Int).SetUint64(math.MaxUint64)).Uint64()
	hi := new(big.Int).Rsh(c, 64).Uint64()
	d := Decimal{Flags: uint32(len(fraction)) << 16, Hi: uint32(hi), Lo: lo}
	if negative && c.Sign() != 0 {
		d.Flags |= 1 << 31
	}
	return d, nil
}

// ---------------------------------------------------------------- Guid

// Guid is System.Guid as its 16 bytes: a little-endian uint32, two little-endian uint16s, then 8 bytes in order.
type Guid [16]byte

// Guid reads a Guid.
func (r *Reader) Guid() (g Guid) {
	copy(g[:], r.Take(16))
	return g
}

// Guid writes a Guid.
func (w *Writer) Guid(g Guid) { w.buf = append(w.buf, g[:]...) }

// String returns the "D" form of g, as .NET writes it ("00112233-4455-6677-8899-aabbccddeeff").
func (g Guid) String() string {
	const hex = "0123456789abcdef"
	order := [16]int{3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15}
	var out [36]byte
	j := 0
	for i, k := range order {
		if i == 4 || i == 6 || i == 8 || i == 10 {
			out[j] = '-'
			j++
		}
		out[j], out[j+1] = hex[g[k]>>4], hex[g[k]&15]
		j += 2
	}
	return string(out[:])
}

// ParseGuid parses the "D", "N" or "B" form of a Guid (hex digits in either case).
func ParseGuid(s string) (Guid, error) {
	var g Guid
	bad := &Error{Code: CodeInvalidValue, Detail: "Guid " + strconv.Quote(s)}
	text := s
	if len(text) == 38 && text[0] == '{' && text[37] == '}' {
		text = text[1:37]
	}
	if len(text) == 36 {
		if text[8] != '-' || text[13] != '-' || text[18] != '-' || text[23] != '-' {
			return g, bad
		}
		text = text[:8] + text[9:13] + text[14:18] + text[19:23] + text[24:]
	}
	if len(text) != 32 {
		return g, bad
	}
	order := [16]int{3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15}
	for i, k := range order {
		hi, lo := hexValue(text[2*i]), hexValue(text[2*i+1])
		if hi < 0 || lo < 0 {
			return Guid{}, bad
		}
		g[k] = byte(hi<<4 | lo)
	}
	return g, nil
}

func hexValue(c byte) int {
	switch {
	case c >= '0' && c <= '9':
		return int(c - '0')
	case c >= 'a' && c <= 'f':
		return int(c-'a') + 10
	case c >= 'A' && c <= 'F':
		return int(c-'A') + 10
	}
	return -1
}

// ---------------------------------------------------------------- dates and times

// Ticks are 100 ns intervals; .NET dates count them from 0001-01-01T00:00:00.
const (
	// MaxTicks is DateTime.MaxValue.Ticks.
	MaxTicks = 3_155_378_975_999_999_999
	// unixEpochTicks is the ticks of 1970-01-01T00:00:00.
	unixEpochTicks = 621_355_968_000_000_000
	ticksPerSecond = 10_000_000
)

// DateTimeKind is the kind of a DateTime.
type DateTimeKind uint8

const (
	KindUnspecified DateTimeKind = 0
	KindUtc         DateTimeKind = 1
	KindLocal       DateTimeKind = 2
)

// DateTime is System.DateTime as its 8 bytes: the ticks in the low 62 bits, the kind in the top 2 (3 is a local
// time in the repeated hour of a daylight saving change). Read and written as is.
type DateTime uint64

// NewDateTime returns the DateTime of ticks (masked to 62 bits) and kind.
func NewDateTime(ticks int64, kind DateTimeKind) DateTime {
	return DateTime(uint64(ticks)&(1<<62-1) | uint64(kind&3)<<62)
}

// DateTimeOf returns t as a UTC DateTime (to the 100 ns), or false when t is out of DateTime's range.
func DateTimeOf(t time.Time) (DateTime, bool) {
	sec, nsec := t.Unix(), int64(t.Nanosecond())
	// Range check in seconds first, so the tick product cannot overflow.
	const minSec, maxSec = -unixEpochTicks / ticksPerSecond, (MaxTicks - unixEpochTicks) / ticksPerSecond
	if sec < minSec || sec > maxSec {
		return 0, false
	}
	ticks := unixEpochTicks + sec*ticksPerSecond + nsec/100
	if ticks < 0 || ticks > MaxTicks {
		return 0, false
	}
	return NewDateTime(ticks, KindUtc), true
}

// Ticks returns the ticks of d.
func (d DateTime) Ticks() int64 { return int64(d & (1<<62 - 1)) }

// Kind returns the kind of d (3 for a local time in the repeated hour, which .NET reports as Local).
func (d DateTime) Kind() DateTimeKind { return DateTimeKind(d >> 62) }

// Time returns d as a time.Time in UTC: its date and time as they are, whatever its kind.
func (d DateTime) Time() time.Time { return ticksTime(d.Ticks()) }

func ticksTime(ticks int64) time.Time {
	t := ticks - unixEpochTicks
	sec, rem := t/ticksPerSecond, t%ticksPerSecond
	if rem < 0 {
		sec--
		rem += ticksPerSecond
	}
	return time.Unix(sec, rem*100).UTC()
}

// Valid reports whether d is a DateTime .NET writes: ticks at most MaxTicks, any kind (3 included: .NET writes it for
// a local time in the repeated hour of a daylight saving change).
func (d DateTime) Valid() bool { return d.Ticks() <= MaxTicks }

// DateTime reads a DateTime. Ticks past MaxTicks fail with CodeInvalidData (the specification's SHOULD; .NET copies
// any bits).
func (r *Reader) DateTime() DateTime {
	d := DateTime(r.U64())
	if !d.Valid() {
		r.Fail(CodeInvalidData, "invalid DateTime")
		return 0
	}
	return d
}

// DateTime writes a DateTime; one .NET cannot hold fails with CodeInvalidValue.
func (w *Writer) DateTime(d DateTime) {
	if !d.Valid() {
		w.Fail(CodeInvalidValue, "DateTime out of range")
		return
	}
	w.U64(uint64(d))
}

// TimeSpan is System.TimeSpan: its ticks.
type TimeSpan int64

// TimeSpanOf returns d as a TimeSpan, to the 100 ns.
func TimeSpanOf(d time.Duration) TimeSpan { return TimeSpan(d / 100) }

// Duration returns s as a time.Duration, or false when it is out of time.Duration's range (about 292 years).
func (s TimeSpan) Duration() (time.Duration, bool) {
	hi, lo := bits.Mul64(uint64(abs64(int64(s))), 100)
	if hi != 0 || lo > math.MaxInt64 {
		return 0, false
	}
	if s < 0 {
		return -time.Duration(lo), true
	}
	return time.Duration(lo), true
}

func abs64(v int64) int64 {
	if v < 0 {
		if v == math.MinInt64 {
			return math.MaxInt64
		}
		return -v
	}
	return v
}

// TimeSpan reads a TimeSpan.
func (r *Reader) TimeSpan() TimeSpan { return TimeSpan(r.I64()) }

// TimeSpan writes a TimeSpan.
func (w *Writer) TimeSpan(s TimeSpan) { w.I64(int64(s)) }

// DateTimeOffset is System.DateTimeOffset: the UTC ticks and the offset in minutes (10 bytes: int64, int16).
type DateTimeOffset struct {
	UtcTicks      int64
	OffsetMinutes int16
}

// DateTimeOffsetSize is the size of a DateTimeOffset on the wire.
const DateTimeOffsetSize = 10

const maxOffsetMinutes = 14 * 60

// Valid reports whether .NET can hold o: an offset of at most 14 hours, a UTC and a local time within
// DateTime's range.
func (o DateTimeOffset) Valid() bool {
	m := int64(o.OffsetMinutes)
	if m < -maxOffsetMinutes || m > maxOffsetMinutes || uint64(o.UtcTicks) > MaxTicks {
		return false
	}
	local := o.UtcTicks + m*60*ticksPerSecond
	return local >= 0 && local <= MaxTicks
}

// Time returns o as a time.Time in a fixed zone of its offset.
func (o DateTimeOffset) Time() time.Time {
	return ticksTime(o.UtcTicks).In(time.FixedZone("", int(o.OffsetMinutes)*60))
}

// DateTimeOffsetOf returns t (to the 100 ns, its offset to the minute), or false when .NET cannot hold it.
func DateTimeOffsetOf(t time.Time) (DateTimeOffset, bool) {
	d, ok := DateTimeOf(t)
	_, offset := t.Zone()
	o := DateTimeOffset{UtcTicks: d.Ticks(), OffsetMinutes: int16(offset / 60)}
	return o, ok && offset%60 == 0 && offset/60 >= -maxOffsetMinutes && offset/60 <= maxOffsetMinutes && o.Valid()
}

// DateTimeOffset reads a DateTimeOffset; one .NET cannot hold fails with CodeInvalidData, as in .NET.
func (r *Reader) DateTimeOffset() DateTimeOffset {
	o := DateTimeOffsetAt(r.Take(DateTimeOffsetSize))
	if !o.Valid() {
		r.Fail(CodeInvalidData, "invalid DateTimeOffset")
		return DateTimeOffset{}
	}
	return o
}

// DateTimeOffsetAt decodes the DateTimeOffset at the start of b, unchecked (zero when b is shorter).
func DateTimeOffsetAt(b []byte) DateTimeOffset {
	if len(b) < DateTimeOffsetSize {
		return DateTimeOffset{}
	}
	return DateTimeOffset{
		UtcTicks:      int64(binary.LittleEndian.Uint64(b)),
		OffsetMinutes: int16(binary.LittleEndian.Uint16(b[8:])),
	}
}

// DateTimeOffset writes a DateTimeOffset; one .NET cannot hold fails with CodeInvalidValue.
func (w *Writer) DateTimeOffset(o DateTimeOffset) {
	if !o.Valid() {
		w.Fail(CodeInvalidValue, "DateTimeOffset out of range")
		return
	}
	b := w.Extend(DateTimeOffsetSize)
	if len(b) < DateTimeOffsetSize {
		return
	}
	binary.LittleEndian.PutUint64(b, uint64(o.UtcTicks))
	binary.LittleEndian.PutUint16(b[8:], uint16(o.OffsetMinutes))
}

// DateOnly is System.DateOnly: its day number (days since 0001-01-01), 0 to MaxDateOnly; the reader and the
// writer reject others.
type DateOnly int32

// MaxDateOnly is the day number of 9999-12-31, DateOnly.MaxValue.
const MaxDateOnly DateOnly = 3652058

// MaxTimeOnly is the ticks of 23:59:59.9999999, TimeOnly.MaxValue.
const MaxTimeOnly TimeOnly = 863999999999

// Valid reports whether d is in DateOnly's range.
func (d DateOnly) Valid() bool { return uint32(d) <= uint32(MaxDateOnly) } // negative is above as unsigned

// Valid reports whether t is in TimeOnly's range.
func (t TimeOnly) Valid() bool { return uint64(t) <= uint64(MaxTimeOnly) } // negative is above as unsigned

// Time returns the date at midnight UTC.
func (d DateOnly) Time() time.Time { return ticksTime(int64(d) * 86400 * ticksPerSecond) }

// DateOnly reads a DateOnly.
func (r *Reader) DateOnly() DateOnly {
	d := DateOnly(r.I32())
	if !d.Valid() {
		r.Fail(CodeInvalidData, "invalid DateOnly")
		return 0
	}
	return d
}

// DateOnly writes a DateOnly.
func (w *Writer) DateOnly(d DateOnly) {
	if !d.Valid() {
		w.Fail(CodeInvalidValue, "DateOnly out of range")
		return
	}
	w.I32(int32(d))
}

// TimeOnly is System.TimeOnly: ticks since midnight, 0 to MaxTimeOnly; the reader and the writer reject others.
type TimeOnly int64

// TimeOnly reads a TimeOnly.
func (r *Reader) TimeOnly() TimeOnly {
	t := TimeOnly(r.I64())
	if !t.Valid() {
		r.Fail(CodeInvalidData, "invalid TimeOnly")
		return 0
	}
	return t
}

// TimeOnly writes a TimeOnly.
func (w *Writer) TimeOnly(t TimeOnly) {
	if !t.Valid() {
		w.Fail(CodeInvalidValue, "TimeOnly out of range")
		return
	}
	w.I64(int64(t))
}

// ---------------------------------------------------------------- 128-bit integers and Half

// Int128 is System.Int128: the low 64 bits, then the high 64 bits (two's complement).
type Int128 struct {
	Lo, Hi uint64
}

// UInt128 is System.UInt128: the low 64 bits, then the high 64 bits.
type UInt128 struct {
	Lo, Hi uint64
}

// I128 reads an Int128.
func (r *Reader) I128() Int128 {
	b := r.Take(16)
	if len(b) < 16 {
		return Int128{}
	}
	return Int128{Lo: binary.LittleEndian.Uint64(b), Hi: binary.LittleEndian.Uint64(b[8:])}
}

// I128 writes an Int128.
func (w *Writer) I128(v Int128) { w.U64(v.Lo); w.U64(v.Hi) }

// U128 reads a UInt128.
func (r *Reader) U128() UInt128 {
	v := r.I128()
	return UInt128(v)
}

// U128 writes a UInt128.
func (w *Writer) U128(v UInt128) { w.U64(v.Lo); w.U64(v.Hi) }

// Big returns v as a big.Int.
func (v Int128) Big() *big.Int {
	b := UInt128(v).Big()
	if int64(v.Hi) < 0 {
		b.Sub(b, new(big.Int).Lsh(big.NewInt(1), 128))
	}
	return b
}

// Big returns v as a big.Int.
func (v UInt128) Big() *big.Int {
	b := new(big.Int).SetUint64(v.Hi)
	b.Lsh(b, 64)
	return b.Or(b, new(big.Int).SetUint64(v.Lo))
}

// Int128Of returns b as an Int128, or false when it is out of range.
func Int128Of(b *big.Int) (Int128, bool) {
	limit := new(big.Int).Lsh(big.NewInt(1), 127)
	if b.Cmp(limit) >= 0 || b.Cmp(new(big.Int).Neg(limit)) < 0 {
		return Int128{}, false
	}
	m := new(big.Int).Set(b)
	if m.Sign() < 0 {
		m.Add(m, new(big.Int).Lsh(big.NewInt(1), 128))
	}
	u, _ := UInt128Of(m)
	return Int128(u), true
}

// UInt128Of returns b as a UInt128, or false when it is out of range.
func UInt128Of(b *big.Int) (UInt128, bool) {
	if b.Sign() < 0 || b.BitLen() > 128 {
		return UInt128{}, false
	}
	lo := new(big.Int).And(b, new(big.Int).SetUint64(math.MaxUint64)).Uint64()
	hi := new(big.Int).Rsh(b, 64).Uint64()
	return UInt128{Lo: lo, Hi: hi}, true
}

// Half is System.Half: the bits of an IEEE 754 binary16.
type Half uint16

// Half reads a Half.
func (r *Reader) Half() Half { return Half(r.U16()) }

// Half writes a Half.
func (w *Writer) Half(h Half) { w.U16(uint16(h)) }

// Float32 returns h as a float32 (exactly; a NaN keeps its payload's top bits).
func (h Half) Float32() float32 {
	sign := uint32(h>>15) << 31
	exp := uint32(h>>10) & 0x1F
	frac := uint32(h) & 0x3FF
	switch {
	case exp == 0x1F:
		return math.Float32frombits(sign | 0xFF<<23 | frac<<13)
	case exp != 0:
		return math.Float32frombits(sign | (exp+112)<<23 | frac<<13)
	case frac == 0:
		return math.Float32frombits(sign)
	}
	// Subnormal: normalize.
	e := uint32(113)
	for frac&0x400 == 0 {
		frac <<= 1
		e--
	}
	return math.Float32frombits(sign | e<<23 | (frac&0x3FF)<<13)
}

// HalfOf returns f rounded to the nearest Half (ties to even), as .NET converts it.
func HalfOf(f float32) Half {
	b := math.Float32bits(f)
	sign := uint16(b>>16) & 0x8000
	exp := int32(b>>23) & 0xFF
	frac := b & 0x7FFFFF
	if exp == 0xFF {
		if frac == 0 {
			return Half(sign | 0x7C00)
		}
		return Half(sign | 0x7E00 | uint16(frac>>13))
	}
	e := exp - 127 + 15
	if e >= 0x1F {
		return Half(sign | 0x7C00)
	}
	if e <= 0 {
		if e < -10 {
			return Half(sign)
		}
		m := frac | 0x800000
		shift := uint32(14 - e)
		half := m >> shift
		rem := m & (1<<shift - 1)
		mid := uint32(1) << (shift - 1)
		if rem > mid || (rem == mid && half&1 != 0) {
			half++
		}
		return Half(sign | uint16(half))
	}
	half := uint32(e)<<10 | frac>>13
	rem := frac & 0x1FFF
	if rem > 0x1000 || (rem == 0x1000 && half&1 != 0) {
		half++
	}
	return Half(sign | uint16(half))
}

// ---------------------------------------------------------------- BigInteger and Version

// BigInteger reads a System.Numerics.BigInteger: a byte count (not null), then its two's complement bytes, least
// significant first. Never nil unless the read failed. Bytes that are not the fewest (and a count of 0) fail with
// CodeInvalidData (the specification's SHOULD; .NET accepts them).
func (r *Reader) BigInteger() *big.Int {
	n := r.Length(1)
	if n < 0 {
		if r.err == nil {
			r.badLength(-1)
		}
		return nil
	}
	b := r.Take(n)
	if len(b) != n {
		return nil
	}
	if n == 0 || n > 1 && (b[n-1] == 0 && b[n-2] < 0x80 || b[n-1] == 0xFF && b[n-2] >= 0x80) {
		r.Fail(CodeInvalidData, "BigInteger not in its fewest bytes")
		return nil
	}
	be := make([]byte, n)
	for i, c := range b {
		be[n-1-i] = c
	}
	v := new(big.Int).SetBytes(be)
	if n > 0 && b[n-1]&0x80 != 0 {
		v.Sub(v, new(big.Int).Lsh(big.NewInt(1), uint(8*n)))
	}
	return v
}

// BigInteger writes a BigInteger in the fewest bytes, as .NET does (nil is zero, BigInteger's default).
func (w *Writer) BigInteger(v *big.Int) {
	var be []byte
	switch {
	case v == nil || v.Sign() == 0:
		be = []byte{0}
	case v.Sign() > 0:
		be = v.Bytes()
		if be[0]&0x80 != 0 {
			be = append([]byte{0}, be...)
		}
	default:
		// Two's complement of -v: the bits of -v - 1, inverted.
		m := new(big.Int).Neg(v)
		m.Sub(m, big.NewInt(1))
		be = m.Bytes()
		for i := range be {
			be[i] = ^be[i]
		}
		if len(be) == 0 || be[0]&0x80 == 0 {
			be = append([]byte{0xFF}, be...)
		}
	}
	w.Length(len(be))
	b := w.Extend(len(be))
	for i := range b {
		b[i] = be[len(be)-1-i]
	}
}

// Version is System.Version. Build and Revision are -1 when undefined.
type Version struct {
	Major, Minor, Build, Revision int32
}

// Version reads a Version: nil for null. As .NET: a negative major or minor fails with CodeInvalidData, a negative
// build leaves build and revision undefined, a negative revision leaves it undefined.
func (r *Reader) Version() *Version {
	major := r.I32()
	if major == NullLength || r.err != nil {
		return nil
	}
	b := r.Take(12)
	if len(b) < 12 {
		return nil
	}
	v := &Version{
		Major:    major,
		Minor:    int32(binary.LittleEndian.Uint32(b)),
		Build:    int32(binary.LittleEndian.Uint32(b[4:])),
		Revision: int32(binary.LittleEndian.Uint32(b[8:])),
	}
	if v.Major|v.Minor < 0 {
		r.Fail(CodeInvalidData, "invalid Version")
		return nil
	}
	if v.Build < 0 {
		v.Build, v.Revision = -1, -1
	} else if v.Revision < 0 {
		v.Revision = -1
	}
	return v
}

// Version writes a Version: nil is null. One .NET cannot hold fails with CodeInvalidValue.
func (w *Writer) Version(v *Version) {
	if v == nil {
		w.Null()
		return
	}
	if v.Major < 0 || v.Minor < 0 || v.Build < -1 || v.Revision < -1 || (v.Build == -1 && v.Revision != -1) {
		w.Fail(CodeInvalidValue, "Version")
		return
	}
	w.I32(v.Major)
	w.I32(v.Minor)
	w.I32(v.Build)
	w.I32(v.Revision)
}

// String returns v as .NET writes it ("1.2", "1.2.3", "1.2.3.4").
func (v Version) String() string {
	s := strconv.Itoa(int(v.Major)) + "." + strconv.Itoa(int(v.Minor))
	if v.Build >= 0 {
		s += "." + strconv.Itoa(int(v.Build))
		if v.Revision >= 0 {
			s += "." + strconv.Itoa(int(v.Revision))
		}
	}
	return s
}
