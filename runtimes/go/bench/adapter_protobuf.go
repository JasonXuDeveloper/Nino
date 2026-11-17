package bench

import (
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"google.golang.org/protobuf/reflect/protoregistry"

	_ "github.com/JasonXuDeveloper/Nino/runtimes/go/bench/benchpb/cases"
	_ "github.com/JasonXuDeveloper/Nino/runtimes/go/bench/benchpb/workloads"
)

// protobufAdapter is google.golang.org/protobuf on the message of the case's protobuf-net contract (benchpb, from
// the .NET suite's .proto files), parsed from protobuf-net's bytes once outside the timing, as an application that
// keeps its data in generated protobuf structs would. A case without such a message has no protobuf row, as in .NET.
type protobufAdapter struct{}

func (protobufAdapter) Name() string { return "Protobuf" }

// The .proto files' packages, where a case's message is looked up by its name.
var protoPackages = []protoreflect.FullName{"nino.cases", "nino.workloads"}

func (protobufAdapter) Prepare(c *Case, _ any) (*Codec, string) {
	if c.Proto == "" {
		return nil, "no Protocol Buffers message (as in .NET: no Google.Protobuf rows)"
	}
	var kind protoreflect.MessageType
	for _, p := range protoPackages {
		if t, err := protoregistry.GlobalTypes.FindMessageByName(p.Append(protoreflect.Name(c.Proto))); err == nil {
			kind = t
			break
		}
	}
	if kind == nil {
		return nil, "no message " + c.Proto + " in benchpb (generate.sh --proto)"
	}
	data, err := c.Bytes("protobuf")
	if err != nil || data == nil {
		return nil, "no protobuf-net bytes"
	}
	m := kind.New().Interface()
	if err := proto.Unmarshal(data, m); err != nil {
		return nil, "protobuf-net's bytes: " + err.Error()
	}
	opts := proto.MarshalOptions{}
	codec := &Codec{
		Encode: func(dst []byte) ([]byte, error) { return opts.MarshalAppend(dst, m) },
		Decode: func(data []byte) (any, error) {
			out := kind.New().Interface()
			err := proto.Unmarshal(data, out)
			return out, err
		},
	}
	back, err := codec.Encode(nil)
	if err != nil {
		return nil, "encode: " + err.Error()
	}
	got, err := codec.Decode(back)
	if err != nil || !proto.Equal(got.(proto.Message), m) {
		return nil, "reads back another message"
	}
	return codec, ""
}
