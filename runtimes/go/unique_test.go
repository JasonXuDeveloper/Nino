package nino

import (
	"bytes"
	"errors"
	"math"
	"math/rand"
	"slices"
	"testing"
	"unsafe"
)

type uniqueKind int16

// checkUnique reads keys as a set and as a dictionary and checks the result against a map: the same values when
// every key differs, CodeDuplicateKey otherwise.
func checkUnique[K comparable](t *testing.T, keys []K, writeKey func(*Writer, K), readKey func(*Reader) K, size int) {
	t.Helper()
	distinct := map[K]struct{}{}
	for _, k := range keys {
		distinct[k] = struct{}{}
	}
	repeated := len(distinct) != len(keys)
	duplicate := func(err error) bool {
		var e *Error
		return errors.As(err, &e) && e.Code == CodeDuplicateKey
	}

	w := NewWriter(0)
	writeSlice(w, keys, writeKey)
	r := NewReader(w.Bytes())
	got := readUnique(r, size, readKey)
	if repeated != duplicate(r.Err()) || repeated != (got == nil) || !repeated && (r.Err() != nil || !slices.Equal(got, keys)) {
		t.Fatalf("set of %d keys (repeated %v): got %v, %v", len(keys), repeated, got, r.Err())
	}
	// CheckUniqueValues alone, as generated code calls it after reading the elements itself.
	r = NewReader(nil)
	values := CheckUniqueValues(r, slices.Clone(keys))
	if repeated != duplicate(r.Err()) || repeated != (values == nil) || !repeated && !slices.Equal(values, keys) {
		t.Fatalf("CheckUniqueValues of %d keys (repeated %v): got %v, %v", len(keys), repeated, values, r.Err())
	}

	entries := make([]Entry[K, int32], len(keys))
	for i, k := range keys {
		entries[i] = Entry[K, int32]{k, int32(i)}
	}
	w = NewWriter(0)
	writeEntries(w, entries, writeKey, (*Writer).I32)
	r = NewReader(w.Bytes())
	gotEntries := readUniqueEntries(r, size+4, readKey, (*Reader).I32)
	if repeated != duplicate(r.Err()) || repeated != (gotEntries == nil) || !repeated && (r.Err() != nil || !slices.Equal(gotEntries, entries)) {
		t.Fatalf("dictionary of %d keys (repeated %v): got %v, %v", len(keys), repeated, gotEntries, r.Err())
	}

	// CheckUniqueKeys alone, as generated code calls it after reading the pairs itself.
	r = NewReader(nil)
	checked := CheckUniqueKeys(r, slices.Clone(entries))
	if repeated != duplicate(r.Err()) || repeated != (checked == nil) || !repeated && !slices.Equal(checked, entries) {
		t.Fatalf("CheckUniqueKeys of %d keys (repeated %v): got %v, %v", len(keys), repeated, checked, r.Err())
	}
}

// TestUniqueNilAndFailed: nil stays nil without failing, and a reader that already failed gets nil and keeps
// its first error.
func TestUniqueNilAndFailed(t *testing.T) {
	r := NewReader(nil)
	if CheckUniqueValues[int32](r, nil) != nil || r.Err() != nil {
		t.Fatalf("nil set: %v", r.Err())
	}
	if CheckUniqueKeys[int32, int32](r, nil) != nil || r.Err() != nil {
		t.Fatalf("nil: %v", r.Err())
	}
	r.Fail(CodeEndOfData, "")
	if CheckUniqueKeys(r, []Entry[string, int32]{{"a", 1}}) != nil {
		t.Fatal("a failed reader returned the pairs")
	}
	if CheckUniqueValues(r, []string{"a"}) != nil || CheckUniqueValues(r, []int64{1}) != nil {
		t.Fatal("a failed reader returned the elements")
	}
	var e *Error
	if !errors.As(r.Err(), &e) || e.Code != CodeEndOfData {
		t.Fatalf("first error replaced: %v", r.Err())
	}
}

// uniqueKeys checks every length up to 100 (pairs up to 16 keys, then a table on the stack) with distinct keys, then
// with each pair of positions holding the same key, and random keys from a small range.
func uniqueKeys[K comparable](t *testing.T, key func(int) K, writeKey func(*Writer, K), readKey func(*Reader) K, size int) {
	t.Helper()
	rng := rand.New(rand.NewSource(1))
	for n := 0; n <= 100; n++ {
		keys := make([]K, n)
		for i := range keys {
			keys[i] = key(i*37 - 1000)
		}
		checkUnique(t, keys, writeKey, readKey, size)
		step := 1
		if n > 20 {
			step = 7
		}
		for i := 0; i < n; i += step {
			for j := i + 1; j < n; j += step {
				dup := slices.Clone(keys)
				dup[j] = dup[i]
				checkUnique(t, dup, writeKey, readKey, size)
			}
		}
		for i := range keys {
			keys[i] = key(rng.Intn(2*n + 1))
		}
		checkUnique(t, keys, writeKey, readKey, size)
	}
}

func TestUniqueIntegerKeys(t *testing.T) {
	uniqueKeys(t, func(i int) int8 { return int8(i % 120) }, (*Writer).I8, (*Reader).I8, 1)
	uniqueKeys(t, func(i int) uint8 { return uint8(i) }, (*Writer).U8, (*Reader).U8, 1)
	uniqueKeys(t, func(i int) uniqueKind { return uniqueKind(i) },
		func(w *Writer, k uniqueKind) { w.I16(int16(k)) }, func(r *Reader) uniqueKind { return uniqueKind(r.I16()) }, 2)
	uniqueKeys(t, func(i int) int32 { return int32(i) << 20 }, (*Writer).I32, (*Reader).I32, 4)
	uniqueKeys(t, func(i int) uint32 { return uint32(i) * 0x01000193 }, (*Writer).U32, (*Reader).U32, 4)
	uniqueKeys(t, func(i int) int64 { return int64(i) << 40 }, (*Writer).I64, (*Reader).I64, 8)
	uniqueKeys(t, func(i int) uint64 { return uint64(i) * 0x9E3779B97F4A7C15 }, (*Writer).U64, (*Reader).U64, 8)
	// Floats (by their bits, NaN and -0 in TestUniqueFloatsAsDotNet) and strings (their own table).
	uniqueKeys(t, func(i int) float64 { return float64(i) / 4 }, (*Writer).F64, (*Reader).F64, 8)
	uniqueKeys(t, func(i int) string { return string(rune('a'+i%26)) + string(rune('a'+i/26%26)) },
		(*Writer).String, (*Reader).String, 1)
}

// TestUniqueLargeSets covers larger sets: distinct keys, one repeated pair at both ends and in
// the middle, keys that differ only in their highest byte, and random keys.
func TestUniqueLargeSets(t *testing.T) {
	rng := rand.New(rand.NewSource(2))
	for _, n := range []int{255, 256, 257, 1000, 4099} {
		perm := rng.Perm(n)
		large := func(shift uint) []int64 {
			keys := make([]int64, n)
			for i, p := range perm {
				keys[i] = int64(p) << shift
			}
			return keys
		}
		for _, shift := range []uint{0, 20, 51} {
			keys := large(shift)
			checkUnique(t, keys, (*Writer).I64, (*Reader).I64, 8)
			for _, at := range [][2]int{{0, 1}, {0, n - 1}, {n / 2, n/2 + 1}, {n - 2, n - 1}} {
				dup := slices.Clone(keys)
				dup[at[1]] = dup[at[0]]
				checkUnique(t, dup, (*Writer).I64, (*Reader).I64, 8)
			}
		}
		u16 := make([]uint16, n)
		for i := range u16 {
			u16[i] = uint16(perm[i] * 13)
		}
		checkUnique(t, u16, (*Writer).U16, (*Reader).U16, 2)
		r32 := make([]int32, n)
		for i := range r32 {
			r32[i] = rng.Int31() - 1<<30
		}
		checkUnique(t, r32, (*Writer).I32, (*Reader).I32, 4)
		r32[n-1] = r32[3]
		checkUnique(t, r32, (*Writer).I32, (*Reader).I32, 4)
	}
}

// checkPrimitiveEntries writes e with WritePrimitiveEntries (one block where Entry[K, V] has no padding on a
// little-endian host, else a pair at a time) and checks the bytes are those of writeEntries, then reads them back
// with ReadPrimitiveEntries and checks the pairs and error against readUniqueEntries' (CodeDuplicateKey for a
// repeated key), and that every truncation fails.
func checkPrimitiveEntries[K, V Primitive](t *testing.T, e []Entry[K, V]) {
	t.Helper()
	w := NewWriter(0)
	WritePrimitiveEntries(w, e)
	b := w.Bytes()
	ref := NewWriter(0)
	writeEntries(ref, e, writePrimitive[K], writePrimitive[V])
	if !bytes.Equal(b, ref.Bytes()) {
		t.Fatalf("%T: %d pairs: bytes %x, want %x", e, len(e), b, ref.Bytes())
	}
	code := func(err error) Code {
		var x *Error
		if errors.As(err, &x) {
			return x.Code
		}
		if err != nil {
			return 255
		}
		return 0
	}
	r := NewReader(b)
	got := ReadPrimitiveEntries[K, V](r)
	var p Entry[K, V]
	rr := NewReader(b)
	want := readUniqueEntries(rr, int(unsafe.Sizeof(p.Key)+unsafe.Sizeof(p.Value)), readPrimitive[K], readPrimitive[V])
	if code(r.Err()) != code(rr.Err()) || (got == nil) != (want == nil) || len(got) != len(want) {
		t.Fatalf("%T: %d pairs: got %d, %v; want %d, %v", e, len(e), len(got), r.Err(), len(want), rr.Err())
	}
	if got != nil {
		back := NewWriter(0)
		WritePrimitiveEntries(back, got)
		if !bytes.Equal(back.Bytes(), b) || r.Remaining() != 0 {
			t.Fatalf("%T: %d pairs read back as %x", e, len(e), back.Bytes())
		}
	}
	for cut := range len(b) {
		r := NewReader(b[:cut])
		if v := ReadPrimitiveEntries[K, V](r); v != nil || r.Err() == nil {
			t.Fatalf("%T: %d pairs cut at %d: got %d pairs, %v", e, len(e), cut, len(v), r.Err())
		}
		r = NewReader(b[:cut])
		if m := ReadPrimitiveMap[K, V](r); m != nil || r.Err() == nil {
			t.Fatalf("map[%T]%T: %d pairs cut at %d: got %d pairs, %v", p.Key, p.Value, len(e), cut, len(m), r.Err())
		}
	}
	checkPrimitiveMap[K, V](t, b, code)
}

// checkPrimitiveMap reads b (a dictionary's bytes) with ReadPrimitiveMap and checks the map and error against
// readMap's, then writes the map with WritePrimitiveMap and checks those bytes read back as the same map with
// readMap.
func checkPrimitiveMap[K, V Primitive](t *testing.T, b []byte, code func(error) Code) {
	t.Helper()
	var p Entry[K, V]
	wire := int(unsafe.Sizeof(p.Key) + unsafe.Sizeof(p.Value))
	r, rr := NewReader(b), NewReader(b)
	got := ReadPrimitiveMap[K, V](r)
	want := readMap(rr, wire, readPrimitive[K], readPrimitive[V])
	if code(r.Err()) != code(rr.Err()) || (got == nil) != (want == nil) || !sameMap(got, want) ||
		r.Remaining() != rr.Remaining() {
		t.Fatalf("map[%T]%T: got %d, %v; want %d, %v", p.Key, p.Value, len(got), r.Err(), len(want), rr.Err())
	}
	if got == nil {
		return
	}
	w := NewWriter(0)
	WritePrimitiveMap(w, got)
	back := NewReader(w.Bytes())
	if m := readMap(back, wire, readPrimitive[K], readPrimitive[V]); back.Err() != nil || back.Remaining() != 0 ||
		!sameMap(m, got) || len(w.Bytes()) != 4+len(got)*wire {
		t.Fatalf("map[%T]%T: %d pairs written as %x", p.Key, p.Value, len(got), w.Bytes())
	}
}

// sameMap reports whether a and b hold the same pairs, values compared bit for bit (a NaN equals itself).
func sameMap[K comparable, V any](a, b map[K]V) bool {
	if len(a) != len(b) {
		return false
	}
	for k, x := range a {
		y, ok := b[k]
		if !ok || !equalSlices([]V{x}, []V{y}) {
			return false
		}
	}
	return true
}

// primitiveEntries checks n pairs for each n up to 40 and 1000: distinct keys, then keys from a small range (most
// with a repeated key).
func primitiveEntries[K, V Primitive](t *testing.T, key func(int) K, value func(int) V) {
	t.Helper()
	rng := rand.New(rand.NewSource(3))
	checkPrimitiveEntries[K, V](t, nil)
	for n := 0; n <= 1000; n++ {
		if n > 40 {
			n = 1000
		}
		e := make([]Entry[K, V], n)
		for i := range e {
			e[i] = Entry[K, V]{key(i), value(rng.Int())}
		}
		checkPrimitiveEntries(t, e)
		for i := range e {
			e[i].Key = key(rng.Intn(2*n + 1))
		}
		checkPrimitiveEntries(t, e)
	}
}

func TestPrimitiveEntries(t *testing.T) {
	// No padding: one block on little-endian hosts.
	primitiveEntries(t, func(i int) int32 { return int32(i) << 12 }, func(i int) int32 { return int32(i) })
	primitiveEntries(t, func(i int) uint8 { return uint8(i) }, func(i int) int8 { return int8(i) })
	primitiveEntries(t, func(i int) uint16 { return uint16(i * 7) }, func(i int) uint16 { return uint16(i) })
	primitiveEntries(t, func(i int) int64 { return int64(i) << 40 }, func(i int) float64 { return float64(i) / 3 })
	// Float keys keep the map: -0 and +0 are one key.
	primitiveEntries(t, func(i int) float32 {
		if i == 0 {
			return float32(math.Copysign(0, -1))
		}
		return float32(i) / 2
	}, func(i int) int32 { return int32(i) })
	primitiveEntries(t, func(i int) float64 { return float64(i - 1) }, func(i int) uint64 { return uint64(i) })
	// Padded in memory: a pair at a time everywhere.
	primitiveEntries(t, func(i int) uint8 { return uint8(i) }, func(i int) int64 { return int64(i) << 33 })
	primitiveEntries(t, func(i int) uniqueKind { return uniqueKind(i) }, func(i int) int8 { return int8(i) })
	primitiveEntries(t, func(i int) int64 { return int64(i) }, func(i int) float32 { return float32(i) })
}

// TestUniqueFloatsAsDotNet: floating-point elements and keys are equal as .NET's double.Equals has it: any two NaNs
// (whatever their payload), and -0 with 0, where Go's == and maps keep NaNs apart.
func TestUniqueFloatsAsDotNet(t *testing.T) {
	quiet, payload := math.NaN(), math.Float64frombits(0x7FF0000000000001)
	for _, n := range []int{2, 16, 17, 300} {
		base := make([]float64, n)
		for i := range base {
			base[i] = float64(i) + 0.5
		}
		for _, pair := range [][2]float64{{quiet, payload}, {quiet, quiet}, {0, math.Copysign(0, -1)}} {
			v := slices.Clone(base)
			v[0], v[n-1] = pair[0], pair[1]
			r := NewReader(nil)
			if CheckUniqueValues(r, v) != nil || r.Err() == nil {
				t.Fatalf("%d float64s with %v and %v: accepted", n, pair[0], pair[1])
			}
			e := []Entry[float64, int32]{{pair[0], 1}, {pair[1], 2}}
			if r = NewReader(nil); CheckUniqueKeys(r, e) != nil || r.Err() == nil {
				t.Fatalf("keys %v and %v: accepted", pair[0], pair[1])
			}
			f := make([]float32, n)
			for i := range f {
				f[i] = float32(v[i])
			}
			if r = NewReader(nil); CheckUniqueValues(r, f) != nil || r.Err() == nil {
				t.Fatalf("%d float32s with %v and %v: accepted", n, pair[0], pair[1])
			}
		}
		v := slices.Clone(base)
		v[0] = quiet
		if r := NewReader(nil); CheckUniqueValues(r, v) == nil || r.Err() != nil {
			t.Fatalf("%d float64s with one NaN: %v", n, r.Err())
		}
	}
}

// TestUniqueStrings: sets of nullable strings (CheckUniqueStrings) and of strings, small and large, distinct, with a
// repeated string or null at both ends and in the middle, and the fallbacks (a sort, a map) against the tables.
func TestUniqueStrings(t *testing.T) {
	for _, n := range []int{0, 1, 2, 16, 17, 255, 256, 257, 1000} {
		texts := make([]string, n)
		ptrs := make([]*string, n)
		for i := range texts {
			texts[i] = "member " + string(rune('a'+i%26)) + string(rune('0'+i/26%10)) + string(rune('0'+i/260))
			ptrs[i] = &texts[i]
		}
		check := func(v []*string, want bool) {
			t.Helper()
			r := NewReader(nil)
			got := CheckUniqueStrings(r, v)
			if want != (r.Err() != nil) || want != (got == nil) {
				t.Fatalf("%d strings (repeated %v): %v", len(v), want, r.Err())
			}
			p := unsafe.Pointer(unsafe.SliceData(v))
			if repeatedStringsMap(p, len(v), int(unsafe.Sizeof(v[0])), true) != want {
				t.Fatalf("%d strings through the map: want %v", len(v), want)
			}
		}
		check(ptrs, false)
		if r := NewReader(nil); CheckUniqueValues(r, texts) == nil && n > 0 || r.Err() != nil {
			t.Fatalf("%d strings: %v", n, r.Err())
		}
		if n < 2 {
			continue
		}
		for _, at := range [][2]int{{0, 1}, {0, n - 1}, {n/2 - 1, n / 2}} {
			dup := slices.Clone(ptrs)
			copyText := *dup[at[0]]
			dup[at[1]] = &copyText // equal text, another pointer
			check(dup, true)
			dup[at[0]], dup[at[1]] = nil, nil
			check(dup, true)
			dup[at[1]] = ptrs[at[1]]
			check(dup, false)
			plain := slices.Clone(texts)
			plain[at[1]] = plain[at[0]]
			if r := NewReader(nil); CheckUniqueValues(r, plain) != nil || r.Err() == nil {
				t.Fatalf("%d strings with a repeat: accepted", n)
			}
		}
	}
}

// TestUniqueFallbacks: the sort that takes over from the integer table when a key probes too far finds the same
// repeats.
func TestUniqueFallbacks(t *testing.T) {
	rng := rand.New(rand.NewSource(3))
	for _, n := range []int{17, 100, 1000} {
		v := make([]int64, n)
		for i := range v {
			v[i] = rng.Int63()
		}
		p := unsafe.Pointer(unsafe.SliceData(v))
		if repeatedSorted(p, n, 8, 8, false) || repeatedBits(p, n, 8, 8, false, new([]uint64)) {
			t.Fatalf("%d distinct keys found repeated", n)
		}
		v[n-1] = v[n/3]
		if !repeatedSorted(p, n, 8, 8, false) || !repeatedBits(p, n, 8, 8, false, new([]uint64)) {
			t.Fatalf("%d keys with a repeat not found", n)
		}
	}
}

// TestMapNaNKeys: Go maps read from the data (readMap, readSet, ReadPrimitiveMap, struct tags) reject a second NaN
// key, which .NET's comparers find equal to the first.
func TestMapNaNKeys(t *testing.T) {
	keys := []float64{1, math.NaN(), 2, math.Float64frombits(0x7FF0000000000001)}
	w := NewWriter(0)
	writeEntries(w, []Entry[float64, int32]{{keys[0], 0}, {keys[1], 1}, {keys[2], 2}, {keys[3], 3}}, (*Writer).F64, (*Writer).I32)
	data := w.Bytes()
	failed := func(r *Reader) bool {
		var e *Error
		return errors.As(r.Err(), &e) && e.Code == CodeDuplicateKey
	}
	if r := NewReader(data); readMap(r, 12, (*Reader).F64, (*Reader).I32) != nil || !failed(r) {
		t.Fatalf("readMap: %v", r.Err())
	}
	if r := NewReader(data); ReadPrimitiveMap[float64, int32](r) != nil || !failed(r) {
		t.Fatalf("ReadPrimitiveMap: %v", r.Err())
	}
	var m map[float64]int32
	if err := Unmarshal(data, &m); err == nil {
		t.Fatal("struct tags accepted two NaN keys")
	}
	ws := NewWriter(0)
	writeSlice(ws, keys, (*Writer).F64)
	if r := NewReader(ws.Bytes()); readSet(r, 8, (*Reader).F64) != nil || !failed(r) {
		t.Fatalf("readSet: %v", r.Err())
	}
	// One NaN is fine.
	w = NewWriter(0)
	writeEntries(w, []Entry[float64, int32]{{1, 0}, {math.NaN(), 1}}, (*Writer).F64, (*Writer).I32)
	if r := NewReader(w.Bytes()); len(ReadPrimitiveMap[float64, int32](r)) != 2 || r.Err() != nil {
		t.Fatalf("one NaN: %v", r.Err())
	}
}
