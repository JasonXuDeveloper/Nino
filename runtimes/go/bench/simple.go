// Package bench compares the Go runtime with Protocol Buffers, MessagePack, encoding/json and encoding/gob on the
// data of Nino's .NET benchmark (SimpleClass, benchmarks/Nino.Benchmark/SimpleBenchmarkData.cs) and on lists of
// strings (strings.go). It is a module of its own, so the runtime's module has no dependencies.
package bench

import (
	"encoding/binary"
	"math/rand"
	"strconv"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// SimpleClass is the .NET benchmark's SimpleClass (its base's members first), with the codec a generator would
// write for it: the fixed-size members at the start as one run, then the rest. Its nino tags map it for
// nino.Marshal and nino.Unmarshal too (the struct-tag path, compared with the generated-style codec).
type SimpleClass struct {
	ID         int32            `nino:"0" json:"id" msgpack:"id"`
	Tag        bool             `nino:"1" json:"tag" msgpack:"tag"`
	Guid       nino.Guid        `nino:"2" json:"guid" msgpack:"guid"`
	CreateTime nino.DateTime    `nino:"3" json:"createTime" msgpack:"createTime"`
	Name       string           `nino:"4" json:"name" msgpack:"name"`
	Desc       string           `nino:"5,utf8" json:"desc" msgpack:"desc"` // [NinoUtf8]
	Numbers    []int32          `nino:"6" json:"numbers" msgpack:"numbers"`
	Dates      []nino.DateTime  `nino:"7" json:"dates" msgpack:"dates"`
	Map1       map[int32]string `nino:"8" json:"map1" msgpack:"map1"`
	Map2       map[int32]int32  `nino:"9" json:"map2" msgpack:"map2"`
}

var simpleClass nino.Class[SimpleClass]

func init() {
	simpleClass = nino.Class[SimpleClass]{Read: readSimpleClassBody, Write: writeSimpleClassBody, Deferrable: true}
}

func ReadSimpleClass(r *nino.Reader) *SimpleClass     { return nino.ReadSealed(r, &simpleClass) }
func WriteSimpleClass(w *nino.Writer, v *SimpleClass) { nino.WriteSealed(w, v, &simpleClass) }

func readSimpleClassBody(r *nino.Reader, v *SimpleClass) {
	b := r.Take(21)
	if len(b) < 21 {
		return
	}
	v.ID = int32(binary.LittleEndian.Uint32(b))
	v.Tag = r.BoolAt(b[4])
	copy(v.Guid[:], b[5:21])
	v.CreateTime = r.DateTime()
	v.Name = r.String()
	v.Desc = r.UTF8()
	v.Numbers = nino.ReadPrimitives[int32](r)
	v.Dates = nino.ReadBlock[nino.DateTime](r)
	v.Map1 = nino.ReadMap(r, 8, (*nino.Reader).I32, (*nino.Reader).String)
	v.Map2 = nino.ReadPrimitiveMap[int32, int32](r)
}

func writeSimpleClassBody(w *nino.Writer, v *SimpleClass) {
	b := w.Extend(21)
	if len(b) < 21 {
		return
	}
	binary.LittleEndian.PutUint32(b, uint32(v.ID))
	b[4] = 0
	if v.Tag {
		b[4] = 1
	}
	copy(b[5:21], v.Guid[:])
	w.DateTime(v.CreateTime)
	w.String(v.Name)
	w.UTF8(v.Desc)
	nino.WritePrimitives(w, v.Numbers)
	nino.WriteBlock(w, v.Dates)
	nino.WriteMap(w, v.Map1, (*nino.Writer).I32, (*nino.Writer).String)
	nino.WritePrimitiveMap(w, v.Map2)
}

// ReadSimpleClasses and WriteSimpleClasses are a List<SimpleClass>.
func ReadSimpleClasses(r *nino.Reader) []*SimpleClass { return nino.ReadSlice(r, 1, ReadSimpleClass) }

func WriteSimpleClasses(w *nino.Writer, v []*SimpleClass) { nino.WriteSlice(w, v, WriteSimpleClass) }

// NewSimpleClass makes a value like the .NET benchmark's SimpleClass.Create, from a seeded source.
func NewSimpleClass(rnd *rand.Rand) *SimpleClass {
	guid := func() (g nino.Guid) {
		rnd.Read(g[:])
		return g
	}
	now := nino.NewDateTime(638712864000000000, nino.KindLocal) // 2025-01-01 12:00, Local
	v := &SimpleClass{
		ID:         rnd.Int31(),
		Tag:        rnd.Intn(2) == 0,
		Guid:       guid(),
		CreateTime: now,
		Name:       guid().String(),
		Numbers:    make([]int32, 100),
		Dates:      make([]nino.DateTime, 10),
		Map1:       map[int32]string{},
		Map2:       map[int32]int32{},
	}
	for i := 0; i < 10; i++ {
		v.Desc += guid().String()
	}
	for i := range v.Numbers {
		v.Numbers[i] = rnd.Int31()
	}
	for i := range v.Dates {
		v.Dates[i] = nino.NewDateTime(now.Ticks()+int64(rnd.Int31())*10_000_000, nino.KindLocal)
	}
	for i := int32(0); i < 10; i++ {
		v.Map1[i] = strconv.Itoa(int(i))
		v.Map2[i] = i * 2
	}
	return v
}
