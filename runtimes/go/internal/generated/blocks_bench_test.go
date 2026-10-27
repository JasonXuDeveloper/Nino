package generated

import (
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The arrays of raw values nino-gen writes as blocks, 1,000 elements each, in the corpus's types:
// BenchmarkBlocks/<case>/{Serialize,Deserialize}. Local numbers are for comparing changes back to back.
func BenchmarkBlocks(b *testing.B) {
	const n = 1000
	vec := make([]Vec2, n)
	padded := make([]Padded, n)
	mixed := make([]Mixed, n)
	bools := make([]bool, n)
	for i := range n {
		vec[i] = Vec2{X: float32(i) / 3, Y: -float32(i)}
		padded[i] = Padded{A: uint8(i), B: int64(i) << 40}
		mixed[i] = Mixed{A: uint8(i), B: int16(-i), C: int32(i) * 7919, D: uint8(i >> 2)}
		bools[i] = i%3 == 0
	}
	blocksCase(b, "vec2-1000", &Structs{VecArray: vec}, WriteStructs, ReadStructs)
	blocksCase(b, "padded-1000", &PaddedArray{Values: padded}, WritePaddedArray, ReadPaddedArray)
	blocksCase(b, "mixed-1000", &Structs{MixedArray: mixed}, WriteStructs, ReadStructs)
	blocksCase(b, "bools-1000", &BoolArray{Values: bools}, WriteBoolArray, ReadBoolArray)
}

func blocksCase[T any](b *testing.B, name string, v T, write func(*nino.Writer, T), read func(*nino.Reader) T) {
	data, err := nino.Serialize(v, write)
	if err != nil {
		b.Fatal(err)
	}
	b.Run(name+"/Serialize", func(b *testing.B) {
		b.SetBytes(int64(len(data)))
		b.ReportAllocs()
		buf := make([]byte, 0, len(data))
		for range b.N {
			buf, _ = nino.Append(buf[:0], v, write)
		}
	})
	b.Run(name+"/Deserialize", func(b *testing.B) {
		b.SetBytes(int64(len(data)))
		b.ReportAllocs()
		for range b.N {
			if _, err := nino.Deserialize(data, read); err != nil {
				b.Fatal(err)
			}
		}
	})
}
