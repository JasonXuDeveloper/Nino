package bench

import (
	"bytes"
	"reflect"

	"github.com/vmihailenco/msgpack/v5"
)

// msgpackAdapter reuses one encoder and its buffer, as a long-lived writer would.
type msgpackAdapter struct{}

func (msgpackAdapter) Name() string { return "MessagePack" }

func (msgpackAdapter) Prepare(c *Case, v any) (*Codec, string) {
	t := reflect.TypeOf(v)
	var buf bytes.Buffer
	enc := msgpack.NewEncoder(&buf)
	return checked(c, v, &Codec{
		Encode: func(dst []byte) ([]byte, error) {
			buf.Reset()
			enc.Reset(&buf)
			err := enc.Encode(v)
			return append(dst, buf.Bytes()...), err
		},
		Decode: func(data []byte) (any, error) {
			out := reflect.New(t)
			err := msgpack.Unmarshal(data, out.Interface())
			return out.Elem().Interface(), err
		},
	})
}
