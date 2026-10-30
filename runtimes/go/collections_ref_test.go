package nino

import "unsafe"

// The collection helpers that take each element's code as a function value, kept for the tests as plain references
// to compare the runtime's direct paths with (the runtime itself, and the code nino-gen writes, loop with direct
// calls: optimize-runtimes, "Dispatch outside .NET").

// writeMap writes a dictionary in Go's map order (any order reads back the same map): nil is null. Use
// [writeEntries] where the order of the bytes matters.
func writeMap[K comparable, V any](w *Writer, m map[K]V, writeKey func(*Writer, K), writeValue func(*Writer, V)) {
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

// readSet reads a set: nil for null. An element read twice fails with CodeDuplicateKey (as .NET's HashSet does
// when filled from empty).
func readSet[T comparable](r *Reader, minSize int, read func(*Reader) T) map[T]struct{} {
	n := r.Length(minSize)
	if n < 0 {
		return nil
	}
	var e T
	m := make(map[T]struct{}, capacity(n, int(unsafe.Sizeof(e))+hashSlot, len(r.buf)))
	nan := false
	for i := 0; i < n; i++ {
		v := read(r)
		if r.err != nil {
			return nil
		}
		if m[v] = struct{}{}; len(m) != i+1 || repeatedNaN(v, &nan) {
			r.Fail(CodeDuplicateKey, "")
			return nil
		}
	}
	return m
}

// writeSet writes a set in Go's map order: nil is null.
func writeSet[T comparable](w *Writer, s map[T]struct{}, write func(*Writer, T)) {
	if s == nil {
		w.Null()
		return
	}
	w.Length(len(s))
	for v := range s {
		write(w, v)
	}
}

// readUnique reads a set as its elements in the order of the data (nil for null): an element read twice fails with
// CodeDuplicateKey, as .NET's HashSet and SortedSet do when filled from empty. Unlike [readSet], writing the slice
// back gives the same bytes.
func readUnique[T comparable](r *Reader, minSize int, read func(*Reader) T) []T {
	return CheckUniqueValues(r, readSlice(r, minSize, read))
}
