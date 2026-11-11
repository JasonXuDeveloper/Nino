package nino

import (
	"encoding/binary"
	"math"
	"math/bits"
	"time"
)

// .NET's date and time types (DateTime, TimeSpan, DateTimeOffset, DateOnly, TimeOnly) as their ticks, with the
// ranges .NET accepts.

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
func (d DateOnly) Valid() bool { return d >= 0 && d <= MaxDateOnly }

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
