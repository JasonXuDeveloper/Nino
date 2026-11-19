package shapes

import (
	"strconv"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

func BenchmarkPreparedKeys353(b *testing.B) {
	const count = 1024
	ids := make([]nino.Guid, count)
	entries := make([]nino.Entry[nino.Guid, Marked], count)
	pairs := make([]nino.Entry[int32, int64], count)
	textKeys := make([]string, count)
	for i := range ids {
		ids[i][0], ids[i][15] = byte(i), byte(i>>8)
		entries[i] = nino.Entry[nino.Guid, Marked]{Key: ids[i], Value: Marked{Id: int32(i), On: i%2 == 0, Code: -3}}
		pairs[i] = nino.Entry[int32, int64]{Key: int32(i) * 7, Value: 50 - int64(i)}
		textKeys[i] = strconv.Itoa(i)
	}

	cases := []struct {
		name  string
		value *FixedCollections
	}{
		{"GuidSet8", &FixedCollections{UniqueIds: ids[:8]}},
		{"GuidMap8", &FixedCollections{Ids: entries[:8]}},
		{"GuidMap1024", &FixedCollections{Ids: entries}},
		{"PaddedPairs8", &FixedCollections{Pairs: pairs[:8]}},
	}
	for _, c := range cases {
		data, err := nino.Serialize(c.value, WriteFixedCollections)
		if err != nil {
			b.Fatal(err)
		}
		b.Run(c.name, func(b *testing.B) {
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
