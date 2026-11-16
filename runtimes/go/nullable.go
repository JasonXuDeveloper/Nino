package nino

import (
	"encoding/binary"
	"unsafe"
)

// .NET writes a nullable raw T (a number, a date, a raw struct) in three ways, and the Go type says which: a *T is a
// single T? (a presence byte, then the value when present; in a raw struct's memory, a slot at its offset), a
// NullableSlot[T] a T? as the element of a sequence, and a NinoNullable[T] a NinoNullable<T> wherever it is.

// NinoNullable is .NET's Nino.Core.NinoNullable<T>: a nullable raw T (a number, a date, a raw struct) that is written
// as its memory wherever it is (a member, an element of any collection, a field of a raw struct), 2 * size(T)
// bytes: the presence, as wide as T (0 or 1 in its first byte, zeros above), then the value, zeros when absent.
// The zero value is absent. A .NET T? is not these bytes: it is a *T alone and a [NullableSlot] in a sequence.
//
// Value is written only when HasValue is set: an absent value is zeros on the wire whatever Value holds. Reading
// rejects a presence other than 0 or 1, a byte set above it and an absent value that is not zeros, as .NET's
// untrusted mode does ([Reader.NinoNullableAt]).
type NinoNullable[T any] struct {
	HasValue bool
	Value    T
}

// NinoNullableOf returns the NinoNullable that holds value.
func NinoNullableOf[T any](value T) NinoNullable[T] {
	return NinoNullable[T]{HasValue: true, Value: value}
}

// Get returns the value and whether there is one (the zero T when absent).
func (n NinoNullable[T]) Get() (T, bool) {
	if !n.HasValue {
		var zero T
		return zero, false
	}
	return n.Value, true
}

// NullableSlot is a .NET T? of a raw T (a number, a date, a raw struct) as the element of a sequence (the
// wire-format specification, 6.2): a slot of align(T) + size(T) bytes, the presence byte first, zeros up to T's
// alignment, then the value, zeros when absent. It is the element type of every collection of them:
// []NullableSlot[int32] is an int?[], a List<int?>, a HashSet<int?> or a Queue<int?>, and a grid of them an int?[,].
// The zero value is absent. A single T? (a member, a map's key or value, a tuple's item, a field of a raw struct) is
// not this type: it is a *T. Nor is a NinoNullable<T>, a [NinoNullable].
//
// Value is written only when HasValue is set: an absent value is zeros on the wire whatever Value holds. Reading
// rejects a presence other than 0 or 1 and leaves Value zero when absent, whatever the data holds there, as .NET
// does.
type NullableSlot[T any] struct {
	HasValue bool
	Value    T
}

// NullableSlotOf returns the NullableSlot that holds value.
func NullableSlotOf[T any](value T) NullableSlot[T] {
	return NullableSlot[T]{HasValue: true, Value: value}
}

// Get returns the value and whether there is one (the zero T when absent).
func (n NullableSlot[T]) Get() (T, bool) {
	return NinoNullable[T](n).Get()
}

// A NullableSlot[T] and a NinoNullable[T] have the same fields, so a slice of one is the memory of a slice of the
// other (nil for nil): the blocks of both are one implementation.

func asNinoNullables[T any](v []NullableSlot[T]) []NinoNullable[T] {
	return unsafe.Slice((*NinoNullable[T])(unsafe.Pointer(unsafe.SliceData(v))), len(v))
}

func asNullableSlots[T any](v []NinoNullable[T]) []NullableSlot[T] {
	return unsafe.Slice((*NullableSlot[T])(unsafe.Pointer(unsafe.SliceData(v))), len(v))
}

// ReadNullableSlots reads a sequence of T? of a primitive T (an array, a list, a set, a queue): length, then the
// slots as one block (nil for null). A primitive is as large as its alignment, so a slot is 2 * size(T) bytes.
// Where the memory of a NullableSlot[T] is its bytes (little-endian hosts that align T to its size) the block is
// copied as memory, then each presence checked and each slot's padding and absent value cleared in one pass.
func ReadNullableSlots[T Primitive](r *Reader) []NullableSlot[T] {
	return asNullableSlots(readNullables[T](r, false))
}

// WriteNullableSlots writes a sequence of T? of a primitive T: length, then the slots as one block (nil is null),
// an absent element as zeros.
func WriteNullableSlots[T Primitive](w *Writer, v []NullableSlot[T]) {
	// The bytes of a NinoNullable<T> of a primitive: its presence is as wide as the slot's byte and padding.
	WriteNinoNullables(w, asNinoNullables(v))
}

// ReadNinoNullables reads an array or list of NinoNullable<T> of a primitive T: length, then the elements as one
// block (nil for null), checked as [Reader.NinoNullableAt] checks each. Where the memory of a NinoNullable[T] is
// its bytes (little-endian hosts that align T to its size) the block is copied as memory.
func ReadNinoNullables[T Primitive](r *Reader) []NinoNullable[T] {
	return readNullables[T](r, true)
}

// readNullables reads a block of presence and value pairs of a primitive T, size(T) bytes each: NinoNullable<T>s
// (whole: every byte of the presence and of an absent value is checked) or the slots of T? (the presence byte is
// checked, an absent value read as zero).
func readNullables[T Primitive](r *Reader, whole bool) []NinoNullable[T] {
	var zero NinoNullable[T]
	size := int(unsafe.Sizeof(zero.Value))
	wire := 2 * size
	n := r.Length(wire)
	if n < 0 {
		return nil
	}
	b := r.Take(n * wire)
	if len(b) != n*wire {
		return nil
	}
	if whole && !ninoNullablesValid(b, size) {
		failNinoNullables(r, b, size)
		return nil
	}
	// As many bytes of memory as of data, or fewer.
	v := make([]NinoNullable[T], n)
	if n == 0 {
		return v
	}
	if littleEndian && int(unsafe.Sizeof(zero)) == wire && int(unsafe.Offsetof(zero.Value)) == size {
		// The presence's first byte (0 or 1, checked) is HasValue, the bytes above it padding.
		m := unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), n*wire)
		copy(m, b)
		if !whole && !clearAbsentSlots(m, size) {
			failNullableSlots(r, b, wire)
			return nil
		}
		return v
	}
	var presence byte
	for i := range v {
		e := b[i*wire : (i+1)*wire : (i+1)*wire]
		presence |= e[0]
		if e[0] == 1 {
			v[i].HasValue = true
			decodePrimitives(unsafe.Slice(&v[i].Value, 1), e[size:])
		}
	}
	if presence > 1 { // slots only: a NinoNullable's is checked above
		failNullableSlots(r, b, wire)
		return nil
	}
	return v
}

// WriteNinoNullables writes an array or list of NinoNullable<T> of a primitive T: length, then the elements as one
// block (nil is null), an absent element as zeros.
func WriteNinoNullables[T Primitive](w *Writer, v []NinoNullable[T]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	var zero T
	size := int(unsafe.Sizeof(zero))
	b := w.Extend(len(v) * 2 * size)
	if len(b) != len(v)*2*size {
		return
	}
	// The value's bits through its address (a number of its size on every host), kept only where it is present.
	switch size {
	case 1:
		for i := range v {
			e, p := &v[i], uint16(0)
			if e.HasValue {
				p = 1
			}
			binary.LittleEndian.PutUint16(b[2*i:], p|(uint16(*(*uint8)(unsafe.Pointer(&e.Value)))&-p)<<8)
		}
	case 2:
		for i := range v {
			e, p := &v[i], uint32(0)
			if e.HasValue {
				p = 1
			}
			binary.LittleEndian.PutUint32(b[4*i:], p|(uint32(*(*uint16)(unsafe.Pointer(&e.Value)))&-p)<<16)
		}
	case 4:
		for i := range v {
			e, p := &v[i], uint64(0)
			if e.HasValue {
				p = 1
			}
			binary.LittleEndian.PutUint64(b[8*i:], p|(uint64(*(*uint32)(unsafe.Pointer(&e.Value)))&-p)<<32)
		}
	case 8:
		for i := range v {
			e, p := &v[i], uint64(0)
			if e.HasValue {
				p = 1
			}
			d := b[16*i : 16*i+16 : 16*i+16]
			binary.LittleEndian.PutUint64(d, p)
			binary.LittleEndian.PutUint64(d[8:], *(*uint64)(unsafe.Pointer(&e.Value))&-p)
		}
	}
}

// ninoNullablesValid reports whether every element of b (a presence and a value of size bytes each) is what
// [Reader.NinoNullableAt] accepts: the presence 0 or 1 as a whole number of size bytes, and the value zeros where
// it is 0. One pass with no branch an element: p>>1 is set for a presence above 1, and p-1 is all ones for 0, so
// (p-1)&value is the value an absent element must not have.
//
//go:noinline
func ninoNullablesValid(b []byte, size int) bool {
	var bad uint64
	switch size {
	case 1:
		for ; len(b) >= 2; b = b[2:] {
			p, x := uint64(b[0]), uint64(b[1])
			bad |= p>>1 | (p-1)&x
		}
	case 2:
		for ; len(b) >= 4; b = b[4:] {
			e := binary.LittleEndian.Uint32(b)
			p, x := uint64(e&0xFFFF), uint64(e>>16)
			bad |= p>>1 | (p-1)&x
		}
	case 4:
		for ; len(b) >= 8; b = b[8:] {
			e := binary.LittleEndian.Uint64(b)
			p, x := e&0xFFFFFFFF, e>>32
			bad |= p>>1 | (p-1)&x
		}
	case 8:
		for ; len(b) >= 16; b = b[16:] {
			p, x := binary.LittleEndian.Uint64(b), binary.LittleEndian.Uint64(b[8:])
			bad |= p>>1 | (p-1)&x
		}
	}
	return bad == 0
}

// failNinoNullables fails r with the error of the first element of b that [Reader.NinoNullableAt] rejects.
//
//go:noinline
func failNinoNullables(r *Reader, b []byte, size int) {
	for ; len(b) >= 2*size && r.err == nil; b = b[2*size:] {
		r.NinoNullableAt(b[:size], b[size:2*size])
	}
}

// clearAbsentSlots clears the padding of each slot of m and the value of each absent one, m the memory of
// NullableSlots of a primitive of size bytes on a host where it is their bytes, and reports whether every presence
// byte is 0 or 1. One pass with no branch an element: -p is all ones for a presence of 1 and zero for 0.
//
//go:noinline
func clearAbsentSlots(m []byte, size int) bool {
	var presence uint64
	switch size {
	case 1:
		for ; len(m) >= 2; m = m[2:] {
			p := m[0]
			presence |= uint64(p)
			m[1] &= -p
		}
	case 2:
		for ; len(m) >= 4; m = m[4:] {
			e := binary.LittleEndian.Uint32(m)
			p := e & 0xFF
			presence |= uint64(p)
			binary.LittleEndian.PutUint32(m, e&(-p<<16|0xFF))
		}
	case 4:
		for ; len(m) >= 8; m = m[8:] {
			e := binary.LittleEndian.Uint64(m)
			p := e & 0xFF
			presence |= p
			binary.LittleEndian.PutUint64(m, e&(-p<<32|0xFF))
		}
	case 8:
		for ; len(m) >= 16; m = m[16:] {
			e := m[:16:16]
			p := uint64(e[0])
			presence |= p
			binary.LittleEndian.PutUint64(e, p)
			binary.LittleEndian.PutUint64(e[8:], binary.LittleEndian.Uint64(e[8:])&-p)
		}
	}
	return presence <= 1
}

// failNullableSlots fails r with the error of the first presence byte of b (slots of wire bytes) that is not 0 or 1.
//
//go:noinline
func failNullableSlots(r *Reader, b []byte, wire int) {
	for ; len(b) >= wire && r.err == nil; b = b[wire:] {
		r.BoolAt(b[0])
	}
}
