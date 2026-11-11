package generated

import (
	"encoding/binary"
	"runtime"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// Nested lists that each claim as many elements as bytes are left, then fail at the end of the data: what the read
// allocates ahead of the elements is bounded for the whole read (Reader.SliceCap), not per list, so 120 such lists
// in 1 MB allocate a few times 16 MB, not 120 times (959 MB with a budget per list).
func TestNestedClaimsShareTheBudget(t *testing.T) {
	const n = 1 << 20
	data := make([]byte, n)
	at := 0
	for k := 0; k < 120; k++ {
		data[at] = 1 // presence; Value 0
		at += 5
		binary.LittleEndian.PutUint32(data[at:], uint32(n-at-4))
		at += 4
	}
	runtime.GC()
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	r := nino.NewReader(data)
	ReadTreeNode(r)
	runtime.ReadMemStats(&after)
	if r.Err() == nil {
		t.Fatal("read past the data")
	}
	if mb := (after.TotalAlloc - before.TotalAlloc) >> 20; mb > 64 {
		t.Fatalf("allocated %d MB for 1 MB of data", mb)
	}
}
