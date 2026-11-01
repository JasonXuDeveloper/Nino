package nino

import "testing"

// The struct-tag path against the same type written by hand, and its unsafe field access against reflect's
// setters (the safe path).
type benchTagged struct {
	ID      int32            `nino:"0"`
	Flag    bool             `nino:"1"`
	Score   int64            `nino:"2"`
	Ratio   float64          `nino:"3"`
	Name    string           `nino:"4"`
	Numbers []int32          `nino:"5"`
	Kids    []benchTaggedKid `nino:"6"`
}

type benchTaggedKid struct {
	A int32   `nino:"0"`
	B *string `nino:"1"`
}

var benchTaggedClass = Class[benchTagged]{Deferrable: true,
	Read: func(r *Reader, v *benchTagged) {
		v.ID, v.Flag, v.Score, v.Ratio, v.Name = r.I32(), r.Bool(), r.I64(), r.F64(), r.String()
		v.Numbers = ReadPrimitives[int32](r)
		v.Kids = readSlice(r, 4, func(r *Reader) benchTaggedKid { return benchTaggedKid{r.I32(), r.StringPtr()} })
	},
	Write: func(w *Writer, v *benchTagged) {
		w.I32(v.ID)
		w.Bool(v.Flag)
		w.I64(v.Score)
		w.F64(v.Ratio)
		w.String(v.Name)
		WritePrimitives(w, v.Numbers)
		writeSlice(w, v.Kids, func(w *Writer, k benchTaggedKid) { w.I32(k.A); w.StringPtr(k.B) })
	},
}

func benchValue() *benchTagged {
	v := &benchTagged{ID: 7, Flag: true, Score: 1 << 40, Ratio: 0.25, Name: "benchmark", Numbers: make([]int32, 64)}
	for i := range v.Numbers {
		v.Numbers[i] = int32(i)
	}
	for i := 0; i < 8; i++ {
		v.Kids = append(v.Kids, benchTaggedKid{int32(i), str("kid")})
	}
	return v
}

func BenchmarkTagged(b *testing.B) {
	v := benchValue()
	write := func(w *Writer, v *benchTagged) { WriteSealed(w, v, &benchTaggedClass) }
	read := func(r *Reader) *benchTagged { return ReadSealed(r, &benchTaggedClass) }
	data, _ := Serialize(v, write)
	b.Run("Marshal/Codec", func(b *testing.B) {
		buf := make([]byte, 0, 1024)
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			buf, _ = Append(buf[:0], v, write)
		}
	})
	b.Run("Unmarshal/Codec", func(b *testing.B) {
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			if _, err := Deserialize(data, read); err != nil {
				b.Fatal(err)
			}
		}
	})
	for _, fast := range []bool{true, false} {
		name := "Safe"
		if fast {
			name = "Unsafe"
		}
		b.Run("Marshal/Tags"+name, func(b *testing.B) {
			prev := unsafeAccess.Load()
			unsafeAccess.Store(fast)
			defer unsafeAccess.Store(prev)
			buf, _ := Marshal(v)
			b.ReportAllocs()
			b.ResetTimer()
			for i := 0; i < b.N; i++ {
				buf, _ = MarshalAppend(buf[:0], v)
			}
		})
		b.Run("Unmarshal/Tags"+name, func(b *testing.B) {
			prev := unsafeAccess.Load()
			unsafeAccess.Store(fast)
			defer unsafeAccess.Store(prev)
			b.ReportAllocs()
			for i := 0; i < b.N; i++ {
				var out *benchTagged
				if err := Unmarshal(data, &out); err != nil {
					b.Fatal(err)
				}
			}
		})
	}
}
