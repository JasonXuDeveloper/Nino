package nino

import (
	"bytes"
	"reflect"
	"strconv"
	"testing"
)

type prefetchItem struct {
	ID   int32
	Name string
	Tags []string
}

func writePrefetchItem(w *Writer, v prefetchItem) {
	w.I32(v.ID)
	w.String(v.Name)
	WriteStrings(w, v.Tags)
}

func writePrefetchItemPtr(w *Writer, v *prefetchItem) {
	w.Present(v != nil)
	if v != nil {
		writePrefetchItem(w, *v)
	}
}

// checkPrefetched writes v with WriteSlice and with a plain loop and checks the bytes are the same.
func checkPrefetched[T any](t *testing.T, v []T, write func(*Writer, T)) {
	t.Helper()
	got := NewWriter(0)
	WriteSlice(got, v, write)
	want := NewWriter(0)
	want.Length(len(v))
	for _, e := range v {
		write(want, e)
	}
	if !bytes.Equal(got.Bytes(), want.Bytes()) {
		t.Fatalf("%T of %d: bytes differ", v, len(v))
	}
}

// withLastLevelCache makes the plans for a last-level cache of n bytes while f runs.
func withLastLevelCache(n int, f func()) {
	clearPlans := func() {
		prefetchPlans.Range(func(k, _ any) bool {
			prefetchPlans.Delete(k)
			return true
		})
	}
	saved := lastLevelCache
	lastLevelCache = func() int { return n }
	clearPlans()
	defer func() {
		lastLevelCache = saved
		clearPlans()
	}()
	f()
}

func TestCacheSize(t *testing.T) {
	for s, want := range map[string]int{"32768K": 32 << 20, "1M": 1 << 20, "2G": 2 << 30, "512": 512, "": 0, "x": 0} {
		if got := parseCacheSize(s); got != want {
			t.Errorf("parseCacheSize(%q) = %d, want %d", s, got, want)
		}
	}
	t.Logf("last-level cache: %d bytes (read: %d)", lastLevelCache(), readLastLevelCache())
	if lastLevelCache() <= 0 {
		t.Fatal("no last-level cache size")
	}
}

func TestPrefetchedWrites(t *testing.T) {
	// A 1 MB cache: the thresholds are a few thousand elements.
	withLastLevelCache(1<<20, func() { testPrefetchedWrites(t) })
}

func testPrefetchedWrites(t *testing.T) {
	p := prefetchPlanOf(reflect.TypeFor[*prefetchItem]())
	if p == nil || !p.pointer || len(p.members) != 2 || p.min < prefetchFloor {
		t.Fatalf("plan of *prefetchItem: %+v", p)
	}
	if q := prefetchPlanOf(reflect.TypeFor[prefetchItem]()); q == nil || q.pointer || len(q.members) != 2 {
		t.Fatalf("plan of prefetchItem: %+v", q)
	}
	for _, typ := range []reflect.Type{reflect.TypeFor[int32](), reflect.TypeFor[DateTime](), reflect.TypeFor[string]()} {
		if q := prefetchPlanOf(typ); q != nil {
			t.Fatalf("plan of %v: %+v, want none", typ, q)
		}
	}
	for _, n := range []int{prefetchFloor - 1, p.min - 1, p.min, p.min + 1, p.min + 7, p.min + 13, 3 * p.min} {
		items := make([]prefetchItem, n)
		ptrs := make([]*prefetchItem, n)
		for i := range items {
			items[i] = prefetchItem{int32(i), "item " + strconv.Itoa(i), []string{"a", strconv.Itoa(i)}}
			if i%5 != 3 {
				ptrs[i] = &items[n-1-i]
			}
		}
		checkPrefetched(t, items, writePrefetchItem)
		checkPrefetched(t, ptrs, writePrefetchItemPtr)
	}
}
