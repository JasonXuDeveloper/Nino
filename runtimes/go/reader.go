package nino

import (
	"encoding/binary"
	"math"
	"strconv"
)

// Reader reads values from bytes written by Nino. Every read is bounds checked, and every length is checked
// against the bytes left before anything is allocated.
//
// Errors are sticky: the first failure is recorded (see [Reader.Err]), the unread data is dropped, and every later
// read returns a zero value without reading. Generated code reads a whole value and checks Err once at the end
// (or once per element in a loop); the top-level [Deserialize] returns no value when Err is set. Nothing in the
// reader panics on any input.
//
// A Reader holds no reference to anything but the data, its own queues and the blocks strings are read into:
// reuse one with [Reader.Reset] to read without allocating it again.
type Reader struct {
	// The unread bytes up to the current end: the end of the data, of the region being read, or of a deferred
	// body. Always a reslice of data, so its capacity gives the position (see pos).
	buf []byte
	// The whole data, for positions and for the sections of deferred bodies.
	data []byte
	err  *Error
	// Open class bodies and open regions (see InlineDepth).
	depth   int32
	regions int32
	// Deferred bodies and open struct bodies, created on first use.
	ctx *readContext
	// The room left in the block short strings are converted into (see [Reader.shortString]), kept across Reset.
	text []byte
	// The strings left in the block StringPtr and Utf8Ptr point into (see [Reader.stringBox]), kept across Reset.
	boxes []string
}

// NewReader returns a reader of data.
func NewReader(data []byte) *Reader {
	r := &Reader{}
	r.Reset(data)
	return r
}

// Reset starts reading data from its start, keeping the queues allocated by earlier reads.
func (r *Reader) Reset(data []byte) {
	// The capacity is cut to the length: positions count from the start of data to the end of its capacity.
	data = data[:len(data):len(data)]
	r.buf = data
	r.data = data
	r.err = nil
	r.depth = 0
	r.regions = 0
	if r.ctx != nil {
		r.ctx.reset()
	}
}

// Err returns the first failure, or nil.
func (r *Reader) Err() error {
	if r.err == nil {
		return nil
	}
	return r.err
}

// Failed reports whether a read failed. Loops in generated code stop on it.
func (r *Reader) Failed() bool { return r.err != nil }

// Pos returns the number of bytes consumed from the start of the data.
func (r *Reader) Pos() int { return cap(r.data) - cap(r.buf) }

// Remaining returns the number of bytes left (in the region being read, inside one).
func (r *Reader) Remaining() int { return len(r.buf) }

// More reports whether bytes are left: the end-of-region check of version-tolerant members and keyed members.
func (r *Reader) More() bool { return len(r.buf) != 0 }

// Fail records an error of code at the current position, unless one is recorded already, and drops the unread
// data. For generated and custom code that finds invalid data.
//
//go:noinline
func (r *Reader) Fail(code Code, detail string) {
	if r.err == nil {
		r.err = &Error{Code: code, Offset: r.Pos(), Detail: detail}
	}
	r.buf = r.buf[:0]
}

//go:noinline
func (r *Reader) eof() { r.Fail(CodeEndOfData, "") }

//go:noinline
func (r *Reader) badLength(n int64) { r.Fail(CodeInvalidLength, strconv.FormatInt(n, 10)) }

// Take consumes n bytes and returns them (a slice of the data, valid as long as the data is), or fails and returns
// nil. A run of fixed-size values is read with one Take and decoded at constant offsets: generated code checks the
// length first (`b := r.Take(12); if len(b) < 12 { return }`), which also lets the compiler drop the bounds checks.
func (r *Reader) Take(n int) []byte {
	b := r.buf
	if uint(n) > uint(len(b)) {
		r.eof()
		return nil
	}
	r.buf = b[n:]
	return b[:n:n]
}

// Skip consumes n bytes.
func (r *Reader) Skip(n int) {
	if uint(n) > uint(len(r.buf)) {
		r.eof()
		return
	}
	r.buf = r.buf[n:]
}

// Bool reads a bool. A byte other than 0 or 1 fails with CodeInvalidData (the specification's SHOULD; .NET reads
// any byte but 0 as true).
func (r *Reader) Bool() bool { return r.flag() }

// flag reads a byte that must be 0 or 1.
func (r *Reader) flag() bool {
	b := r.buf
	if len(b) < 1 {
		r.eof()
		return false
	}
	if b[0] > 1 {
		r.Fail(CodeInvalidData, "invalid bool or presence byte "+strconv.Itoa(int(b[0])))
		return false
	}
	r.buf = b[1:]
	return b[0] == 1
}

// BoolAt decodes a bool byte taken from a raw block (a raw struct's field): it fails for anything but 0 or 1, as
// [Reader.Bool] does (.NET copies such bytes as they are).
func (r *Reader) BoolAt(b byte) bool {
	if b > 1 {
		r.Fail(CodeInvalidData, "invalid bool "+strconv.Itoa(int(b)))
		return false
	}
	return b == 1
}

// U8 reads a byte.
func (r *Reader) U8() uint8 {
	b := r.buf
	if len(b) < 1 {
		r.eof()
		return 0
	}
	r.buf = b[1:]
	return b[0]
}

// I8 reads an sbyte.
func (r *Reader) I8() int8 { return int8(r.U8()) }

// U16 reads a ushort.
func (r *Reader) U16() uint16 {
	b := r.buf
	if len(b) < 2 {
		r.eof()
		return 0
	}
	r.buf = b[2:]
	return binary.LittleEndian.Uint16(b)
}

// I16 reads a short.
func (r *Reader) I16() int16 { return int16(r.U16()) }

// Char reads a char: one UTF-16 code unit.
func (r *Reader) Char() uint16 { return r.U16() }

// U32 reads a uint.
func (r *Reader) U32() uint32 {
	b := r.buf
	if len(b) < 4 {
		r.eof()
		return 0
	}
	r.buf = b[4:]
	return binary.LittleEndian.Uint32(b)
}

// I32 reads an int.
func (r *Reader) I32() int32 { return int32(r.U32()) }

// U64 reads a ulong (also UIntPtr, always 8 bytes).
func (r *Reader) U64() uint64 {
	b := r.buf
	if len(b) < 8 {
		r.eof()
		return 0
	}
	r.buf = b[8:]
	return binary.LittleEndian.Uint64(b)
}

// I64 reads a long (also IntPtr, always 8 bytes).
func (r *Reader) I64() int64 { return int64(r.U64()) }

// F32 reads a float, its bits kept (NaN payloads included).
func (r *Reader) F32() float32 { return math.Float32frombits(r.U32()) }

// F64 reads a double, its bits kept (NaN payloads included).
func (r *Reader) F64() float64 { return math.Float64frombits(r.U64()) }

// Present reads the presence byte of a nullable value, a Tuple or a sealed class: true when the value follows. A
// byte other than 0 or 1 fails with CodeInvalidData (as the TypeScript runtime; .NET reads any byte but 0 as
// present).
func (r *Reader) Present() bool { return r.flag() }

// TypeID reads the type id in front of a polymorphic value (0 for null).
func (r *Reader) TypeID() int32 { return r.I32() }

// Length reads a collection length: -1 for null, else a count n for which n * minSize bytes are left (minSize is
// the fewest bytes an element takes on the wire, at least 1 unless the elements may take none). Any other value
// fails with CodeInvalidLength and returns -1, so a failed read reads as null.
func (r *Reader) Length(minSize int) int {
	n := r.I32()
	if n < 0 || r.err != nil {
		if n < NullLength {
			r.badLength(int64(n))
		}
		return -1
	}
	if uint64(n)*uint64(max(minSize, 0)) > uint64(len(r.buf)) {
		r.badLength(int64(n))
		return -1
	}
	return int(n)
}

// ByteArray reads a byte[]: length, then the bytes (a copy). nil for null.
func (r *Reader) ByteArray() []byte {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	b := r.Take(n)
	if len(b) != n {
		return nil
	}
	out := make([]byte, n)
	copy(out, b)
	return out
}

// ByteArrayView reads a byte[] without copying it: a slice of the data (nil for null), valid as long as the data is.
func (r *Reader) ByteArrayView() []byte {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	return r.Take(n)
}

// SliceCap returns the capacity to give a slice of n elements of elemSize bytes in memory before reading them:
// n, unless they would take more than 16 bytes of memory per byte of data left (or 16 KB), and then what fits
// that bound. Data claiming more elements than it holds then fails after allocating a bounded multiple of the
// memory of the elements it held (see [ReadSlice], which grows the slice as it reads).
func (r *Reader) SliceCap(n, elemSize int) int {
	return capacity(n, elemSize, len(r.buf))
}

func capacity(n, slotSize, remaining int) int {
	if n <= 0 {
		return 0
	}
	budget := max(uint64(remaining)*amplification, presizeFloor)
	slot := uint64(max(slotSize, 1))
	if uint64(n)*slot <= budget {
		return n
	}
	return int(max(budget/slot, 1))
}
