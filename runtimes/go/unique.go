package nino

import (
	"hash/maphash"
	"math/bits"
	"reflect"
	"slices"
	"sync"
	"unsafe"
)

// Sets and dictionaries read in the order of the data (CheckUniqueValues, CheckUniqueKeys, CheckUniqueStrings and the
// readers built on them) fail on a key read twice. The keys are checked once all are read, compared as .NET's default
// comparer compares them: integers and floating-point numbers by their bits (every NaN equal to every other, -0 equal
// to 0, as double.Equals has it, where Go's == and maps keep NaNs apart), strings by their text, a NullableSlot of a
// number as .NET's Nullable<T> (null equal to null, else the numbers). Up to 16 keys are
// compared pair by pair; more go into an open-addressing table of their own (no map to allocate and fill), indexed by a
// hash seeded per process: integers multiplied by a random odd number, strings through hash/maphash. Data cannot choose
// keys that share a slot without knowing the seed, and a key that still probes past maxProbes slots sends the whole
// check to a path whose cost the data cannot raise: a sort for numbers (pdqsort falls back to heapsort), a Go map
// (seeded too) for strings and bytewise keys. Other comparable keys go through a Go map.

// maxProbes bounds the slots one key may probe in a table before the check falls back (see above). With the table at
// most half full and a seeded hash, honest data almost never comes near it.
const maxProbes = 64

var (
	hashSeed = maphash.MakeSeed()
	// intSeed multiplies integer keys before their table index is taken from the top bits (multiply-shift).
	intSeed = maphash.String(hashSeed, "nino") | 1
)

// keyKind is how a key type is compared: size is its size in bytes for an integer or floating-point type (0
// otherwise), float says it is floating-point, text that it is a string. slot is the offset of Value in a
// NullableSlot of such a number (0 for any other type): the key is then null, or that number.
type keyKind struct {
	size  int
	float bool
	text  bool
	slot  uintptr
}

// kindOf returns how keys of type K are compared.
func kindOf[K comparable]() keyKind {
	t := reflect.TypeFor[K]()
	if t.Kind() == reflect.Struct && isNullableSlotType(t) {
		// A T? of a number, compared as .NET's Nullable<T> is: null equals null, a value as T's values are. A slot
		// of any other T takes the default below (HasValue and Value as Go compares them: what the reader
		// returns holds the zero Value in every absent slot, so two nulls are equal there too).
		value := t.Field(1)
		if k := kindOfType(value.Type); k.size != 0 {
			k.slot = value.Offset
			return k
		}
		return keyKind{}
	}
	return kindOfType(t)
}

func kindOfType(t reflect.Type) keyKind {
	switch t.Kind() {
	case reflect.Int8, reflect.Uint8:
		return keyKind{size: 1}
	case reflect.Int16, reflect.Uint16:
		return keyKind{size: 2}
	case reflect.Int32, reflect.Uint32:
		return keyKind{size: 4}
	case reflect.Int64, reflect.Uint64:
		return keyKind{size: 8}
	case reflect.Float32:
		return keyKind{size: 4, float: true}
	case reflect.Float64:
		return keyKind{size: 8, float: true}
	case reflect.String:
		return keyKind{text: true}
	}
	return keyKind{}
}

// repeated reports whether two of the n keys of type K, the first at p and each stride bytes after the one before,
// are equal (see the top of this file). scratch is the reader's memory for the tables (see table).
func repeated[K comparable](p unsafe.Pointer, n, stride int, scratch *[]uint64) bool {
	if n < 2 {
		return false
	}
	switch k := kindOf[K](); {
	case k.slot != 0:
		return repeatedSlots(p, n, stride, k, scratch)
	case k.size != 0:
		return repeatedBits(p, n, stride, k.size, k.float, scratch)
	case k.text:
		return repeatedStrings(p, n, stride, false, scratch)
	}
	return repeatedOtherKeys[K](p, n, stride, scratch)
}

// repeatedSlots is repeated for NullableSlots of a number (k): two nulls are equal, and the values present are
// compared as repeatedBits compares numbers. The Values are checked where they lie, an absent slot's with them (zero
// in what the reader returns); only when that finds two equal and one slot is null (a null beside a zero, in honest
// data) are the values present copied out and checked alone, so the cost stays within a constant of a set of
// numbers whatever the data.
func repeatedSlots(p unsafe.Pointer, n, stride int, k keyKind, scratch *[]uint64) bool {
	nulls := 0
	for i := 0; i < n; i++ {
		if !*(*bool)(unsafe.Add(p, i*stride)) { // HasValue, the first field
			nulls++
		}
	}
	if nulls > 1 {
		return true
	}
	if !repeatedBits(unsafe.Add(p, k.slot), n, stride, k.size, k.float, scratch) {
		return false
	}
	if nulls == 0 {
		return true
	}
	return repeatedPresent(p, n, stride, k, scratch)
}

// repeatedPresent is repeatedSlots on a copy of the values present alone (their bits, canonical already, 8 bytes
// each whatever the number's size).
//
//go:noinline
func repeatedPresent(p unsafe.Pointer, n, stride int, k keyKind, scratch *[]uint64) bool {
	keys := make([]uint64, 0, n)
	for i := 0; i < n; i++ {
		if e := unsafe.Add(p, i*stride); *(*bool)(e) {
			keys = append(keys, keyBits(unsafe.Add(e, k.slot), k.size, k.float))
		}
	}
	return repeatedBits(unsafe.Pointer(unsafe.SliceData(keys)), len(keys), 8, 8, false, scratch)
}

// repeatedValues is repeated for keys compared as Go compares them, through a map.
//
//go:noinline
func repeatedValues[K comparable](p unsafe.Pointer, n, stride int) bool {
	seen := make(map[K]struct{}, n)
	for i := 0; i < n; i++ {
		if seen[*(*K)(unsafe.Add(p, i*stride))] = struct{}{}; len(seen) != i+1 {
			return true
		}
	}
	return false
}

// keyBits returns the size bytes at p (a key) as a uint64, a floating-point key's canonical (floatBits).
func keyBits(p unsafe.Pointer, size int, float bool) uint64 {
	var k uint64
	switch size {
	case 1:
		k = uint64(*(*uint8)(p))
	case 2:
		k = uint64(*(*uint16)(p))
	case 4:
		k = uint64(*(*uint32)(p))
	default:
		k = *(*uint64)(p)
	}
	if float {
		k = floatBits(k, size)
	}
	return k
}

// floatBits returns the bits k of a float32 (size 4) or float64 as .NET compares them: every NaN as one value, -0
// as 0.
func floatBits(k uint64, size int) uint64 {
	if size == 4 {
		if k&0x7FFFFFFF == 0 {
			return 0
		}
		if k&0x7FFFFFFF > 0x7F800000 {
			return 0x7FC00000
		}
		return k
	}
	if k&0x7FFFFFFFFFFFFFFF == 0 {
		return 0
	}
	if k&0x7FFFFFFFFFFFFFFF > 0x7FF0000000000000 {
		return 0x7FF8000000000000
	}
	return k
}

// tableBits returns the log2 of the table size for n keys: at least 2n slots, so the table is at most half full.
func tableBits(n int) int { return bits.Len(uint(2*n - 1)) }

// maxScratch is the most slots (8 bytes each) a Reader keeps for the next check (see table).
const maxScratch = 1 << 16

// table returns 1<<b zeroed 8-byte slots: the reader's scratch when it has room, else new memory, kept as the
// scratch up to maxScratch slots. A check runs after its keys are read, never inside another, so one scratch serves.
func table(scratch *[]uint64, b int) []uint64 {
	n := 1 << b
	if t := *scratch; cap(t) >= n {
		t = t[:n]
		clear(t)
		return t
	}
	t := make([]uint64, n)
	if n <= maxScratch {
		*scratch = t
	}
	return t
}

// repeatedBits is repeated for integer and floating-point keys of size bytes, compared by their bits (keyBits).
func repeatedBits(p unsafe.Pointer, n, stride, size int, float bool, scratch *[]uint64) bool {
	if n <= 16 {
		for i := 1; i < n; i++ {
			k := keyBits(unsafe.Add(p, i*stride), size, float)
			for j := 0; j < i; j++ {
				if keyBits(unsafe.Add(p, j*stride), size, float) == k {
					return true
				}
			}
		}
		return false
	}
	switch size {
	case 1:
		var seen [4]uint64
		return repeatedSmall(p, n, stride, seen[:])
	case 2:
		var seen [1024]uint64
		return repeatedSmall(p, n, stride, seen[:])
	case 4:
		// Half as many 8-byte slots hold the 4-byte ones.
		b := tableBits(n)
		t := table(scratch, max(b-1, 0))
		return probeKeys(p, n, stride, b, unsafe.Slice((*uint32)(unsafe.Pointer(unsafe.SliceData(t))), 1<<b), float)
	}
	b := tableBits(n)
	return probeKeys[uint64](p, n, stride, b, table(scratch, b), float)
}

// repeatedSmall is repeatedBits for 1- or 2-byte keys: a bit per possible key in seen (zeroed, 2^(8*size) bits).
func repeatedSmall(p unsafe.Pointer, n, stride int, seen []uint64) bool {
	small := len(seen) == 4
	for i := 0; i < n; i++ {
		var k uint
		if small {
			k = uint(*(*uint8)(unsafe.Add(p, i*stride)))
		} else {
			k = uint(*(*uint16)(unsafe.Add(p, i*stride)))
		}
		w, bit := k>>6, uint64(1)<<(k&63)
		if w >= uint(len(seen)) {
			return false // unreachable: seen holds a bit for every key
		}
		if seen[w]&bit != 0 {
			return true
		}
		seen[w] |= bit
	}
	return false
}

// probeKeys puts the n keys of type U (the first at p, each stride bytes after the one before; floating-point bits
// made canonical when float) into table, 1<<b slots of zero, by linear probing from a multiply-shift hash, and
// reports whether one was there already. A key that probes past maxProbes slots hands the whole check to
// repeatedSorted.
func probeKeys[U uint32 | uint64](p unsafe.Pointer, n, stride, b int, table []U, float bool) bool {
	mask, shift := uint64(len(table)-1), uint(64-b)
	size := int(unsafe.Sizeof(U(0)))
	zero := false
	for i := 0; i < n; i++ {
		k := *(*U)(unsafe.Add(p, i*stride))
		if float {
			k = U(floatBits(uint64(k), size))
		}
		if k == 0 {
			// 0 marks an empty slot: the key 0 is tracked apart.
			if zero {
				return true
			}
			zero = true
			continue
		}
		at := (uint64(k) * intSeed) >> shift
		for probes := 0; ; probes++ {
			s := &table[at&mask]
			if *s == 0 {
				*s = k
				break
			}
			if *s == k {
				return true
			}
			if probes == maxProbes {
				return repeatedSorted(p, n, stride, size, float)
			}
			at++
		}
	}
	return false
}

// repeatedSorted is repeatedBits by sorting the keys' bits: n log n steps whatever the keys.
//
//go:noinline
func repeatedSorted(p unsafe.Pointer, n, stride, size int, float bool) bool {
	keys := make([]uint64, n)
	for i := range keys {
		keys[i] = keyBits(unsafe.Add(p, i*stride), size, float)
	}
	slices.Sort(keys)
	for i := 1; i < len(keys); i++ {
		if keys[i] == keys[i-1] {
			return true
		}
	}
	return false
}

// stringAt returns the string at p (through a pointer when ptrs) and whether it is not null.
func stringAt(p unsafe.Pointer, ptrs bool) (string, bool) {
	if !ptrs {
		return *(*string)(p), true
	}
	s := *(**string)(p)
	if s == nil {
		return "", false
	}
	return *s, true
}

// repeatedStrings is repeated for strings, the first at p and each stride bytes after the one before (each through a
// pointer when ptrs, where two nulls are equal too).
func repeatedStrings(p unsafe.Pointer, n, stride int, ptrs bool, scratch *[]uint64) bool {
	if int64(n) >= 1<<31 {
		return repeatedStringsMap(p, n, stride, ptrs)
	}
	if n <= 16 {
		for i := 1; i < n; i++ {
			s, ok := stringAt(unsafe.Add(p, i*stride), ptrs)
			for j := 0; j < i; j++ {
				if t, ok2 := stringAt(unsafe.Add(p, j*stride), ptrs); ok == ok2 && t == s {
					return true
				}
			}
		}
		return false
	}
	// A slot holds the top 32 bits of the string's hash and its index plus one (0 marks an empty slot).
	table := table(scratch, tableBits(n))
	mask := uint64(len(table) - 1)
	null := false
	for i := 0; i < n; i++ {
		s, ok := stringAt(unsafe.Add(p, i*stride), ptrs)
		if !ok {
			if null {
				return true
			}
			null = true
			continue
		}
		h := maphash.String(hashSeed, s)
		tag := h &^ 0xFFFFFFFF
		at := h
		for probes := 0; ; probes++ {
			slot := table[at&mask]
			if slot == 0 {
				table[at&mask] = tag | uint64(i+1)
				break
			}
			if slot&^0xFFFFFFFF == tag {
				if t, _ := stringAt(unsafe.Add(p, int(uint32(slot)-1)*stride), ptrs); t == s {
					return true
				}
			}
			if probes == maxProbes {
				return repeatedStringsMap(p, n, stride, ptrs)
			}
			at++
		}
	}
	return false
}

// repeatedStringsMap is repeatedStrings through a Go map.
//
//go:noinline
func repeatedStringsMap(p unsafe.Pointer, n, stride int, ptrs bool) bool {
	seen := make(map[string]struct{}, n)
	null := false
	for i := 0; i < n; i++ {
		s, ok := stringAt(unsafe.Add(p, i*stride), ptrs)
		if !ok {
			if null {
				return true
			}
			null = true
			continue
		}
		if _, dup := seen[s]; dup {
			return true
		}
		seen[s] = struct{}{}
	}
	return false
}

// repeatedNaN reports whether k is a NaN (a key not equal to itself) after another: a Go map adds an entry for every
// NaN key, which .NET's comparers find equal (double.Equals). *nan records that one was seen.
func repeatedNaN[K comparable](k K, nan *bool) bool {
	if k == k {
		return false
	}
	if *nan {
		return true
	}
	*nan = true
	return false
}

var bytewiseKeySizes sync.Map // reflect.Type -> int, 0 for keys that need Go equality

// bytewiseKeySize caches the result of walking a composite key's fields. Numeric and text keys
// take their existing paths before they get here.
func bytewiseKeySize[K comparable]() int {
	t := reflect.TypeFor[K]()
	switch t.Kind() {
	case reflect.Array, reflect.Struct:
	default:
		return 0
	}
	if size, ok := bytewiseKeySizes.Load(t); ok {
		return size.(int)
	}
	size := 0
	if bytewiseKey(t) {
		size = int(t.Size())
	}
	actual, _ := bytewiseKeySizes.LoadOrStore(t, size)
	return actual.(int)
}

// bytewiseKey proves that equality is a comparison of every byte, including all fields of a nested value.
// Floats, blank fields, padding and pointers rule it out. DateTime, DateTimeOffset, Half and Decimal carry
// more than one representation of some values in .NET.
func bytewiseKey(t reflect.Type) bool {
	switch t {
	case reflect.TypeFor[DateTime](), reflect.TypeFor[DateTimeOffset](), reflect.TypeFor[Half](), reflect.TypeFor[Decimal]():
		return false
	}
	switch t.Kind() {
	case reflect.Bool, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64, reflect.Int,
		reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64, reflect.Uint:
		return true
	case reflect.Array:
		return bytewiseKey(t.Elem())
	case reflect.Struct:
		var end uintptr
		for i := 0; i < t.NumField(); i++ {
			f := t.Field(i)
			if f.Name == "_" || f.Offset != end || !bytewiseKey(f.Type) {
				return false
			}
			end += f.Type.Size()
		}
		return end == t.Size()
	}
	return false
}

// repeatedOtherKeys keeps bytewise key handling off the scalar and string paths.
//
//go:noinline
func repeatedOtherKeys[K comparable](p unsafe.Pointer, n, stride int, scratch *[]uint64) bool {
	if size := bytewiseKeySize[K](); size != 0 && n <= 1<<(bits.UintSize-6) && int64(n) < 1<<31 {
		if result, bounded := repeatedBytewise(p, n, stride, size, scratch); bounded {
			return result
		}
	}
	return repeatedValues[K](p, n, stride)
}

// repeatedBytewise uses the reader's seeded, probe-bounded table and a fixed-width view of each key.
// The caller has proved that the key covers its full memory representation and contains no pointers.
func repeatedBytewise(p unsafe.Pointer, n, stride, size int, scratch *[]uint64) (repeated, bounded bool) {
	if n <= 16 {
		for i := 1; i < n; i++ {
			key := unsafe.String((*byte)(unsafe.Add(p, i*stride)), size)
			for j := 0; j < i; j++ {
				if key == unsafe.String((*byte)(unsafe.Add(p, j*stride)), size) {
					return true, true
				}
			}
		}
		return false, true
	}
	t := table(scratch, tableBits(n))
	mask := uint64(len(t) - 1)
	for i := 0; i < n; i++ {
		key := unsafe.String((*byte)(unsafe.Add(p, i*stride)), size)
		h := maphash.String(hashSeed, key)
		tag := h &^ 0xFFFFFFFF
		for at, probes := h, 0; ; at, probes = at+1, probes+1 {
			slot := t[at&mask]
			if slot == 0 {
				t[at&mask] = tag | uint64(i+1)
				break
			}
			if slot&^0xFFFFFFFF == tag {
				other := unsafe.Add(p, int(uint32(slot)-1)*stride)
				if key == unsafe.String((*byte)(other), size) {
					return true, true
				}
			}
			if probes == maxProbes {
				return false, false
			}
		}
	}
	return false, true
}
