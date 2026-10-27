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
	WriteSlice(w, keys, writeKey)
	r := NewReader(w.Bytes())
	got := ReadUnique(r, size, readKey)
	if repeated != duplicate(r.Err()) || repeated != (got == nil) || !repeated && (r.Err() != nil || !slices.Equal(got, keys)) {
		t.Fatalf("set of %d keys (repeated %v): got %v, %v", len(keys), repeated, got, r.Err())
	}

	entries := make([]Entry[K, int32], len(keys))
	for i, k := range keys {
		entries[i] = Entry[K, int32]{k, int32(i)}
	}
	w = NewWriter(0)
	WriteEntries(w, entries, writeKey, (*Writer).I32)
	r = NewReader(w.Bytes())
	gotEntries := ReadUniqueEntries(r, size+4, readKey, (*Reader).I32)
	if repeated != duplicate(r.Err()) || repeated != (gotEntries == nil) || !repeated && (r.Err() != nil || !slices.Equal(gotEntries, entries)) {
		t.Fatalf("dictionary of %d keys (repeated %v): got %v, %v", len(keys), repeated, gotEntries, r.Err())
	}
}

// uniqueKeys checks every length up to 100 (pairs up to 16 keys, then a sort, its scratch on the stack up to 64) with distinct keys, then with
// each pair of positions holding the same key, and random keys from a small range.
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
	// Keys that keep the map: floats (NaN, -0) and strings.
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
// little-endian host, else a pair at a time) and checks the bytes are those of WriteEntries, then reads them back
// with ReadPrimitiveEntries and checks the pairs and error against ReadUniqueEntries' (CodeDuplicateKey for a
// repeated key), and that every truncation fails.
func checkPrimitiveEntries[K, V Primitive](t *testing.T, e []Entry[K, V]) {
	t.Helper()
	w := NewWriter(0)
	WritePrimitiveEntries(w, e)
	b := w.Bytes()
	ref := NewWriter(0)
	WriteEntries(ref, e, writePrimitive[K], writePrimitive[V])
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
	want := ReadUniqueEntries(rr, int(unsafe.Sizeof(p.Key)+unsafe.Sizeof(p.Value)), readPrimitive[K], readPrimitive[V])
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
	}
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
