package bench

import (
	"bytes"
	"fmt"
)

// An Adapter is one serializer the cases are timed with (adapter_*.go, one file each). Prepare makes its codec of a
// case's value, read from .NET's Nino bytes by the generated codec: nil and the reason when the serializer cannot
// express the case (it fails, or what it reads back is not the value); a case it then times differently would be
// another case.
type Adapter interface {
	Name() string
	Prepare(c *Case, v any) (*Codec, string)
}

// A Codec is an adapter's two timed operations on one value: Encode appends its bytes to dst (reusing dst's room
// where the library can), Decode reads them into a new value.
type Codec struct {
	Encode func(dst []byte) ([]byte, error)
	Decode func(data []byte) (any, error)
}

// adapters are the serializers in the order the report's columns follow (runtimes_report.py go).
var adapters = []Adapter{ninoAdapter{}, ninoTagsAdapter{}, protobufAdapter{}, msgpackAdapter{}, jsonAdapter{}, gobAdapter{}}

// checked is how every adapter but Nino's checks its codec: it encodes the value, decodes it, and what it read back
// must write the same Nino bytes as the value (so a shared reference, an interface value or a NaN it loses shows).
func checked(c *Case, v any, codec *Codec) (_ *Codec, why string) {
	// A library that panics on a value (msgpack decoding an interface field) cannot express the case.
	defer func() {
		if p := recover(); p != nil {
			why = fmt.Sprint("panics: ", p)
		}
	}()
	want, err := c.ninoBytes(v)
	if err != nil {
		return nil, "the generated codec: " + err.Error()
	}
	data, err := codec.Encode(nil)
	if err != nil {
		return nil, "encode: " + err.Error()
	}
	back, err := codec.Decode(data)
	if err != nil {
		return nil, "decode: " + err.Error()
	}
	got, err := c.ninoBytes(back)
	if err != nil {
		return nil, "the value it read back: " + err.Error()
	}
	if !bytes.Equal(got, want) {
		return nil, fmt.Sprintf("reads back another value (%d Nino bytes, %d expected)", len(got), len(want))
	}
	return codec, ""
}
