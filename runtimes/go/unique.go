package nino

import (
	"reflect"
	"slices"
	"unsafe"
)

// Sets and dictionaries read in the order of the data (ReadUnique, ReadUniqueEntries) fail on a key read twice. For
// integer keys (of any integer type, named or not) the keys are not put in a map as they are read: once all are
// read, their bits are compared pair by pair (16 keys or fewer) or sorted and compared with their neighbors: no map
// to allocate and fill, and at most n log n steps however the data chose the keys (pdqsort falls back to heapsort).

// intKeySize returns the size of K when it is an integer type, else 0.
func intKeySize[K comparable]() int {
	switch reflect.TypeFor[K]().Kind() {
	case reflect.Int8, reflect.Uint8:
		return 1
	case reflect.Int16, reflect.Uint16:
		return 2
	case reflect.Int32, reflect.Uint32:
		return 4
	case reflect.Int64, reflect.Uint64:
		return 8
	}
	return 0
}

// keyBits returns the size bytes at p (an integer key) as a uint64.
func keyBits(p unsafe.Pointer, size int) uint64 {
	switch size {
	case 1:
		return uint64(*(*uint8)(p))
	case 2:
		return uint64(*(*uint16)(p))
	case 4:
		return uint64(*(*uint32)(p))
	}
	return *(*uint64)(p)
}

// repeatedKey reports whether two of the n integer keys of size bytes, the first at p and each stride bytes after
// the one before, are equal.
func repeatedKey(p unsafe.Pointer, n, stride, size int) bool {
	if n < 2 {
		return false
	}
	if n <= 16 {
		for i := 1; i < n; i++ {
			k := keyBits(unsafe.Add(p, i*stride), size)
			for j := 0; j < i; j++ {
				if keyBits(unsafe.Add(p, j*stride), size) == k {
					return true
				}
			}
		}
		return false
	}
	var room [64]uint64
	bits := room[:0]
	if n > len(room) {
		bits = make([]uint64, 0, n)
	}
	for i := 0; i < n; i++ {
		bits = append(bits, keyBits(unsafe.Add(p, i*stride), size))
	}
	slices.Sort(bits)
	for i := 1; i < len(bits); i++ {
		if bits[i] == bits[i-1] {
			return true
		}
	}
	return false
}
