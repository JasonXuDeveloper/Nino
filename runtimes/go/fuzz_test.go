package nino

import (
	"bytes"
	"errors"
	"math"
	"testing"
	"unsafe"
)

// FuzzReader runs a program of reads (the first bytes, one op each) over the rest of the input. No sequence of
// reads may panic, read past the data, or leave a failed reader returning anything but zeros. Every value read
// successfully must write back to bytes that read to the same value.
func FuzzReader(f *testing.F) {
	f.Add([]byte{0, 1, 2, 3}, []byte("\x01\x02\x00\x00\x00abc"))
	f.Add([]byte{4, 5, 6}, hx("0361 03000000 0400000061626364"))
	f.Add([]byte{7, 8, 9, 10}, hx("0A000080 01000000 00 00000000 00000000"))
	f.Add([]byte{11, 12, 13, 14, 15}, hx("02000000 FF00 01000000 02000000 03000000 0400000005000000"))
	f.Add([]byte{16, 17, 18, 19}, hx("01000000 07 01 0000 D00102030405060708090A0B0C0D0E0F"))
	f.Add([]byte{24, 25}, hx("02000000 01000000 02000000 03000000 04000000 01000000 07 0000000000000840"))
	f.Add([]byte{20, 21, 22, 23}, hx("01000000 0000000000000000 01000000 00001C00000000000000000000000000 01000000 01000000 01000000 0000803F 01 02 0300"))
	f.Fuzz(func(t *testing.T, program, data []byte) {
		if len(program) > 64 {
			program = program[:64]
		}
		r := NewReader(data)
		w := NewWriter(0)
		for _, op := range program {
			before := r.Pos()
			fuzzOp(t, op, r, w)
			if r.Pos() < before || r.Pos() > len(data) {
				t.Fatalf("op %d moved from %d to %d (of %d)", op%fuzzOps, before, r.Pos(), len(data))
			}
			if r.Failed() {
				var e *Error
				if !errors.As(r.Err(), &e) {
					t.Fatalf("error of type %T", r.Err())
				}
				if r.U64() != 0 || r.String() != "" || r.Length(1) != -1 || r.Take(1) != nil {
					t.Fatal("a failed reader returned data")
				}
				return
			}
		}
	})
}

// fuzzOp runs one read, and checks that its value writes back to bytes that read to the same value.
func fuzzOp(t *testing.T, op byte, r *Reader, w *Writer) {
	check := func(write func(*Writer), read func(*Reader) bool) {
		if r.Failed() {
			return
		}
		w.Reset()
		write(w)
		if w.Err() != nil {
			t.Fatalf("op %d: a value read did not write back: %v", op%fuzzOps, w.Err())
		}
		back := NewReader(w.Bytes())
		if !read(back) || back.Err() != nil || back.More() {
			t.Fatalf("op %d: written bytes %x read back differently (%v)", op%fuzzOps, w.Bytes(), back.Err())
		}
	}
	switch op % fuzzOps {
	case 0:
		v := r.Bool()
		check(func(w *Writer) { w.Bool(v) }, func(b *Reader) bool { return b.Bool() == v })
	case 1:
		v := r.I32()
		check(func(w *Writer) { w.I32(v) }, func(b *Reader) bool { return b.I32() == v })
	case 2:
		v := r.U64()
		check(func(w *Writer) { w.U64(v) }, func(b *Reader) bool { return b.U64() == v })
	case 3:
		v, ok := r.NullableString()
		check(func(w *Writer) { w.NullableString(v, ok) }, func(b *Reader) bool {
			s, o := b.NullableString()
			return s == v && o == ok
		})
	case 4:
		v := r.UTF16()
		check(func(w *Writer) { w.UTF16(v) }, func(b *Reader) bool {
			u := b.UTF16()
			return (u == nil) == (v == nil) && equalUnits(u, v)
		})
	case 5:
		v, ok := r.NullableUTF8()
		check(func(w *Writer) { w.NullableUTF8(v, ok) }, func(b *Reader) bool {
			s, o := b.NullableUTF8()
			return s == v && o == ok
		})
	case 6:
		v := r.ByteArray()
		check(func(w *Writer) { w.ByteArray(v) }, func(b *Reader) bool {
			u := b.ByteArray()
			return (u == nil) == (v == nil) && bytes.Equal(u, v)
		})
	case 7:
		g := r.BeginRegion()
		for r.More() && !r.Failed() {
			r.U8()
		}
		r.EndRegion(g)
	case 8:
		e := r.BeginEntry()
		r.SkipEntry(e)
	case 9:
		v := r.Decimal()
		check(func(w *Writer) { w.Decimal(v) }, func(b *Reader) bool { return b.Decimal() == v })
	case 10:
		v := r.DateTime()
		check(func(w *Writer) { w.DateTime(v) }, func(b *Reader) bool { return b.DateTime() == v })
	case 11:
		v := r.DateTimeOffset()
		check(func(w *Writer) { w.DateTimeOffset(v) }, func(b *Reader) bool { return b.DateTimeOffset() == v })
	case 12:
		v := r.BigInteger()
		check(func(w *Writer) { w.BigInteger(v) }, func(b *Reader) bool { return b.BigInteger().Cmp(v) == 0 })
	case 13:
		v := r.Version()
		check(func(w *Writer) { w.Version(v) }, func(b *Reader) bool {
			u := b.Version()
			return (u == nil) == (v == nil) && (u == nil || *u == *v)
		})
	case 14:
		v := ReadPrimitives[int32](r)
		check(func(w *Writer) { WritePrimitives(w, v) }, func(b *Reader) bool {
			u := ReadPrimitives[int32](b)
			return (u == nil) == (v == nil) && len(u) == len(v)
		})
	case 15:
		v := ReadBools(r)
		check(func(w *Writer) { WriteBools(w, v) }, func(b *Reader) bool { return len(ReadBools(b)) == len(v) })
	case 16:
		v := ReadUnique(r, 4, (*Reader).I32)
		check(func(w *Writer) { WritePrimitives(w, v) }, func(b *Reader) bool {
			return len(ReadUnique(b, 4, (*Reader).I32)) == len(v)
		})
	case 17:
		v := ReadMap(r, 5, (*Reader).I32, (*Reader).String)
		check(func(w *Writer) { WriteMap(w, v, (*Writer).I32, (*Writer).String) }, func(b *Reader) bool {
			return len(ReadMap(b, 5, (*Reader).I32, (*Reader).String)) == len(v)
		})
	case 18:
		s, ok := r.ReadShape(2+int(r.U8()%3), 4)
		if ok {
			items := make([]int32, s.Total)
			ReadPrimitivesInto(r, items)
		}
	case 19:
		v := r.Guid()
		check(func(w *Writer) { w.Guid(v) }, func(b *Reader) bool { return b.Guid() == v })
	case 20:
		v := ReadBlock[DateTime](r)
		check(func(w *Writer) { WriteBlock(w, v) }, func(b *Reader) bool { return equalSlices(ReadBlock[DateTime](b), v) })
	case 21:
		v := ReadBlock[Decimal](r)
		check(func(w *Writer) { WriteBlock(w, v) }, func(b *Reader) bool { return equalSlices(ReadBlock[Decimal](b), v) })
	case 22:
		v := ReadBlock[DateOnly](r)
		check(func(w *Writer) { WriteBlock(w, v) }, func(b *Reader) bool { return equalSlices(ReadBlock[DateOnly](b), v) })
	case 23:
		// A raw struct with a bool, copied as a block where its memory is its bytes.
		v := ReadRaw(r, 8, rawPointAsIs, []int{4}, decodeRawPoint)
		check(func(w *Writer) { WriteRaw(w, v, 8, rawPointAsIs, []int{4}, nil, encodeRawPoint) }, func(b *Reader) bool {
			return equalSlices(ReadRaw(b, 8, rawPointAsIs, []int{4}, decodeRawPoint), v)
		})
	case 24:
		v := ReadPrimitiveEntries[int32, int32](r)
		check(func(w *Writer) { WritePrimitiveEntries(w, v) }, func(b *Reader) bool {
			return equalSlices(ReadPrimitiveEntries[int32, int32](b), v)
		})
	case 25:
		// Padded in memory: a pair at a time.
		v := ReadPrimitiveEntries[uint8, float64](r)
		check(func(w *Writer) { WritePrimitiveEntries(w, v) }, func(b *Reader) bool {
			back := ReadPrimitiveEntries[uint8, float64](b)
			if (back == nil) != (v == nil) || len(back) != len(v) {
				return false
			}
			for i, e := range back {
				if e.Key != v[i].Key || math.Float64bits(e.Value) != math.Float64bits(v[i].Value) {
					return false
				}
			}
			return true
		})
	}
}

// fuzzOps is the number of ops fuzzOp runs.
const fuzzOps = 26

// equalSlices reports whether a and b are both nil, or both not nil with the same memory (bit for bit: a NaN equals
// itself; the types compared have no padding).
func equalSlices[T any](a, b []T) bool {
	if (a == nil) != (b == nil) || len(a) != len(b) {
		return false
	}
	var zero T
	size := len(a) * int(unsafe.Sizeof(zero))
	return size == 0 || bytes.Equal(unsafe.Slice((*byte)(unsafe.Pointer(&a[0])), size),
		unsafe.Slice((*byte)(unsafe.Pointer(&b[0])), size))
}

func equalUnits(a, b []uint16) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}
