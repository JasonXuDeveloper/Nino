package bench

import (
	"encoding/binary"
	"math"
	"math/rand"
	"strconv"
	"unsafe"

	"github.com/JasonXuDeveloper/Nino/runtimes/go/bench/benchpb"
	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The scattered case: a list of 1,000,000 small classes with two strings each, in an order unrelated to where they
// are in memory (allocated in order, then shuffled), as a long-lived collection that was sorted or filled from
// several places is. Far larger than the caches, it measures the cache misses of walking objects and their
// strings, which the rest of the cases (small, allocated in order) do not.

// ScatteredCount is the number of records in the scattered case.
const ScatteredCount = 1_000_000

// Record is a class with a fixed-size run and two strings.
type Record struct {
	ID    int32   `nino:"0" json:"id" msgpack:"id"`
	Score float64 `nino:"1" json:"score" msgpack:"score"`
	Name  string  `nino:"2" json:"name" msgpack:"name"`
	Email string  `nino:"3" json:"email" msgpack:"email"`
}

var record nino.Class[Record]

func init() {
	record = nino.Class[Record]{Read: readRecordBody, Write: writeRecordBody, Deferrable: true}
}

func ReadRecord(r *nino.Reader) *Record {
	if !r.Present() {
		return nil
	}
	v := new(Record)
	if d, ok := r.BeginBody(); ok {
		readRecordBody(r, v)
		r.EndBody(d)
	} else {
		nino.ReadClassInto(r, v, &record)
	}
	return v
}
func WriteRecord(w *nino.Writer, v *Record) {
	if v == nil {
		w.U8(0)
		return
	}
	w.U8(1)
	if d, ok := w.BeginBody(); ok {
		writeRecordBody(w, v)
		w.EndBody(d)
	} else {
		nino.WriteClass(w, v, &record)
	}
}

func readRecordBody(r *nino.Reader, v *Record) {
	b := r.Take(12)
	if len(b) < 12 {
		return
	}
	v.ID = int32(binary.LittleEndian.Uint32(b))
	v.Score = math.Float64frombits(binary.LittleEndian.Uint64(b[4:12]))
	v.Name = r.String()
	v.Email = r.String()
}

func writeRecordBody(w *nino.Writer, v *Record) {
	b := w.Extend(12)
	if len(b) < 12 {
		return
	}
	binary.LittleEndian.PutUint32(b, uint32(v.ID))
	binary.LittleEndian.PutUint64(b[4:12], math.Float64bits(v.Score))
	w.String(v.Name)
	w.String(v.Email)
}

// ReadRecords and WriteRecords are a List<Record>.
func ReadRecords(r *nino.Reader) []*Record {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e *Record
	v := make([]*Record, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = ReadRecord(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func WriteRecords(w *nino.Writer, v []*Record) {
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		WriteRecord(w, e)
	}
}

// scatteredOrder returns the order the records are listed in: a seeded shuffle.
func scatteredOrder() []int {
	return rand.New(rand.NewSource(20240607)).Perm(ScatteredCount)
}

// ScatteredRecords returns the records, allocated in order (each with its strings) and listed shuffled.
func ScatteredRecords() []*Record {
	made := make([]*Record, ScatteredCount)
	for i := range made {
		s := strconv.Itoa(i)
		made[i] = &Record{ID: int32(i), Score: float64(i) / 7, Name: "record " + s, Email: "user" + s + "@example.com"}
	}
	v := make([]*Record, ScatteredCount)
	for i, j := range scatteredOrder() {
		v[i] = made[j]
	}
	return v
}

// protoRecords maps the records to messages allocated in the same order as the records and listed in the same
// shuffled order, so that their layout in memory is the same as the Go values'.
func protoRecords(v []*Record) *benchpb.Records {
	values := make([]*Record, len(v))
	for _, x := range v {
		values[x.ID] = x
	}
	byID := make([]*benchpb.Record, len(v))
	for i, x := range values {
		byID[i] = &benchpb.Record{Id: x.ID, Score: x.Score, Name: x.Name, Email: x.Email}
	}
	p := &benchpb.Records{Values: make([]*benchpb.Record, len(v))}
	for i, x := range v {
		p.Values[i] = byID[x.ID]
	}
	return p
}
