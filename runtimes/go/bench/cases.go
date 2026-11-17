package bench

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"

	"github.com/JasonXuDeveloper/Nino/runtimes/go/bench/model"
	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// A case of the .NET suite (runtimes/csharp/benchmarks), as Nino.Benchmark --write-data lists it in cases.json: the
// .NET type of its value, whose codec the generated model has (model.NinoCodecOf), and the files of its bytes.
type Case struct {
	Class string `json:"class"`
	Name  string `json:"case"`
	// Type is the .NET type's runtime-form name.
	Type string `json:"type"`
	// Proto is the Protocol Buffers message protobuf-net's bytes follow (cases.proto, workloads.proto), or "".
	Proto string `json:"proto"`
	Quick bool   `json:"quick"`
	// Old: the bytes are a previous version of the type (a version-tolerant read), which the type writes otherwise.
	Old bool `json:"old"`

	dir   string
	codec model.NinoCodec
}

// Bytes reads the case's bytes as the library wrote them in .NET (nino, protobuf); nil when it wrote none.
func (c *Case) Bytes(library string) ([]byte, error) {
	data, err := os.ReadFile(filepath.Join(c.dir, c.Name+"."+library+".bin"))
	if os.IsNotExist(err) {
		return nil, nil
	}
	return data, err
}

// dataDir is where generate.sh writes the cases' bytes: NINO_BENCH_DATA, or data/ here.
func dataDir() string {
	if dir := os.Getenv("NINO_BENCH_DATA"); dir != "" {
		return dir
	}
	return "data"
}

// loadCases lists the cases of dir's cases.json that the model has a codec for, in its order: the quick subset with
// NINO_BENCH_QUICK=1, those whose name matches NINO_BENCH_CASES (a regular expression) when it is set. The others
// are returned as missing, each with the reason.
func loadCases(dir string) (cases []*Case, missing []string, err error) {
	text, err := os.ReadFile(filepath.Join(dir, "cases.json"))
	if err != nil {
		return nil, nil, err
	}
	var all []*Case
	if err := json.Unmarshal(text, &all); err != nil {
		return nil, nil, fmt.Errorf("%s: %w", filepath.Join(dir, "cases.json"), err)
	}
	var only *regexp.Regexp
	if pattern := os.Getenv("NINO_BENCH_CASES"); pattern != "" {
		if only, err = regexp.Compile(pattern); err != nil {
			return nil, nil, fmt.Errorf("NINO_BENCH_CASES: %w", err)
		}
	}
	quick := os.Getenv("NINO_BENCH_QUICK") == "1"
	for _, c := range all {
		if (quick && !c.Quick) || (only != nil && !only.MatchString(c.Name)) {
			continue
		}
		codec, ok := model.NinoCodecOf(c.Type)
		if !ok {
			missing = append(missing, fmt.Sprintf("%s: nino-gen writes no Go codec for %s (generate.sh's warnings say why)", c.Name, c.Type))
			continue
		}
		c.dir, c.codec = dir, codec
		cases = append(cases, c)
	}
	return cases, missing, nil
}

// ninoBytes writes v with the case's generated codec: the bytes every adapter's read-back is compared by.
func (c *Case) ninoBytes(v any) ([]byte, error) { return nino.Serialize(v, c.codec.Write) }
