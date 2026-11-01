package nino

import (
	"encoding/binary"
	"math"
	"strconv"
)

// Writer writes values in Nino's format, appending to a buffer that grows by doubling (amortized O(1) per byte).
// A writer reused with [Writer.Reset] keeps its buffer: writing then allocates nothing.
//
// Errors are sticky, as in [Reader]: the first failure (a value with no encoding, a string over 2^31-1 chars, a
// cycle...) is recorded in [Writer.Err] and the output is then not valid.
type Writer struct {
	buf []byte
	err *Error
	// Open class bodies and open regions (see InlineDepth).
	depth   int32
	regions int32
	// Deferred bodies, created on first use.
	ctx *writeContext
}

// NewWriter returns a writer with room for size bytes.
func NewWriter(size int) *Writer {
	return &Writer{buf: make([]byte, 0, max(size, 0))}
}

// Reset empties the writer, keeping its buffer (and the queues of deferred bodies).
func (w *Writer) Reset() {
	w.buf = w.buf[:0]
	w.err = nil
	w.depth = 0
	w.regions = 0
	if w.ctx != nil {
		w.ctx.reset()
	}
}

// Bytes returns the bytes written: a slice of the writer's buffer, valid until it writes again or is reset.
func (w *Writer) Bytes() []byte { return w.buf }

// Len returns the number of bytes written.
func (w *Writer) Len() int { return len(w.buf) }

// Err returns the first failure, or nil.
func (w *Writer) Err() error {
	if w.err == nil {
		return nil
	}
	return w.err
}

// Fail records an error of code, unless one is recorded already. For generated and custom code that finds a value
// with no encoding.
//
//go:noinline
func (w *Writer) Fail(code Code, detail string) {
	if w.err == nil {
		w.err = &Error{Code: code, Offset: len(w.buf), Detail: detail}
	}
}

// Extend appends n bytes and returns them, for a run of fixed-size values written at constant offsets. The bytes
// are not cleared: write every one of them, or use [Writer.ExtendZero]. A negative n fails and returns nil.
func (w *Writer) Extend(n int) []byte {
	l := len(w.buf)
	if uint(n) > uint(cap(w.buf)-l) {
		if n < 0 {
			w.Fail(CodeTooLarge, strconv.Itoa(n))
			return nil
		}
		w.grow(n)
	}
	w.buf = w.buf[:l+n]
	return w.buf[l : l+n : l+n]
}

// ExtendZero is [Writer.Extend] with the bytes cleared: for raw structs with padding, which is written as zeros.
func (w *Writer) ExtendZero(n int) []byte {
	b := w.Extend(n)
	clear(b)
	return b
}

//go:noinline
func (w *Writer) grow(n int) {
	need := len(w.buf) + n
	c := max(2*cap(w.buf), need, 256)
	b := make([]byte, len(w.buf), c)
	copy(b, w.buf)
	w.buf = b
}

// Raw appends bytes as they are, without a length.
func (w *Writer) Raw(b []byte) { w.buf = append(w.buf, b...) }

// Bool writes a bool as 0 or 1.
func (w *Writer) Bool(v bool) {
	var b byte
	if v {
		b = 1
	}
	w.buf = append(w.buf, b)
}

// U8 writes a byte.
func (w *Writer) U8(v uint8) { w.buf = append(w.buf, v) }

// I8 writes an sbyte.
func (w *Writer) I8(v int8) { w.buf = append(w.buf, uint8(v)) }

// U16 writes a ushort.
func (w *Writer) U16(v uint16) { w.buf = binary.LittleEndian.AppendUint16(w.buf, v) }

// I16 writes a short.
func (w *Writer) I16(v int16) { w.U16(uint16(v)) }

// Char writes a char: one UTF-16 code unit.
func (w *Writer) Char(v uint16) { w.U16(v) }

// U32 writes a uint.
func (w *Writer) U32(v uint32) { w.buf = binary.LittleEndian.AppendUint32(w.buf, v) }

// I32 writes an int.
func (w *Writer) I32(v int32) { w.U32(uint32(v)) }

// U64 writes a ulong.
func (w *Writer) U64(v uint64) { w.buf = binary.LittleEndian.AppendUint64(w.buf, v) }

// I64 writes a long.
func (w *Writer) I64(v int64) { w.U64(uint64(v)) }

// F32 writes a float, its bits kept.
func (w *Writer) F32(v float32) { w.U32(math.Float32bits(v)) }

// F64 writes a double, its bits kept.
func (w *Writer) F64(v float64) { w.U64(math.Float64bits(v)) }

// NullLength writes the null length (-1) of an array or collection (.NET's WriteNullLength).
func (w *Writer) NullLength() { w.buf = append(w.buf, 0xFF, 0xFF, 0xFF, 0xFF) }

// Present writes the presence byte of a nullable value, a Tuple or a sealed class.
func (w *Writer) Present(present bool) { w.Bool(present) }

// TypeID writes the type id in front of a polymorphic value (0 for null).
func (w *Writer) TypeID(id int32) { w.I32(id) }

// Length writes a collection length; it fails for one past 2^31-1.
func (w *Writer) Length(n int) {
	if uint(n) > maxInt32 {
		w.Fail(CodeTooLarge, strconv.Itoa(n))
		return
	}
	w.U32(uint32(n))
}

// ByteArray writes a byte[]: nil is null.
func (w *Writer) ByteArray(b []byte) {
	if b == nil {
		w.NullLength()
		return
	}
	w.Length(len(b))
	w.buf = append(w.buf, b...)
}

// ReserveInt32 appends 4 bytes to be patched later with [Writer.PatchInt32] and returns their position.
func (w *Writer) ReserveInt32() int {
	p := len(w.buf)
	w.buf = append(w.buf, 0, 0, 0, 0)
	return p
}

// PatchInt32 writes v over the 4 bytes at pos, returned by [Writer.ReserveInt32].
func (w *Writer) PatchInt32(pos int, v int32) {
	if pos < 0 || pos > len(w.buf)-4 {
		w.Fail(CodeInvalidValue, "patch out of range")
		return
	}
	binary.LittleEndian.PutUint32(w.buf[pos:], uint32(v))
}
