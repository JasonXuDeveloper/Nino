package bench

import (
	"reflect"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// ninoTagsAdapter is the struct-tag path on the generated structs: nino.MarshalAppend and nino.Unmarshal.
type ninoTagsAdapter struct{}

func (ninoTagsAdapter) Name() string { return "NinoTags" }

func (ninoTagsAdapter) Prepare(c *Case, v any) (*Codec, string) {
	t := reflect.TypeOf(v)
	return checked(c, v, &Codec{
		Encode: func(dst []byte) ([]byte, error) { return nino.MarshalAppend(dst, v) },
		Decode: func(data []byte) (any, error) {
			out := reflect.New(t)
			err := nino.Unmarshal(data, out.Interface())
			return out.Elem().Interface(), err
		},
	})
}
