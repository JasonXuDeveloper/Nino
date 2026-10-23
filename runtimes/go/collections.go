package nino

import "unsafe"

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
		if _, dup := m[key]; dup {
			r.Fail(CodeDuplicateKey, "")
			return nil
		}
		m[key] = value
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
		if _, dup := m[v]; dup {
			r.Fail(CodeDuplicateKey, "")
			return nil
		}
		m[v] = struct{}{}
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
	n := r.Length(minSize)
	if n < 0 {
		return nil
	}
	var zero T
	size := int(unsafe.Sizeof(zero))
	v := make([]T, r.SliceCap(n, size))
	seen := make(map[T]struct{}, capacity(n, size+hashSlot, len(r.buf)))
	for i := 0; i < n; i++ {
		if i == len(v) {
			v = growSlice(v, n)
		}
		e := read(r)
		if r.err != nil {
			return nil
		}
		if _, dup := seen[e]; dup {
			r.Fail(CodeDuplicateKey, "")
			return nil
		}
		seen[e] = struct{}{}
		v[i] = e
	}
	return v
}

// ReadUniqueEntries reads a dictionary as its pairs in the order of the data (nil for null): a key read twice
// fails with CodeDuplicateKey, as .NET's dictionaries do when filled from empty.
func ReadUniqueEntries[K comparable, V any](r *Reader, minSize int, readKey func(*Reader) K, readValue func(*Reader) V) []Entry[K, V] {
	n := r.Length(minSize)
	if n < 0 {
		return nil
	}
	var e Entry[K, V]
	var k K
	v := make([]Entry[K, V], r.SliceCap(n, int(unsafe.Sizeof(e))))
	seen := make(map[K]struct{}, capacity(n, int(unsafe.Sizeof(k))+hashSlot, len(r.buf)))
	for i := 0; i < n; i++ {
		if i == len(v) {
			v = growSlice(v, n)
		}
		key := readKey(r)
		value := readValue(r)
		if r.err != nil {
			return nil
		}
		if _, dup := seen[key]; dup {
			r.Fail(CodeDuplicateKey, "")
			return nil
		}
		seen[key] = struct{}{}
		v[i] = Entry[K, V]{key, value}
	}
	return v
}
