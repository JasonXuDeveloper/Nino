package sample

import (
	"math/big"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go"
)

// Target is one of the small sealed classes (namespace Conformance.Targets) the corpus forges invalid data against:
// a presence byte, then one member.
type Target[T any] struct {
	Value T
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
	return nino.ReadUniqueEntries(r, 8, (*nino.Reader).I32, (*nino.Reader).I32)
}

func WriteIntMap(w *nino.Writer, v []nino.Entry[int32, int32]) {
	nino.WriteEntries(w, v, (*nino.Writer).I32, (*nino.Writer).I32)
}

func ReadStringIntMap(r *nino.Reader) []nino.Entry[string, int32] {
	return nino.ReadUniqueEntries(r, 8, keyString, (*nino.Reader).I32)
}

func WriteStringIntMap(w *nino.Writer, v []nino.Entry[string, int32]) {
	nino.WriteEntries(w, v, (*nino.Writer).String, (*nino.Writer).I32)
}

func ReadIntSet(r *nino.Reader) []int32 { return nino.ReadUnique(r, 4, (*nino.Reader).I32) }

func ReadStringSet(r *nino.Reader) []*string { return readStringSet(r) }

func ReadStringList(r *nino.Reader) []*string { return readStringList(r) }

func WriteStringList(w *nino.Writer, v []*string) { writeStringList(w, v) }

func ReadIntGrid(r *nino.Reader) *Grid[int32] { return readGrid(r, 4, (*nino.Reader).I32) }

func WriteIntGrid(w *nino.Writer, v *Grid[int32]) { writeGrid(w, v, (*nino.Writer).I32) }

func ReadMaybeIntArray(r *nino.Reader) []*int32 { return nino.ReadFixed(r, 8, decodeStrideInt32) }

func WriteMaybeIntArray(w *nino.Writer, v []*int32) {
	nino.WriteFixed(w, v, 8, encodeStrideInt32)
}

func ReadMaybeInt(r *nino.Reader) *int32 { return readNullableInt32(r) }

func WriteMaybeInt(w *nino.Writer, v *int32) { writeNullableInt32(w, v) }

func ReadPaddedArray(r *nino.Reader) []Padded { return nino.ReadFixed(r, 16, decodePadded) }

func WritePaddedArray(w *nino.Writer, v []Padded) { nino.WriteFixed(w, v, 16, encodePadded) }

func ReadPointList(r *nino.Reader) []*Point { return nino.ReadSlice(r, 1, ReadPoint) }

func WritePointList(w *nino.Writer, v []*Point) { nino.WriteSlice(w, v, WritePoint) }

func ReadBig(r *nino.Reader) *big.Int { return r.BigInteger() }
