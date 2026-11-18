package shapes

import (
	"strconv"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

func BenchmarkFixedCollections353(b *testing.B) {
	const count = 1024
	ids := make([]nino.Guid, count)
	entries := make([]nino.Entry[nino.Guid, Marked], count)
	pairs := make([]nino.Entry[int32, int64], count)
	offsets := make([]nino.DateTimeOffset, count)
	textKeys := make([]string, count)
	grid := &NinoGrid[uint8]{Lengths: []int{32, 32}, Items: make([]byte, count)}
	for i := range ids {
		ids[i][0], ids[i][15] = byte(i), byte(i>>8)
		mark := Marked{Id: int32(i), On: i%2 == 0, Code: -3}
		entries[i] = nino.Entry[nino.Guid, Marked]{Key: ids[i], Value: mark}
		pairs[i] = nino.Entry[int32, int64]{Key: int32(i) * 7, Value: 50 - int64(i)}
		offsets[i] = nino.DateTimeOffset{UtcTicks: 638712864000000000 + int64(i), OffsetMinutes: -60}
		grid.Items[i] = byte(i)
		textKeys[i] = strconv.Itoa(i)
	}

	cases := []struct {
		name  string
		value *FixedCollections
	}{
		{"GuidSet", &FixedCollections{UniqueIds: ids}},
		{"GuidMap", &FixedCollections{Ids: entries}},
		{"PaddedPairs", &FixedCollections{Pairs: pairs}},
		{"ByteGrid", &FixedCollections{Grid: grid}},
		{"OffsetSequence", &FixedCollections{Offsets: offsets}},
	}
	for _, c := range cases {
		data, err := nino.Serialize(c.value, WriteFixedCollections)
		if err != nil {
			b.Fatal(err)
		}
		b.Run(c.name+"/Encode", func(b *testing.B) {
			dst := make([]byte, 0, len(data))
			b.SetBytes(int64(len(data)))
			b.ReportAllocs()
			b.ResetTimer()
			for range b.N {
				out, err := nino.Append(dst[:0], c.value, WriteFixedCollections)
				if err != nil || len(out) != len(data) {
					b.Fatalf("fixed collection write returned %d bytes (%v)", len(out), err)
				}
			}
		})
		b.Run(c.name+"/Decode", func(b *testing.B) {
			b.SetBytes(int64(len(data)))
			b.ReportAllocs()
			b.ResetTimer()
			for range b.N {
				if _, err := nino.DeserializeExact(data, ReadFixedCollections); err != nil {
					b.Fatal(err)
				}
			}
		})
	}
	b.Run("IntKeys8", func(b *testing.B) {
		benchmarkUniqueValues(b, []int32{1, 2, 3, 4, 5, 6, 7, 8})
	})
	b.Run("StringKeys1024", func(b *testing.B) {
		benchmarkUniqueValues(b, textKeys)
	})
}

func benchmarkUniqueValues[T comparable](b *testing.B, values []T) {
	r := nino.NewReader(nil)
	b.ReportAllocs()
	b.ResetTimer()
	for range b.N {
		if len(nino.CheckUniqueValues(r, values)) != len(values) {
			b.Fatal(r.Err())
		}
	}
}
