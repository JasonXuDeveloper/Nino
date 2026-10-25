package bench

import (
	"bytes"
	"encoding/gob"
	"encoding/json"
	"math/rand"
	"reflect"
	"testing"

	"github.com/JasonXuDeveloper/Nino/runtimes/go/bench/benchpb"
	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
	"github.com/vmihailenco/msgpack/v5"
	"google.golang.org/protobuf/proto"
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

// A serializer of one case's value: encode appends the value's bytes to dst (reusing its room where the library
// can), decode reads them into a new value and returns it.

func equals[T any](v T) func(any) bool {
	return func(got any) bool { return reflect.DeepEqual(got, v) }
}

type codec struct {
	name   string
	encode func(dst []byte) ([]byte, error)
	decode func(data []byte) (any, error)
	// same reports whether a decoded value equals the one encoded.
	same func(got any) bool
}

// A case: a value and every serializer compared on it, Nino first. The benchmarks are
// Benchmark{Serialize,Deserialize}/<case>/<serializer>.
type benchCase struct {
	name   string
	value  any
	codecs []codec
}

// ninoCodec is the codec in the shape nino-gen generates.
func ninoCodec[T any](v T, write func(*nino.Writer, T), read func(*nino.Reader) T) codec {
	return codec{"Nino",
		func(dst []byte) ([]byte, error) { return nino.Append(dst, v, write) },
		func(data []byte) (any, error) { return nino.Deserialize(data, read) },
		equals(v)}
}

// tagsCodec is the struct-tag path: nino.MarshalAppend and nino.Unmarshal.
func tagsCodec[T any](v T) codec {
	return codec{"NinoTags",
		func(dst []byte) ([]byte, error) { return nino.MarshalAppend(dst, v) },
		func(data []byte) (any, error) {
			var out T
			err := nino.Unmarshal(data, &out)
			return out, err
		},
		equals(v)}
}

// protoCodec marshals the message the value maps to (benchpb), made once outside the timing, as an application
// that keeps its data in generated protobuf structs would.
func protoCodec[M proto.Message](m M, fresh func() M) codec {
	opts := proto.MarshalOptions{}
	return codec{"Protobuf",
		func(dst []byte) ([]byte, error) { return opts.MarshalAppend(dst, m) },
		func(data []byte) (any, error) {
			out := fresh()
			err := proto.Unmarshal(data, out)
			return out, err
		},
		func(got any) bool { return proto.Equal(got.(proto.Message), m) }}
}

// msgpackCodec reuses one encoder and its buffer, as a long-lived writer would.
func msgpackCodec[T any](v T) codec {
	var buf bytes.Buffer
	enc := msgpack.NewEncoder(&buf)
	return codec{"MessagePack",
		func(dst []byte) ([]byte, error) {
			buf.Reset()
			enc.Reset(&buf)
			err := enc.Encode(v)
			return append(dst, buf.Bytes()...), err
		},
		func(data []byte) (any, error) {
			var out T
			err := msgpack.Unmarshal(data, &out)
			return out, err
		},
		equals(v)}
}

func jsonCodec[T any](v T) codec {
	return codec{"JSON",
		func(dst []byte) ([]byte, error) {
			b, err := json.Marshal(v)
			return append(dst, b...), err
		},
		func(data []byte) (any, error) {
			var out T
			err := json.Unmarshal(data, &out)
			return out, err
		},
		equals(v)}
}

// gobCodec uses a new encoder and decoder per value (each message carries its type), as a one-shot serializer
// would.
func gobCodec[T any](v T) codec {
	var buf bytes.Buffer
	return codec{"Gob",
		func(dst []byte) ([]byte, error) {
			buf.Reset()
			err := gob.NewEncoder(&buf).Encode(v)
			return append(dst, buf.Bytes()...), err
		},
		func(data []byte) (any, error) {
			var out T
			err := gob.NewDecoder(bytes.NewReader(data)).Decode(&out)
			return out, err
		},
		equals(v)}
}

func stringsCase(name string, v []string) benchCase {
	return benchCase{name, v, []codec{
		ninoCodec(v, WriteStrings, ReadStrings),
		tagsCodec(v),
		protoCodec(&benchpb.Strings{Values: v}, func() *benchpb.Strings { return &benchpb.Strings{} }),
		msgpackCodec(v),
		jsonCodec(v),
		gobCodec(v),
	}}
}

func cases() []benchCase {
	v, vs := one(), list()
	return []benchCase{
		{"simple-class", v, []codec{
			ninoCodec(v, WriteSimpleClass, ReadSimpleClass),
			tagsCodec(v),
			protoCodec(protoSimpleClass(v), func() *benchpb.SimpleClass { return &benchpb.SimpleClass{} }),
			msgpackCodec(v),
			jsonCodec(v),
			gobCodec(v),
		}},
		{"simple-class-100", vs, []codec{
			ninoCodec(vs, WriteSimpleClasses, ReadSimpleClasses),
			tagsCodec(vs),
			protoCodec(protoSimpleClasses(vs), func() *benchpb.SimpleClasses { return &benchpb.SimpleClasses{} }),
			msgpackCodec(vs),
			jsonCodec(vs),
			gobCodec(vs),
		}},
		stringsCase("strings-1000", ASCIIStrings()),
		stringsCase("strings-mixed-1000", MixedStrings()),
	}
}

// TestRoundTrip checks that every serializer compared reads back what it wrote, and logs the sizes.
func TestRoundTrip(t *testing.T) {
	for _, c := range cases() {
		for _, s := range c.codecs {
			data, err := s.encode(nil)
			if err != nil {
				t.Fatalf("%s/%s: %v", c.name, s.name, err)
			}
			got, err := s.decode(data)
			if err != nil {
				t.Fatalf("%s/%s: %v", c.name, s.name, err)
			}
			if !s.same(got) {
				t.Fatalf("%s/%s: read back differs", c.name, s.name)
			}
			t.Logf("%s/%s: %d bytes", c.name, s.name, len(data))
		}
	}
	// The struct-tag path reads the codec's bytes too (it writes the maps in key order, so the bytes may differ).
	v := one()
	data, _ := nino.Serialize(v, WriteSimpleClass)
	var back *SimpleClass
	if err := nino.UnmarshalExact(data, &back); err != nil || !reflect.DeepEqual(back, v) {
		t.Fatalf("tags reading the codec's bytes: %v", err)
	}
}

// Each benchmark reports the encoded size of its value as wire-B (after the loop, which resets the metrics), and
// sets it as the bytes per op, for MB/s.

func BenchmarkSerialize(b *testing.B) {
	for _, c := range cases() {
		for _, s := range c.codecs {
			b.Run(c.name+"/"+s.name, func(b *testing.B) {
				buf, err := s.encode(nil)
				if err != nil {
					b.Fatal(err)
				}
				b.SetBytes(int64(len(buf)))
				b.ReportAllocs()
				for b.Loop() {
					buf, _ = s.encode(buf[:0])
				}
				b.ReportMetric(float64(len(buf)), "wire-B")
			})
		}
	}
}

func BenchmarkDeserialize(b *testing.B) {
	for _, c := range cases() {
		for _, s := range c.codecs {
			b.Run(c.name+"/"+s.name, func(b *testing.B) {
				data, err := s.encode(nil)
				if err != nil {
					b.Fatal(err)
				}
				b.SetBytes(int64(len(data)))
				b.ReportAllocs()
				for b.Loop() {
					if _, err := s.decode(data); err != nil {
						b.Fatal(err)
					}
				}
				b.ReportMetric(float64(len(data)), "wire-B")
			})
		}
	}
}
