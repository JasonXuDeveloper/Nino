package nino

import (
	"encoding/binary"
	"unsafe"
)

// Collections: an int32 length (-1 for null), then the elements; a dictionary's pairs key first. A nil slice or
// map is null, an empty one is empty.
//
// Every count is checked against the bytes left (count * the fewest bytes an element takes) before anything is
// allocated, and a slice or map whose elements take far more memory than their bytes on the wire starts smaller
// and grows as its elements are read (see [Reader.SliceCap]): data claiming more elements than it holds fails
// after allocating a bounded multiple of what it held.
//
// These helpers take the element's functions as values (an indirect call per element); generated code may write
// the same loops inline.

// ReadSlice reads a sequence (an array, a list, a set read in order...): nil for null. minSize is the fewest bytes
// an element takes on the wire (1 unless it is of fixed size).
func ReadSlice[T any](r *Reader, minSize int, read func(*Reader) T) []T {
	n := r.Length(minSize)
	if n < 0 {
		return nil
	}
	var zero T
	v := make([]T, r.SliceCap(n, int(unsafe.Sizeof(zero))))
	for i := 0; i < n; i++ {
		if i == len(v) {
			v = growSlice(v, n)
		}
		v[i] = read(r)
		if r.err != nil {
			return nil
		}
	}
	return v
}

// growSlice returns v with its elements in a slice twice as long (at most n).
//
//go:noinline
func growSlice[T any](v []T, n int) []T {
	grown := make([]T, min(n, max(2*len(v), 1)))
	copy(grown, v)
	return grown
}

// WriteSlice writes a sequence: nil is null.
func WriteSlice[T any](w *Writer, v []T, write func(*Writer, T)) {
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		write(w, e)
	}
}

// ReadMap reads a dictionary: nil for null. A key read twice fails with CodeDuplicateKey (as .NET's dictionaries
// do when filled from empty). minSize is the fewest bytes a pair takes on the wire.
func ReadMap[K comparable, V any](r *Reader, minSize int, readKey func(*Reader) K, readValue func(*Reader) V) map[K]V {
	n := r.Length(minSize)
	if n < 0 {
		return nil
	}
	var k K
	var v V
	m := make(map[K]V, capacity(n, int(unsafe.Sizeof(k))+int(unsafe.Sizeof(v))+hashSlot, len(r.buf)))
	for i := 0; i < n; i++ {
		key := readKey(r)
		value := readValue(r)
		if r.err != nil {
			return nil
		}
		// One hash operation: a repeated key leaves the size unchanged.
		if m[key] = value; len(m) != i+1 {
			r.Fail(CodeDuplicateKey, "")
			return nil
		}
	}
	return m
}

// WriteMap writes a dictionary in Go's map order (any order reads back the same map): nil is null. Use
// [WriteEntries] where the order of the bytes matters.
func WriteMap[K comparable, V any](w *Writer, m map[K]V, writeKey func(*Writer, K), writeValue func(*Writer, V)) {
	if m == nil {
		w.Null()
		return
	}
	w.Length(len(m))
	for k, v := range m {
		writeKey(w, k)
		writeValue(w, v)
	}
}

// Entry is one pair of a dictionary read or written in order.
type Entry[K, V any] struct {
	Key   K
	Value V
}

// ReadEntries reads a dictionary as its pairs in the order of the data (nil for null), without checking for
// repeated keys.
func ReadEntries[K, V any](r *Reader, minSize int, readKey func(*Reader) K, readValue func(*Reader) V) []Entry[K, V] {
	return ReadSlice(r, minSize, func(r *Reader) Entry[K, V] {
		k := readKey(r)
		return Entry[K, V]{k, readValue(r)}
	})
}

// WriteEntries writes a dictionary as its pairs, in order: nil is null.
func WriteEntries[K, V any](w *Writer, e []Entry[K, V], writeKey func(*Writer, K), writeValue func(*Writer, V)) {
	if e == nil {
		w.Null()
		return
	}
	w.Length(len(e))
	for _, p := range e {
		writeKey(w, p.Key)
		writeValue(w, p.Value)
	}
}

// ReadSet reads a set: nil for null. An element read twice fails with CodeDuplicateKey (as .NET's HashSet does
// when filled from empty).
func ReadSet[T comparable](r *Reader, minSize int, read func(*Reader) T) map[T]struct{} {
	n := r.Length(minSize)
	if n < 0 {
		return nil
	}
	var e T
	m := make(map[T]struct{}, capacity(n, int(unsafe.Sizeof(e))+hashSlot, len(r.buf)))
	for i := 0; i < n; i++ {
		v := read(r)
		if r.err != nil {
			return nil
		}
		if m[v] = struct{}{}; len(m) != i+1 {
			r.Fail(CodeDuplicateKey, "")
			return nil
		}
	}
	return m
}

// WriteSet writes a set in Go's map order: nil is null.
func WriteSet[T comparable](w *Writer, s map[T]struct{}, write func(*Writer, T)) {
	if s == nil {
		w.Null()
		return
	}
	w.Length(len(s))
	for v := range s {
		write(w, v)
	}
}

// ReadFixed reads a sequence of elements of size bytes each (raw structs, tuples of raw values...) with one bounds
// check: decode gets each element's bytes (and the reader, to fail on an invalid value). nil for null.
func ReadFixed[T any](r *Reader, size int, decode func(r *Reader, b []byte) T) []T {
	n := r.Length(size)
	if n < 0 {
		return nil
	}
	b := r.Take(n * size)
	if len(b) != n*size {
		return nil
	}
	var zero T
	v := make([]T, r.SliceCap(n, int(unsafe.Sizeof(zero))))
	for i := 0; i < n; i++ {
		if i == len(v) {
			v = growSlice(v, n)
		}
		v[i] = decode(r, b[i*size:(i+1)*size:(i+1)*size])
		if r.err != nil {
			return nil
		}
	}
	return v
}

// WriteFixed writes a sequence of elements of size bytes each with one reservation: encode fills each element's
// bytes, cleared first (padding is written as zeros). nil is null.
func WriteFixed[T any](w *Writer, v []T, size int, encode func(b []byte, e T)) {
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * size)
	if len(b) != len(v)*size {
		return
	}
	for i, e := range v {
		encode(b[i*size:(i+1)*size:(i+1)*size], e)
	}
}

// ReadRaw reads a sequence of raw elements of size bytes each, as [ReadFixed] does, copying them as one block where
// T's memory is their bytes. Generated code passes asIs, a constant that says so for a type without pointers whose
// size and field offsets (unsafe.Sizeof, unsafe.Offsetof) are those of the wire; bools, the offsets of its bools in
// an element, whose bytes must be 0 or 1; and decode, for the hosts where the block is not the memory (big-endian
// ones) and asIs false (a 32-bit host that aligns an int64 to 4 bytes). Never pass asIs true for a T with pointers.
func ReadRaw[T any](r *Reader, size int, asIs bool, bools []int, decode func(r *Reader, b []byte) T) []T {
	var zero T
	if !asIs || !littleEndian || int(unsafe.Sizeof(zero)) != size {
		return ReadFixed(r, size, decode)
	}
	n := r.Length(size)
	if n < 0 {
		return nil
	}
	b := r.Take(n * size)
	if len(b) != n*size {
		return nil
	}
	if len(bools) != 0 && !rawBools(b, size, bools) {
		r.Fail(CodeInvalidData, "invalid bool")
		return nil
	}
	// As many bytes of memory as of data: no more than ReadFixed's bound.
	v := make([]T, n)
	copy(unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), n*size), b)
	return v
}

// rawBools reports whether the bytes at each offset of bools in each element (size bytes) of b are 0 or 1: ORed into
// four accumulators, four elements a step, read through the slice's pointer (an index per byte kept a bounds check
// per element). An offset outside an element takes the bounds-checked loop.
func rawBools(b []byte, size int, bools []int) bool {
	if size <= 0 {
		return true // no bytes (Take of n*size)
	}
	var a0, a1, a2, a3 byte
	p, s, n := unsafe.Pointer(unsafe.SliceData(b)), uintptr(size), len(b)/size
	for _, o := range bools {
		if uint(o) >= uint(size) {
			for i := uint(o); i < uint(len(b)); i += uint(size) {
				a0 |= b[i]
			}
			continue
		}
		// Element k's byte is at o + k*size, k < n: within b (only in-range offsets are added to p).
		i, k := uintptr(o), n
		for ; k >= 4; k -= 4 {
			a0 |= *(*byte)(unsafe.Add(p, i))
			a1 |= *(*byte)(unsafe.Add(p, i+s))
			a2 |= *(*byte)(unsafe.Add(p, i+2*s))
			a3 |= *(*byte)(unsafe.Add(p, i+3*s))
			i += 4 * s
		}
		for ; k > 0; k-- {
			a0 |= *(*byte)(unsafe.Add(p, i))
			i += s
		}
	}
	return a0|a1|a2|a3 <= 1
}

// WriteRaw writes a sequence of raw elements of size bytes each, as [WriteFixed] does, copying their memory as one
// block where it is their bytes: asIs and bools as for [ReadRaw], and pads the offsets of the bytes no field fills
// (padding), which [WriteFixed] writes as zeros and so does the block once copied. A bool whose byte is not 0 or 1
// (set through unsafe) is written as 1.
func WriteRaw[T any](w *Writer, v []T, size int, asIs bool, bools, pads []int, encode func(b []byte, e T)) {
	var zero T
	if !asIs || !littleEndian || int(unsafe.Sizeof(zero)) != size {
		WriteFixed(w, v, size, encode)
		return
	}
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	b := w.Extend(len(v) * size)
	if len(b) != len(v)*size {
		return
	}
	copy(b, unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), len(v)*size))
	if len(pads) != 0 {
		clearPads(b, size, pads)
	}
	if len(bools) != 0 && !rawBools(b, size, bools) {
		normalizeRawBools(b, size, bools)
	}
}

// clearPads sets the bytes at the offsets pads in each element (size bytes) of b to 0. Elements of up to 32 bytes
// are cleared 8 at a time, ANDed a word at a time with a mask of 8 elements (size words, on the stack); larger ones
// byte by byte.
func clearPads(b []byte, size int, pads []int) {
	if size > 32 {
		for _, o := range pads {
			for i := uint(o); i < uint(len(b)); i += uint(size) {
				b[i] = 0
			}
		}
		return
	}
	var bytes [32 * 8]byte
	m := bytes[:8*size]
	for i := range m {
		m[i] = 0xFF
	}
	for e := 0; e < len(m); e += size {
		for _, o := range pads {
			m[e+o] = 0
		}
	}
	var words [32]uint64
	mask := words[:size]
	for k := range mask {
		mask[k] = binary.LittleEndian.Uint64(m[8*k:])
	}
	for len(b) >= len(m) {
		for k, x := range mask {
			w := b[8*k : 8*k+8 : 8*k+8]
			binary.LittleEndian.PutUint64(w, binary.LittleEndian.Uint64(w)&x)
		}
		b = b[len(m):]
	}
	for i := range b {
		b[i] &= m[i]
	}
}

// normalizeRawBools sets each byte of a bool (at the offsets bools in each element of b) that is not 0 to 1.
//
//go:noinline
func normalizeRawBools(b []byte, size int, bools []int) {
	for _, o := range bools {
		for i := uint(o); i < uint(len(b)); i += uint(size) {
			if b[i] != 0 {
				b[i] = 1
			}
		}
	}
}

// Shape is the lengths of a multi-dimensional array (T[,], T[,,], T[,,,]), whose elements are written in row-major
// order after them.
type Shape struct {
	Dims  [4]int
	Rank  int
	Total int
}

// ReadShape reads the rank lengths of a multi-dimensional array; ok is false for null (the first length -1). As
// .NET: a negative length, one past .NET's longest array, or more elements than the bytes left (minSize each)
// fail; the first length alone is not checked against the bytes left, as another may be 0.
func (r *Reader) ReadShape(rank, minSize int) (s Shape, ok bool) {
	if rank < 2 || rank > 4 {
		r.Fail(CodeInvalidValue, "rank")
		return s, false
	}
	s.Rank = rank
	first := r.Length(0)
	if first < 0 {
		return s, false
	}
	s.Dims[0] = first
	total := uint64(first)
	for i := 1; i < rank; i++ {
		d := r.I32()
		if d < 0 {
			r.badLength(int64(d))
			return s, false
		}
		s.Dims[i] = int(d)
	}
	for i := 0; i < rank; i++ {
		if s.Dims[i] > maxArrayLength {
			r.badLength(int64(s.Dims[i]))
			return s, false
		}
	}
	for i := 1; i < rank; i++ {
		if total *= uint64(s.Dims[i]); total > 1<<32-1 {
			r.badLength(int64(min(total, maxInt32)))
			return s, false
		}
	}
	if total*uint64(max(minSize, 0)) > uint64(len(r.buf)) {
		r.badLength(int64(min(total, maxInt32)))
		return s, false
	}
	if r.err != nil {
		return s, false
	}
	s.Total = int(total)
	return s, true
}

// WriteShape writes the lengths of a multi-dimensional array.
func (w *Writer) WriteShape(dims ...int) {
	for _, d := range dims {
		w.Length(d)
	}
}

// ReadUnique reads a set as its elements in the order of the data (nil for null): an element read twice fails with
// CodeDuplicateKey, as .NET's HashSet and SortedSet do when filled from empty. Unlike [ReadSet], writing the slice
// back gives the same bytes.
func ReadUnique[T comparable](r *Reader, minSize int, read func(*Reader) T) []T {
	return UniqueValues(r, ReadSlice(r, minSize, read))
}

// UniqueValues returns v, the elements of a set read in the order of the data, unless two of them are equal: then
// it fails r with CodeDuplicateKey (as .NET's HashSet and SortedSet do when filled from empty) and returns nil. It
// returns nil for nil and after r failed. Generated code reads the elements with direct calls, then checks them
// once here, as [UniqueEntries] does for a dictionary's keys. Numbers and strings are compared as .NET compares them
// (a NaN equals every NaN), without a map; other values as Go compares them (unique.go).
func UniqueValues[T comparable](r *Reader, v []T) []T {
	if v == nil || r.err != nil {
		return nil
	}
	var e T
	if repeated[T](unsafe.Pointer(unsafe.SliceData(v)), len(v), int(unsafe.Sizeof(e)), &r.keys) {
		r.Fail(CodeDuplicateKey, "")
		return nil
	}
	return v
}

// UniqueStrings is [UniqueValues] for a set of nullable strings (a HashSet<string> as nino-gen's Go holds it): two
// equal strings, or two nulls, fail r with CodeDuplicateKey.
func UniqueStrings(r *Reader, v []*string) []*string {
	if v == nil || r.err != nil {
		return nil
	}
	if repeatedStrings(unsafe.Pointer(unsafe.SliceData(v)), len(v), int(unsafe.Sizeof(v[0])), true, &r.keys) {
		r.Fail(CodeDuplicateKey, "")
		return nil
	}
	return v
}

// ReadUniqueEntries reads a dictionary as its pairs in the order of the data (nil for null): a key read twice
// fails with CodeDuplicateKey, as .NET's dictionaries do when filled from empty.
func ReadUniqueEntries[K comparable, V any](r *Reader, minSize int, readKey func(*Reader) K, readValue func(*Reader) V) []Entry[K, V] {
	return UniqueEntries(r, ReadEntries(r, minSize, readKey, readValue))
}

// UniqueEntries returns e, the pairs of a dictionary read in the order of the data, unless two of its keys are equal:
// then it fails r with CodeDuplicateKey (as .NET's dictionaries do when filled from empty) and returns nil. It
// returns nil for nil and after r failed. Generated code reads the pairs with direct calls, then checks them once
// here. Keys are compared as [UniqueValues] compares elements.
func UniqueEntries[K comparable, V any](r *Reader, e []Entry[K, V]) []Entry[K, V] {
	if e == nil || r.err != nil {
		return nil
	}
	var pair Entry[K, V]
	if repeated[K](unsafe.Pointer(unsafe.SliceData(e)), len(e), int(unsafe.Sizeof(pair)), &r.keys) {
		r.Fail(CodeDuplicateKey, "")
		return nil
	}
	return e
}
