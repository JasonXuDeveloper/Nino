package bench

import (
	"testing"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The cases of the .NET suite on the bytes it wrote (generate.sh): for each, the generated codec reads .NET's Nino
// bytes and must write them back, then every adapter (adapter_*.go) that can express the case is timed. The
// benchmarks are Benchmark{Serialize,Deserialize}/<case>/<adapter>; an adapter that cannot express a case is
// skipped with the reason.

// load reads the cases, or skips: without generate.sh's data there is nothing to run.
func load(tb testing.TB) []*Case {
	cases, missing, err := loadCases(dataDir())
	if err != nil {
		tb.Skipf("no benchmark data (run generate.sh --data, or set NINO_BENCH_DATA): %v", err)
	}
	for _, m := range missing {
		tb.Log(m)
	}
	return cases
}

// value reads the case's value from .NET's Nino bytes with the generated codec, and checks it writes them back.
// An object holding a .NET type Go has no form of (BclTypesTest's BigInteger, Uri...) is not a case Go can
// express: its reason, and no value.
func value(tb testing.TB, c *Case) (any, string) {
	data, err := c.Bytes("nino")
	if err != nil || data == nil {
		tb.Fatalf("%s: no Nino bytes: %v", c.Name, err)
	}
	v, err := nino.DeserializeExact(data, c.codec.Read)
	if e, ok := err.(*nino.Error); ok && e.Code == nino.CodeUnknownType && c.Type == "System.Object" {
		return nil, "an object holding a .NET type with no Go form: " + err.Error()
	}
	if err != nil {
		tb.Fatalf("%s: reading .NET's bytes: %v", c.Name, err)
	}
	if err := checkNino(c, v, data); err != nil {
		tb.Fatalf("%s: %v", c.Name, err)
	}
	return v, ""
}

// TestCases reads and writes back every case, and checks every adapter on it, logging which cannot and the sizes.
func TestCases(t *testing.T) {
	for _, c := range load(t) {
		v, why := value(t, c)
		if why != "" {
			t.Logf("%s: skipped: %s", c.Name, why)
			continue
		}
		for _, a := range adapters {
			codec, why := a.Prepare(c, v)
			if codec == nil {
				t.Logf("%s/%s: skipped: %s", c.Name, a.Name(), why)
				continue
			}
			data, _ := codec.Encode(nil)
			t.Logf("%s/%s: %d bytes", c.Name, a.Name(), len(data))
		}
	}
}

// Each benchmark reports the encoded size of its value as wire-B (after the loop, which resets the metrics), and
// sets it as the bytes per op, for MB/s. Each case's value lives only while its benchmarks run.

func BenchmarkSerialize(b *testing.B) {
	run(b, func(b *testing.B, codec *Codec) {
		buf, err := codec.Encode(nil)
		if err != nil {
			b.Fatal(err)
		}
		b.SetBytes(int64(len(buf)))
		b.ReportAllocs()
		for b.Loop() {
			buf, _ = codec.Encode(buf[:0])
		}
		b.ReportMetric(float64(len(buf)), "wire-B")
	})
}

func BenchmarkDeserialize(b *testing.B) {
	run(b, func(b *testing.B, codec *Codec) {
		data, err := codec.Encode(nil)
		if err != nil {
			b.Fatal(err)
		}
		b.SetBytes(int64(len(data)))
		b.ReportAllocs()
		for b.Loop() {
			if _, err := codec.Decode(data); err != nil {
				b.Fatal(err)
			}
		}
		b.ReportMetric(float64(len(data)), "wire-B")
	})
}

func run(b *testing.B, time func(*testing.B, *Codec)) {
	for _, c := range load(b) {
		b.Run(c.Name, func(b *testing.B) {
			v, why := value(b, c)
			if why != "" {
				b.Skip(why)
			}
			for _, a := range adapters {
				b.Run(a.Name(), func(b *testing.B) {
					codec, why := a.Prepare(c, v)
					if codec == nil {
						b.Skip(why)
					}
					time(b, codec)
				})
			}
		})
	}
}
