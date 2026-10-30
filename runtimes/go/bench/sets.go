package bench

import (
	"math/rand"
	"strconv"
	"unsafe"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The set cases, the same data as the Rust and TypeScript benchmarks' (runtimes/rust/bench, Program.cs): a
// HashSet<int> of 1,000 distinct integers in no order, and a HashSet<string> of 1,000 distinct short ASCII strings.
// nino-gen reads a set as a slice in the order of the data and fails on a repeated element; the other serializers
// write and read the same slices (a set has no type of its own in protobuf, MessagePack, JSON or gob).

// SetInts returns 1,000 distinct non-negative integers in a shuffled order (a fixed seed).
func SetInts() []int32 {
	rnd := rand.New(rand.NewSource(20240607))
	seen := make(map[int32]bool, 1000)
	v := make([]int32, 0, 1000)
	for len(v) < 1000 {
		if x := rnd.Int31(); !seen[x] {
			seen[x] = true
			v = append(v, x)
		}
	}
	return v
}

// SetStrings returns "member 0" to "member 999" in a shuffled order (a fixed seed), as nino-gen's Go holds the
// elements of a HashSet<string> (nil for null).
func SetStrings() []*string {
	rnd := rand.New(rand.NewSource(20240607))
	v := make([]*string, 1000)
	for i, k := range rnd.Perm(len(v)) {
		s := "member " + strconv.Itoa(k)
		v[i] = &s
	}
	return v
}

// ReadIntSet and WriteIntSet are a HashSet<int>, as nino-gen writes them.
func ReadIntSet(r *nino.Reader) []int32 {
	return nino.UniqueValues(r, nino.ReadPrimitives[int32](r))
}

func WriteIntSet(w *nino.Writer, v []int32) { nino.WritePrimitives(w, v) }

// ReadStringSet and WriteStringSet are a HashSet<string>, as nino-gen writes them.
func ReadStringSet(r *nino.Reader) []*string {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e *string
	v := make([]*string, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = r.StringPtr()
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.UniqueStrings(r, v)
}

func WriteStringSet(w *nino.Writer, v []*string) { nino.WriteStringPtrs(w, v) }
