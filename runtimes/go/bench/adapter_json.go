package bench

import (
	"encoding/json"
	"reflect"
)

// jsonAdapter is encoding/json.
type jsonAdapter struct{}

func (jsonAdapter) Name() string { return "JSON" }

func (jsonAdapter) Prepare(c *Case, v any) (*Codec, string) {
	t := reflect.TypeOf(v)
	return checked(c, v, &Codec{
		Encode: func(dst []byte) ([]byte, error) {
			b, err := json.Marshal(v)
			return append(dst, b...), err
		},
		Decode: func(data []byte) (any, error) {
			out := reflect.New(t)
			err := json.Unmarshal(data, out.Interface())
			return out.Elem().Interface(), err
		},
	})
}
