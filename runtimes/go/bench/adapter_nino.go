package bench

import (
	"bytes"
	"fmt"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// ninoAdapter is the codec nino-gen writes (model/), with nino.Append and nino.Deserialize. Its check is the
// conformance one: the value read from .NET's bytes writes those bytes again (a case of a previous version writes
// the current one, which must read back to itself).
type ninoAdapter struct{}

func (ninoAdapter) Name() string { return "Nino" }

func (ninoAdapter) Prepare(c *Case, v any) (*Codec, string) {
	codec := &Codec{
		Encode: func(dst []byte) ([]byte, error) { return nino.Append(dst, v, c.codec.Write) },
		Decode: func(data []byte) (any, error) { return nino.Deserialize(data, c.codec.Read) },
	}
	return codec, ""
}

// checkNino is the check the harness makes before timing any adapter of a case: an error fails the run.
func checkNino(c *Case, v any, dotnet []byte) error {
	data, err := c.ninoBytes(v)
	if err != nil {
		return err
	}
	if !c.Old && !bytes.Equal(data, dotnet) {
		return fmt.Errorf("writes %d bytes back, not the %d .NET wrote (first difference at %d)", len(data), len(dotnet), firstDiff(data, dotnet))
	}
	back, err := nino.DeserializeExact(data, c.codec.Read)
	if err != nil {
		return fmt.Errorf("reading its own bytes: %w", err)
	}
	again, err := c.ninoBytes(back)
	if err != nil || !bytes.Equal(again, data) {
		return fmt.Errorf("its own bytes do not read back to the same value (%v)", err)
	}
	return nil
}

func firstDiff(a, b []byte) int {
	for i := range min(len(a), len(b)) {
		if a[i] != b[i] {
			return i
		}
	}
	return min(len(a), len(b))
}
