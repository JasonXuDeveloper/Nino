package bench

import (
	"bytes"
	"encoding/gob"
	"encoding/json"
	"math/rand"
	"reflect"
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go"
	"github.com/vmihailenco/msgpack/v5"
)

func one() *SimpleClass { return NewSimpleClass(rand.New(rand.NewSource(20240607))) }

func list() []*SimpleClass {
	rnd := rand.New(rand.NewSource(20240607))
	v := make([]*SimpleClass, 100)
	for i := range v {
		v[i] = NewSimpleClass(rnd)
	}
	return v
}

// TestRoundTrip checks that every serializer compared reads back what it wrote.
func TestRoundTrip(t *testing.T) {
	v := one()
	data, err := nino.Serialize(v, WriteSimpleClass)
	if err != nil {
		t.Fatal(err)
	}
	got, err := nino.DeserializeExact(data, ReadSimpleClass)
	if err != nil || !reflect.DeepEqual(got, v) {
		t.Fatalf("nino: %v", err)
	}
	// The struct-tag path reads the same value (it writes the maps in key order, so the bytes may differ).
	tagged, err := nino.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	var back *SimpleClass
	if err := nino.UnmarshalExact(tagged, &back); err != nil || !reflect.DeepEqual(back, v) {
		t.Fatalf("tags: %v", err)
	}
	if err := nino.UnmarshalExact(data, &back); err != nil || !reflect.DeepEqual(back, v) {
		t.Fatalf("tags reading the codec's bytes: %v", err)
	}
	j, _ := json.Marshal(v)
	m, _ := msgpack.Marshal(v)
	var g bytes.Buffer
	if err := gob.NewEncoder(&g).Encode(v); err != nil {
		t.Fatal(err)
	}
	t.Logf("sizes: nino %d, json %d, msgpack %d, gob %d", len(data), len(j), len(m), g.Len())
}

func benchmarkWrite[T any](b *testing.B, v T, write func(*nino.Writer, T)) {
	buf, _ := nino.Append(nil, v, write)
	b.SetBytes(int64(len(buf)))
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf, _ = nino.Append(buf[:0], v, write)
	}
}

func benchmarkRead[T any](b *testing.B, v T, write func(*nino.Writer, T), read func(*nino.Reader) T) {
	data, _ := nino.Serialize(v, write)
	b.SetBytes(int64(len(data)))
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		if _, err := nino.Deserialize(data, read); err != nil {
			b.Fatal(err)
		}
	}
}

// The struct-tag path: nino.MarshalAppend and nino.Unmarshal.
func benchmarkMarshal(b *testing.B, v any) {
	buf, err := nino.Marshal(v)
	if err != nil {
		b.Fatal(err)
	}
	b.SetBytes(int64(len(buf)))
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf, _ = nino.MarshalAppend(buf[:0], v)
	}
}

func benchmarkUnmarshal[T any](b *testing.B, v T) {
	data, err := nino.Marshal(v)
	if err != nil {
		b.Fatal(err)
	}
	b.SetBytes(int64(len(data)))
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var out T
		if err := nino.Unmarshal(data, &out); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkSerialize(b *testing.B) {
	v, vs := one(), list()
	b.Run("Nino", func(b *testing.B) { benchmarkWrite(b, v, WriteSimpleClass) })
	b.Run("NinoList", func(b *testing.B) { benchmarkWrite(b, vs, WriteSimpleClasses) })
	b.Run("NinoTags", func(b *testing.B) { benchmarkMarshal(b, v) })
	b.Run("NinoTagsList", func(b *testing.B) { benchmarkMarshal(b, vs) })
	b.Run("JSON", func(b *testing.B) {
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			if _, err := json.Marshal(v); err != nil {
				b.Fatal(err)
			}
		}
	})
	b.Run("MsgPack", func(b *testing.B) {
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			if _, err := msgpack.Marshal(v); err != nil {
				b.Fatal(err)
			}
		}
	})
	b.Run("MsgPackList", func(b *testing.B) {
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			if _, err := msgpack.Marshal(vs); err != nil {
				b.Fatal(err)
			}
		}
	})
	// gob with a new encoder per value (each message carries its type), as a one-shot serializer would.
	b.Run("Gob", func(b *testing.B) {
		b.ReportAllocs()
		var buf bytes.Buffer
		for i := 0; i < b.N; i++ {
			buf.Reset()
			if err := gob.NewEncoder(&buf).Encode(v); err != nil {
				b.Fatal(err)
			}
		}
	})
}

func BenchmarkDeserialize(b *testing.B) {
	v, vs := one(), list()
	b.Run("Nino", func(b *testing.B) { benchmarkRead(b, v, WriteSimpleClass, ReadSimpleClass) })
	b.Run("NinoList", func(b *testing.B) { benchmarkRead(b, vs, WriteSimpleClasses, ReadSimpleClasses) })
	b.Run("NinoTags", func(b *testing.B) { benchmarkUnmarshal[*SimpleClass](b, v) })
	b.Run("NinoTagsList", func(b *testing.B) { benchmarkUnmarshal[[]*SimpleClass](b, vs) })
	b.Run("JSON", func(b *testing.B) {
		data, _ := json.Marshal(v)
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			var out SimpleClass
			if err := json.Unmarshal(data, &out); err != nil {
				b.Fatal(err)
			}
		}
	})
	b.Run("MsgPack", func(b *testing.B) {
		data, _ := msgpack.Marshal(v)
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			var out SimpleClass
			if err := msgpack.Unmarshal(data, &out); err != nil {
				b.Fatal(err)
			}
		}
	})
	b.Run("MsgPackList", func(b *testing.B) {
		data, _ := msgpack.Marshal(vs)
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			var out []*SimpleClass
			if err := msgpack.Unmarshal(data, &out); err != nil {
				b.Fatal(err)
			}
		}
	})
	b.Run("Gob", func(b *testing.B) {
		var buf bytes.Buffer
		if err := gob.NewEncoder(&buf).Encode(v); err != nil {
			b.Fatal(err)
		}
		data := buf.Bytes()
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			var out SimpleClass
			if err := gob.NewDecoder(bytes.NewReader(data)).Decode(&out); err != nil {
				b.Fatal(err)
			}
		}
	})
}
