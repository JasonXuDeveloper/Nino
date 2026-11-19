package nino

import (
	"bytes"
	"strconv"
	"testing"
)

const freshReadProbeSize = 1 << 20

// A positional object with a byte array and a trailing scalar.
func writeBytePayload(w *Writer, payload []byte) {
	w.Present(true)
	w.ByteArray(payload)
	w.U32(0x12345678)
}

func readBytePayload(r *Reader) []byte {
	if !r.Present() {
		return nil
	}
	payload := r.ByteArray()
	if r.U32() != 0x12345678 {
		r.Fail(CodeInvalidData, "payload trailer")
	}
	return payload
}

func TestFreshRawRead(t *testing.T) {
	for _, n := range []int{0, 128, freshReadProbeSize - 1, freshReadProbeSize, freshReadProbeSize + 1} {
		payload := make([]byte, n)
		for i := range payload {
			payload[i] = byte(i*73 + i/64)
		}
		wire, err := Serialize(payload, writeBytePayload)
		if err != nil {
			t.Fatal(err)
		}
		shifted := make([]byte, len(wire)+63)
		for off := range 64 {
			input := shifted[off : off+len(wire)]
			copy(input, wire)
			out, err := DeserializeExact(input, readBytePayload)
			if err != nil || out == nil || !bytes.Equal(out, payload) {
				t.Fatalf("size %d, offset %d: copy differs (%v)", n, off, err)
			}
			if n != 0 {
				out[0] ^= 0xFF
				if !bytes.Equal(input, wire) {
					t.Fatalf("size %d, offset %d: decoded value aliases the input", n, off)
				}
			}
		}
	}
}

func BenchmarkFreshRawRead(b *testing.B) {
	for _, n := range []int{128, freshReadProbeSize - (64 << 10), freshReadProbeSize, freshReadProbeSize + (64 << 10)} {
		payload := make([]byte, n)
		for i := range payload {
			payload[i] = byte(i*73 + i/64)
		}
		wire, err := Serialize(payload, writeBytePayload)
		if err != nil {
			b.Fatal(err)
		}
		b.Run(strconv.Itoa(n)+"/HotSource", func(b *testing.B) {
			benchmarkFreshRawRead(b, [][]byte{wire}, payload)
		})
		if n != freshReadProbeSize {
			continue // Adjacent-size controls need only a hot source.
		}
		b.Run(strconv.Itoa(n)+"/Rotating64MiB", func(b *testing.B) {
			// Keep about 64 MiB of distinct source buffers live, with phase-varying alignment.
			// The read always allocates its own destination, including in the hot-source control.
			const workingSet = 64 << 20
			stride := (len(wire) + 126) &^ 63
			storage := make([]byte, workingSet)
			inputs := make([][]byte, len(storage)/stride)
			for i := range inputs {
				off := i*stride + ((i * 17) & 63)
				inputs[i] = storage[off : off+len(wire)]
				copy(inputs[i], wire)
			}
			benchmarkFreshRawRead(b, inputs, payload)
		})
	}
}

func benchmarkFreshRawRead(b *testing.B, inputs [][]byte, payload []byte) {
	for _, i := range []int{0, len(inputs) - 1} {
		out, err := DeserializeExact(inputs[i], readBytePayload)
		if err != nil || !bytes.Equal(out, payload) {
			b.Fatalf("decoded payload differs (%v)", err)
		}
	}
	b.ReportAllocs()
	b.SetBytes(int64(len(payload)))
	b.ResetTimer()
	cursor := 0
	for range b.N {
		out, err := DeserializeExact(inputs[cursor], readBytePayload)
		if err != nil || len(out) != len(payload) {
			b.Fatalf("decoded payload has %d bytes (%v)", len(out), err)
		}
		cursor++
		if cursor == len(inputs) {
			cursor = 0
		}
	}
}
