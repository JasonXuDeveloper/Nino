package sample

import (
	"math/big"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// Target is one of the small sealed classes (namespace Conformance.Targets) the corpus forges invalid data against:
// a presence byte, then one member.
type Target[T any] struct {
	Value T `nino:"0" json:"value" yaml:"value"`
}

// TargetCodec reads and writes a Target whose member is read by read and written by write.
type TargetCodec[T any] struct {
	class nino.Class[Target[T]]
}

// NewTargetCodec returns the codec of a Target.
func NewTargetCodec[T any](read func(*nino.Reader) T, write func(*nino.Writer, T)) *TargetCodec[T] {
	return &TargetCodec[T]{class: nino.Class[Target[T]]{
		Read:       func(r *nino.Reader, v *Target[T]) { v.Value = read(r) },
		Write:      func(w *nino.Writer, v *Target[T]) { write(w, v.Value) },
		Deferrable: true,
	}}
}

func (c *TargetCodec[T]) Read(r *nino.Reader) *Target[T]     { return nino.ReadSealed(r, &c.class) }
func (c *TargetCodec[T]) Write(w *nino.Writer, v *Target[T]) { nino.WriteSealed(w, v, &c.class) }

// The targets' members.

func ReadIntMap(r *nino.Reader) []nino.Entry[int32, int32] {
	return readI32I32Entries(r)
}

func WriteIntMap(w *nino.Writer, v []nino.Entry[int32, int32]) {
	writeI32I32Entries(w, v)
}

func ReadStringIntMap(r *nino.Reader) []nino.Entry[string, int32] {
	return readStringI32Entries(r)
}

func WriteStringIntMap(w *nino.Writer, v []nino.Entry[string, int32]) {
	writeStringI32Entries(w, v)
}

func ReadIntSet(r *nino.Reader) []int32 { return nino.UniqueValues(r, nino.ReadPrimitives[int32](r)) }

func ReadStringSet(r *nino.Reader) []*string { return readStringSet(r) }

func ReadStringList(r *nino.Reader) []*string { return readStringList(r) }

func WriteStringList(w *nino.Writer, v []*string) { writeStringList(w, v) }

func ReadIntGrid(r *nino.Reader) *Grid[int32] { return readIntGrid(r) }

func WriteIntGrid(w *nino.Writer, v *Grid[int32]) { writeIntGrid(w, v) }

func ReadMaybeIntArray(r *nino.Reader) []*int32 { return readStrideInt32s(r) }

func WriteMaybeIntArray(w *nino.Writer, v []*int32) {
	writeStrideInt32s(w, v)
}

func ReadMaybeInt(r *nino.Reader) *int32 { return readNullableInt32(r) }

func WriteMaybeInt(w *nino.Writer, v *int32) { writeNullableInt32(w, v) }

func ReadPaddedArray(r *nino.Reader) []Padded { return readPaddeds(r) }

func WritePaddedArray(w *nino.Writer, v []Padded) { writePaddeds(w, v) }

func ReadPointList(r *nino.Reader) []*Point { return readPoints(r) }

func WritePointList(w *nino.Writer, v []*Point) { writePoints(w, v) }

func ReadBig(r *nino.Reader) *big.Int { return r.BigInteger() }
