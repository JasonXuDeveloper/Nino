package nino

import (
	"encoding/binary"
	"math"
	"math/big"
	"math/bits"
	"strconv"
	"strings"
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
		w.NullLength()
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
