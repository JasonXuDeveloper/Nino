package bench

import (
	"bytes"
	"encoding/gob"
	"reflect"
)

// gobAdapter uses a new encoder and decoder per value (each message carries its type), as a one-shot serializer
// would.
type gobAdapter struct{}

func (gobAdapter) Name() string { return "Gob" }

func (gobAdapter) Prepare(c *Case, v any) (*Codec, string) {
	t := reflect.TypeOf(v)
	var buf bytes.Buffer
	return checked(c, v, &Codec{
		Encode: func(dst []byte) ([]byte, error) {
			buf.Reset()
			err := gob.NewEncoder(&buf).Encode(v)
			return append(dst, buf.Bytes()...), err
		},
		Decode: func(data []byte) (any, error) {
			out := reflect.New(t)
			err := gob.NewDecoder(bytes.NewReader(data)).Decode(out.Interface())
			return out.Elem().Interface(), err
		},
	})
}
